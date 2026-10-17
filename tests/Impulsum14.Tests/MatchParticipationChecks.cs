using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Impulsum14;

// Native-shaped HTTP checks; optional per-player duration fields are server
// extensions, not a claim that retail FIFA sends them.
internal static class MatchParticipationChecks
{
    private const string Root = "/fut/rs4/ut/game/fifa14";
    private const long FirstPlayer = 850_940_000;
    private const long PlayedSeconds = FirstPlayer + 11;
    private const long PlayedMinutes = FirstPlayer + 12;
    private const long UnusedBench = FirstPlayer + 13;
    private const long OutsidePlayer = FirstPlayer + 14;
    private const long PlayedManager = 970_940_001;
    private const long OtherManager = 970_940_002;

    internal static void Register(Action<string, Func<Task>> test, HttpClient http,
        PlayerSession account, Func<Action, Task> run)
    {
        test("Native match consumes the bound manager once after the active squad changes", () => Fixture(account, run, async () =>
        {
            await run(() => AddSquads(account));
            long match = await BeginReady(http);
            using (var changed = await http.GetAsync(Root + "/squad/1")) changed.EnsureSuccessStatusCode();
            await run(() => Check(account.Account.Data.Club.ActiveSquadId == 1, "The spare squad was not selected for the drift check."));
            var document = new { matchId = match, endReason = "WIN", myMatchStats = new { goals = 1 }, items = new[] { new { id = FirstPlayer, fitness = 91 } } };
            string receipt = await Finish(http, document);
            using (var items = await ReadItems(http, PlayedManager, OtherManager, FirstPlayer))
            {
                var rows = items.RootElement.GetProperty("itemData").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt64());
                Equal(11, rows[PlayedManager].GetProperty("contract").GetInt32());
                Equal(12, rows[OtherManager].GetProperty("contract").GetInt32());
                Equal(9, rows[FirstPlayer].GetProperty("contract").GetInt32());
            }
            Equal(receipt, await Finish(http, document));
            await run(() =>
            {
                var club = account.Account.Data.Club;
                Equal(11, club.ManagerMods[PlayedManager].Contract);
                Equal(12, club.ManagerMods[OtherManager].Contract);
                Check(Enumerable.Range(0, 11).All(i => club.PlayerMods[FirstPlayer + i].Contract == 9), "The starting XI did not consume exactly one contract each.");
                Equal(1, account.Account.Data.Profile.Wins);
            });
        }));

        test("Only explicitly played bound substitutes consume match state", () => Fixture(account, run, async () =>
        {
            await run(() => AddSquads(account));
            long match = await BeginReady(http);
            var document = new
            {
                matchId = match, endReason = "WIN", myMatchStats = new { goals = 1 },
                items = new object[]
                {
                    // A nested array before fitness used to terminate the
                    // regular-expression parser before the actual report.
                    new { id = FirstPlayer, unrelated = new { rows = new[] { new { value = 7 } } }, fitness = 99 },
                    new { id = PlayedSeconds, unrelated = new { rows = new[] { new { value = 8 } } }, secondsPlayed = 900, fitness = 88, yellowCards = 1, injuryType = "hamstring", injuryGames = 3 },
                    new { id = PlayedMinutes, minutesPlayed = 15, fitness = 0, redCards = 1 },
                    new { id = UnusedBench, fitness = 12, yellowCards = 2, redCards = 1, injuryType = "hamstring", injuryGames = 7 }
                }
            };
            string receipt = await Finish(http, document);
            await Verify();
            Equal(receipt, await Finish(http, document));
            await Verify();
            using var reread = await ReadItems(http, FirstPlayer, PlayedSeconds, PlayedMinutes, UnusedBench);
            var rows = reread.RootElement.GetProperty("itemData").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt64());
            Equal(99, rows[FirstPlayer].GetProperty("fitness").GetInt32());
            Equal(88, rows[PlayedSeconds].GetProperty("fitness").GetInt32());
            Equal(0, rows[PlayedMinutes].GetProperty("fitness").GetInt32());
            Equal(1, rows[PlayedMinutes].GetProperty("suspension").GetInt32());
            Equal(90, rows[UnusedBench].GetProperty("fitness").GetInt32());

            Task Verify() => run(() =>
            {
                var mods = account.Account.Data.Club.PlayerMods;
                Check(Enumerable.Range(0, 11).All(i => mods[FirstPlayer + i].Contract == 9), "Starting XI contracts were not settled once.");
                foreach (long id in new[] { PlayedSeconds, PlayedMinutes })
                {
                    Equal(9, mods[id].Contract); Equal(0, mods[id].TrainingFlag);
                    Check(mods[id].AttrBoost.All(boost => boost == 0), "A played substitute retained its one-match training boost.");
                }
                Equal(88, mods[PlayedSeconds].Fitness); Equal(1, mods[PlayedSeconds].YellowCards);
                Equal("hamstring", mods[PlayedSeconds].Injury); Equal(3, mods[PlayedSeconds].InjuryGames);
                Equal(0, mods[PlayedMinutes].Fitness); Equal(1, mods[PlayedMinutes].Suspension);
                var unused = mods[UnusedBench];
                Equal(10, unused.Contract); Equal(90, unused.Fitness); Equal(321, unused.TrainingFlag);
                Check(unused.AttrBoost.All(boost => boost == 3), "An unused bench report consumed its training boost.");
                Equal(0, unused.Suspension); Equal(0, unused.YellowCards); Equal(0, unused.InjuryGames);
            });
        }));

        test("An outside-player match report rejects before any wallet or card mutation", () => Fixture(account, run, async () =>
        {
            await run(() => AddSquads(account));
            long match = await BeginReady(http);
            using var rejected = await http.PostAsJsonAsync(Root + "/match/end", new
            {
                matchId = match, endReason = "WIN", myMatchStats = new { goals = 2 },
                items = new[] { new { id = OutsidePlayer, secondsPlayed = 5400, fitness = 0, redCards = 1 } }
            });
            Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            await run(() =>
            {
                var data = account.Account.Data;
                Equal(1000L, data.Profile.Coins); Equal(0, data.Profile.Wins);
                Check(data.Match is { Settled: false }, "Rejected report finalized the match.");
                Equal(12, data.Club.ManagerMods[PlayedManager].Contract);
                Check(data.Club.PlayerMods.Values.All(mod => mod.Contract == 10 && mod.Fitness == 90 && mod.Suspension == 0), "Rejected report partially applied card changes.");
            });
            await Finish(http, new { matchId = match, endReason = "WIN", myMatchStats = new { goals = 1 }, items = new[] { new { id = FirstPlayer, fitness = 95 } } });
            using var outsider = await ReadItems(http, OutsidePlayer);
            var item = outsider.RootElement.GetProperty("itemData")[0];
            Equal(10, item.GetProperty("contract").GetInt32()); Equal(90, item.GetProperty("fitness").GetInt32()); Equal(0, item.GetProperty("suspension").GetInt32());
            await run(() =>
            {
                Equal(1, account.Account.Data.Profile.Wins);
                Equal(11, account.Account.Data.Club.ManagerMods[PlayedManager].Contract);
            });
        }));
    }

    private static void AddSquads(PlayerSession account)
    {
        var data = account.Account.Data;
        data.Profile.Coins = 1000;
        var club = data.Club;
        var players = RealPlayers.All.Take(15).ToArray();
        var played = new Squad { Id = 0, Name = "Played squad", ManagerId = PlayedManager };
        for (int i = 0; i < players.Length; i++)
        {
            long id = FirstPlayer + i;
            club.Inventory.Add(new ClubItem(id, players[i], 7));
            club.PlayerMods[id] = new PlayerMod { Contract = 10, Fitness = 90, TrainingFlag = 321, AttrBoost = Enumerable.Repeat(3, 6).ToArray() };
            if (i < 14) played.Slots[i] = id;
        }
        club.Squads.Add(played);
        club.Squads.Add(new Squad { Id = 1, Name = "Other squad", ManagerId = OtherManager, Slots = new() { [0] = FirstPlayer } });
        club.ActiveSquadId = 0;
        club.Managers.AddRange(Managers.All.Take(2));
        club.ManagerItemIds.AddRange(new[] { PlayedManager, OtherManager });
        club.ManagerMods[PlayedManager] = new ManagerMod { Contract = 12 };
        club.ManagerMods[OtherManager] = new ManagerMod { Contract = 12 };
        PlayerContext.MarkDirty();
    }

    private static async Task<long> BeginReady(HttpClient http)
    {
        using var created = await http.PostAsJsonAsync(Root + "/match", new { squadId = 0, type = "offline" });
        created.EnsureSuccessStatusCode();
        using var match = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        long id = match.RootElement.GetProperty("matchId").GetInt64();
        using var ready = await http.PutAsJsonAsync(Root + "/match", new { matchId = id, items = Enumerable.Range(0, 11).Select(i => new { id = FirstPlayer + i }).ToArray() });
        ready.EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<string> Finish(HttpClient http, object document)
    {
        using var response = await http.PostAsJsonAsync(Root + "/match/end", document);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonDocument> ReadItems(HttpClient http, params long[] ids) =>
        JsonDocument.Parse(await http.GetStringAsync(Root + "/item?idList=" + string.Join(',', ids)));

    private static async Task Fixture(PlayerSession account, Func<Action, Task> run, Func<Task> action)
    {
        PlayerState original = null!;
        await run(() =>
        {
            original = account.Account.Data;
            var profile = original.Profile;
            account.Account.Data = new PlayerState();
            var next = account.Account.Data.Profile;
            next.NucleusId = account.Account.Id; next.PersonaName = account.Account.Name; next.Club.Established = true;
            next.RankedCoinsApplied = profile.RankedCoinsApplied; next.RankedRevisionApplied = profile.RankedRevisionApplied;
            next.RankedWinsApplied = profile.RankedWinsApplied; next.RankedDrawsApplied = profile.RankedDrawsApplied;
            next.RankedLossesApplied = profile.RankedLossesApplied;
            PlayerContext.MarkDirty();
        });
        try { await action(); }
        finally { await run(() => { account.Account.Data = original; PlayerContext.MarkDirty(); }); }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
