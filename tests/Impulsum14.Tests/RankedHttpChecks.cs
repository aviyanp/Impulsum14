using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Impulsum14;
using Microsoft.Extensions.Logging.Abstractions;

// Register only after the shared HTTP/Blaze servers and their repository have
// stopped. Each check owns its accounts, Kestrel listener and ranked ledger.
// These are native-shaped protocol checks, not retail-executable certification.
internal static class RankedHttpChecks
{
    private const string Root = "/fut/rs4/ut/game/fifa14";
    private const long InitialCoins = 1000;

    internal static void Register(Action<string, Func<Task>> add)
    {
        add("Concurrent native ranked ends settle both wallets and projections once", () => WithFixture(async fixture =>
        {
            var rankedMatch = fixture.Bind();
            long one = await fixture.Begin(0), two = await fixture.Begin(1);
            await fixture.Ready(0, one); await fixture.Ready(1, two);

            string[] replies = await Task.WhenAll(fixture.End(0, one, 2, 1), fixture.End(1, two, 1, 2));
            AssertReceipt(replies[0], one, InitialCoins + 540, 540, pending: false);
            AssertReceipt(replies[1], two, InitialCoins + 220, 220, pending: false);
            using (var winner = JsonDocument.Parse(replies[0]))
            using (var loser = JsonDocument.Parse(replies[1]))
            {
                Equal(1024, winner.RootElement.GetProperty("elo").GetInt32());
                Equal(24, winner.RootElement.GetProperty("eloChange").GetInt32());
                Equal(976, loser.RootElement.GetProperty("elo").GetInt32());
                Equal(-24, loser.RootElement.GetProperty("eloChange").GetInt32());
            }
            var receipt = fixture.Ranked.GetMatch(fixture.AccountId(0), rankedMatch.Id);
            Check(receipt.Settled && receipt.GoalsA == 2 && receipt.GoalsB == 1, "Inverse native reports were not paired.");
            Equal(0, receipt.RatingDeltaA + receipt.RatingDeltaB);
            Equal(1, fixture.Ranked.GetRating(fixture.AccountId(0)).GamesPlayed);
            Equal(1, fixture.Ranked.GetRating(fixture.AccountId(1)).GamesPlayed);
            Equal(InitialCoins + 540, await fixture.Balance(0));
            Equal(InitialCoins + 220, await fixture.Balance(1));
            Equal(InitialCoins, await fixture.Balance(2));

            for (int index = 0; index < 2; index++)
            {
                int player = index;
                await fixture.Run(player, () =>
                {
                    var profile = PlayerContext.Current.Data.Profile;
                    Equal(player == 0 ? 2 : 1, profile.Wins);
                    Equal(1, profile.Draws);
                    Equal(player == 1 ? 1 : 0, profile.Losses);
                    Equal(1, profile.OnlineSeason.GamesPlayed);
                    Equal(10, profile.OnlineDivision);
                    Equal(7, profile.OfflineDivision);
                    Equal(2, profile.Season.GamesPlayed);
                    Equal(4, profile.Season.Points);
                    Equal(9, PlayerContext.Current.Data.Club.PlayerMods[fixture.ItemId(player)].Contract);
                    Check(PlayerContext.Current.Data.Match!.RankedFinalized, "Final HTTP receipt was not persisted.");
                });
            }
            await fixture.Run(2, () =>
            {
                Equal(0, PlayerContext.Current.Data.Profile.OnlineSeason.GamesPlayed);
                Equal(0L, PlayerContext.Current.Data.Profile.RankedCoinsApplied);
            });
        }, waitMilliseconds: 2500));

        add("Final native ranked receipts remain identical after a real wallet spend", () => WithFixture(async fixture =>
        {
            fixture.Bind();
            long one = await fixture.Begin(0), two = await fixture.Begin(1);
            await fixture.Ready(0, one); await fixture.Ready(1, two);
            AssertReceipt(await fixture.End(0, one, 2, 1), one, InitialCoins, 0, pending: true);
            string secondReceipt = await fixture.End(1, two, 1, 2);
            string firstReceipt = await fixture.End(0, one, 2, 1);
            AssertReceipt(firstReceipt, one, InitialCoins + 540, 540, pending: false);

            await fixture.SpendOnBronzePack(0);
            Equal(InitialCoins + 540 - 400, await fixture.Balance(0));
            Equal(firstReceipt, await fixture.End(0, one, 2, 1));
            Equal(secondReceipt, await fixture.End(1, two, 1, 2));
            Equal(InitialCoins + 540 - 400, await fixture.Balance(0));
            await fixture.Run(0, () => Equal(9, PlayerContext.Current.Data.Club.PlayerMods[fixture.ItemId(0)].Contract));
            Equal(1, fixture.Ranked.GetRating(fixture.AccountId(0)).GamesPlayed);
        }));

        add("A pending native ranked report blocks rebinding another local match", () => WithFixture(async fixture =>
        {
            var rankedMatch = fixture.Bind();
            long one = await fixture.Begin(0);
            await fixture.Ready(0, one);
            string pending = await fixture.End(0, one, 2, 1);
            AssertReceipt(pending, one, InitialCoins, 0, pending: true);
            using var duplicateBegin = await fixture.Clients[0].PostAsJsonAsync(Root + "/match", new { type = "online", squadId = 0 });
            Equal(HttpStatusCode.Conflict, duplicateBegin.StatusCode);
            Equal(pending, await fixture.End(0, one, 2, 1));
            Equal(rankedMatch.Id, fixture.Ranked.GetActive(fixture.AccountId(0))!.Id);
            Equal(0, fixture.Ranked.GetRating(fixture.AccountId(0)).GamesPlayed);
            await fixture.Run(0, () =>
            {
                Equal(one, PlayerContext.Current.Data.Match!.Id);
                Equal(9, PlayerContext.Current.Data.Club.PlayerMods[fixture.ItemId(0)].Contract);
            });
        }));

        add("Unready native reset concedes after the opponent has reported", () => ClientCancellationConcedes(reset: true));
        add("Unready native NO_CONTEST concedes after the opponent has reported", () => ClientCancellationConcedes(reset: false));

        add("Expired ranked matches finalize pending native receipts without awards", () => WithFixture(async fixture =>
        {
            var rankedMatch = fixture.Bind();
            long one = await fixture.Begin(0);
            await fixture.Ready(0, one);
            AssertReceipt(await fixture.End(0, one, 2, 1), one, InitialCoins, 0, pending: true);
            Equal(1, fixture.Ranked.ExpireMatches(DateTimeOffset.UtcNow.AddMinutes(1)));
            Check(fixture.Ranked.GetMatch(fixture.AccountId(0), rankedMatch.Id).Cancelled, "Timeout did not cancel the unresolved match.");

            string final = await fixture.End(0, one, 2, 1);
            AssertReceipt(final, one, InitialCoins, 0, pending: false);
            Equal(final, await fixture.End(0, one, 2, 1));
            Equal(InitialCoins, await fixture.Balance(0));
            Equal(InitialCoins, await fixture.Balance(1));
            Equal(0, fixture.Ranked.GetRating(fixture.AccountId(0)).GamesPlayed);
            await fixture.Run(0, () =>
            {
                Check(PlayerContext.Current.Data.Match!.RankedFinalized, "Cancelled native receipt remained pending on disk.");
                Equal(9, PlayerContext.Current.Data.Club.PlayerMods[fixture.ItemId(0)].Contract);
            });
        }));

        add("Retired ranked receipts finalize gracefully without replaying awards", () => WithFixture(async fixture =>
        {
            var rankedMatch = fixture.Bind();
            long one = await fixture.Begin(0), two = await fixture.Begin(1);
            await fixture.Ready(0, one); await fixture.Ready(1, two);
            AssertReceipt(await fixture.End(0, one, 2, 1), one, InitialCoins, 0, pending: true);
            AssertReceipt(await fixture.End(1, two, 1, 2), two, InitialCoins + 220, 220, pending: false);
            bool retired = false;
            try { fixture.Ranked.GetMatch(fixture.AccountId(0), rankedMatch.Id); }
            catch (KeyNotFoundException) { retired = true; }
            Check(retired, "RetainedMatches=0 did not exercise receipt retirement.");

            string final = await fixture.End(0, one, 2, 1);
            AssertReceipt(final, one, InitialCoins + 540, 0, pending: false);
            Equal(InitialCoins + 540, await fixture.Balance(0));
            Equal(540L, fixture.Ranked.GetRating(fixture.AccountId(0)).CoinsEarned);
            Equal(1, fixture.Ranked.GetRating(fixture.AccountId(0)).GamesPlayed);
            await fixture.SpendOnBronzePack(0);
            Equal(final, await fixture.End(0, one, 2, 1));
            Equal(InitialCoins + 540 - 400, await fixture.Balance(0));
            await fixture.Run(0, () =>
            {
                Check(PlayerContext.Current.Data.Match!.RankedFinalized, "Retired native receipt was not finalized.");
                Equal(540L, PlayerContext.Current.Data.Profile.RankedCoinsApplied);
                Equal(9, PlayerContext.Current.Data.Club.PlayerMods[fixture.ItemId(0)].Contract);
            });
        }, retainedMatches: 0));
    }

    private static Task ClientCancellationConcedes(bool reset) => WithFixture(async fixture =>
    {
        var rankedMatch = fixture.Bind();
        long one = await fixture.Begin(0), two = await fixture.Begin(1);
        await fixture.Ready(0, one); // The opponent intentionally has not called Ready.
        AssertReceipt(await fixture.End(0, one, 2, 1), one, InitialCoins, 0, pending: true);
        if (reset) await fixture.Post(1, "/match/reset", new { });
        else await fixture.Post(1, "/match/end", new { matchId = two, endReason = "NO_CONTEST" });

        var receipt = fixture.Ranked.GetMatch(fixture.AccountId(0), rankedMatch.Id);
        Check(receipt.Settled && !receipt.Cancelled, "Client departure voided its opponent's submitted result.");
        Equal(fixture.AccountId(1), receipt.ForfeitedBy!.Value);
        Equal(0L, receipt.CoinsB);
        Equal(1, fixture.Ranked.GetRating(fixture.AccountId(0)).Wins);
        Equal(1, fixture.Ranked.GetRating(fixture.AccountId(1)).Losses);
        AssertReceipt(await fixture.End(0, one, 2, 1), one, InitialCoins + 500, 500, pending: false);
        Equal(InitialCoins + 500, await fixture.Balance(0));
        Equal(InitialCoins, await fixture.Balance(1));
        if (reset)
        {
            await fixture.Post(1, "/match/reset", new { });
            await fixture.Run(1, () => Check(PlayerContext.Current.Data.Match is null, "Reset left a local match behind."));
        }
        Equal(1, fixture.Ranked.GetRating(fixture.AccountId(1)).GamesPlayed);
    });

    private static async Task WithFixture(Func<Fixture, Task> action, int waitMilliseconds = 0, int retainedMatches = 2000)
    {
        Check(PlayerRepository.Instance is null, "Ranked HTTP fixtures must run after the shared repository closes.");
        var previousOptions = ServerOptions.Current;
        var previousRanked = RankedService.Current;
        string directory = Path.Combine(Path.GetTempPath(), "impulsum-ranked-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = new Fixture(directory);
        try
        {
            await fixture.StartAsync(waitMilliseconds, retainedMatches);
            await action(fixture);
        }
        finally
        {
            try { await fixture.DisposeAsync(); }
            finally
            {
                RankedService.Current = previousRanked;
                ServerOptions.Current = previousOptions;
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class Fixture(string directory) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _server;
        private PlayerSession[] _sessions = Array.Empty<PlayerSession>();
        private static readonly string[] Tokens = { "ranked-http-private-alpha", "ranked-http-private-beta", "ranked-http-private-gamma" };
        internal PlayerRepository Repository { get; private set; } = null!;
        internal RankedService Ranked { get; private set; } = null!;
        internal HttpClient[] Clients { get; private set; } = Array.Empty<HttpClient>();
        internal long AccountId(int index) => _sessions[index].Account.Id;
        internal long ItemId(int index) => 850_920_000 + index;
        internal RankedMatchView Bind() => Ranked.BindMatch(AccountId(0), AccountId(1), 1);

        internal async Task StartAsync(int waitMilliseconds, int retainedMatches)
        {
            string accounts = Path.Combine(directory, "accounts.json");
            File.WriteAllText(accounts, JsonSerializer.Serialize(Enumerable.Range(0, 3).Select(index => new
            {
                Id = 31001 + index, Name = "RankedHttp" + index,
                TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Tokens[index])))
            })));
            var ports = new HashSet<int>();
            while (ports.Count < 3) ports.Add(FreePort());
            int[] selected = ports.ToArray();
            var options = new ServerOptions
            {
                DataDirectory = directory, AccountsFile = accounts, LocalMode = false,
                WebPort = selected[0], BlazePort = selected[1], RedirectorPort = selected[2],
                PublicWebUrl = $"http://127.0.0.1:{selected[0]}", RequestsPerSecond = 10000,
                MaintenanceIntervalMs = 60000, MarketPlayers = 1000, MarketConsumables = 100,
                MarketClubItems = 100, MarketStaff = 100,
                Ranked = new RankedOptions { SettlementWaitMilliseconds = waitMilliseconds, RetainedMatches = retainedMatches }
            };
            options.Validate(); ServerOptions.Current = options;
            Repository = new PlayerRepository();
            Ranked = new RankedService(directory, options.Ranked);
            RankedService.Current = Ranked;
            _sessions = Tokens.Select(Repository.Authenticate).ToArray();
            for (int index = 0; index < _sessions.Length; index++)
            {
                int player = index;
                await Run(player, () =>
                {
                    var data = PlayerContext.Current.Data;
                    data.Profile.Club.Established = true;
                    data.Profile.Coins = InitialCoins;
                    data.Profile.Wins = 1; data.Profile.Draws = 1;
                    data.Profile.OfflineDivision = 7;
                    data.Profile.Season = new FutSeason { GamesPlayed = 2, GamesWon = 1, GamesDraw = 1, Points = 4 };
                    data.Club.Inventory.Clear(); data.Club.Squads.Clear(); data.Club.PlayerMods.Clear();
                    data.Club.Inventory.Add(new ClubItem(ItemId(player), RealPlayers.All.First(), 7));
                    data.Club.Squads.Add(new Squad { Id = 0, Name = "Ranked HTTP XI", Slots = new() { [0] = ItemId(player) } });
                    data.Club.ActiveSquadId = 0;
                    data.Club.PlayerMods[ItemId(player)] = new PlayerMod { Contract = 10, Fitness = 99 };
                    PlayerContext.MarkDirty();
                });
            }
            Clients = Enumerable.Range(0, 3).Select(index =>
            {
                var client = new HttpClient { BaseAddress = new Uri(options.PublicWebUrl), Timeout = TimeSpan.FromSeconds(10) };
                if (index == 2) client.DefaultRequestHeaders.Add("X-UT-SID", _sessions[index].Key);
                else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[index]);
                return client;
            }).ToArray();
            _server = new WebServer(options.WebPort, NullLogger.Instance).StartAsync(_stop.Token);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (_server.IsCompleted) { await _server; throw new IOException("Ranked HTTP fixture stopped before readiness."); }
                try
                {
                    using var ready = await Clients[0].GetAsync("/readyz");
                    if (ready.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Ranked HTTP fixture did not become ready.");
        }

        internal Task Run(int index, Action action) => Repository.RunAsync(_sessions[index], () => { action(); return Task.FromResult(0); });
        internal async Task<long> Begin(int index)
        {
            using var result = JsonDocument.Parse(await Post(index, "/match", new { type = "online", squadId = 0 }));
            return result.RootElement.GetProperty("matchId").GetInt64();
        }
        internal Task<string> Ready(int index, long matchId) => Post(index, "/match/ready", new { matchId });
        internal Task<string> End(int index, long matchId, int goalsFor, int goalsAgainst) => Post(index, "/match/end", new
        {
            matchId, endReason = goalsFor > goalsAgainst ? "WIN" : goalsFor < goalsAgainst ? "LOSS" : "DRAW",
            myMatchStats = new { goals = goalsFor }, opponentMatchStats = new { goals = goalsAgainst },
            secondsPlayed = 5400, items = new[] { new { id = ItemId(index), fitness = 90 } }
        });
        internal async Task<string> Post<T>(int index, string path, T body)
        {
            using var response = await Clients[index].PostAsJsonAsync(Root + path, body);
            string text = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode, $"POST {path} returned {(int)response.StatusCode}: {text}");
            return text;
        }
        internal async Task<long> Balance(int index)
        {
            using var response = await Clients[index].GetAsync(Root + "/user/credits");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("credits").GetInt64();
        }
        internal async Task SpendOnBronzePack(int index)
        {
            long before = await Balance(index);
            using var purchase = JsonDocument.Parse(await Post(index, "/purchased/items", new { packId = 100 }));
            Check(purchase.RootElement.GetProperty("numberItems").GetInt32() > 0, "Wallet spend did not buy a real pack.");
            Equal(before - 400, await Balance(index));
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                if (_server is not null)
                {
                    try { await _server; }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                }
            }
            finally
            {
                foreach (var client in Clients) client.Dispose();
                Repository?.Dispose(); Ranked?.Dispose(); _stop.Dispose();
            }
        }
    }

    private static void AssertReceipt(string text, long matchId, long balance, long earned, bool pending)
    {
        using var document = JsonDocument.Parse(text);
        var receipt = document.RootElement;
        Equal(matchId, receipt.GetProperty("matchId").GetInt64());
        Equal(balance, receipt.GetProperty("credits").GetInt64());
        Equal(balance, receipt.GetProperty("coins").GetInt64());
        Equal(balance, receipt.GetProperty("allCoins").GetInt64());
        Equal(balance, receipt.GetProperty("currencies")[0].GetProperty("funds").GetInt64());
        Equal(balance, receipt.GetProperty("userData").GetProperty("credits").GetInt64());
        Equal(earned, receipt.GetProperty("coinsEarned").GetInt64());
        Equal(earned, receipt.GetProperty("totalAward").GetInt64());
        Equal(earned, receipt.GetProperty("matchCoins").GetInt64());
        Equal(0L, receipt.GetProperty("seasonCoins").GetInt64());
        Equal(pending, receipt.GetProperty("settlementPending").GetBoolean());
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
