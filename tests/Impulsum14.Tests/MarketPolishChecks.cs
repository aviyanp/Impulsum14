using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using Impulsum14;

internal static class MarketPolishChecks
{
    internal static void Register(Action<string, Func<Task>> test, PlayerSession first, PlayerSession second,
        Func<PlayerSession, Action, Task> run)
    {
        test("Typed native market filters preserve every base and special card selection", () => run(first, () =>
        {
            var cards = SpecialCards.All.Concat(RealPlayers.All).ToArray();
            Check(cards.Length > 0, "Player catalogue is empty.");
            var samples = new List<NameValueCollection>
            {
                new(), new() { ["minRating"] = "bad", ["maxRating"] = "-2", ["nat"] = "-99", ["definitionId"] = "0" },
                new() { ["minRating"] = "75", ["maxRating"] = "84", ["lev"] = "GoLd" },
                new() { ["minRating"] = "90", ["maxRating"] = "50" },
                new() { ["nat"] = "0", ["leag"] = "0", ["team"] = "0" },
                new() { ["lev"] = "bronze" }, new() { ["lev"] = "silver" }, new() { ["lev"] = "gold" }
            };
            foreach (var card in cards.Where(c => c.IsSpecial).Take(8).Concat(cards.TakeLast(4)))
            {
                samples.Add(new() { ["definitionId"] = card.CardId.ToString() });
                samples.Add(new() { ["maskedDefId"] = card.CardId.ToString(), ["nat"] = card.NationId.ToString() });
                samples.Add(new() { ["team"] = card.TeamId.ToString(), ["leag"] = TeamLeagues.LeagueOf(card.TeamId).ToString() });
            }
            const long now = 2_000_000_000;
            foreach (var query in samples)
            {
                var typed = Market.ParsePlayerFilter(query);
                foreach (var card in cards)
                    Check(typed.Matches(card) == LegacyMatches(query, card), $"Native filter changed selection for {card.CardId}.");
                for (int start = 0; start < 18; start += 6)
                {
                    string expected = Market.PageJson(start, 6, now, c => LegacyMatches(query, c));
                    string actual = Market.PageJson(start, 6, now, selection: typed);
                    Check(expected == actual, "Cached native filter changed listing order, variant or auction JSON.");
                    Check(Market.PageJson(start, 6, now, selection: typed) == expected, "A repeated native view changed its result.");
                }
            }
        }));

        test("Cached auction history agrees with the original forty-cycle oracle", () =>
        {
            long[] totals = { Market.Total, Market.CTotal, Market.ETotal, Market.STotal };
            long[] cycles = { 0, 1, 2, 39, 40, 41, 197_531 };
            for (byte family = 0; family < totals.Length; family++)
            {
                Check(totals[family] > 0, "Auction catalogue is empty.");
                for (int sample = 0; sample < 24; sample++)
                {
                    long listing = sample * (totals[family] - 1) / 23;
                    foreach (long cycle in cycles)
                    {
                        long expected = HistoryOracle(family, listing, cycle);
                        Check(StartingPrice(family, listing, cycle) == expected, "Cached auction history changed its starting price.");
                        Check(StartingPrice(family, listing, cycle) == expected, "Warm auction history differs from the original oracle.");
                    }
                }
            }
            return Task.CompletedTask;
        });

        test("Purchased managers preserve advertised contracts and league modifiers across relistings", () => run(first, () =>
        {
            var managers = (Manager[])Field("FManager");
            var isManager = (bool[])Field("FIsManager");
            var prefix = (long[])Field("FPrefix");
            bool sawWorn = false, sawFull = false, sawModifiedLeague = false;
            long staffTrade = -1;
            for (int index = 0; index < isManager.Length; index++)
            {
                if (!isManager[index]) { staffTrade = Market.StaffTradeIdBase + prefix[index]; break; }
                if (index >= 24) continue;
                foreach (long cycle in new long[] { 0, 1, 2, 40, 41, 197_531 })
                {
                    long listing = prefix[index] + (prefix[index + 1] - prefix[index]) / 2;
                    var lifecycle = Call<(long, long, long)>("FLifecycle", listing);
                    long now = lifecycle.Item3 + cycle * (lifecycle.Item1 + lifecycle.Item2) + lifecycle.Item1 / 2;
                    long trade = Market.StaffTradeIdBase + listing;
                    var mods = Market.ManagerListingMods(trade, now);
                    Check(mods != null, "Manager listing has no transferable modifications.");
                    using var document = JsonDocument.Parse(Market.EntryByTradeId(trade, now));
                    var item = document.RootElement.GetProperty("itemData");
                    Check(item.GetProperty("itemType").GetString() == "manager", "Manager fixture resolved to a different item type.");
                    Check(mods!.Contract == item.GetProperty("contract").GetInt32()
                        && mods.LeagueId == item.GetProperty("leagueId").GetInt32()
                        && mods.LeagueId == item.GetProperty("leagueid").GetInt32(),
                        "Purchased manager modifications differ from the advertised card.");
                    sawWorn |= mods.Contract < 7; sawFull |= mods.Contract == 7;
                    sawModifiedLeague |= mods.LeagueId != managers[index].LeagueId;
                    mods.Contract = 999; mods.LeagueId = 999;
                    var fresh = Market.ManagerListingMods(trade, now)!;
                    Check(fresh.Contract == item.GetProperty("contract").GetInt32()
                        && fresh.LeagueId == item.GetProperty("leagueId").GetInt32(), "One purchase mutated the shared listing state.");
                }
            }
            Check(sawWorn && sawFull && sawModifiedLeague, "Manager fixtures did not cover worn, full and league-modified cards.");
            Check(Market.ManagerListingMods(Market.StaffTradeIdBase - 1, 0) == null
                && Market.ManagerListingMods(Market.StaffTradeIdBase + Market.STotal, 0) == null
                && (staffTrade < 0 || Market.ManagerListingMods(staffTrade, 0) == null), "Non-manager trade IDs exposed manager modifications.");
            int returned = 0;
            foreach (int league in managers.Take(24).Select(m => m.LeagueId).Concat(TeamLeagues.AllLeagues.Take(12)).Where(l => l > 0).Distinct())
            {
                using var page = JsonDocument.Parse(Market.StaffPageJson(0, 12, 2_000_000_000, "manager", "", leag: league));
                foreach (var auction in page.RootElement.EnumerateArray())
                {
                    var item = auction.GetProperty("itemData");
                    Check(item.GetProperty("leagueId").GetInt32() == league && item.GetProperty("leagueid").GetInt32() == league,
                        "Native manager league search returned a card advertising a different modified league.");
                    returned++;
                }
            }
            Check(returned > 0, "Manager league search had no fixtures.");
        }));

        test("Auction current-price filters match displayed native bid values", () => run(first, () =>
        {
            const long now = 2_000_000_000;
            string[] pages =
            {
                Market.PageJson(0, 24, now),
                Market.ConsumablePageJson(0, 24, now, "", ""),
                Market.ClubItemPageJson(0, 24, now, "", ""),
                Market.StaffPageJson(0, 24, now, "", "")
            };
            long[] bases = { Market.TradeIdBase, Market.ConsumableTradeIdBase, Market.ClubItemTradeIdBase, Market.StaffTradeIdBase };
            string[] methods = { "ByPrice", "CByPrice", "EByPrice", "FByPrice" };
            for (int family = 0; family < pages.Length; family++)
            {
                using var page = JsonDocument.Parse(pages[family]);
                Check(page.RootElement.GetArrayLength() > 0, "A native market family returned no listings.");
                foreach (var entry in page.RootElement.EnumerateArray())
                {
                    long tradeId = entry.GetProperty("tradeId").GetInt64();
                    int current = entry.GetProperty("currentBid").GetInt32();
                    object[] Args(int min, int max) => family == 0
                        ? new object[] { Market.ListingCard(tradeId)!.Value, tradeId - bases[family], now, 0, 0, min, max }
                        : new object[] { tradeId - bases[family], now, 0, 0, min, max };
                    Check(Call<bool>(methods[family], Args(current, current)), "Price filter rejected its displayed current bid.");
                    Check(!Call<bool>(methods[family], Args(current + 1, 0)), "Minimum bid filter included a cheaper displayed bid.");
                    Check(!Call<bool>(methods[family], Args(0, current - 1)), "Maximum bid filter included a higher displayed bid.");
                }
            }
        }));

        test("Shared market caches preserve private purchases, watches and escrow", async () =>
        {
            const long now = 2_000_000_000;
            PlayerMarketState? savedFirst = null, savedSecond = null;
            try
            {
                await run(first, () => { savedFirst = first.Account.Data.Market; first.Account.Data.Market = new(); PlayerContext.MarkDirty(); });
                await run(second, () => { savedSecond = second.Account.Data.Market; second.Account.Data.Market = new(); PlayerContext.MarkDirty(); });
                long trade = 0;
                long referenceValue = 0;
                RealPlayer purchasedCard = default;
                var selection = Market.ParsePlayerFilter(new());
                await run(first, () =>
                {
                    using var page = JsonDocument.Parse(Market.PageJson(0, 12, now, selection: selection));
                    trade = page.RootElement[0].GetProperty("tradeId").GetInt64();
                    purchasedCard = Market.ListingCard(trade)!.Value;
                    Market.MarkBought(trade, now);
                    Market.Watched[trade] = 0;
                    Market.MyBids[trade] = 500;
                    Market.HeldCoins = 500;
                    PlayerContext.MarkDirty();
                    using var own = JsonDocument.Parse(Market.PageJson(0, 12, now, selection: selection));
                    Check(own.RootElement.EnumerateArray().All(e => e.GetProperty("tradeId").GetInt64() != trade), "Bought listing remained in the same account's search.");
                    referenceValue = Market.CheapestLiveBuyNow(purchasedCard, now);
                    Check(referenceValue > 0, "Live player has no reference market valuation.");
                    var cards = (RealPlayer[])Field("Cards");
                    int index = Array.FindIndex(cards, c => c.CardId == purchasedCard.CardId);
                    var prefix = (long[])Field("Prefix");
                    for (long g = prefix[index]; g < prefix[index + 1]; g++) Market.MarkBought(Market.TradeIdBase + g, now);
                    object cache = Field("CheapCache");
                    cache.GetType().GetMethod("Clear")!.Invoke(cache, null);
                    Check(Market.CheapestLiveBuyNow(purchasedCard, now) == referenceValue, "Private purchases poisoned the shared valuation cache.");
                });
                await run(second, () =>
                {
                    using var page = JsonDocument.Parse(Market.PageJson(0, 12, now, selection: selection));
                    var same = page.RootElement.EnumerateArray().Single(e => e.GetProperty("tradeId").GetInt64() == trade);
                    Check(!same.GetProperty("watched").GetBoolean() && same.GetProperty("bidState").GetString() == "none", "Shared cache leaked another account's watch or bid.");
                    Check(Market.HeldCoins == 0 && !Market.Bought(trade, now), "Shared cache leaked another account's escrow or ownership.");
                    Check(Market.CheapestLiveBuyNow(purchasedCard, now) == referenceValue, "Another account received a private purchase's reference value.");
                });
                await run(first, () =>
                {
                    Market.AcceptedOffers[trade] = new(975, 0, now + 30);
                    using var entry = JsonDocument.Parse(Market.EntryByTradeId(trade, now));
                    Check(entry.RootElement.GetProperty("currentBid").GetInt32() == 975, "Accepted offer was not reflected in its auction.");
                    Check(Call<bool>("ByPrice", Market.ListingCard(trade)!.Value, trade - Market.TradeIdBase, now, 0, 0, 975, 975), "Current-bid filter ignored an accepted offer.");
                    Check(Market.WatchlistCounts(now) == (1, 1, 0), "Accepted offer was counted twice in the watchlist.");
                });
            }
            finally
            {
                if (savedFirst != null) await run(first, () => { first.Account.Data.Market = savedFirst; PlayerContext.MarkDirty(); });
                if (savedSecond != null) await run(second, () => { second.Account.Data.Market = savedSecond; PlayerContext.MarkDirty(); });
            }
        });

        test("Native watchlist and settlement use the auction's historical starting bid", async () =>
        {
            PlayerMarketState? saved = null;
            try
            {
                await run(first, () =>
                {
                    saved = first.Account.Data.Market; first.Account.Data.Market = new(); PlayerContext.MarkDirty();
                    const long now = 2_000_000_000;
                    var expected = new Dictionary<long, (long Start, bool Won)>();
                    int winning = 0, outbid = 0;
                    for (int sample = 0; sample < 96; sample++)
                    {
                        long g = sample * (Market.Total - 1) / 95;
                        var cycle = Call<(long, long, long, long, long)>("Cycle", g, now);
                        if (cycle.Item5 >= cycle.Item3) continue;
                        var card = Market.ListingCard(Market.TradeIdBase + g)!.Value;
                        var price = Call<(int, int)>("Price", card, g, cycle.Item1);
                        int start = (int)HistoryOracle(0, g, cycle.Item1);
                        var bids = Call<(bool, long, int, long)>("SimBids", card, g, cycle.Item1, cycle.Item3, start, price.Item2, cycle.Item3);
                        long amount = Math.Max(150, bids.Item4 - Market.Step(bids.Item4));
                        long trade = Market.TradeIdBase + g;
                        Market.MyBids[trade] = amount;
                        first.Account.Data.Market.BidTimings[trade] = new(cycle.Item1, cycle.Item3, cycle.Item2 + cycle.Item3);
                        expected[trade] = (start, amount >= bids.Item4);
                        long current = Call<(bool, long, int, long)>("SimBids", card, g, cycle.Item1, cycle.Item3, start, price.Item2, cycle.Item5).Item2;
                        if (amount >= current) winning++; else outbid++;
                    }
                    Check(expected.Count > 0, "No historical auction fixtures were available.");
                    Check(Market.WatchlistCounts(now) == (winning + outbid, winning, outbid), "Watchlist status differs from displayed historical bids.");
                    var results = Market.CollectMyBidResults(now + 86_400);
                    Check(results.Count == expected.Count, "Ended auction disappeared from settlement.");
                    foreach (var result in results)
                        Check(result.StartingBid == expected[result.TradeId].Start && result.Won == expected[result.TradeId].Won,
                            "Winner or starting bid differs from the original historical auction.");
                });
            }
            finally
            {
                if (saved != null) await run(first, () => { first.Account.Data.Market = saved; PlayerContext.MarkDirty(); });
            }
        });

        test("Shared market cache memory remains bounded under distinct searches", () => run(first, () =>
        {
            const long now = 2_000_000_000;
            for (int nation = 100_000; nation < 100_100; nation++)
                Market.PageJson(0, 1, now, selection: Market.ParsePlayerFilter(new() { ["nat"] = nation.ToString() }));
            for (int rating = 0; rating < 64; rating++)
                Market.PageJson(0, 1, now, selection: Market.ParsePlayerFilter(new() { ["minRating"] = rating.ToString() }));
            object selections = Field("SelectionCache");
            Check((int)selections.GetType().GetProperty("Count")!.GetValue(selections)! <= 64, "Selection cache exceeded its entry limit.");
            Check((long)Field("SelectionCacheBytes") <= 4 * 1024 * 1024, "Selection cache exceeded its array/key-text budget.");
            // More distinct listing/cycle keys than capacity exercise eviction.
            for (long cycle = 1; cycle <= 8300; cycle++) StartingPrice(0, 0, cycle);
            object starts = Field("StartCache");
            Check((int)starts.GetType().GetProperty("Count")!.GetValue(starts)! <= 8192, "Auction history cache exceeded its entry limit.");
        }));
    }

    private static bool LegacyMatches(NameValueCollection q, RealPlayer p)
    {
        int Int(string name) => int.TryParse(q[name], out int value) ? value : -1;
        int min = Int("minRating"), max = Int("maxRating"), nation = Int("nat"), league = Int("leag"), team = Int("team");
        int masked = Int("maskedDefId"), definition = Int("definitionId");
        string level = (q["lev"] ?? "").ToLowerInvariant();
        return (min < 0 || p.Rating >= min) && (max < 0 || p.Rating <= max)
            && (nation < 0 || p.NationId == nation) && (league < 0 || TeamLeagues.LeagueOf(p.TeamId) == league)
            && (team < 0 || p.TeamId == team) && (masked <= 0 || p.Id == masked % SpecialCards.Band)
            && (definition <= 0 || p.CardId == definition) && (level switch
            { "bronze" => p.Rating < 65, "silver" => p.Rating is >= 65 and < 75, "gold" => p.Rating >= 75, _ => true });
    }

    private static object Field(string name) => typeof(Market).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static T Call<T>(string name, params object[] args) =>
        (T)typeof(Market).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    private static string Prefix(byte family) => family switch { 0 => "", 1 => "C", 2 => "E", _ => "F" };

    private static long StartingPrice(byte family, long listing, long cycle)
    {
        string prefix = Prefix(family);
        long duration = Call<(long, long, long)>(prefix + "Lifecycle", listing).Item1;
        var card = family == 0 ? Market.ListingCard(Market.TradeIdBase + listing)!.Value : default;
        var price = family == 0 ? Call<(int, int)>("Price", card, listing, cycle) : Call<(int, int)>(prefix + "Price", listing, cycle);
        return family == 0 ? Call<long>("EffStart", card, listing, cycle, duration, price.Item1, price.Item2)
            : Call<long>(prefix + "EffStart", listing, cycle, duration, price.Item1, price.Item2);
    }

    private static long HistoryOracle(byte family, long listing, long cycle)
    {
        string prefix = Prefix(family);
        long duration = Call<(long, long, long)>(prefix + "Lifecycle", listing).Item1;
        var card = family == 0 ? Market.ListingCard(Market.TradeIdBase + listing)!.Value : default;
        (int, int) Price(long at) => family == 0 ? Call<(int, int)>("Price", card, listing, at) : Call<(int, int)>(prefix + "Price", listing, at);
        long effective = Price(cycle).Item1, previous = 0;
        long k0 = Math.Max(0, cycle - 40);
        for (long k = k0 + 1; k <= cycle; k++)
        {
            var currentPrice = Price(k); var previousPrice = Price(k - 1);
            long previousStart = k - 1 > k0 ? previous : previousPrice.Item1;
            (bool, long, int, long) bids;
            if (family == 0) bids = Call<(bool, long, int, long)>("SimBids", card, listing, k - 1, duration, (int)previousStart, previousPrice.Item2, duration);
            else if (family == 1)
            {
                int index = Call<int>("LocateIn", Field("CPrefix"), listing);
                bids = Call<(bool, long, int, long)>("CSimBids", ((int[])Field("CTier"))[index], listing, k - 1, duration, (int)previousStart, previousPrice.Item2, duration);
            }
            else if (family == 2)
            {
                int index = Call<int>("LocateIn", Field("EPrefix"), listing);
                var cosmetic = ((CosmeticItem[])Field("ECards"))[index];
                bids = Call<(bool, long, int, long)>("ESimBids", cosmetic.Rare, cosmetic.Rating, listing, k - 1, duration, (int)previousStart, previousPrice.Item2, duration);
            }
            else
            {
                int index = Call<int>("LocateIn", Field("FPrefix"), listing);
                bool manager = ((bool[])Field("FIsManager"))[index];
                int rating = manager ? ((Manager[])Field("FManager"))[index].Rating : ((StaffCard[])Field("FStaff"))[index].Rating;
                bids = Call<(bool, long, int, long)>("FSimBids", rating, manager, listing, k - 1, duration, (int)previousStart, previousPrice.Item2, duration);
            }
            effective = bids.Item1 && bids.Item4 < previousPrice.Item2 ? Math.Max(currentPrice.Item1, bids.Item4) : currentPrice.Item1;
            if (effective > currentPrice.Item2 - Market.Step(currentPrice.Item2))
                effective = Math.Max(currentPrice.Item1, currentPrice.Item2 - Market.Step(currentPrice.Item2));
            previous = effective;
        }
        return effective;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
