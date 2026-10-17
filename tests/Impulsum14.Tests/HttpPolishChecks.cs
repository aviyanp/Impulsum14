using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Impulsum14;

internal static class HttpPolishChecks
{
    private const string Root = "/fut/rs4/ut/game/fifa14";
    private const string Credits = Root + "/user/credits";
    private const long OfferedId = 850_940_001;

    internal static void Register(Action<string, Func<Task>> test, HttpClient http,
        PlayerSession account, Func<Action, Task> run)
    {
        test("POW responses respect gzip exclusions and alternate encoding preferences", () => Fixture(account, run, async () =>
        {
            await run(() => FutProfileStore.Mutate(profile => profile.Coins = 123));
            using var raw = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
                { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
            raw.DefaultRequestHeaders.Add("X-UT-SID", account.Key);
            // POW's bank handler matches this path, which also matches the generic
            // credits cache suffix. Encoding preferences must never cross requests.
            const string path = "/pow/bank/user/account/user/credits";
            string expected = "";
            foreach (string accept in new[] { "gzip;q=0, *;q=1", "gzip", "identity" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
                using var response = await raw.SendAsync(request);
                response.EnsureSuccessStatusCode();
                Check(response.Headers.Vary.Any(value => value.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)),
                    "A negotiated POW response omitted Vary: Accept-Encoding.");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                if (accept == "gzip")
                {
                    Check(response.Content.Headers.ContentEncoding.Contains("gzip"), "A gzip-capable POW request was not compressed.");
                    using var compressed = new MemoryStream(bytes);
                    using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip, Encoding.UTF8);
                    Check(await reader.ReadToEndAsync() == expected, "The gzip variant changed its JSON payload.");
                }
                else
                {
                    Check(response.Content.Headers.ContentEncoding.Count == 0, "An excluded or identity encoding received a compressed cached body.");
                    string content = Encoding.UTF8.GetString(bytes);
                    if (expected.Length == 0) expected = content;
                    Check(content == expected, "Identity encoding changed its JSON payload.");
                }
            }
            using var json = JsonDocument.Parse(expected);
            Check(json.RootElement.GetProperty("balance").GetInt64() == 123, "Compression check did not exercise the account's bank response.");
        }));

        test("Idle active auctions preserve committed state and reusable read responses", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                var club = account.Account.Data.Club;
                club.Inventory.Add(new ClubItem(OfferedId, RealPlayers.All.First(), 3));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                club.Listings[OfferedId] = new Auction
                {
                    ItemId = OfferedId, TradeId = 1_000_940_001, State = "active",
                    StartingBid = 1_000_000, BuyNowPrice = 1_000_000,
                    ListedAtUnix = now, ExpiresAtUnix = now + 7200, BotBuyAtUnix = now + 3600
                };
                FutProfileStore.Mutate(profile => profile.Coins = 500);
            });
            using (var first = await http.GetAsync(Credits)) first.EnsureSuccessStatusCode();
            byte[] committed = null!;
            CachedHttpReply cached = null!;
            long revision = 0;
            await run(() =>
            {
                committed = account.Account.Committed; revision = account.Account.Revision;
                Check(account.Account.HttpCache.TryGetValue(Credits, out cached!), "The credits response was not cached.");
            });
            // Cross a Unix-second boundary so unchanged maintenance is exercised
            // again, and the stable cache is tested beyond its former one-second cap.
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Task.Delay((int)(1100 - nowMs % 1000));
            for (int i = 0; i < 3; i++)
            {
                using var read = JsonDocument.Parse(await http.GetStringAsync(Credits));
                Check(read.RootElement.GetProperty("credits").GetInt64() == 500, "Idle auction maintenance changed the wallet.");
            }
            await run(() =>
            {
                Check(account.Account.Revision == revision && ReferenceEquals(account.Account.Committed, committed),
                    "Idle auction reads serialized and replaced an unchanged committed snapshot.");
                Check(account.Account.HttpCache.TryGetValue(Credits, out var current) && ReferenceEquals(current, cached),
                    "An unchanged active auction invalidated a reusable read response.");
                Check(account.Account.Data.Club.Listings[OfferedId].State == "active", "The future auction was prematurely settled.");
            });
        }));

        test("Native wallet mutations immediately invalidate a warm credits response", () => Fixture(account, run, async () =>
        {
            var player = RealPlayers.All.First();
            await run(() =>
            {
                account.Account.Data.Club.Inventory.Add(new ClubItem(OfferedId, player, 6));
                FutProfileStore.Mutate(profile => profile.Coins = 500);
            });
            using (var warm = JsonDocument.Parse(await http.GetStringAsync(Credits)))
                Check(warm.RootElement.GetProperty("credits").GetInt64() == 500, "Initial credits did not match the wallet.");
            using var sold = await http.PostAsJsonAsync("/fut/rs4/ut/delete/game/fifa14/item", new { itemId = OfferedId });
            sold.EnsureSuccessStatusCode();
            using var fresh = JsonDocument.Parse(await http.GetStringAsync(Credits));
            Check(fresh.RootElement.GetProperty("credits").GetInt64() == 500L + player.Rating * 4,
                "A wallet mutation left a stale cached credits response.");
        }));

        test("Quick selling a captain clears squad roles and duplicate-owner recovery references", () => Fixture(account, run, async () =>
        {
            const long retainedId = OfferedId + 1;
            var player = RealPlayers.All.First();
            await run(() =>
            {
                var data = account.Account.Data;
                data.Club.Inventory.Add(new ClubItem(OfferedId, player, 7));
                data.Club.Inventory.Add(new ClubItem(retainedId, player, 7));
                data.Club.Squads.Add(new Squad
                {
                    Id = 0, Name = "Captain fixture", CaptainId = OfferedId,
                    Slots = new() { [0] = OfferedId, [1] = retainedId },
                    KickTakers = new() { [0] = OfferedId, [1] = retainedId, [2] = OfferedId, [3] = retainedId, [4] = OfferedId }
                });
                data.PendingPackItems.Add((OfferedId, JsonSerializer.Serialize(new { id = OfferedId })));
                data.PendingPackItems.Add((retainedId, JsonSerializer.Serialize(new { id = retainedId })));
                data.PendingDuplicates.Add((retainedId, OfferedId));
                PlayerContext.MarkDirty();
            });
            using var sold = await http.PostAsJsonAsync("/fut/rs4/ut/delete/game/fifa14/item", new { itemId = OfferedId });
            sold.EnsureSuccessStatusCode();
            await run(() =>
            {
                var data = account.Account.Data;
                var squad = data.Club.Squads.Single();
                Check(squad.CaptainId != OfferedId && squad.KickTakers.Values.All(id => id != OfferedId) &&
                    squad.Slots.Values.All(id => id != OfferedId), "A quick-sold captain retained squad or indexed role references.");
                Check(squad.Slots.Values.Contains(retainedId) && squad.KickTakers.Values.Contains(retainedId),
                    "Selling one role holder removed the retained player's squad roles.");
                Check(data.PendingPackItems.All(item => item.Id != OfferedId) &&
                    data.PendingDuplicates.All(item => item.NewId != OfferedId && item.OwnedId != OfferedId),
                    "Quick sell retained a recovery receipt referencing the removed owner.");
            });
            using var active = JsonDocument.Parse(await http.GetStringAsync(Root + "/squad/active"));
            Check(active.RootElement.GetProperty("captain").GetInt64() != OfferedId &&
                active.RootElement.GetProperty("kicktakers").EnumerateArray().All(role => role.GetProperty("id").GetInt64() != OfferedId),
                "Native active-squad response advertised a quick-sold captain or kick taker.");
            using var list = JsonDocument.Parse(await http.GetStringAsync(Root + "/squad/list"));
            Check(list.RootElement.GetProperty("squadList").GetArrayLength() == 1 &&
                list.RootElement.GetProperty("squadList")[0].GetProperty("squadName").GetString() == "Captain fixture",
                "Selling a captain removed or renamed its squad in the native squad list.");
            using var pending = JsonDocument.Parse(await http.GetStringAsync(Root + "/purchased/items"));
            Check(pending.RootElement.GetProperty("duplicateItemIdList").GetArrayLength() == 0 &&
                pending.RootElement.GetProperty("itemData").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).SequenceEqual(new[] { retainedId }),
                "Native recovery retained the sold owner or lost the retained unassigned card.");
        }));

        test("A delayed swap with a missing offered card cannot mint its target", () => InvalidSwap(missingCard: true));
        test("A delayed swap with spent coins cannot partially pay for its target", () => InvalidSwap(missingCard: false));

        test("A completed delayed swap clears the offered card's squad, modifiers and recovery", () => Fixture(account, run, async () =>
        {
            long tradeId = 0;
            await run(() =>
            {
                tradeId = FindLiveTrade();
                var data = account.Account.Data;
                data.Profile.Coins = 500;
                data.Club.Inventory.Add(new ClubItem(OfferedId, RealPlayers.All.First(), 7));
                data.Club.PlayerMods[OfferedId] = new PlayerMod { Contract = 20, Fitness = 81 };
                data.Club.Squads.Add(new Squad { Id = 0, Name = "Swap fixture", Slots = new() { [0] = OfferedId } });
                data.PendingPackItems.Add((OfferedId, JsonSerializer.Serialize(new { id = OfferedId })));
                data.PendingDuplicates.Add((OfferedId, OfferedId));
                Market.AcceptedOffers[tradeId] = new(100, OfferedId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
                account.Account.LastMarketMaintenanceSecond = -1;
                PlayerContext.MarkDirty();
            });
            using var credits = JsonDocument.Parse(await http.GetStringAsync(Credits));
            Check(credits.RootElement.GetProperty("credits").GetInt64() == 400, "A completed swap did not charge its exact agreed price.");
            await run(() =>
            {
                var data = account.Account.Data;
                Check(data.Club.Inventory.Count == 1 && data.Club.Inventory[0].ItemId != OfferedId,
                    "A completed swap retained the offered card or failed to grant exactly one target.");
                Check(data.Club.Squads.All(squad => squad.Slots.Values.All(id => id != OfferedId)) && !data.Club.PlayerMods.ContainsKey(OfferedId),
                    "A swapped-away card retained squad membership or modifiers.");
                Check(data.PendingPackItems.All(item => item.Id != OfferedId) && data.PendingDuplicates.All(item => item.NewId != OfferedId),
                    "A swapped-away card remained in unassigned recovery.");
            });
            using var pending = JsonDocument.Parse(await http.GetStringAsync(Root + "/purchased/items"));
            Check(pending.RootElement.GetProperty("itemData").EnumerateArray().All(item => item.GetProperty("id").GetInt64() != OfferedId),
                "Native recovery advertised a card that had left the club.");
        }));

        test("A same-card swap reports duplicates only against a copy retained in the club", async () =>
        {
            foreach (bool retainAnotherCopy in new[] { false, true })
                await Fixture(account, run, async () =>
                {
                    const long retainedId = OfferedId + 1;
                    await run(() =>
                    {
                        long tradeId = FindLiveTrade();
                        var target = Market.ListingCard(tradeId)!.Value;
                        var data = account.Account.Data;
                        data.Profile.Coins = 500;
                        data.Club.Inventory.Add(new ClubItem(OfferedId, target, 6));
                        if (retainAnotherCopy) data.Club.Inventory.Add(new ClubItem(retainedId, target, 6));
                        Market.AcceptedOffers[tradeId] = new(100, OfferedId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
                        account.Account.LastMarketMaintenanceSecond = -1;
                        PlayerContext.MarkDirty();
                    });
                    using (var settlement = await http.GetAsync(Credits)) settlement.EnsureSuccessStatusCode();
                    using var pending = JsonDocument.Parse(await http.GetStringAsync(Root + "/purchased/items"));
                    var duplicates = pending.RootElement.GetProperty("duplicateItemIdList");
                    Check(duplicates.GetArrayLength() == (retainAnotherCopy ? 1 : 0),
                        "A same-card swap created a false duplicate or lost a real retained duplicate.");
                    if (retainAnotherCopy)
                        Check(duplicates[0].GetProperty("duplicateItemId").GetInt64() == retainedId,
                            "A real duplicate receipt pointed to the swapped-away copy instead of the retained copy.");
                    await run(() =>
                    {
                        var data = account.Account.Data;
                        Check(data.Profile.Coins == 400 && data.Club.Inventory.Count == (retainAnotherCopy ? 2 : 1) &&
                            data.Club.Inventory.All(item => item.ItemId != OfferedId), "Same-card swap did not settle the exact wallet and ownership change.");
                        Check(data.PendingDuplicates.All(item => item.OwnedId != OfferedId),
                            "A same-card swap retained a duplicate reference to its removed offered item.");
                    });
                });
        });

        Task InvalidSwap(bool missingCard) => Fixture(account, run, async () =>
        {
            long tradeId = 0;
            long coins = missingCard ? 500 : 25;
            await run(() =>
            {
                tradeId = FindLiveTrade();
                account.Account.Data.Profile.Coins = coins;
                if (!missingCard) account.Account.Data.Club.Inventory.Add(new ClubItem(OfferedId, RealPlayers.All.First(), 6));
                Market.AcceptedOffers[tradeId] = new(100, OfferedId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
                account.Account.LastMarketMaintenanceSecond = -1;
                PlayerContext.MarkDirty();
            });
            using var credits = JsonDocument.Parse(await http.GetStringAsync(Credits));
            Check(credits.RootElement.GetProperty("credits").GetInt64() == coins, "An invalid swap charged coins or refunded an unheld amount.");
            await run(() =>
            {
                var data = account.Account.Data;
                Check(data.Club.Inventory.Count == (missingCard ? 0 : 1) && data.Club.Inventory.All(item => item.ItemId == OfferedId),
                    "An invalid delayed swap granted a target card or removed the offered card.");
                Check(!Market.Bought(tradeId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "An invalid swap marked its target as purchased.");
                Check(!Market.AcceptedOffers.ContainsKey(tradeId) && data.PendingPackItems.Count == 0,
                    "An invalid swap remained pending or advertised a new acquisition.");
            });
        });
    }

    private static long FindLiveTrade()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int page = 0; page < 16; page++)
        {
            using var listings = JsonDocument.Parse(Market.PageJson(page * 60, 60, now));
            foreach (var entry in listings.RootElement.EnumerateArray())
            {
                long id = entry.GetProperty("tradeId").GetInt64();
                if (Market.Timing(id, now).EndsAt > now + 60 && Market.ResolveTradeId(id, out _, out _, out _)) return id;
            }
        }
        throw new InvalidOperationException("No sufficiently long-lived player auction was available for the bounded swap fixture.");
    }

    private static async Task Fixture(PlayerSession account, Func<Action, Task> run, Func<Task> action)
    {
        PlayerState original = null!;
        long lastMarketSecond = account.Account.LastMarketMaintenanceSecond;
        await run(() =>
        {
            original = account.Account.Data;
            var previous = original.Profile;
            account.Account.Data = new PlayerState();
            var profile = account.Account.Data.Profile;
            profile.NucleusId = account.Account.Id; profile.PersonaName = account.Account.Name; profile.Club.Established = true;
            profile.RankedCoinsApplied = previous.RankedCoinsApplied; profile.RankedRevisionApplied = previous.RankedRevisionApplied;
            profile.RankedWinsApplied = previous.RankedWinsApplied; profile.RankedDrawsApplied = previous.RankedDrawsApplied;
            profile.RankedLossesApplied = previous.RankedLossesApplied;
            account.Account.LastMarketMaintenanceSecond = -1;
            PlayerContext.MarkDirty();
        });
        try { await action(); }
        finally
        {
            await run(() =>
            {
                account.Account.Data = original;
                account.Account.LastMarketMaintenanceSecond = lastMarketSecond;
                PlayerContext.MarkDirty();
            });
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
