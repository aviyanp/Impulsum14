using System.Net.Http.Json;
using System.Text.Json;
using Impulsum14;

// Register beside FunctionalFlowTests after the shared native HTTP fixture starts:
// ConsumablePolishChecks.Register(Test, http, a, action => Run(a, action));
internal static class ConsumablePolishChecks
{
    private const string Root = "/fut/rs4/ut/game/fifa14";
    private const long FirstPlayer = 850_930_001;
    private const long SecondPlayer = 850_930_002;
    private const long FirstCopy = 970_930_001;
    private const long SelectedCopy = 970_930_002;

    internal static void Register(Action<string, Func<Task>> test, HttpClient http,
        PlayerSession account, Func<Action, Task> run)
    {
        test("Repeated native apply targets cannot multiply one contract card", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                AddPlayer(account, FirstPlayer, RealPlayers.All.First(p => p.Rating <= 64));
                AddConsumable(account, 5_001_001, FirstCopy);
                AddConsumable(account, 5_001_001, SelectedCopy);
            });
            using var applied = await Apply(http, SelectedCopy, FirstPlayer, FirstPlayer, FirstPlayer);
            applied.EnsureSuccessStatusCode();
            using var response = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
            Check(response.RootElement.GetProperty("success").GetBoolean(), "A valid contract was not applied.");
            Check(response.RootElement.GetProperty("itemData").GetArrayLength() == 1, "Duplicate target rows escaped into the applied response.");
            using var player = await ReadItem(http, FirstPlayer);
            Check(player.RootElement.GetProperty("itemData")[0].GetProperty("contract").GetInt32() == 15,
                "One bronze contract did not add exactly eight games once.");
            await run(() =>
            {
                var copies = account.Account.Data.Club.Consumables;
                Check(copies.Count == 1 && copies[0].ItemId == FirstCopy,
                    "Applying a selected copy consumed a different copy of the same resource.");
            });
            Check((await PendingIds(http)).SetEquals(new[] { FirstCopy }),
                "The consumed copy and unassigned recovery receipts disagree.");
        }));

        test("One player consumable cannot apply to two different players", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                var player = RealPlayers.All.First(p => p.Rating <= 64);
                AddPlayer(account, FirstPlayer, player);
                AddPlayer(account, SecondPlayer, player);
                AddConsumable(account, 5_001_001, SelectedCopy);
            });
            using var applied = await Apply(http, SelectedCopy, FirstPlayer, SecondPlayer);
            await CheckRejected(applied);
            using var items = await ReadItem(http, FirstPlayer, SecondPlayer);
            Check(items.RootElement.GetProperty("itemData").EnumerateArray().All(x => x.GetProperty("contract").GetInt32() == 7),
                "A rejected multi-player application partially changed contracts.");
            await RequireOwned(account, run, SelectedCopy);
            Check((await PendingIds(http)).Contains(SelectedCopy), "Rejected application lost its recovery receipt.");
        }));

        test("An unknown apply target preserves its consumable and recovery receipt", () => Fixture(account, run, async () =>
        {
            await run(() => AddConsumable(account, 5_001_001, SelectedCopy));
            using var applied = await Apply(http, SelectedCopy, FirstPlayer);
            await CheckRejected(applied);
            await RequireOwned(account, run, SelectedCopy);
            Check((await PendingIds(http)).Contains(SelectedCopy), "An invalid apply target removed an unassigned item.");
        }));

        test("Fitness already at its cap does not spend a card or lose recovery", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                AddPlayer(account, FirstPlayer, RealPlayers.All.First());
                account.Account.Data.Club.PlayerMods[FirstPlayer] = new PlayerMod { Fitness = 99 };
                AddConsumable(account, 5_002_001, SelectedCopy);
            });
            using var applied = await Apply(http, SelectedCopy, FirstPlayer);
            await CheckRejected(applied);
            await RequireOwned(account, run, SelectedCopy);
            Check((await PendingIds(http)).Contains(SelectedCopy), "A no-effect application lost an unassigned card.");
            using var player = await ReadItem(http, FirstPlayer);
            Check(player.RootElement.GetProperty("itemData")[0].GetProperty("fitness").GetInt32() == 99,
                "A capped player's fitness changed.");
        }));

        test("Position cards require the current source position and persist their destination", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                AddPlayer(account, FirstPlayer, RealPlayers.All.First(p => p.Position == "CM"));
                AddPlayer(account, SecondPlayer, RealPlayers.All.First(p => p.Position == "GK"));
                account.Account.Data.Club.PlayerMods[FirstPlayer] = new PlayerMod { Position = "CDM" };
                AddConsumable(account, 5_003_071, SelectedCopy); // CM > CAM
            });
            using (var wrongRole = await Apply(http, SelectedCopy, SecondPlayer)) await CheckRejected(wrongRole);
            using (var wrongSource = await Apply(http, SelectedCopy, FirstPlayer)) await CheckRejected(wrongSource);
            await RequireOwned(account, run, SelectedCopy);
            using (var unchanged = await ReadItem(http, FirstPlayer))
                Check(unchanged.RootElement.GetProperty("itemData")[0].GetProperty("preferredPosition").GetString() == "CDM",
                    "A position card ignored an already converted source position.");
            await run(() =>
            {
                account.Account.Data.Club.PlayerMods[FirstPlayer].Position = "CM";
                PlayerContext.MarkDirty();
            });
            using var applied = await Apply(http, SelectedCopy, FirstPlayer);
            applied.EnsureSuccessStatusCode();
            using var player = await ReadItem(http, FirstPlayer);
            Check(player.RootElement.GetProperty("itemData")[0].GetProperty("preferredPosition").GetString() == "CAM",
                "A valid position conversion did not survive a separate native item read.");
            Check(!(await PendingIds(http)).Contains(SelectedCopy), "A used position card remained unassigned.");
        }));

        test("Squad fitness restores the active squad while leaving other owned players unchanged", () => Fixture(account, run, async () =>
        {
            long[] squadPlayers = Enumerable.Range(0, 4).Select(i => FirstPlayer + i).ToArray();
            const long outsidePlayer = FirstPlayer + 10;
            await run(() =>
            {
                var club = account.Account.Data.Club;
                var players = RealPlayers.All.Take(5).ToArray();
                int[] slots = { 0, 1, 11, 20 }; // Starting XI, bench and reserves.
                var squad = new Squad { Id = 0, Name = "Fitness fixture" };
                for (int i = 0; i < squadPlayers.Length; i++)
                {
                    AddPlayer(account, squadPlayers[i], players[i]);
                    club.PlayerMods[squadPlayers[i]] = new PlayerMod { Fitness = 70 };
                    squad.Slots[slots[i]] = squadPlayers[i];
                }
                AddPlayer(account, outsidePlayer, players[4]);
                club.PlayerMods[outsidePlayer] = new PlayerMod { Fitness = 70 };
                club.Squads.Add(squad);
                AddConsumable(account, 5_002_004, SelectedCopy); // Squad +10
            });
            using var applied = await Apply(http, SelectedCopy, squadPlayers[0]);
            applied.EnsureSuccessStatusCode();
            using var items = await ReadItem(http, squadPlayers.Append(outsidePlayer).ToArray());
            var fitness = items.RootElement.GetProperty("itemData").EnumerateArray()
                .ToDictionary(x => x.GetProperty("id").GetInt64(), x => x.GetProperty("fitness").GetInt32());
            Check(squadPlayers.All(id => fitness.GetValueOrDefault(id) == 80) && fitness.GetValueOrDefault(outsidePlayer) == 70,
                "Squad fitness missed a member or changed a player outside the active squad.");
            Check(!(await PendingIds(http)).Contains(SelectedCopy), "The spent squad fitness card remained unassigned.");
        }));

        test("Goalkeeper training cannot modify an outfield player", () => Fixture(account, run, async () =>
        {
            await run(() =>
            {
                AddPlayer(account, FirstPlayer, RealPlayers.All.First(p => p.Position != "GK"));
                AddConsumable(account, 5_003_001, SelectedCopy);
            });
            using var applied = await Apply(http, SelectedCopy, FirstPlayer);
            await CheckRejected(applied);
            await RequireOwned(account, run, SelectedCopy);
            await run(() => Check(!account.Account.Data.Club.PlayerMods.TryGetValue(FirstPlayer, out var mod) ||
                (mod.TrainingFlag == 0 && mod.AttrBoost.All(boost => boost == 0)), "Rejected goalkeeper training modified an outfield player."));
            Check((await PendingIds(http)).Contains(SelectedCopy), "Rejected goalkeeper training lost its recovery receipt.");
        }));

        test("Manager contracts reject players and survive a separate manager item read", () => Fixture(account, run, async () =>
        {
            const long managerId = 970_930_100;
            var manager = Managers.All.First();
            int expected = 7 + (manager.Rating <= 64 ? 8 : manager.Rating <= 74 ? 2 : 1);
            await run(() =>
            {
                AddPlayer(account, FirstPlayer, RealPlayers.All.First());
                account.Account.Data.Club.Managers.Add(manager);
                account.Account.Data.Club.ManagerItemIds.Add(managerId);
                AddConsumable(account, 5_001_007, SelectedCopy);
            });
            using (var wrongTarget = await Apply(http, SelectedCopy, FirstPlayer)) await CheckRejected(wrongTarget);
            await RequireOwned(account, run, SelectedCopy);
            using var applied = await Apply(http, SelectedCopy, managerId);
            applied.EnsureSuccessStatusCode();
            using var items = await ReadItem(http, managerId, FirstPlayer);
            var rows = items.RootElement.GetProperty("itemData").EnumerateArray()
                .ToDictionary(x => x.GetProperty("id").GetInt64(), x => x.GetProperty("contract").GetInt32());
            Check(rows.GetValueOrDefault(managerId) == expected && rows.GetValueOrDefault(FirstPlayer) == 7,
                "Manager contract was lost on reread or applied to the wrong item type.");
            Check(!(await PendingIds(http)).Contains(SelectedCopy), "The spent manager contract remained unassigned.");
        }));
    }

    private static Task<HttpResponseMessage> Apply(HttpClient http, long itemId, params long[] targetIds) =>
        http.PutAsJsonAsync(Root + "/item/" + itemId, new { apply = targetIds.Select(id => new { id }).ToArray() });

    private static async Task<JsonDocument> ReadItem(HttpClient http, params long[] ids) =>
        JsonDocument.Parse(await http.GetStringAsync(Root + "/item?idList=" + string.Join(',', ids)));

    private static async Task<HashSet<long>> PendingIds(HttpClient http)
    {
        using var pending = JsonDocument.Parse(await http.GetStringAsync(Root + "/purchased/items"));
        return pending.RootElement.GetProperty("itemData").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToHashSet();
    }

    private static async Task CheckRejected(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Check((int)response.StatusCode is 400 or 409 or 422, "Invalid consumable application returned an unexpected HTTP error.");
            return;
        }
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(body.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean(),
            "An invalid or ineffective consumable application reported success.");
    }

    private static Task RequireOwned(PlayerSession account, Func<Action, Task> run, long itemId) =>
        run(() => Check(account.Account.Data.Club.Consumables.Any(x => x.ItemId == itemId), "A rejected application consumed its card."));

    private static void AddPlayer(PlayerSession account, long id, RealPlayer player)
    {
        account.Account.Data.Club.Inventory.Add(new ClubItem(id, player, 6));
        PlayerContext.MarkDirty();
    }

    private static void AddConsumable(PlayerSession account, long resourceId, long itemId)
    {
        var card = ConsumableItems.Catalog.First(c => c.ResourceId == resourceId) with { ItemId = itemId };
        account.Account.Data.Club.Consumables.Add(card);
        account.Account.Data.PendingPackItems.Add((itemId, ConsumableItems.BuildJson(card, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        PlayerContext.MarkDirty();
    }

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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
