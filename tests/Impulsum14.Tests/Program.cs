using ProtoFire.Tls;
using PlayerState = Impulsum14.PlayerState;
using HttpMethod = System.Net.Http.HttpMethod;
using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.GameManager;
using Blaze3SDK.Components;
using EATDF;
using EATDF.Types;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Impulsum14;
using Microsoft.Extensions.Logging.Abstractions;
using ProtoFire;
using ProtoFire.Frames;
using EATDF.Serialization;
using Blaze3SDK.Blaze.Authentication;

string temp = Path.Combine(Path.GetTempPath(), "impulsum-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string token1 = "test-only-credential-one", token2 = "test-only-credential-two";
string accountsFile = Path.Combine(temp, "accounts.json");
File.WriteAllText(accountsFile, JsonSerializer.Serialize(new[]
{
    new { Id = 1001, Name = "Alpha", TokenSha256 = Hash(token1) }, new { Id = 1002, Name = "Beta", TokenSha256 = Hash(token2) }
}));
var options = new ServerOptions { DataDirectory = temp, LocalMode = false, AccountsFile = accountsFile, RequestsPerSecond = 10000, WebPort = FreePort(), RedirectorPort = FreePort(), BlazePort = FreePort(), MaintenanceIntervalMs = 60000 };
options.PublicWebUrl = $"http://127.0.0.1:{options.WebPort}";
options.Validate();
ServerOptions.Current = options;
using var repo = new PlayerRepository();
using var ranked = new RankedService(temp, options.Ranked);
RankedService.Current = ranked;
var a = repo.Authenticate(token1); var b = repo.Authenticate(token2);
var tests = new List<(string, Func<Task>)>();
void Test(string name, Func<Task> test) => tests.Add((name, test));
Task Run(PlayerSession who, Action action) => repo.RunAsync(who, () => { action(); return Task.FromResult(0); });
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool value, string message) { if (!value) throw new Exception(message); }

Test("Configuration rejects unsafe bind, invalid ticks, overlapping ports", () =>
{
    foreach (var invalid in new[] { new ServerOptions { BindAddress = "0.0.0.0" }, new ServerOptions { TickRate = 31 }, new ServerOptions { BlazePort = 9988 } })
    { try { invalid.Validate(); throw new Exception("Invalid config accepted"); } catch (InvalidDataException) { } }
    return Task.CompletedTask;
});
Test("Configured division rules drive catalogue and settlement and reject invalid tables", () =>
{
    var saved = options.OfflineDivisionRules;
    try
    {
        var custom = Enumerable.Range(1, 10).Select(d => Seasons.Rules(d, false)).ToArray();
        custom[9] = custom[9] with { Title = 15, TitleCoins = 4321 };
        options.OfflineDivisionRules = custom; options.Validate();
        Equal(4321, Seasons.Evaluate(10, 15, false).Coins);
        Equal("PROMOTION", Seasons.Evaluate(10, 12, false).Outcome);
        options.OfflineDivisionRules = custom.Take(9).ToArray();
        try { options.Validate(); throw new Exception("Short division policy accepted"); } catch (InvalidDataException) { }
        options.OfflineDivisionRules = custom.Select(r => r with { Hold = 31 }).ToArray();
        try { options.Validate(); throw new Exception("Impossible division policy accepted"); } catch (InvalidDataException) { }
    }
    finally { options.OfflineDivisionRules = saved; }
    return Task.CompletedTask;
});
Test("Credentials and identity binding", () =>
{
    Equal(1001L, a.Account.Id); Equal(1002L, b.Account.Id);
    try { repo.Authenticate("unknown"); throw new Exception("Unknown credential accepted"); } catch (UnauthorizedAccessException) { }
    Equal(1001L, repo.ResolveWeb(new NameValueCollection { ["X-UT-SID"] = a.Key }).Account.Id);
    return Task.CompletedTask;
});
Test("Concurrent transactions preserve balances and isolate accounts", async () =>
{
    await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Run(i % 2 == 0 ? a : b, () => FutProfileStore.Mutate(p => p.Coins++))));
    await Run(a, () => Equal(20L, FutProfileStore.Get().Coins));
    await Run(b, () => Equal(20L, FutProfileStore.Get().Coins));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task first = repo.RunAsync(a, async () => { entered.SetResult(); await release.Task; return 0; });
    await entered.Task;
    await Run(b, () => Equal("Beta", PlayerContext.Current.Name)).WaitAsync(TimeSpan.FromSeconds(2));
    release.SetResult(); await first;
});
Test("Failed transactions roll wallet and inventory back together", async () =>
{
    long before = a.Account.Data.Profile.Coins;
    a.Account.HttpCache["invalid-transaction"] = new(a.Account.Revision, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new HttpReply(200, "application/json", new byte[1], new()));
    a.Account.HttpCacheBytes = 1;
    try { await Run(a, () => { FutProfileStore.Mutate(p => p.Coins += 900); ClubStore.Mutate(c => c.ActiveSquadId = 99); throw new InvalidOperationException("test"); }); } catch (InvalidOperationException) { }
    await Run(a, () => { Equal(before, FutProfileStore.Get().Coins); Equal(0, ClubStore.Get().ActiveSquadId); Equal(0, a.Account.HttpCache.Count); });
    using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp, "players/1001.json")));
    Equal(before, saved.RootElement.GetProperty("Profile").GetProperty("Coins").GetInt64());
});
Test("Tournament stubs excluded; native buffers precede versions", () => Run(a, () =>
{
    Tournaments.SaveProgress(1, 1, 1, "AAAAAA==", 1, "AAAAAA==");
    Equal("{\"tournamentId\":[]}", Tournaments.UserListJson());
    Equal("{\"tournamentId\":1}", Tournaments.UserTournamentJson(1));
    Tournaments.SettleTournamentMatch(1, "WIN");
    Tournaments.SaveProgress(1, 2, 1, "AQIDBA==", 1, "AQIDBA==");
    string payload = Tournaments.UserTournamentJson(1);
    True(payload.IndexOf("tournamentData", StringComparison.Ordinal) < payload.IndexOf("dataVersion", StringComparison.Ordinal), "Native buffer/version order changed");
    True(payload.Contains("progressdata"), "Native lowercase progressdata missing");
    Equal((0, false), Tournaments.SettleTournamentMatch(1, "WIN"));
    Equal(3, FutProfileStore.Get().SavedTournaments[1].Round);
    Equal("{\"tournamentId\":1}", Tournaments.UserTournamentJson(1));
    Tournaments.SettleTournamentMatch(1, "LOSS");
    True(!FutProfileStore.Get().SavedTournaments[1].Active && FutProfileStore.Get().SavedTournaments[1].CompletedRounds == 0, "Lost cup not reset");
    Tournaments.SaveProgress(1, 4, 1, "AQIDBA==", 1, "AQIDBA==");
    Equal((0, false), Tournaments.SettleTournamentMatch(1, "WIN"));
    Equal(2, FutProfileStore.Get().SavedTournaments[1].Round);
    Tournaments.SettleTournamentMatch(1, "LOSS");
}));
Test("Ten-match seasons promote, relegate, pay once, and keep history", () => Run(a, () =>
{
    var profile = FutProfileStore.Get();
    profile.OfflineDivision = 10; profile.Season = new();
    for (int i = 0; i < 9; i++) Equal(0, Seasons.Settle(profile, "WIN", false));
    Equal(1900, Seasons.Settle(profile, "WIN", false));
    Equal(9, profile.OfflineDivision); Equal(1, profile.SeasonHistory.Count); Equal(0, profile.Season.GamesPlayed);
    profile.OfflineDivision = 9; profile.Season = new();
    for (int i = 0; i < 10; i++) Seasons.Settle(profile, "LOSS", false);
    Equal(10, profile.OfflineDivision); Equal(2, profile.SeasonHistory.Count);
    Equal(0, profile.OnlineSeason.GamesPlayed);
    PlayerContext.MarkDirty();
}));
Test("Season catalog IDs resolve and stub saves are not resumable", () => Run(a, () =>
{
    using var catalogue = JsonDocument.Parse(Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" }));
    var first = catalogue.RootElement.GetProperty("seasons")[0];
    Equal(1, first.GetProperty("id").GetInt32()); Equal(11, first.GetProperty("divisionId").GetInt32());
    Equal(0, first.GetProperty("matches")[0].GetProperty("roundId").GetInt32());
    Seasons.CaptureSave(FutProfileStore.Get(), "{\"round\":1,\"data\":\"AAAAAA==\"}");
    using var state = JsonDocument.Parse(Seasons.UserJson(FutProfileStore.Get()));
    Equal(-1, state.RootElement.GetProperty("seasonId").GetInt32());
    True(!CompetitionSave.Resumable("not-base64", "", 2, true), "Invalid native save accepted");
    PlayerContext.MarkDirty();
}));
Test("Match retries settle once, DNF pays zero, IDs are monotonic", () => Run(a, () =>
{
    var data = PlayerContext.Current.Data; data.Match = null!; data.LastCompetition = "";
    long before = data.Profile.Coins;
    var match = MatchService.Begin("{}");
    Equal(match.Id, MatchService.Begin("{}").Id);
    int consequences = 0;
    string body = $"{{\"matchId\":{match.Id},\"endReason\":\"WIN\",\"myMatchStats\":{{\"goals\":2}}}}";
    string response = MatchService.End(body, _ => consequences++);
    Equal(response, MatchService.End(body, _ => consequences++));
    Equal(1, consequences); Equal(before + 540, data.Profile.Coins);
    var next = MatchService.Begin("{}"); True(next.Id > match.Id, "Match IDs repeated");
    MatchService.End("{\"endReason\":\"DNF\"}", _ => consequences++);
    Equal(before + 540, data.Profile.Coins); Equal(1, consequences);
}));
Test("Manager claims use observed events and cannot pay twice", () => Run(a, () =>
{
    var data = PlayerContext.Current.Data;
    data.ManagerTasks.Clear(); data.ManagerCompletionClaimed = false;
    long before = data.Profile.Coins;
    ManagerTasks.Claim(); Equal(before, data.Profile.Coins);
    foreach (var definition in ManagerTasks.Definitions) ManagerTasks.Observe(definition.Event);
    ManagerTasks.Claim(); long paid = data.Profile.Coins;
    Equal(before + ManagerTasks.Definitions.Length * options.ManagerTaskCoins + options.ManagerCompletionCoins, paid);
    ManagerTasks.Claim(); Equal(paid, data.Profile.Coins);
}));
Test("Legends disabled in stock catalog; complete verified metadata", () => Run(a, () =>
{
    Equal(42, Legends.Catalog.Length);
    True(SpecialCards.All.All(p => p.Set != "legend"), "Legends leaked into stock PC catalog");
    options.EnableLegends = true;
    try { Legends.ValidateClientManifest(); throw new Exception("Missing manifest accepted"); } catch (InvalidDataException) { }
    finally { options.EnableLegends = false; }
}));
Test("Bounded market searches emit valid unique comparable cards", () => Run(a, () =>
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var page = JsonDocument.Parse(Market.PageJson(0, 21, now));
    True(page.RootElement.GetArrayLength() > 0, "Empty synthetic market");
    var rows = page.RootElement.EnumerateArray().ToArray();
    Equal(rows.Length, rows.Select(r => r.GetProperty("tradeId").GetInt64()).Distinct().Count());
    int cardId = rows[0].GetProperty("itemData").GetProperty("resourceId").GetInt32();
    using var compare = JsonDocument.Parse(Market.PageJson(0, 21, now, p => p.CardId == cardId));
    True(compare.RootElement.EnumerateArray().All(r => r.GetProperty("itemData").GetProperty("resourceId").GetInt32() == cardId), "Compare returned a different card variant");
}));
Test("Fire/Fire2 framing rejects oversized input and round-trips payloads", async () =>
{
    var serializer = new Heat2Serializer();
    var auth = new OriginLoginRequest { AuthToken = "test" };
    byte[] payload = serializer.Serialize(auth);
    foreach (bool fire2 in new[] { false, true })
    {
        using var wire = new MemoryStream();
        ProtoFirePacket packet = fire2 ? new ProtoFire2Packet(new Fire2Frame { Component = 1, Command = 152 }, Array.Empty<byte>(), payload) : new ProtoFirePacket(new FireFrame { Component = 1, Command = 152 }, payload);
        if (packet is ProtoFire2Packet p2) await p2.WriteToAsync(wire); else await packet.WriteToAsync(wire);
        wire.Position = 0;
        var decoded = fire2 ? await ProtoFire2Packet.ReadFromAsync(wire) : await ProtoFirePacket.ReadFromAsync<FireFrame>(wire);
        True(decoded is not null && decoded.Data.SequenceEqual(payload), "Packet payload changed");
        wire.Position = 0;
        var oversized = fire2 ? await ProtoFire2Packet.ReadFromAsync(wire, 1) : await ProtoFirePacket.ReadFromAsync<FireFrame>(wire, 1);
        True(oversized is null, "Oversized packet accepted");
    }
});

using var stop = new CancellationTokenSource();
var router = new BlazeRouter();
router.AddComponent<AuthenticationComponent>();
router.AddComponent(new UtilComponent(NullLogger.Instance, options.PublicWebUrl));
router.AddComponent<GameReportingComponent>();
router.AddComponent(new GameManagerComponent(ranked));
var blaze = new BlazeServer(new BlazeServerConfig { LocalEndpoint = new IPEndPoint(IPAddress.Loopback, options.BlazePort), Router = router, CallbackHandler = new ServerCallbacks(null!, NullLogger.Instance), CommandMiddleware = repo.RunRpcAsync }, NullLogger<BlazeServer>.Instance);
Task blazeTask = blaze.StartAsync(stop.Token);
var testCertificate = ProtoSSLCertificate.FromPkcs12File(Path.Combine(AppContext.BaseDirectory, "certs/server.pfx"));
var redirectorRouter = new BlazeRouter(); redirectorRouter.AddComponent(new RedirectorComponent(options.BlazePort, NullLogger.Instance));
var redirector = new BlazeServer(new BlazeServerConfig { LocalEndpoint = new IPEndPoint(IPAddress.Loopback, options.RedirectorPort), Secure = true, Router = redirectorRouter, CallbackHandler = new ServerCallbacks(testCertificate, NullLogger.Instance) }, NullLogger<BlazeServer>.Instance);
Task redirectorTask = redirector.StartAsync(stop.Token);
Task server = new WebServer(options.WebPort, NullLogger.Instance).StartAsync(stop.Token);
using var http = new HttpClient { BaseAddress = new Uri(options.PublicWebUrl), Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.Add("X-UT-SID", a.Key);
Test("Live HTTP auth, chunked bodies, method overrides and identity checks", async () =>
{
    var ready = await http.GetAsync("/healthz"); ready.EnsureSuccessStatusCode();
    (await http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/user", new { clubName = "Alpha FC", clubAbbr = "ALP" })).EnsureSuccessStatusCode();
    using var unauth = new HttpClient { BaseAddress = http.BaseAddress };
    Equal(HttpStatusCode.Unauthorized, (await unauth.GetAsync("/fut/rs4/ut/game/fifa14/user")).StatusCode);
    Equal(HttpStatusCode.OK, (await unauth.GetAsync("/fut/dynamicmessages/fut/loc/pc/icebreaker.ENG_US.xml")).StatusCode);
    using var spoof = new HttpRequestMessage(HttpMethod.Get, "/fut/rs4/ut/game/fifa14/user"); spoof.Headers.Add("Easw-Session-Data-Nucleus-Id", "1002");
    Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(spoof)).StatusCode);
    using var save = new HttpRequestMessage(HttpMethod.Post, "/fut/rs4/ut/game/fifa14/clientdata/network-test");
    save.Headers.Add("X-HTTP-Method-Override", "PUT");
    save.Headers.TransferEncodingChunked = true;
    save.Content = new StringContent("{\"sample\":7}", Encoding.UTF8, "application/json");
    (await http.SendAsync(save)).EnsureSuccessStatusCode();
    Equal("{\"sample\":7}", await http.GetStringAsync("/fut/rs4/ut/game/fifa14/clientdata/network-test"));
    var invalid = await http.PostAsync("/fut/rs4/ut/game/fifa14/clientdata/network-test", new StringContent("{bad", Encoding.UTF8, "application/json"));
    Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
});
Test("Live HTTP concurrent duplicate settlement pays exactly once", async () =>
{
    await Run(a, () => { PlayerContext.Current.Data.Match = null!; PlayerContext.Current.Data.LastCompetition = ""; PlayerContext.MarkDirty(); });
    long before = a.Account.Data.Profile.Coins;
    var created = await http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/match", new { }); created.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); long id = doc.RootElement.GetProperty("matchId").GetInt64();
    var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/match/end", new { matchId = id, endReason = "WIN" })));
    foreach (var response in responses) response.EnsureSuccessStatusCode();
    Equal(before + 500, a.Account.Data.Profile.Coins);
    Equal(1, (await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct().Count());
});
Test("Zero-value configurable manager rewards still persist their claim receipts", async () =>
{
    int taskCoins = options.ManagerTaskCoins, completionCoins = options.ManagerCompletionCoins;
    try
    {
        options.ManagerTaskCoins = 0; options.ManagerCompletionCoins = 0;
        await Run(a, () => { a.Account.Data.ManagerTasks[1].Claimed = false; PlayerContext.MarkDirty(); });
        long balance = a.Account.Data.Profile.Coins;
        await Run(a, () => ManagerTasks.Claim(1));
        Equal(balance, a.Account.Data.Profile.Coins);
        using var stored = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp, "players/1001.json")));
        True(stored.RootElement.GetProperty("ManagerTasks").GetProperty("1").GetProperty("Claimed").GetBoolean(), "Zero-value claim was not persisted");
    }
    finally { options.ManagerTaskCoins = taskCoins; options.ManagerCompletionCoins = completionCoins; }
});
Test("Native managerquest round-trips account UI state without minting rewards", async () =>
{
    const string path = "/fut/rs4/ut/game/fifa14/managerquest";
    long balance = a.Account.Data.Profile.Coins;
    const string payload = "{\"entries\":[{\"key\":0,\"value\":2147483647},{\"key\":1,\"value\":-1},{\"key\":2,\"value\":17}]}";
    (await http.PutAsync(path, new StringContent(payload, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
    Equal(payload, await http.GetStringAsync(path));
    Equal(balance, a.Account.Data.Profile.Coins);
    using var other = new HttpClient { BaseAddress = http.BaseAddress };
    other.DefaultRequestHeaders.Add("X-UT-SID", b.Key);
    using var isolated = JsonDocument.Parse(await other.GetStringAsync(path));
    Equal(0, isolated.RootElement.GetProperty("entries")[0].GetProperty("value").GetInt32());
    foreach (string invalid in new[] { "{\"entries\":[]}", "{\"entries\":[{\"key\":0,\"value\":1},{\"key\":0,\"value\":2},{\"key\":2,\"value\":3}]}", "{\"entries\":[{\"key\":0,\"value\":\"bad\"},{\"key\":1,\"value\":2},{\"key\":2,\"value\":3}]}" })
        Equal(HttpStatusCode.BadRequest, (await http.PutAsync(path, new StringContent(invalid, Encoding.UTF8, "application/json"))).StatusCode);
    Equal(payload, await http.GetStringAsync(path));
    using var stored = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp, "players/1001.json")));
    Equal(17, stored.RootElement.GetProperty("NativeManagerQuest")[2].GetInt32());
});
Test("Live HTTP insufficient funds and duplicate buy cannot corrupt wallet", async () =>
{
    var response = await http.GetAsync("/fut/rs4/ut/game/fifa14/transfermarket?num=10&type=player"); response.EnsureSuccessStatusCode();
    using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var first = page.RootElement.GetProperty("auctionInfo")[0];
    int beforeCount = a.Account.Data.Club.Inventory.Count(x => x.Player.CardId == first.GetProperty("itemData").GetProperty("resourceId").GetInt32());
    long id = first.GetProperty("tradeId").GetInt64(); int price = first.GetProperty("buyNowPrice").GetInt32();
    var malformed = await http.PutAsJsonAsync($"/fut/rs4/ut/game/fifa14/trade/{id}/bid", new { bid = "wrong-type" });
    Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    await Run(a, () => FutProfileStore.Mutate(p => p.Coins = price));
    using var cachedCredits = JsonDocument.Parse(await http.GetStringAsync("/fut/rs4/ut/game/fifa14/user/credits"));
    Equal((long)price, cachedCredits.RootElement.GetProperty("credits").GetInt64());
    var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.PutAsJsonAsync($"/fut/rs4/ut/game/fifa14/trade/{id}/bid", new { bid = price })));
    foreach (var reply in replies) reply.EnsureSuccessStatusCode();
    Equal(0L, a.Account.Data.Profile.Coins);
    using var freshCredits = JsonDocument.Parse(await http.GetStringAsync("/fut/rs4/ut/game/fifa14/user/credits"));
    Equal(0L, freshCredits.RootElement.GetProperty("credits").GetInt64());
    True(a.Account.HttpCacheBytes <= options.ResponseCacheBytes, "Response cache exceeded byte budget");
    True(a.Account.Data.Club.Inventory.Count(x => x.Player.CardId == first.GetProperty("itemData").GetProperty("resourceId").GetInt32()) == beforeCount + 1, "Auction bought more than once");
});
Test("Live packs validate catalogue and funds before granting owned items", async () =>
{
    await Run(a, () => FutProfileStore.Mutate(p => p.Coins = 0));
    int before = a.Account.Data.Club.Inventory.Count;
    var denied = await http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/purchased/items", new { packId = 300 });
    using var denial = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
    Equal("INSUFFICIENT_COINS", denial.RootElement.GetProperty("reason").GetString());
    Equal(before, a.Account.Data.Club.Inventory.Count);
    var unknown = await http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/purchased/items", new { packId = 999999 });
    Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    await Run(a, () => FutProfileStore.Mutate(p => p.Coins = 5000));
    var bought = await http.PostAsJsonAsync("/fut/rs4/ut/game/fifa14/purchased/items", new { packId = 300 }); bought.EnsureSuccessStatusCode();
    using var purchase = JsonDocument.Parse(await bought.Content.ReadAsStringAsync());
    True(purchase.RootElement.GetProperty("numberItems").GetInt32() > 0, "Pack granted no items");
    Equal(0L, a.Account.Data.Profile.Coins);
    Equal(a.Account.Data.Club.Managers.Count, a.Account.Data.Club.ManagerItemIds.Count);
    Equal(a.Account.Data.Club.Staff.Count, a.Account.Data.Club.StaffItemIds.Count);
});
Test("TLS redirector handshake returns the configured Blaze address", async () =>
{
    var handler = new ReadyHandler();
    var client = await ProtoFireClient.ConnectAsync("127.0.0.1", options.RedirectorPort, true, handler, FrameType.FireFrame);
    try
    {
        await handler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var packet = await client.SendRequestAsync(new ProtoFirePacket(new FireFrame { Component = 5, Command = 1, MessageNum = 1 }, new Heat2Serializer().Serialize(new Blaze3SDK.Blaze.Redirector.ServerInstanceRequest())));
        Equal((ushort)0, packet.Frame.ErrorCode);
        var info = new Blaze3SDK.Blaze.Redirector.ServerInstanceInfo();
        True(new Heat2Serializer().Deserialize(packet.GetDataStream(), info), "Invalid redirector TDF");
        Equal((ushort)options.BlazePort, info.Address.IpAddress!.Port);
        Equal(options.AdvertisedAddress, info.Address.IpAddress!.Hostname);
    }
    finally { client.Connection.Disconnect(); }
});
Test("Live Blaze authentication, persisted settings and terminal reporting", async () =>
{
    await using var client = await RpcClient.Connect(options.BlazePort);
    var login = await client.Call(1, (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin, new OriginLoginRequest { AuthToken = token1 });
    Equal((ushort)0, login.Frame.ErrorCode);
    var auth = new FullLoginResponse(); True(new Heat2Serializer().Deserialize(login.GetDataStream(), auth), "Login TDF invalid");
    Equal(1001L, auth.SessionInfo!.UserId);
    var save = await client.Call(9, (ushort)UtilComponentBase.UtilComponentCommand.userSettingsSave, new Blaze3SDK.Blaze.Util.UserSettingsSaveRequest { Key = "test", Data = "persisted" });
    Equal((ushort)0, save.Frame.ErrorCode);
    var load = await client.Call(9, (ushort)UtilComponentBase.UtilComponentCommand.userSettingsLoad, new Blaze3SDK.Blaze.Util.UserSettingsLoadRequest { Key = "test" });
    var settings = new Blaze3SDK.Blaze.Util.UserSettingsResponse(); new Heat2Serializer().Deserialize(load.GetDataStream(), settings); Equal("persisted", settings.Data);
    var report = await client.Call(28, 2, new Blaze3SDK.Blaze.GameReporting.SubmitGameReportRequest { GameReport = new Blaze3SDK.Blaze.GameReporting.GameReport { GameReportingId = 42 } });
    Equal((ushort)0, report.Frame.ErrorCode);
    var notification = await client.Notification(28, (ushort)GameReportingComponentBase.GameReportingComponentNotification.ResultNotification);
    var terminal = new Blaze3SDK.Blaze.GameReporting.ResultNotification(); new Heat2Serializer().Deserialize(notification.GetDataStream(), terminal);
    True(terminal.FinalResult, "Terminal reporting notification missing"); Equal(42UL, terminal.GameReportingId);
});
Test("Peer lobbies enforce ownership, capacity and state transitions over Blaze", async () =>
{
    await using var one = await RpcClient.Connect(options.BlazePort);
    await using var two = await RpcClient.Connect(options.BlazePort);
    await one.Call(1, (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin, new OriginLoginRequest { AuthToken = token1 });
    await two.Call(1, (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin, new OriginLoginRequest { AuthToken = token2 });
    var disabled = await one.Call(4, 1, new CreateGameRequest());
    Equal(ServerError.ERR_COMMAND_NOT_FOUND.WithErrorPrefix(), disabled.Frame.ErrorCode);
    options.EnablePeerLobbies = true;
    try
    {
        var packet = await one.Call(4, 1, new CreateGameRequest { GameName = "test", GameProtocolVersionString = "test-version", MaxPlayerCapacity = 2, NetworkTopology = GameNetworkTopology.PEER_TO_PEER_FULL_MESH });
        Equal((ushort)0, packet.Frame.ErrorCode);
        var created = new CreateGameResponse(); new Heat2Serializer().Deserialize(packet.GetDataStream(), created);
        uint id = created.GameId;
        var forbidden = await two.Call(4, 2, new DestroyGameRequest { GameId = id }); True(forbidden.Frame.ErrorCode != 0, "Nonmember destroyed lobby");
        var join = await two.Call(4, (ushort)GameManagerBase.GameManagerCommand.joinGame, new JoinGameRequest { GameId = id, GameProtocolVersionString = "test-version" }); Equal((ushort)0, join.Frame.ErrorCode);
        var duplicate = await two.Call(4, (ushort)GameManagerBase.GameManagerCommand.joinGame, new JoinGameRequest { GameId = id, GameProtocolVersionString = "test-version" }); True(duplicate.Frame.ErrorCode != 0, "Duplicate lobby member accepted");
        var badState = await one.Call(4, 3, new AdvanceGameStateRequest { GameId = id, NewGameState = GameState.IN_GAME }); True(badState.Frame.ErrorCode != 0, "Invalid state transition accepted");
        var goodState = await one.Call(4, 3, new AdvanceGameStateRequest { GameId = id, NewGameState = GameState.PRE_GAME }); Equal((ushort)0, goodState.Frame.ErrorCode);
        var destroy = await one.Call(4, 2, new DestroyGameRequest { GameId = id }); Equal((ushort)0, destroy.Frame.ErrorCode);
    }
    finally { options.EnablePeerLobbies = false; }
});
Test("PC bridge injects credentials and rewrites URLs through real sockets", async () =>
{
    var bridgeOptions = new ServerOptions { WebPort = FreePort(), BlazePort = FreePort(), RedirectorPort = FreePort() };
    bridgeOptions.PublicWebUrl = $"http://127.0.0.1:{bridgeOptions.WebPort}";
    var bridge = new ClientBridge(bridgeOptions, options.PublicWebUrl, "127.0.0.1", options.BlazePort, token2);
    using var halt = new CancellationTokenSource();
    var bridgeTasks = new[] { bridge.StartBlazeAsync(halt.Token), bridge.StartWebAsync(halt.Token) };
    try
    {
        using var web = new HttpClient { BaseAddress = new Uri(bridgeOptions.PublicWebUrl) };
        var response = await web.GetAsync("/fut/rs4/ut/game/fifa14/user");
        True(response.Headers.TryGetValues("EASW-Userid", out var ids) && ids.Single() == "1002", "Bridge did not authenticate the configured account");
        await using var rpc = await RpcClient.Connect(bridgeOptions.BlazePort);
        var login = await rpc.Call(1, (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin, new OriginLoginRequest { AuthToken = "the-game-token-is-replaced" });
        Equal((ushort)0, login.Frame.ErrorCode);
        var auth = new FullLoginResponse(); new Heat2Serializer().Deserialize(login.GetDataStream(), auth); Equal(1002L, auth.SessionInfo!.UserId);
        var packet = await rpc.Call(9, 1, new Blaze3SDK.Blaze.Util.FetchClientConfigRequest { ConfigSection = "OSDK" });
        var config = new Blaze3SDK.Blaze.Util.FetchConfigResponse(); new Heat2Serializer().Deserialize(packet.GetDataStream(), config);
        True(config.Config.Values.Any(v => v.StartsWith(bridgeOptions.PublicWebUrl, StringComparison.Ordinal)), "Bridge URLs were not rewritten");
        True(config.Config.Values.All(v => !v.Contains(token2)), "Bridge exposed the private token in config");
    }
    finally { halt.Cancel(); await Task.WhenAll(bridgeTasks); }
});
Test("SDK rejects excessive TDF lengths and nesting before allocation", () =>
{
    var serializer = new Heat2Serializer();
    // Unknown BLAB binary member, length 2^31. Body contains only the header and length.
    var bomb = new byte[] { 0x8a, 0xc8, 0x62, 2, 0x80, 0x80, 0x80, 0x80, 0x10 };
    True(!serializer.Deserialize(new MemoryStream(bomb), new EmptyMessage()), "Oversized TDF blob accepted");
    var nested = new List<byte>();
    nested.AddRange(new byte[] { 0x8a, 0xc8, 0x62, 4 });
    for (int i = 0; i < 80; i++) nested.AddRange(new byte[] { 4, 1 });
    nested.AddRange(new byte[] { 0, 0 });
    True(!serializer.Deserialize(new MemoryStream(nested.ToArray()), new EmptyMessage()), "Deep TDF accepted");
    return Task.CompletedTask;
});
Test("Owned staff IDs survive filtering, claims and serialization", () => Run(a, () =>
{
    var data = ClubStore.Get();
    True(data.Managers.Count > 0 && data.ManagerItemIds.Count == data.Managers.Count, "Starter manager identity missing");
    Equal(data.ManagerItemIds[0], data.Squads[0].ManagerId);
    long next = ++PlayerContext.Current.Data.NextPackExtraId;
    data.Managers.Add(Managers.All[^1]); data.ManagerItemIds.Add(next);
    PlayerContext.MarkDirty();
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(PlayerContext.Current.Data, PlayerRepository.JsonOptions);
    var copy = JsonSerializer.Deserialize<PlayerState>(json, PlayerRepository.JsonOptions)!;
    Equal(next, copy.Club.ManagerItemIds[^1]);
}));
Test("Bid timing remains attached to the original auction after its next cycle", () => Run(a, () =>
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var page = JsonDocument.Parse(Market.PageJson(0, 1, now));
    long id = page.RootElement[0].GetProperty("tradeId").GetInt64();
    var timing = Market.Timing(id, now);
    var data = PlayerContext.Current.Data.Market;
    data.MyBids[id] = 150; data.BidTimings[id] = timing;
    True(Market.CollectMyBidResults(timing.EndsAt + 86400).Any(r => r.TradeId == id), "Bid was stranded in a later auction cycle");
    Market.RemoveMyBid(id);
}));
Test("Metrics expose latency buckets", async () =>
{
    string metrics = await http.GetStringAsync("/metrics");
    True(metrics.Contains("le=\"16.667\""), "60 Hz latency bucket missing"); True(metrics.Contains("le=\"33.333\""), "30 Hz latency bucket missing");
});

FunctionalFlowTests.Register(Test, http, a, action => Run(a, action));
ConsumablePolishChecks.Register(Test, http, a, action => Run(a, action));
HttpPolishChecks.Register(Test, http, a, action => Run(a, action));
MatchParticipationChecks.Register(Test, http, a, action => Run(a, action));
CompetitionInvariantTests.Register(Test, repo, a);
ProgressionPolishChecks.Register(Test, repo, a);
MarketPolishChecks.Register(Test, a, b, Run);
TransportTests.Register(Test);
Test("Ranked pairing, corroboration, self-forfeit and ownership", () => { RankedChecks.PairingAndCorroboration(temp); return Task.CompletedTask; });
Test("Ranked zero-sum Elo bounds and ten-game online seasons", () => { RankedChecks.EloBoundsAndSeasons(temp); return Task.CompletedTask; });
Test("Ranked concurrent duplicate reports and settlement waits", () => RankedChecks.ConcurrentRetries(temp));
Test("Ranked journal restart and torn-tail recovery", () => { RankedChecks.JournalRestartAndTailRecovery(temp); return Task.CompletedTask; });
Test("Ranked checkpoint replay and bounded receipt retention", () => { RankedChecks.CheckpointReplayAndRetention(temp); return Task.CompletedTask; });
Test("Ranked corruption fails closed and excludes a second writer", () => { RankedChecks.CorruptionAndWriterExclusion(temp); return Task.CompletedTask; });
Test("Indexed matchmaking preserves oldest compatible fairness and bounds", MatchmakingChecks.QueueInvariantsAsync);
Test("Live matchmaking lobby protocol and response ordering", () => MatchmakingChecks.LobbyProtocolAsync(options.BlazePort, token1, token2, options));
Test("Live ranked matchmaking lifecycle and explicit self departure", () => MatchmakingChecks.RankedLifecycleAsync(options.BlazePort, token1, token2, options, ranked));

Test("Durable state reopens with the same wallet, inventory and replay receipt", async () =>
{
    await Run(a, () => { FutProfileStore.Mutate(p => p.Club.Established = true); CompetitionInvariantTests.PrepareRestartState(); });
    long coins = a.Account.Data.Profile.Coins;
    int inventory = a.Account.Data.Club.Inventory.Count;
    string receipt = a.Account.Data.Match!.Response;
    stop.Cancel(); await Task.WhenAll(server, blazeTask, redirectorTask);
    repo.Dispose();
    using var reopened = new PlayerRepository();
    var session = reopened.Authenticate(token1);
    await reopened.RunAsync(session, () =>
    {
        CompetitionInvariantTests.AssertRestartState();
        Equal(coins, PlayerContext.Current.Data.Profile.Coins);
        Equal(inventory, ClubStore.Get().Inventory.Count);
        Equal(receipt, PlayerContext.Current.Data.Match!.Response);
        Equal(receipt, MatchService.End("{\"endReason\":\"WIN\"}", _ => throw new Exception("Replayed match consequences")));
        return Task.FromResult(0);
    });
});

RankedHttpChecks.Register(Test);
PersistenceTests.Register(Test);

int failures = 0;
try
{
    foreach (var (name, test) in tests)
    {
        var watch = Stopwatch.StartNew();
        try { await test(); Console.WriteLine($"PASS {name} ({watch.ElapsedMilliseconds} ms)"); }
        catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }
}
finally { stop.Cancel(); await Task.WhenAll(server, blazeTask, redirectorTask); }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed");
return failures == 0 ? 0 : 1;

static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

internal sealed class RpcClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream Stream => _client.GetStream();
    private uint _sequence;
    private readonly List<ProtoFirePacket> _notifications = new();
    internal static async Task<RpcClient> Connect(int port) { var client = new RpcClient(); await client._client.ConnectAsync(IPAddress.Loopback, port); return client; }
    internal async Task<ProtoFirePacket> Call(ushort component, ushort command, Tdf body)
    {
        uint number = ++_sequence;
        var packet = new ProtoFirePacket(new FireFrame { Component = component, Command = command, MessageNum = number }, new Heat2Serializer().Serialize(body));
        await packet.WriteToAsync(Stream);
        while (true)
        {
            var received = await ProtoFirePacket.ReadFromAsync<FireFrame>(Stream).WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Blaze disconnected");
            if (received.Frame.MessageType == MessageType.Notification) { _notifications.Add(received); continue; }
            if (received.Frame.MessageNum == number) return received;
            throw new IOException("Mismatched Blaze sequence");
        }
    }
    internal async Task<ProtoFirePacket> Notification(ushort component, ushort command)
    {
        while (true)
        {
            int i = _notifications.FindIndex(p => p.Frame.Component == component && p.Frame.Command == command);
            if (i >= 0) { var packet = _notifications[i]; _notifications.RemoveAt(i); return packet; }
            var next = await ProtoFirePacket.ReadFromAsync<FireFrame>(Stream).WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Blaze disconnected");
            _notifications.Add(next);
        }
    }
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class ReadyHandler : IProtoFireHandler
{
    internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> OnConnectedAsync(ProtoFireConnection c) => Task.FromResult(true);
    public ProtoSSLCertificate? SelectCertificate(ProtoFireConnection c) => null;
    public Task OnAuthenticatedAsync(ProtoFireConnection c, bool secure) { Ready.TrySetResult(); return Task.CompletedTask; }
    public Task OnDisconnectedAsync(ProtoFireConnection c) => Task.CompletedTask;
    public Task OnPacketSentAsync(ProtoFireConnection c, ProtoFirePacket p) => Task.CompletedTask;
    public Task OnPacketReceivedAsync(ProtoFireConnection c, ProtoFirePacket p) => Task.CompletedTask;
    public Task OnErrorAsync(ProtoFireConnection c, Exception e) { Ready.TrySetException(e); return Task.CompletedTask; }
}
