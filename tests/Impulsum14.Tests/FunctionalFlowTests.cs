using System.Net.Http.Json;
using System.Text.Json;
using Impulsum14;

// Ordinary client transactions. Register after the shared HTTP fixture starts:
// FunctionalFlowTests.Register(Test, http, a, action => Run(a, action));
// These checks exercise the real router and durable account transaction. They do
// not establish compatibility with a running FIFA executable.
internal static class FunctionalFlowTests
{
    private const string Root = "/fut/rs4/ut/game/fifa14";
    private const string DeleteItems = "/fut/rs4/ut/delete/game/fifa14/item";

    internal static void Register(Action<string, Func<Task>> test, HttpClient http,
        PlayerSession account, Func<Action, Task> run)
    {
        test("Single selected player quick sell pays the displayed value once", () => WithFixture(account, run, async () =>
        {
            const long itemId = 850_910_001;
            RealPlayer player = RealPlayers.All.First();
            await run(() =>
            {
                account.Account.Data.Profile.Coins = 1000;
                account.Account.Data.Club.Inventory.Add(new ClubItem(itemId, player, 6));
                PlayerContext.MarkDirty();
            });
            using var reply = await http.PostAsJsonAsync(DeleteItems, new { itemId });
            reply.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            long expected = 1000L + player.Rating * 4;
            Check(body.RootElement.GetProperty("totalCredits").GetInt64() == expected, "Scalar itemId did not pay its advertised discard value.");
            await run(() =>
            {
                Check(account.Account.Data.Club.Inventory.All(x => x.ItemId != itemId), "Quick sold player remains owned.");
                Check(account.Account.Data.Profile.Coins == expected, "Quick sell response differs from committed wallet.");
            });
            using var retry = await http.PostAsJsonAsync(DeleteItems, new { itemId });
            retry.EnsureSuccessStatusCode();
            await run(() => Check(account.Account.Data.Profile.Coins == expected, "Quick sell retry paid twice."));
        }));

        test("An empty squad players array clears the saved squad", () => ClearSquad(Array.Empty<object>()));
        test("Native empty-slot rows clear the saved squad", () => ClearSquad(
            Enumerable.Range(0, 23).Select(index => new { index, itemData = new { id = 0L } }).ToArray()));

        test("Native Copy Squad creates a distinct populated squad", () => WithFixture(account, run, async () =>
        {
            long[] sourceIds = Enumerable.Range(0, 23).Select(i => 850_910_400L + i).ToArray();
            await run(() =>
            {
                var club = account.Account.Data.Club;
                var source = new Squad { Id = 0, Name = "Original XI", Formation = "f442" };
                var players = RealPlayers.All.Take(23).ToArray();
                for (int i = 0; i < 23; i++)
                {
                    club.Inventory.Add(new ClubItem(sourceIds[i], players[i], 7));
                    source.Slots[i] = sourceIds[i];
                    source.KitNumbers[i] = i + 1;
                }
                club.Squads.Add(source);
                PlayerContext.MarkDirty();
            });
            using var copy = await http.PostAsJsonAsync(Root + "/squad", new
            {
                id = 0, squadName = "COPY Original XI", chemistry = 56, starRating = 60, rating = 60,
                formation = "f4141", manager = new[] { new { id = 0L } },
                players = sourceIds.Select((id, index) => new { index, kitNumber = index + 1, itemData = new { id } }).ToArray()
            });
            copy.EnsureSuccessStatusCode();
            using var parsed = JsonDocument.Parse(await copy.Content.ReadAsStringAsync());
            int copyId = parsed.RootElement.GetProperty("id").GetInt32();
            Check(copyId != 0, "Copy returned the original squad identity.");
            Check(parsed.RootElement.GetProperty("players").EnumerateArray().Select(x => x.GetProperty("itemData").GetProperty("id").GetInt64()).SequenceEqual(sourceIds), "Copy response lost source players.");
            Check(parsed.RootElement.GetProperty("players").EnumerateArray().Select(x => x.GetProperty("kitNumber").GetInt32()).SequenceEqual(Enumerable.Range(1, 23)), "Copy response lost native shirt numbers.");
            await run(() =>
            {
                var squads = account.Account.Data.Club.Squads;
                Check(squads.Count == 2, "Copy did not create exactly one new squad.");
                var original = squads.Single(x => x.Id == 0);
                var copied = squads.Single(x => x.Id == copyId);
                Check(sourceIds.Select((id, index) => original.Slots.GetValueOrDefault(index) == id && copied.Slots.GetValueOrDefault(index) == id).All(x => x), "Copy changed original or copied membership.");
                Check(original.Name == "Original XI" && copied.Name == "COPY Original XI", "Copy changed squad names.");
                Check(copied.Formation == "f4141" && copied.Chemistry == 56 && copied.StarRating == 60, "Copy discarded squad metadata.");
                Check(Enumerable.Range(0, 23).All(index => copied.KitNumbers.GetValueOrDefault(index) == index + 1 && original.KitNumbers.GetValueOrDefault(index) == index + 1), "Copy lost the copied or original shirt numbers.");
            });
        }));

        test("A native nameless player update preserves the squad name", () => WithFixture(account, run, async () =>
        {
            const long itemId = 850_910_450;
            await run(() =>
            {
                var club = account.Account.Data.Club;
                club.Inventory.Add(new ClubItem(itemId, RealPlayers.All.First(), 6));
                club.Squads.Add(new Squad { Id = 0, Name = "Named XI" });
                PlayerContext.MarkDirty();
            });
            using var update = await http.PutAsJsonAsync(Root + "/squad/0", new
            {
                id = 0, squadName = "", formation = "f4141", chemistry = 0, starRating = 0,
                players = Enumerable.Range(0, 23).Select(index => new { index, kitNumber = 0, itemData = new { id = index == 0 ? itemId : 0L } }).ToArray()
            });
            update.EnsureSuccessStatusCode();
            await run(() =>
            {
                var squad = account.Account.Data.Club.Squads.Single();
                Check(squad.Name == "Named XI" && squad.Slots.GetValueOrDefault(0) == itemId, "Nameless player update renamed the squad or lost the selected player.");
            });
        }));

        test("Choosing an owned manager from the native manager array persists", () => WithFixture(account, run, async () =>
        {
            const long firstManagerId = 970_910_001, selectedManagerId = 970_910_002;
            await run(() =>
            {
                var club = account.Account.Data.Club;
                club.Managers.AddRange(Managers.All.Take(2));
                Check(club.Managers.Count == 2, "Manager catalogue is unavailable.");
                club.ManagerItemIds.AddRange(new[] { firstManagerId, selectedManagerId });
                club.Squads.Add(new Squad { Id = 0, Name = "Managed XI", ManagerId = firstManagerId });
                PlayerContext.MarkDirty();
            });
            using var changed = await http.PutAsJsonAsync(Root + "/squad/0", new { manager = new[] { new { id = selectedManagerId, itemType = "manager" } } });
            changed.EnsureSuccessStatusCode();
            using var response = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
            Check(response.RootElement.GetProperty("manager")[0].GetProperty("id").GetInt64() == selectedManagerId, "Selected manager was not returned.");
            using var reread = JsonDocument.Parse(await http.GetStringAsync(Root + "/squad/0"));
            Check(reread.RootElement.GetProperty("manager")[0].GetProperty("id").GetInt64() == selectedManagerId, "Selected manager was not persisted.");
            using var removed = await http.PutAsJsonAsync(Root + "/squad/0", new { manager = new[] { new { id = 0L } } });
            removed.EnsureSuccessStatusCode();
            await run(() => Check(account.Account.Data.Club.Squads.Single().ManagerId == 0, "Removing the manager did not persist."));
        }));

        test("Native captain and five indexed kick takers persist without replacing players", () => WithFixture(account, run, async () =>
        {
            const long firstId = 850_910_470, captainId = 850_910_471;
            await run(() =>
            {
                var club = account.Account.Data.Club;
                var players = RealPlayers.All.Take(2).ToArray();
                club.Inventory.Add(new ClubItem(firstId, players[0], 7));
                club.Inventory.Add(new ClubItem(captainId, players[1], 7));
                club.Squads.Add(new Squad { Id = 0, Name = "Role XI", Slots = new() { [0] = firstId, [1] = captainId } });
                PlayerContext.MarkDirty();
            });
            long[] takerIds = { captainId, captainId, firstId, firstId, captainId };
            var roleDocument = new { id = 0, captain = captainId, kicktakers = takerIds.Select((id, index) => new { index, id }).ToArray() };
            using var saved = await http.PutAsJsonAsync(Root + "/squad/0", roleDocument);
            saved.EnsureSuccessStatusCode();
            using var reread = JsonDocument.Parse(await http.GetStringAsync(Root + "/squad/0"));
            Check(reread.RootElement.GetProperty("captain").GetInt64() == captainId, "Selected captain did not round-trip.");
            var returnedTakers = reread.RootElement.GetProperty("kicktakers").EnumerateArray().ToDictionary(x => x.GetProperty("index").GetInt32(), x => x.GetProperty("id").GetInt64());
            Check(returnedTakers.Count == 5 && takerIds.Select((id, index) => returnedTakers.GetValueOrDefault(index) == id).All(x => x), "Native indexed kick takers did not round-trip.");
            await run(() =>
            {
                var squad = account.Account.Data.Club.Squads.Single();
                Check(squad.Slots.Count == 2 && squad.Name == "Role XI", "A role-only update replaced players or name.");
            });
            using var removal = await http.PutAsJsonAsync(Root + "/squad/0", new
            {
                players = new[] { new { index = 0, itemData = new { id = firstId } }, new { index = 1, itemData = new { id = 0L } } }
            });
            removal.EnsureSuccessStatusCode();
            await run(() =>
            {
                var squad = account.Account.Data.Club.Squads.Single();
                Check(squad.CaptainId == 0 && squad.KickTakers.Values.All(id => id != captainId), "Removing a player left stale captain or kick-taker references.");
                Check(squad.Slots.Count == 1 && squad.Slots[0] == firstId, "Removing a role holder lost the remaining player.");
            });
        }));

        test("MatchReady requires an active match", () => WithFixture(account, run, async () =>
        {
            using var response = await http.PutAsJsonAsync(Root + "/match", new { items = Array.Empty<object>() });
            Check(response.StatusCode == System.Net.HttpStatusCode.Conflict, "Ready without a created match did not report conflict.");
        }));

        test("MatchReady binds the played XI even after another squad is viewed", () => WithFixture(account, run, async () =>
        {
            long[] starterIds = Enumerable.Range(0, 11).Select(i => 850_910_200L + i).ToArray();
            long[] spareIds = Enumerable.Range(0, 11).Select(i => 850_910_220L + i).ToArray();
            const long benchId = 850_910_211;
            var players = RealPlayers.All.Take(23).ToArray();
            await run(() =>
            {
                var club = account.Account.Data.Club;
                var played = new Squad { Id = 0, Name = "Played XI" };
                var spare = new Squad { Id = 1, Name = "Spare XI" };
                for (int i = 0; i < 11; i++)
                {
                    club.Inventory.Add(new ClubItem(starterIds[i], players[i], 7));
                    club.Inventory.Add(new ClubItem(spareIds[i], players[i + 12], 7));
                    played.Slots[i] = starterIds[i];
                    spare.Slots[i] = spareIds[i];
                }
                club.Inventory.Add(new ClubItem(benchId, players[11], 7));
                played.Slots[11] = benchId;
                club.Squads.Add(played); club.Squads.Add(spare);
                PlayerContext.MarkDirty();
            });
            using var created = await http.PostAsJsonAsync(Root + "/match", new { squadId = 0, type = "offline" });
            created.EnsureSuccessStatusCode();
            var readyDocument = new { items = starterIds.Select(id => new { id }).ToArray() };
            using var ready = await http.PutAsJsonAsync(Root + "/match", readyDocument);
            ready.EnsureSuccessStatusCode();
            using var readyRetry = await http.PutAsJsonAsync(Root + "/match", readyDocument);
            readyRetry.EnsureSuccessStatusCode();
            using var spareViewed = await http.GetAsync(Root + "/squad/1");
            spareViewed.EnsureSuccessStatusCode();
            var endDocument = new
            {
                endReason = "WIN", myMatchStats = new { goals = 1 },
                items = starterIds.Select(id => new { id, fitness = 94, redCards = 0, yellowCards = 0 }).ToArray()
            };
            using var finished = await http.PostAsJsonAsync(Root + "/match/end", endDocument);
            finished.EnsureSuccessStatusCode();
            await run(() => VerifyPlayedXi());
            using var retry = await http.PostAsJsonAsync(Root + "/match/end", endDocument);
            retry.EnsureSuccessStatusCode();
            await run(() => VerifyPlayedXi());

            void VerifyPlayedXi()
            {
                var mods = account.Account.Data.Club.PlayerMods;
                foreach (long id in starterIds)
                {
                    Check(mods.TryGetValue(id, out var mod) && mod.Contract == 6 && mod.Fitness == 94, "The MatchReady XI did not consume exactly one contract with its reported fitness.");
                }
                foreach (long id in spareIds.Append(benchId))
                {
                    Check(!mods.TryGetValue(id, out var mod) || ((mod.Contract is -1 or 7) && (mod.Fitness is -1 or 99)), "An unused bench or spare-squad player consumed match state.");
                }
            }
        }));

        // Native FutDestroyMatchServerResponse contract independently documented
        // in reference-f14/server/beta_identity.py settle_match_end, and
        // tools/verify_fifa14_postmatch_beta2256.py. Match stat items are sparse
        // per-player rows, not full ItemData cards.
        test("A completed native match preserves stats, result, duration and its retry receipt", () => WithFixture(account, run, async () =>
        {
            const long itemId = 850_910_101;
            long[] starterIds = Enumerable.Range(0, 11).Select(i => itemId + i).ToArray();
            await run(() =>
            {
                var data = account.Account.Data;
                var squad = new Squad { Id = 0, Name = "Flow Fixture" };
                var players = RealPlayers.All.Take(11).ToArray();
                for (int i = 0; i < 11; i++)
                {
                    data.Club.Inventory.Add(new ClubItem(starterIds[i], players[i], 7));
                    squad.Slots[i] = starterIds[i];
                }
                data.Club.Squads.Add(squad);
                PlayerContext.MarkDirty();
            });
            using var created = await http.PostAsJsonAsync(Root + "/match", new { squadId = 0, type = "offline" });
            created.EnsureSuccessStatusCode();
            using var ready = await http.PutAsJsonAsync(Root + "/match", new { items = starterIds.Select(id => new { id }).ToArray() });
            ready.EnsureSuccessStatusCode();
            var stats = new { goals = 2, shotsOnTarget = 3, successfulTackles = 8, corners = 1, cleansheets = 1, passingPercentage = 84, possessionPercentage = 55, manOfTheMatch = 1, fouls = 0, yellowCards = 0, redCards = 0, offsides = 0 };
            var opponentStats = new { goals = 0, shotsOnTarget = 1, successfulTackles = 5, corners = 0, cleansheets = 0, passingPercentage = 75, possessionPercentage = 45, manOfTheMatch = 0, fouls = 0, yellowCards = 0, redCards = 0, offsides = 0 };
            var document = new
            {
                endReason = "WIN", myRating = 10, opponentRating = 10,
                items = new[] { new { id = itemId, goals = 2, shots = 3, fitness = 91, yellowCards = 0, redCards = 0 } },
                myMatchStats = stats, opponentMatchStats = opponentStats,
                matchData = "ordinary-native-completion"
            };
            using var finished = await http.PostAsJsonAsync(Root + "/match/end", document);
            finished.EnsureSuccessStatusCode();
            string receipt = await finished.Content.ReadAsStringAsync();
            using var parsed = JsonDocument.Parse(receipt);
            var result = parsed.RootElement;
            Check(result.GetProperty("endReason").GetString() == "WIN", "Native terminal result changed.");
            Check(result.GetProperty("secondsPlayed").GetInt32() == 5400, "Completed match has no 90-minute duration.");
            Check(result.GetProperty("matchDifficulty").GetInt32() > 0, "Played difficulty is absent.");
            Check(result.GetProperty("myMatchStats").GetRawText() == JsonSerializer.Serialize(stats), "Player stats did not round-trip.");
            Check(result.GetProperty("opponentMatchStats").GetRawText() == JsonSerializer.Serialize(opponentStats), "Opponent stats did not round-trip.");
            Check(result.GetProperty("matchData").GetString() == document.matchData, "Native match data did not round-trip.");
            var item = result.GetProperty("items")[0];
            Check(item.GetProperty("id").GetInt64() == itemId && item.GetProperty("goals").GetInt32() == 2 && item.GetProperty("fitness").GetInt32() == 91, "Per-player match stats changed.");
            Check(!item.TryGetProperty("resourceId", out _) && !item.TryGetProperty("itemType", out _), "DestroyMatch returned card objects instead of native match-stat rows.");
            long settledCoins = 0;
            await run(() =>
            {
                settledCoins = account.Account.Data.Profile.Coins;
                Check(settledCoins > 0 && result.GetProperty("credits").GetInt64() == settledCoins, "Result wallet differs from committed wallet.");
                Check(account.Account.Data.Profile.Wins == 1, "Completed win was not recorded once.");
            });
            using var retry = await http.PostAsJsonAsync(Root + "/match/end", document);
            retry.EnsureSuccessStatusCode();
            Check(await retry.Content.ReadAsStringAsync() == receipt, "Match retry returned a different receipt.");
            await run(() =>
            {
                Check(account.Account.Data.Profile.Coins == settledCoins, "Match retry paid twice.");
                Check(account.Account.Data.Profile.Wins == 1, "Match retry advanced the record twice.");
            });
        }));

        test("Opening another pack preserves all unassigned recovery items", () => WithFixture(account, run, async () =>
        {
            await run(() => FutProfileStore.Mutate(p => p.Coins = 2000));
            using var firstReply = await http.PostAsJsonAsync(Root + "/purchased/items", new { packId = 100 }); firstReply.EnsureSuccessStatusCode();
            using var first = JsonDocument.Parse(await firstReply.Content.ReadAsStringAsync());
            long[] firstIds = first.RootElement.GetProperty("itemList").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToArray();
            using var secondReply = await http.PostAsJsonAsync(Root + "/purchased/items", new { packId = 100 }); secondReply.EnsureSuccessStatusCode();
            using var second = JsonDocument.Parse(await secondReply.Content.ReadAsStringAsync());
            long[] secondIds = second.RootElement.GetProperty("itemList").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToArray();
            using var recovery = JsonDocument.Parse(await http.GetStringAsync(Root + "/purchased/items"));
            var recovered = recovery.RootElement.GetProperty("itemData").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToHashSet();
            Check(recovered.SetEquals(firstIds.Concat(secondIds)), "Second pack replaced unassigned first-pack recovery items.");
        }));
        test("New-card recovery reflects current claims and current balance", () => WithFixture(account, run, async () =>
        {
            await run(() => FutProfileStore.Mutate(p => p.Coins = 2000));
            using var bought = await http.PostAsJsonAsync(Root + "/purchased/items", new { packId = 100 }); bought.EnsureSuccessStatusCode();
            using var pack = JsonDocument.Parse(await bought.Content.ReadAsStringAsync());
            var ids = pack.RootElement.GetProperty("itemList").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToArray();
            using var claim = await http.PutAsJsonAsync(Root + "/item", new { itemData = ids.Select(id => new { id, pile = "club" }).ToArray() }); claim.EnsureSuccessStatusCode();
            await run(() => FutProfileStore.Mutate(p => p.Coins -= 25));
            using var newcards = JsonDocument.Parse(await http.GetStringAsync(Root + "/club/stats/newcards"));
            Check(newcards.RootElement.GetProperty("itemList").GetArrayLength() == 0, "Recovery republished already claimed cards.");
            Check(newcards.RootElement.GetProperty("credits").GetInt64() == 1575, "Recovery returned the old pack-purchase balance.");
            await run(() => Check(account.Account.Data.PendingDuplicates.Count == 0, "Claimed duplicate receipts were retained."));
        }));

        Task ClearSquad(object players) => WithFixture(account, run, async () =>
        {
            var catalog = RealPlayers.All.Take(2).ToArray();
            await run(() =>
            {
                var data = account.Account.Data;
                data.Club.Inventory.Add(new ClubItem(850_910_010, catalog[0], 7));
                data.Club.Inventory.Add(new ClubItem(850_910_011, catalog[1], 7));
                data.Club.Squads.Add(new Squad { Id = 0, Name = "Clear Fixture", Slots = new() { [0] = 850_910_010, [1] = 850_910_011 } });
                PlayerContext.MarkDirty();
            });
            using var cleared = await http.PutAsJsonAsync(Root + "/squad/0", new { players });
            cleared.EnsureSuccessStatusCode();
            using var response = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
            Check(response.RootElement.GetProperty("players").EnumerateArray().All(x => x.GetProperty("itemData").GetProperty("id").GetInt64() == 0), "Clear response still contains old players.");
            await run(() =>
            {
                Check(account.Account.Data.Club.Squads.Single().Slots.Values.All(x => x == 0), "Clear was not persisted.");
                Check(account.Account.Data.Club.Inventory.All(x => x.Pile == 6), "Unassigned squad players did not return to club pile.");
            });
        });
    }

    private static async Task WithFixture(PlayerSession account, Func<Action, Task> run, Func<Task> action)
    {
        PlayerState? original = null;
        await run(() =>
        {
            original = account.Account.Data;
            account.Account.Data = new PlayerState();
            account.Account.Data.Profile.NucleusId = account.Account.Id;
            account.Account.Data.Profile.PersonaName = account.Account.Name;
            account.Account.Data.Profile.Club.Established = true;
            // Keep account-level ranked receipts when replacing only the local
            // club fixture, so ledger reconciliation cannot repay old rewards.
            account.Account.Data.Profile.RankedCoinsApplied = original.Profile.RankedCoinsApplied;
            account.Account.Data.Profile.RankedRevisionApplied = original.Profile.RankedRevisionApplied;
            account.Account.Data.Profile.RankedWinsApplied = original.Profile.RankedWinsApplied;
            account.Account.Data.Profile.RankedDrawsApplied = original.Profile.RankedDrawsApplied;
            account.Account.Data.Profile.RankedLossesApplied = original.Profile.RankedLossesApplied;
            PlayerContext.MarkDirty();
        });
        try { await action(); }
        finally
        {
            await run(() =>
            {
                account.Account.Data = original!;
                PlayerContext.MarkDirty();
            });
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
