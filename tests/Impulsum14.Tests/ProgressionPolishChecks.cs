using System.Collections.Specialized;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Impulsum14;
using Microsoft.AspNetCore.Http;

internal static class ProgressionPolishChecks
{
    internal static void Register(Action<string, Func<Task>> add, PlayerRepository repository, PlayerSession session)
    {
        Task Run(Action check) => repository.RunAsync(session, () =>
        {
            var account = PlayerContext.Current;
            var original = account.Data;
            var previousRanked = RankedService.Current;
            account.Data = new PlayerState();
            account.Data.Profile.NucleusId = account.Id;
            account.Data.Profile.PersonaName = account.Name;
            RankedService.Current = null;
            try { check(); }
            finally
            {
                account.Data = original;
                RankedService.Current = previousRanked;
                PlayerContext.MarkDirty();
            }
            return Task.FromResult(0);
        });

        add("MatchReady freezes a bound squad and accepts a reordered XI retry", () => Run(() =>
        {
            var data = PlayerContext.Current.Data;
            long[] ids = Enumerable.Range(0, 12).Select(i => 850_930_000L + i).ToArray();
            var players = RealPlayers.All.Take(12).ToArray();
            for (int i = 0; i < ids.Length; i++) data.Club.Inventory.Add(new ClubItem(ids[i], players[i], 7));
            data.Club.Squads.Add(new Squad { Id = 0, ManagerId = 970_930_001, Slots = ids.Take(11).Select((id, index) => (id, index)).ToDictionary(p => p.index, p => p.id) });

            long nextMatchId = data.NextMatchId;
            Throws<BadHttpRequestException>(() => MatchService.Begin("{\"squadId\":99}"));
            Check(data.Match is null && data.NextMatchId == nextMatchId, "An unknown squad created a match or consumed its identity.");
            var match = MatchService.Begin("{\"squadId\":0}");
            Equal(970_930_001L, match.ManagerId);
            data.Club.Squads[0].ManagerId = 970_930_002;
            Throws<BadHttpRequestException>(() => MatchService.Ready(JsonSerializer.Serialize(new { items = new[] { new { id = ids[11] } } })));
            Check(!match.Ready, "A player outside the selected squad was bound at Ready.");
            var unavailable = data.Club.Inventory[0];
            data.Club.Inventory.RemoveAt(0);
            Throws<BadHttpRequestException>(() => MatchService.Ready("{}"));
            Check(!match.Ready, "An implicit XI accepted a player removed after kickoff creation.");
            data.Club.Inventory.Insert(0, unavailable);
            MatchService.Ready(JsonSerializer.Serialize(new { items = ids.Take(11).Select(id => new { id }).ToArray() }));
            MatchService.Ready(JsonSerializer.Serialize(new { items = ids.Take(11).Reverse().Select(id => new { id }).ToArray() }));
            Check(match.StartingXiIds.SequenceEqual(ids.Take(11)), "A harmless reordered retry changed the frozen starters.");
            Equal(970_930_001L, match.ManagerId);
            Throws<BadHttpRequestException>(() => MatchService.Ready(JsonSerializer.Serialize(new { items = ids.Take(10).Select(id => new { id }).ToArray() })));
            MatchService.End("{\"endReason\":\"WIN\"}", _ => { });
            Throws<BadHttpRequestException>(() => MatchService.Ready("{}"));
        }));

        add("Server-bound ranked kickoff overrides a stale offline cup browsing hint", () => Run(() => WithRanked(ranked =>
        {
            var data = PlayerContext.Current.Data;
            data.LastCompetition = "tournament"; data.ActiveTournamentId = 1;
            var binding = ranked.BindMatch(PlayerContext.Current.Id, PlayerContext.Current.Id + 1);
            Throws<BadHttpRequestException>(() => MatchService.Begin("{\"tournamentId\":1}"));
            Check(data.Match is null, "An explicit cup request replaced the paired online game.");
            var match = MatchService.Begin("{}");
            Equal(binding.Id, match.RankedMatchId); Equal("ranked", match.Competition); Equal(0, match.TournamentId);
            Equal(0, data.Profile.SavedTournaments.Count);
        })));

        add("Late ranked concessions and cancellations refresh the native terminal result", () => Run(() => WithRanked(ranked =>
        {
            long self = PlayerContext.Current.Id, opponent = self + 1;
            var binding = ranked.BindMatch(self, opponent);
            MatchService.Begin("{}");
            const string submittedLoss = "{\"endReason\":\"LOSS\",\"myMatchStats\":{\"goals\":0},\"opponentMatchStats\":{\"goals\":1}}";
            using (var pending = JsonDocument.Parse(MatchService.End(submittedLoss, _ => { })))
                Check(pending.RootElement.GetProperty("settlementPending").GetBoolean(), "First report was incorrectly treated as corroborated.");
            ranked.Report(opponent, binding.Id, forfeit: true);
            string final = MatchService.EndAsync(submittedLoss, _ => throw new Exception("A retry applied match consequences again.")).GetAwaiter().GetResult();
            using (var receipt = JsonDocument.Parse(final))
            {
                Equal("WIN", receipt.RootElement.GetProperty("endReason").GetString());
                Equal(ServerOptions.Current.Ranked.WinCoins, receipt.RootElement.GetProperty("matchCoins").GetInt32());
                Check(receipt.RootElement.GetProperty("eloChange").GetInt32() > 0, "The late winner received no authoritative Elo result.");
            }
            Equal(1, PlayerContext.Current.Data.Profile.Wins); Equal(0, PlayerContext.Current.Data.Profile.Losses);
            Equal(final, MatchService.EndAsync(submittedLoss, _ => throw new Exception("A finalized retry applied consequences.")).GetAwaiter().GetResult());

            var cancelled = ranked.BindMatch(self, opponent);
            MatchService.Begin("{}");
            MatchService.End(submittedLoss, _ => { });
            ranked.AbandonMatch(cancelled.Id, allowReported: true);
            using var noResult = JsonDocument.Parse(MatchService.EndAsync(submittedLoss, _ => throw new Exception("A cancellation retry applied consequences.")).GetAwaiter().GetResult());
            Equal("NO_CONTEST", noResult.RootElement.GetProperty("endReason").GetString());
            Equal(0, noResult.RootElement.GetProperty("totalAward").GetInt32());
            Check(!noResult.RootElement.TryGetProperty("eloChange", out _), "A cancelled game advertised a rating result.");
            Equal(1, PlayerContext.Current.Data.Profile.OnlineSeason.GamesPlayed);
        })));

        add("Season fixture rewards follow each mode's configured match economy", () => Run(() =>
        {
            var options = ServerOptions.Current;
            int offline = options.MatchWinCoins, online = options.Ranked.WinCoins;
            try
            {
                options.MatchWinCoins = 712; options.Ranked.WinCoins = 834;
                foreach (bool isOnline in new[] { false, true })
                {
                    using var catalogue = JsonDocument.Parse(Seasons.ListJson(new NameValueCollection { ["type"] = isOnline ? "online" : "offline" }));
                    foreach (var season in catalogue.RootElement.GetProperty("seasons").EnumerateArray())
                    foreach (var fixture in season.GetProperty("matches").EnumerateArray())
                        Equal(isOnline ? 834 : 712, fixture.GetProperty("coins").GetInt32());
                }
            }
            finally { options.MatchWinCoins = offline; options.Ranked.WinCoins = online; }
        }));

        add("Interleaved online and offline season history follows completion time", () => Run(() =>
        {
            var history = FutProfileStore.Get().SeasonHistory;
            history.Add(new(4, 8, false, 30, 10, 0, 0, "CHAMPIONSHIP", 10, 200));
            history.Add(new(5, 7, false, 30, 10, 0, 0, "CHAMPIONSHIP", 20, 300));
            history.Add(new(12, 6, true, 10, 0, 10, 0, "MAINTENANCE", 30, 100));
            history.Add(new(6, 6, false, 30, 10, 0, 0, "CHAMPIONSHIP", 40, 300));
            string before = JsonSerializer.Serialize(history);
            using var all = JsonDocument.Parse(Seasons.HistoryJson());
            using var online = JsonDocument.Parse(Seasons.HistoryJson(true));
            using var offline = JsonDocument.Parse(Seasons.HistoryJson(false));
            Check(all.RootElement.GetProperty("seasons").EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).SequenceEqual(new[] { 6, 5, 4, 12 }), "Mixed history followed merge order instead of completion time.");
            Equal(4, all.RootElement.GetProperty("totalResults").GetInt32());
            Equal(1, online.RootElement.GetProperty("totalResults").GetInt32());
            Equal(12, online.RootElement.GetProperty("seasons")[0].GetProperty("id").GetInt32());
            Equal(3, offline.RootElement.GetProperty("totalResults").GetInt32());
            Equal(before, JsonSerializer.Serialize(history));
        }));

        add("Bounded competition hashing preserves persisted fingerprints and Unicode fallback bytes", () =>
        {
            var pairs = new[]
            {
                ("", ""), ("AQID BA==", "AAAA AA=="), ("not-base64", "another:opaque:buffer"),
                (Convert.ToBase64String(new byte[CompetitionSave.MaxBlobLength / 2]), "AAAAAA=="),
                (new string('x', 1364) + "\ud83d\ude80tail", new string('y', 2729) + "\ud83d\ude80end"),
                (new string('x', 1364) + "\ud800tail", "\udfff" + new string('y', 4096) + "\ud800")
            };
            var culture = CultureInfo.CurrentCulture;
            try
            {
                foreach (var name in new[] { "en-US", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                    foreach (var (data, progress) in pairs)
                    {
                        string Canonical(string value)
                        {
                            try { return Convert.ToBase64String(Convert.FromBase64String(value)); }
                            catch (FormatException) { return value; }
                        }
                        string canonicalData = Canonical(data), canonicalProgress = Canonical(progress);
                        string oldInput = "7:" + canonicalData.Length + ":" + canonicalData + ":" + canonicalProgress;
                        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldInput)));
                        Equal(expected, CompetitionSave.Fingerprint(7, data, progress));
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = culture; }
            return Task.CompletedTask;
        });
    }

    private static void WithRanked(Action<RankedService> check)
    {
        var previous = RankedService.Current;
        string directory = Path.Combine(Path.GetTempPath(), "impulsum-progression-polish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var ranked = new RankedService(directory, ServerOptions.Current.Ranked);
            RankedService.Current = ranked;
            try { check(ranked); }
            finally { RankedService.Current = previous; }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
}
