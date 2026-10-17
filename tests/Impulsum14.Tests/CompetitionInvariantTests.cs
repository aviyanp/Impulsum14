using System.Collections.Specialized;
using System.IO.Compression;
using System.Text.Json;
using Impulsum14;

internal static class CompetitionInvariantTests
{
    internal static void Register(Action<string, Func<Task>> add, PlayerRepository repository, PlayerSession session)
    {
        Task Run(Action check) => repository.RunAsync(session, () =>
        {
            var state = PlayerContext.Current.Data;
            var original = state.Profile;
            int active = state.ActiveTournamentId;
            state.Profile = new FutProfile();
            try { check(); }
            finally { state.Profile = original; state.ActiveTournamentId = active; PlayerContext.MarkDirty(); }
            return Task.FromResult(0);
        });

        add("Season threshold boundaries, mode separation and ladder endpoints", () => Run(() =>
        {
            foreach (bool online in new[] { false, true })
            foreach (int division in Enumerable.Range(1, 10))
            {
                var rule = Seasons.Rules(division, online);
                var title = Seasons.Evaluate(division, rule.Title, online);
                Equal("CHAMPIONSHIP", title.Outcome); True(title.Title, "Title not counted");
                Equal(Math.Max(1, division - 1), title.NextDivision); Equal(rule.TitleCoins, title.Coins);
                True(!Seasons.Evaluate(division, rule.Title - 1, online).Title, "Title awarded below its threshold");
                if (division > 1)
                {
                    Equal("PROMOTION", Seasons.Evaluate(division, rule.Promotion, online).Outcome);
                    True(Seasons.Evaluate(division, rule.Promotion - 1, online).Outcome != "PROMOTION", "Promotion awarded early");
                }
                else
                {
                    Equal("MAINTENANCE", Seasons.Evaluate(division, rule.Hold, online).Outcome);
                    Equal(1, Seasons.Evaluate(division, rule.Title - 1, online).NextDivision);
                }
                if (division < 10)
                {
                    Equal("RELEGATION", Seasons.Evaluate(division, rule.Hold - 1, online).Outcome);
                    Equal(division, Seasons.Evaluate(division, rule.Hold, online).NextDivision);
                }
                else Equal("MAINTENANCE", Seasons.Evaluate(division, 0, online).Outcome);
                foreach (int points in Enumerable.Range(0, 31))
                {
                    var result = Seasons.Evaluate(division, points, online);
                    True(result.NextDivision is >= 1 and <= 10 && Math.Abs(result.NextDivision - division) <= 1, "Invalid division movement");
                    True(result.Coins >= 0, "Negative season award");
                }
            }
            Equal(23, Seasons.Rules(1, true).Title); Equal(28, Seasons.Rules(1, false).Title);
            Equal(2000, Seasons.Evaluate(10, 12, true).Coins); Equal(1900, Seasons.Evaluate(10, 12, false).Coins);
            Throws<InvalidDataException>(() => Seasons.Evaluate(0, 0, false));
            Throws<InvalidDataException>(() => Seasons.Evaluate(11, 0, false));
            Throws<InvalidDataException>(() => Seasons.Evaluate(1, -1, true));
            Throws<InvalidDataException>(() => Seasons.Evaluate(1, 31, true));
        }));

        add("Every feasible ten-game record settles only on game ten", () => Run(() =>
        {
            foreach (bool online in new[] { false, true })
            foreach (int division in Enumerable.Range(1, 10))
            for (int wins = 0; wins <= 10; wins++)
            for (int draws = 0; draws <= 10 - wins; draws++)
            {
                var profile = new FutProfile { OfflineDivision = division, OnlineDivision = division };
                var season = online ? profile.OnlineSeason : profile.Season;
                season.SeasonId = 42; season.TitlesWon = 3; season.Promotions = 4; season.Relegations = 5;
                int losses = 10 - wins - draws;
                string[] results = Enumerable.Repeat("WIN", wins).Concat(Enumerable.Repeat("DRAW", draws)).Concat(Enumerable.Repeat("LOSS", losses)).ToArray();
                for (int i = 0; i < 9; i++)
                {
                    Equal(0, Seasons.Settle(profile, results[i], online, 42));
                    Equal(0, profile.SeasonHistory.Count);
                    Equal(division, online ? profile.OnlineDivision : profile.OfflineDivision);
                }
                var expected = Seasons.Evaluate(division, 3 * wins + draws, online);
                Equal(expected.Coins, Seasons.Settle(profile, results[9], online, 42));
                var next = online ? profile.OnlineSeason : profile.Season;
                Equal(43, next.SeasonId); Equal(0, next.GamesPlayed); Equal(0, next.Points);
                Equal(expected.NextDivision, online ? profile.OnlineDivision : profile.OfflineDivision);
                Equal(3 + (expected.Title ? 1 : 0), next.TitlesWon);
                Equal(4 + (expected.NextDivision < division ? 1 : 0), next.Promotions);
                Equal(5 + (expected.NextDivision > division ? 1 : 0), next.Relegations);
                Equal(0, online ? profile.Season.GamesPlayed : profile.OnlineSeason.GamesPlayed);
                var entry = profile.SeasonHistory.Single();
                Equal(42, entry.SeasonId); Equal(division, entry.Division); Equal(online, entry.Online);
                Equal(wins, entry.Won); Equal(draws, entry.Draw); Equal(losses, entry.Lost);
                Equal(3 * wins + draws, entry.Points); Equal(expected.Outcome, entry.Outcome); Equal(expected.Coins, entry.Coins);
                Equal(0L, profile.Coins); // Caller applies the returned award in its match transaction.
            }
        }));

        add("Season DNF, draws, history retention and reset preserve authoritative counters", () => Run(() =>
        {
            var profile = FutProfileStore.Get();
            profile.OfflineDivision = 9;
            foreach (string reason in new[] { "WIN", "win", "DRAW", "TIE", "DNF", "QUIT", "LOSS", "LOSS", "DRAW", "LOSS" }) Seasons.Settle(profile, reason, false);
            var entry = profile.SeasonHistory.Single();
            Equal(2, entry.Won); Equal(3, entry.Draw); Equal(5, entry.Lost); Equal(9, entry.Points);
            string before = JsonSerializer.Serialize(profile);
            Throws<InvalidDataException>(() => Seasons.Settle(profile, "invented", false)); Equal(before, JsonSerializer.Serialize(profile));
            profile.Season.TitlesWon = 7; profile.Season.Promotions = 6; profile.Season.Relegations = 5; profile.Season.Coins = 100;
            int id = profile.Season.SeasonId, division = profile.OfflineDivision;
            Seasons.Reset(profile);
            Equal(id + 1, profile.Season.SeasonId); Equal(division, profile.OfflineDivision);
            Equal(7, profile.Season.TitlesWon); Equal(6, profile.Season.Promotions); Equal(5, profile.Season.Relegations); Equal(100, profile.Season.Coins);
            Equal(1, profile.SeasonHistory.Count);
            Throws<InvalidDataException>(() => Seasons.Reset(profile, true));
            for (int i = 0; i < 105; i++) for (int match = 0; match < 10; match++) Seasons.Settle(profile, "LOSS", false);
            Equal(Seasons.HistoryLimit, profile.SeasonHistory.Count);
            True(profile.SeasonHistory.Zip(profile.SeasonHistory.Skip(1)).All(p => p.First.SeasonId < p.Second.SeasonId), "History generation order lost");
            using var history = JsonDocument.Parse(Seasons.HistoryJson(false));
            Equal(Seasons.HistoryLimit, history.RootElement.GetProperty("totalResults").GetInt32());
            var newest = history.RootElement.GetProperty("seasons")[0];
            Equal(profile.SeasonHistory[^1].SeasonId, newest.GetProperty("id").GetInt32());
            Equal("OFFLINE", newest.GetProperty("type").GetString()); Equal(10, newest.GetProperty("numMatches").GetInt32());
            using var onlineHistory = JsonDocument.Parse(Seasons.HistoryJson(true)); Equal(0, onlineHistory.RootElement.GetProperty("totalResults").GetInt32());
        }));

        add("Native season catalogues resolve exact positions and server reward rules", () => Run(() =>
        {
            var profile = FutProfileStore.Get();
            foreach (bool online in new[] { false, true })
            foreach (int division in new[] { 10, 9, 1 })
            foreach (string requested in new[] { "11", "1,11,9,10,1", "8,7", "bad,0,12", "" })
            {
                if (online) { profile.OnlineDivision = division; profile.OnlineSeason = new(); }
                else { profile.OfflineDivision = division; profile.Season = new(); }
                var query = new NameValueCollection { ["divisionList"] = requested, ["type"] = online ? "online" : "offline" };
                using var doc = JsonDocument.Parse(Seasons.ListJson(query));
                var rows = doc.RootElement.GetProperty("seasons");
                var selected = online ? profile.OnlineSeason : profile.Season;
                Equal(selected.SeasonId, selected.CatalogSeasonId);
                Equal(division, RealDivision(rows[selected.CatalogId - 1].GetProperty("divisionId").GetInt32()));
                for (int i = 0; i < rows.GetArrayLength(); i++)
                {
                    var row = rows[i]; int rowDivision = RealDivision(row.GetProperty("divisionId").GetInt32());
                    Equal(i + 1, row.GetProperty("id").GetInt32()); Equal(10, row.GetProperty("numMatches").GetInt32());
                    Equal(online ? "ONLINE" : "OFFLINE", row.GetProperty("type").GetString());
                    var matches = row.GetProperty("matches"); Equal(10, matches.GetArrayLength());
                    for (int m = 0; m < 10; m++)
                    {
                        Equal(m, matches[m].GetProperty("roundId").GetInt32());
                        True(Seasons.NativeTeamIds.Contains(matches[m].GetProperty("teamId").GetInt32()), "Season opponent lacks native assets or players");
                    }
                    var prizes = row.GetProperty("prizeSet");
                    Equal(rowDivision == 1 ? 3 : 4, prizes.GetArrayLength());
                    var championship = prizes.EnumerateArray().Single(p => p.GetProperty("prizeLevel").GetString() == "CHAMPIONSHIP");
                    Equal(Seasons.Rules(rowDivision, online).Title, championship.GetProperty("thresholdPoint").GetInt32());
                    var award = championship.GetProperty("awardMappings")[0].GetProperty("awards")[0];
                    Equal(1, award.GetProperty("awardType").GetInt32()); True(!award.TryGetProperty("assetId", out _), "Coin award asks for item zero");
                }
            }
            profile.OfflineDivision = 10; profile.Season = new();
            Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" });
            Seasons.CaptureSave(profile, "{\"round\":1,\"data\":\"AAAAAA==\"}");
            using var fresh = JsonDocument.Parse(Seasons.UserJson(profile));
            Equal(-1, fresh.RootElement.GetProperty("seasonId").GetInt32()); Equal(10, fresh.RootElement.GetProperty("divisionId").GetInt32());
            True(!fresh.RootElement.TryGetProperty("data", out _), "Kickoff stub exposed");
        }));

        add("Native season buffers, aliases and save validation cannot forge results", () => Run(() =>
        {
            var profile = FutProfileStore.Get(); profile.Coins = 123;
            Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" });
            Seasons.Settle(profile, "LOSS", false);
            string blob = SeasonBlob(1), progress = "AAAAAA==";
            Seasons.CaptureSave(profile, SeasonSave(2, blob, progress, forged: true), path: "/season/1/division/11/user");
            Equal(1, profile.Season.GamesPlayed); Equal(0, profile.Season.Points); Equal(10, profile.OfflineDivision);
            Equal(0, profile.SeasonHistory.Count); Equal(0, profile.Season.TitlesWon); Equal(123L, profile.Coins);
            string json = Seasons.UserJson(profile); AssertBuffers(json, "data");
            using var resume = JsonDocument.Parse(json);
            Equal(1, resume.RootElement.GetProperty("seasonId").GetInt32()); Equal(11, resume.RootElement.GetProperty("divisionId").GetInt32());
            Equal(blob, resume.RootElement.GetProperty("data").GetString());
            bool enabled = ServerOptions.Current.ResumeCompetitionBlobs;
            try
            {
                ServerOptions.Current.ResumeCompetitionBlobs = false;
                using var disabled = JsonDocument.Parse(Seasons.UserJson(profile)); Equal(-1, disabled.RootElement.GetProperty("seasonId").GetInt32());
            }
            finally { ServerOptions.Current.ResumeCompetitionBlobs = enabled; }
            string preserved = profile.Season.Data;
            Seasons.CaptureSave(profile, SeasonSave(3, SeasonBlob(2), progress)); Equal(preserved, profile.Season.Data);
            Seasons.CaptureSave(profile, SeasonSave(2, SeasonBlob(3), progress), path: "/season/7/division/11/user"); Equal(preserved, profile.Season.Data);
            Seasons.CaptureSave(profile, SeasonSave(2, SeasonBlob(4), progress), query: new NameValueCollection { ["divisionId"] = "1" }); Equal(preserved, profile.Season.Data);
            Seasons.CaptureSave(profile, SeasonSave(2, SeasonBlob(5), progress), query: new NameValueCollection { ["seasonId"] = "2" }); Equal(preserved, profile.Season.Data);
            Throws<InvalidDataException>(() => Seasons.CaptureSave(profile, "{\"round\":0}"));
            Throws<InvalidDataException>(() => Seasons.CaptureSave(profile, "{\"round\":12}"));
            Throws<InvalidDataException>(() => Seasons.CaptureSave(profile, "{\"round\":\"2\"}"));
            Throws<InvalidDataException>(() => Seasons.CaptureSave(profile, "{\"round\":2,\"data\":null}"));
            Throws<InvalidDataException>(() => Seasons.CaptureSave(profile, JsonSerializer.Serialize(new { round = 2, data = new string('A', CompetitionSave.MaxBlobLength + 1) })));
        }));

        add("Season rollover and snapshot reopen reject retired native saves", () => Run(() =>
        {
            var profile = FutProfileStore.Get();
            Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" });
            Seasons.Settle(profile, "LOSS", false);
            string retired = SeasonBlob(1), current = SeasonBlob(2);
            Seasons.CaptureSave(profile, SeasonSave(2, retired));
            for (int i = 0; i < 9; i++) Seasons.Settle(profile, "LOSS", false);
            Equal(2, profile.Season.SeasonId); Equal(10, profile.OfflineDivision);
            Seasons.CaptureSave(profile, SeasonSave(11, retired)); Equal("", profile.Season.Data);
            Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" });
            Seasons.Settle(profile, "LOSS", false);
            Seasons.CaptureSave(profile, SeasonSave(2, retired)); Equal("", profile.Season.Data);
            Seasons.CaptureSave(profile, SeasonSave(2, current)); Equal(current, profile.Season.Data);
            var snapshot = JsonSerializer.Deserialize<FutProfile>(JsonSerializer.Serialize(profile, PlayerRepository.JsonOptions), PlayerRepository.JsonOptions)!;
            PlayerContext.Current.Data.Profile = snapshot;
            AssertBuffers(Seasons.UserJson(snapshot), "data");
            Seasons.CaptureSave(snapshot, SeasonSave(2, retired)); Equal(current, snapshot.Season.Data);
            Throws<InvalidDataException>(() => Seasons.Settle(snapshot, "WIN", false, expectedSeasonId: 1)); Equal(1, snapshot.Season.GamesPlayed);
            Seasons.Reset(snapshot); Equal(3, snapshot.Season.SeasonId);
            Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" }); Seasons.Settle(snapshot, "LOSS", false);
            Seasons.CaptureSave(snapshot, SeasonSave(2, current)); Equal("", snapshot.Season.Data);
            // Legacy snapshots without explicit generation bindings fail closed.
            snapshot.Season.Data = SeasonBlob(3); snapshot.Season.SavedRound = 2; snapshot.Season.SavedSeasonId = 0;
            using var legacy = JsonDocument.Parse(Seasons.UserJson(snapshot)); Equal(-1, legacy.RootElement.GetProperty("seasonId").GetInt32());
        }));

        add("Native cup catalogue, per-cup trophies and opponent pools are coherent", () => Run(() =>
        {
            var profile = FutProfileStore.Get(); profile.TrophiesWon = 10;
            profile.TournamentWins[1] = 2; profile.TournamentWins[2] = 7;
            using var catalogue = JsonDocument.Parse(Tournaments.CatalogJson());
            var rows = catalogue.RootElement.GetProperty("tournament"); Equal(14, rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                int id = row.GetProperty("id").GetInt32(); Equal("knockout", row.GetProperty("treeType").GetString());
                Equal(id, row.GetProperty("aigroup").GetInt32()); Equal(16, row.GetProperty("numTeams").GetInt32()); Equal(4, row.GetProperty("numRounds").GetInt32());
                Equal(profile.TournamentWins.GetValueOrDefault(id), row.GetProperty("trophyUserCount").GetInt32());
                Equal(4, row.GetProperty("rounds").GetArrayLength());
                using var opponents = JsonDocument.Parse(Tournaments.TeamsJson(id));
                var teams = opponents.RootElement.GetProperty("teamId").EnumerateArray().Select(t => t.GetInt32()).ToArray();
                Equal(15, teams.Length); Equal(15, teams.Distinct().Count()); True(teams.All(Seasons.NativeTeamIds.Contains), "Cup opponent lacks native assets or players");
            }
            using var partial = JsonDocument.Parse(Tournaments.TeamsJson(1, 7)); Equal(7, partial.RootElement.GetProperty("teamId").GetArrayLength());
            using var empty = JsonDocument.Parse(Tournaments.TeamsJson(1, 0)); Equal(0, empty.RootElement.GetProperty("teamId").GetArrayLength());
            Throws<InvalidDataException>(() => Tournaments.TeamsJson(1, 16)); Throws<InvalidDataException>(() => Tournaments.TeamsJson(999));
            int original = Tournaments.GetTeamIds(1)[0]; var copy = Tournaments.GetTeamIds(1); copy[0] = -1; Equal(original, Tournaments.GetTeamIds(1)[0]);
        }));

        add("Cup saves cannot skip rounds or award trophies; final and DNF retire runs", () => Run(() =>
        {
            var profile = FutProfileStore.Get(); profile.Coins = 456;
            var first = Tournaments.BeginMatch(1); Equal(1L, first.RunId); Equal(1, first.Round);
            Equal("{\"tournamentId\":1}", Tournaments.SaveProgress(1, 1, 1, "AAAAAA==", 1, "AAAAAA=="));
            Equal("{\"tournamentId\":[]}", Tournaments.UserListJson());
            Equal("{\"tournamentId\":1}", Tournaments.SaveProgress(1, 4, 1, "AQIDBA==", 1, "AQIDBA=="));
            Equal(0, profile.SavedTournaments[1].CompletedRounds); Equal(0, profile.TrophiesWon); Equal(456L, profile.Coins);
            Throws<InvalidDataException>(() => Tournaments.SettleTournamentMatch(1, "DRAW", first.RunId, first.Round));
            Equal(0, profile.SavedTournaments[1].CompletedRounds);
            var result = Tournaments.SettleTournamentMatch(1, "WIN", first.RunId, first.Round); Equal((0, false), result);
            Equal("{\"tournamentId\":1}", Tournaments.UserTournamentJson(1));
            string bracket = TournamentBlob(1);
            AssertBuffers(Tournaments.SaveProgress(1, 2, 1, bracket, 1, "AQIDBA=="), "tournamentData");
            Throws<InvalidDataException>(() => Tournaments.SettleTournamentMatch(1, "WIN", first.RunId, first.Round));
            Equal(1, profile.SavedTournaments[1].CompletedRounds);
            var second = Tournaments.BeginMatch(1); Tournaments.SettleTournamentMatch(1, "WIN", second.RunId, second.Round);
            var third = Tournaments.BeginMatch(1); Tournaments.SettleTournamentMatch(1, "WIN", third.RunId, third.Round);
            var final = Tournaments.BeginMatch(1); Equal(4, final.Round);
            Equal((Tournaments.AwardCoins(1), true), Tournaments.SettleTournamentMatch(1, "WIN", final.RunId, final.Round));
            Equal(1, profile.TrophiesWon); Equal(1, profile.TournamentWins[1]); Equal(456L, profile.Coins);
            True(!profile.SavedTournaments[1].Active, "Completed cup left underway"); Equal(2L, profile.SavedTournaments[1].RunId);
            Equal("{\"tournamentId\":[]}", Tournaments.UserListJson());
            Throws<InvalidDataException>(() => Tournaments.SettleTournamentMatch(1, "WIN", final.RunId, final.Round));
            Equal(1, profile.TrophiesWon);
            Equal("{\"tournamentId\":1}", Tournaments.SaveProgress(1, 2, 1, bracket, 1, "AQIDBA=="));
            var next = Tournaments.BeginMatch(1); Tournaments.SettleTournamentMatch(1, "WIN", next.RunId, next.Round);
            Equal("{\"tournamentId\":1}", Tournaments.SaveProgress(1, 2, 1, bracket, 1, "AQIDBA=="));
            Tournaments.SaveProgress(1, 2, 1, TournamentBlob(2), 1, "AQIDBA==");
            var dnf = Tournaments.BeginMatch(1); Equal((0, false), Tournaments.SettleTournamentMatch(1, "DNF", dnf.RunId, dnf.Round));
            Equal(1, profile.TrophiesWon); Equal(1, profile.TournamentWins[1]); True(!profile.SavedTournaments[1].Active, "DNF cup left active");
            Equal("{\"tournamentId\":[]}", Tournaments.UserListJson());
            Throws<InvalidDataException>(() => Tournaments.BeginMatch(14)); Throws<InvalidDataException>(() => Tournaments.BeginMatch(999));
            Throws<InvalidDataException>(() => Tournaments.SaveProgress(1, 0, 1, "", 1, ""));
            Throws<InvalidDataException>(() => Tournaments.SaveProgress(1, 1, 0, "", 1, ""));
            Throws<InvalidDataException>(() => Tournaments.SaveProgress(1, "{\"round\":1.5}"));
            Throws<InvalidDataException>(() => Tournaments.SaveProgress(1, "{\"tournamentId\":2,\"round\":1}"));
        }));

        add("Native blob bounds, malformed gzip and retired fingerprints fail closed", () => Run(() =>
        {
            True(!CompetitionSave.Resumable(null!, null!, 2, true), "Null season blob accepted");
            True(!CompetitionSave.Resumable("not-base64", "", 2, true), "Invalid base64 accepted");
            True(!CompetitionSave.Resumable(SeasonBlob(1), "", 1, true), "Season round-one save accepted");
            True(!CompetitionSave.Resumable(SeasonBlob(1), "", 11, true), "Terminal season save accepted");
            True(!CompetitionSave.Resumable("AAAAAA==", "AAAAAA==", 1), "Cup kickoff stub accepted");
            True(!CompetitionSave.Resumable("AQIDBA==", "not-base64", 2), "Invalid cup progress accepted");
            True(!CompetitionSave.Resumable(TournamentBlob(1), "AQIDBA==", 5), "Terminal cup save accepted");
            True(CompetitionSave.Resumable(SeasonBlob(1, gzip: true), "", 2, true), "Bounded valid gzip shape rejected");
            True(!CompetitionSave.Resumable(SeasonBlob(1, gzip: true, payloadLength: CompetitionSave.MaxBlobLength + 1), "", 2, true), "Decompression bomb accepted");
            True(!CompetitionSave.Resumable(Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0x1f, 0x8b, 0, 1 }), "", 2, true), "Malformed gzip accepted");
            var hashes = new List<string>();
            for (int i = 0; i < CompetitionSave.RetiredHashLimit + 5; i++) CompetitionSave.Retire(hashes, i.ToString());
            Equal(CompetitionSave.RetiredHashLimit, hashes.Count); Equal("5", hashes[0]);
            CompetitionSave.Retire(hashes, hashes[^1]); Equal(CompetitionSave.RetiredHashLimit, hashes.Count);
            True(CompetitionSave.Fingerprint(2, "a", "bc") != CompetitionSave.Fingerprint(2, "ab", "c"), "Fingerprint pair delimiter collision");
            Equal(CompetitionSave.Fingerprint(2, "AQIDBA==", "AAAAAA=="), CompetitionSave.Fingerprint(2, "AQID BA==", "AAAA AA=="));
        }));
    }

    // Integrate these two methods into the existing repository-close/reopen check.
    // No second repository is opened while the live server owns its singleton.
    internal static void PrepareRestartState()
    {
        var profile = FutProfileStore.Get();
        profile.OfflineDivision = 10;
        profile.Season = new FutSeason { SeasonId = 901, GamesPlayed = 1, GamesLost = 1 };
        profile.Season.RetiredSaveHashes.Add(CompetitionSave.Fingerprint(2, SeasonBlob(90), "AAAAAA=="));
        Seasons.ListJson(new NameValueCollection { ["divisionList"] = "11" });
        Seasons.CaptureSave(profile, SeasonSave(2, SeasonBlob(91)));
        profile.TrophiesWon = Math.Max(10, profile.TrophiesWon);
        profile.SavedTournaments[1] = new SavedTournament
        {
            RunId = 901, SavedRunId = 901, Round = 2, CompletedRounds = 1, Active = true,
            TournamentData = TournamentBlob(91), ProgressData = "AQIDBA==",
            RetiredSaveHashes = new() { CompetitionSave.Fingerprint(2, TournamentBlob(90), "AQIDBA==") }
        };
        PlayerContext.MarkDirty();
    }

    internal static void AssertRestartState()
    {
        var profile = FutProfileStore.Get();
        Equal(901, profile.Season.SeasonId); Equal(1, profile.Season.GamesPlayed);
        Equal(SeasonBlob(91), profile.Season.Data); AssertBuffers(Seasons.UserJson(profile), "data");
        Seasons.CaptureSave(profile, SeasonSave(2, SeasonBlob(90))); Equal(SeasonBlob(91), profile.Season.Data);
        Equal(901L, profile.SavedTournaments[1].RunId); AssertBuffers(Tournaments.UserTournamentJson(1), "tournamentData");
        Tournaments.SaveProgress(1, 2, 1, TournamentBlob(90), 1, "AQIDBA=="); Equal(TournamentBlob(91), profile.SavedTournaments[1].TournamentData);
        Throws<InvalidDataException>(() => Seasons.Settle(profile, "WIN", false, expectedSeasonId: 900));
        Throws<InvalidDataException>(() => Tournaments.SettleTournamentMatch(1, "WIN", expectedRunId: 900, expectedRound: 2));
    }

    private static int RealDivision(int division) => division == 11 ? 10 : division;
    private static string SeasonSave(int round, string data, string progress = "AAAAAA==", bool forged = false) => JsonSerializer.Serialize(new
    {
        round, dataVersion = 1, data, progressDataVersion = 1, progressData = progress,
        points = forged ? 30 : 0, gamesPlayed = forged ? 10 : 0, titlesWon = forged ? 999 : 0, coins = forged ? 999999 : 0, division = forged ? 1 : 10
    });

    private static string SeasonBlob(byte marker, bool gzip = false, int payloadLength = 64)
    {
        var payload = new byte[payloadLength]; payload[8] = 18; payload[16] = marker;
        byte[] encoded = payload;
        if (gzip)
        {
            using var stream = new MemoryStream();
            using (var compressor = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) compressor.Write(payload);
            encoded = stream.ToArray();
        }
        return Convert.ToBase64String(BitConverter.GetBytes(payload.Length).Concat(encoded).ToArray());
    }
    private static string TournamentBlob(byte marker) => Convert.ToBase64String(new byte[] { 0, 0, 0, 0, marker, 2, 3, 4 });
    private static void AssertBuffers(string json, string dataKey)
    {
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        int data = Array.IndexOf(names, dataKey), dataVersion = Array.IndexOf(names, "dataVersion");
        True(data >= 0 && dataVersion > data, "Native data buffer/version ordering is unsafe");
        int progress = Array.IndexOf(names, "progressdata"), progressVersion = Array.IndexOf(names, "progressDataVersion");
        True(progress >= 0 && progressVersion > progress, "Native progress buffer/version ordering is unsafe");
        True(!names.Contains("progressData"), "Read progress casing does not match native key table");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
