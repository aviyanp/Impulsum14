using System.Collections.Specialized;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Impulsum14;

internal sealed record SeasonHistoryEntry(int SeasonId, int Division, bool Online, int Points, int Won, int Draw, int Lost, string Outcome, int Coins, long FinishedAt);
internal sealed record SeasonResolution(int NextDivision, int Coins, string Outcome, bool Title);
internal sealed record SeasonRule(int Title, int Promotion, int Hold, int TitleCoins, int PromotionCoins, int HoldCoins, int RelegationCoins = 0);

internal static class Seasons
{
    internal const int Rounds = 10;
    internal const int HistoryLimit = 100;

    // Server competition presets, shared by catalogue and settlement. These are not a
    // claim that every historical retail league, pack award or entry restriction is implemented.
    private static readonly SeasonRule[] OfflineRewards =
    {
        null, new(28,0,15,5600,0,5300), new(26,23,14,5500,5800,3500), new(26,23,15,5600,5300,2800),
        new(24,21,14,4600,3700,2500), new(22,19,13,4100,3300,2300), new(20,17,12,3500,3100,2300),
        new(18,15,10,3500,2800,2100), new(16,13,9,2900,2500,1900), new(14,11,7,2100,1700,1300), new(12,9,0,1900,1500,300)
    };
    private static readonly SeasonRule[] OnlineRewards =
    {
        null, new(23,0,14,15000,0,10500,4500), new(21,18,12,13000,10400,7400,3400), new(21,18,12,11000,8800,6200,3100),
        new(19,16,10,9000,7200,5000,2900), new(19,16,10,7000,5600,3900,2200), new(19,16,10,6000,4800,3400,1900),
        new(17,14,8,5000,4000,2800,1800), new(15,12,8,4000,3200,2500,1600), new(13,10,6,3000,2400,1900,1300), new(12,9,0,2000,1600,1000)
    };

    // Only squads backed by players, a kit and a badge may appear in native fixtures.
    private static readonly int[] RankedTeams = RankTeams();
    internal static IReadOnlyList<int> NativeTeamIds => RankedTeams;
    private static int[] RankTeams()
    {
        var kits = ClubItems.Catalog.Where(c => c.Type == "kit").Select(c => c.TeamId != 0 ? c.TeamId : c.AssetId).ToHashSet();
        var badges = ClubItems.Catalog.Where(c => c.Type == "badge").Select(c => c.TeamId != 0 ? c.TeamId : c.AssetId).ToHashSet();
        return RealPlayers.All.GroupBy(p => p.TeamId).Where(g => g.Count() >= 16 && kits.Contains(g.Key) && badges.Contains(g.Key))
            .OrderBy(g => g.OrderByDescending(p => p.Rating).Take(18).Average(p => p.Rating)).ThenBy(g => g.Key).Select(g => g.Key).ToArray();
    }

    internal static SeasonRule Rules(int division, bool online = false)
    {
        if (division is < 1 or > 10) throw new InvalidDataException("Invalid season division.");
        var configured = online ? ServerOptions.Current.OnlineDivisionRules : ServerOptions.Current.OfflineDivisionRules;
        if (configured.Length == 10) return configured[division - 1];
        return (online ? OnlineRewards : OfflineRewards)[division];
    }

    internal static SeasonResolution Evaluate(int division, int points, bool online)
    {
        var reward = Rules(division, online);
        if (points is < 0 or > Rounds * 3) throw new InvalidDataException("Invalid season points.");
        if (points >= reward.Title) return new(Math.Max(1, division - 1), reward.TitleCoins, "CHAMPIONSHIP", true);
        if (division > 1 && points >= reward.Promotion) return new(division - 1, reward.PromotionCoins, "PROMOTION", false);
        if (division < 10 && points < reward.Hold) return new(division + 1, reward.RelegationCoins, "RELEGATION", false);
        return new(division, reward.HoldCoins, "MAINTENANCE", false);
    }

    internal static int Difficulty(int division, int round) => Math.Clamp(1 + (10 - Math.Clamp(division, 1, 10)) * 4 / 9 + (round >= 8 ? 1 : 0), 1, 5);

    internal static string ListJson(NameValueCollection query = null)
    {
        bool online = string.Equals(query?["type"], "online", StringComparison.OrdinalIgnoreCase);
        var p = FutProfileStore.Get();
        var season = online ? p.OnlineSeason : p.Season;
        int current = Math.Clamp(online ? p.OnlineDivision : p.OfflineDivision, 1, 10);
        var wanted = (query?["divisionList"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int d) ? d : -1).Where(d => d is >= 1 and <= 11).Distinct().ToList();
        if (wanted.Count == 0) wanted.AddRange(Enumerable.Range(1, 10).Reverse());
        if (!wanted.Any(d => RealDivision(d) == current)) wanted.Add(current);
        // CardsDLL decrements seasonId and uses it as a position in this exact list.
        // 11 is the unplaced client's alias for Division 10, not an extra ladder tier.
        int selected = wanted.FindIndex(d => RealDivision(d) == current) + 1;
        if (season.CatalogId != selected || season.CatalogDivision != wanted[selected - 1] || season.CatalogSeasonId != season.SeasonId)
        {
            season.CatalogId = selected; season.CatalogDivision = wanted[selected - 1]; season.CatalogSeasonId = season.SeasonId;
            PlayerContext.MarkDirty();
        }
        var entries = wanted.Select((wire, index) =>
        {
            int division = RealDivision(wire);
            var reward = Rules(division, online);
            object Prize(string level, int threshold, int coins) => new
            {
                prizeLevel = level, thresholdPoint = threshold,
                awardMappings = new[] { new { awards = coins > 0 ? new[] { new { awardType = 1, value = coins, halid = 0 } } : Array.Empty<object>() } }
            };
            var prizes = new List<object>
            {
                Prize("RELEGATION", 0, reward.RelegationCoins),
                Prize("MAINTENANCE", reward.Hold, reward.HoldCoins)
            };
            if (division > 1) prizes.Add(Prize("PROMOTION", reward.Promotion, reward.PromotionCoins));
            prizes.Add(Prize("CHAMPIONSHIP", reward.Title, reward.TitleCoins));
            return new
            {
                id = index + 1, divisionId = wire, type = online ? "ONLINE" : "OFFLINE", numMatches = Rounds, matchLengthMin = 6,
                matches = Enumerable.Range(0, Rounds).Select(i => new { teamId = TeamFor(division, i), difficulty = Difficulty(division, i + 1), rewardMult = 1, roundId = i, coins = 250 + (10 - division) * 25 + i * 10 }),
                prizeSet = prizes, elgOperation = "AND", elgReq = Array.Empty<object>(), trophyResourceId = -1, trophyUseCount = 0,
                visStartDays = 3650, visEndDays = 3650, startDateTime = 0, endDateTime = int.MaxValue, untilStartSeconds = 0, untilEndSeconds = 315360000
            };
        });
        return JsonSerializer.Serialize(new { seasons = entries });
    }

    private static int TeamFor(int division, int index)
    {
        if (RankedTeams.Length == 0) return 241;
        int center = (10 - division) * Math.Max(0, RankedTeams.Length - Rounds) / 9;
        return RankedTeams[Math.Clamp(center + index, 0, RankedTeams.Length - 1)];
    }

    private static int RealDivision(int wire) => wire == 11 ? 10 : wire;

    internal static string UserJson(FutProfile p, bool online = false)
    {
        var s = online ? p.OnlineSeason : p.Season;
        int division = Math.Clamp(online ? p.OnlineDivision : p.OfflineDivision, 1, 10);
        bool resume = ServerOptions.Current.ResumeCompetitionBlobs && s.SavedSeasonId == s.SeasonId && s.CatalogSeasonId == s.SeasonId
            && s.CatalogId > 0 && RealDivision(s.CatalogDivision) == division && s.SavedRound == s.GamesPlayed + 1
            && s.DataVersion > 0 && s.ProgressDataVersion > 0 && CompetitionSave.Resumable(s.Data, s.ProgressData, s.SavedRound, season: true);
        var doc = new Dictionary<string, object>
        {
            ["seasonId"] = resume ? s.CatalogId : -1,
            ["divisionId"] = resume ? s.CatalogDivision : division,
            ["round"] = Math.Clamp(s.GamesPlayed + 1, 1, Rounds)
        };
        // The native decoder consumes a buffer when it sees its following version.
        if (resume)
        {
            doc["data"] = s.Data; doc["dataVersion"] = s.DataVersion;
            if (!string.IsNullOrEmpty(s.ProgressData)) { doc["progressdata"] = s.ProgressData; doc["progressDataVersion"] = s.ProgressDataVersion; }
        }
        return JsonSerializer.Serialize(doc);
    }

    internal static void CaptureSave(FutProfile p, string body, bool online = false, string path = null, NameValueCollection query = null)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid season save.");
        int round = CompetitionSave.ReadInt(root, "round", 1);
        int dataVersion = CompetitionSave.ReadInt(root, "dataVersion", 1);
        int progressVersion = CompetitionSave.ReadInt(root, "progressDataVersion", 1);
        if (round is < 1 or > Rounds + 1 || dataVersion < 1 || progressVersion < 1) throw new InvalidDataException("Invalid season save round or version.");
        string data = CompetitionSave.ReadString(root, "data");
        string progress = CompetitionSave.ReadString(root, "progressData");
        if (progress.Length == 0) progress = CompetitionSave.ReadString(root, "progressdata");
        var s = online ? p.OnlineSeason : p.Season;
        int division = online ? p.OnlineDivision : p.OfflineDivision;
        // Opaque saves never change W/D/L, points, divisions, history, titles or wallets.
        // A final round-11 write belongs to the retired season after settlement.
        if (s.CatalogSeasonId != s.SeasonId || RealDivision(s.CatalogDivision) != division || round != s.GamesPlayed + 1 || round > Rounds
            || !SaveTargetMatches(s, division, path, query, root)) return;
        string fingerprint = CompetitionSave.Fingerprint(round, data, progress);
        if ((s.RetiredSaveHashes ??= new()).Contains(fingerprint, StringComparer.Ordinal)) return;
        if (s.Data != data || s.ProgressData != progress || s.SavedRound != round) RetireSave(s);
        s.SavedRound = round; s.Data = data; s.ProgressData = progress; s.SavedSeasonId = s.SeasonId;
        s.DataVersion = dataVersion; s.ProgressDataVersion = progressVersion;
        if (!online) p.SeasonSaveBlob = progress;
    }

    private static bool SaveTargetMatches(FutSeason s, int division, string path, NameValueCollection query, JsonElement root)
    {
        bool Target(string text, bool isDivision)
        {
            if (string.IsNullOrEmpty(text)) return true;
            return int.TryParse(text, out int value) && (isDivision ? RealDivision(value) == division : value == s.CatalogId);
        }
        if (!Target(query?["divisionId"], true) || !Target(query?["seasonId"], false)) return false;
        if (!string.IsNullOrEmpty(path))
        {
            var m = Regex.Match(path, @"/season/(\d+)(?:/division/(\d+))?/user(?:$|/)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (m.Success && (!Target(m.Groups[1].Value, false) || !Target(m.Groups[2].Value, true))) return false;
        }
        if (root.TryGetProperty("seasonId", out _) && CompetitionSave.ReadInt(root, "seasonId", -1) != s.CatalogId) return false;
        if (root.TryGetProperty("divisionId", out _) && RealDivision(CompetitionSave.ReadInt(root, "divisionId", -1)) != division) return false;
        if (root.TryGetProperty("seasonInstanceId", out _) && CompetitionSave.ReadInt(root, "seasonInstanceId", -1) != s.SeasonId) return false;
        return true;
    }

    private static void RetireSave(FutSeason s)
    {
        if (!string.IsNullOrEmpty(s.Data) || !string.IsNullOrEmpty(s.ProgressData))
            CompetitionSave.Retire(s.RetiredSaveHashes ??= new(), CompetitionSave.Fingerprint(s.SavedRound, s.Data, s.ProgressData));
        s.Data = ""; s.ProgressData = ""; s.SavedSeasonId = 0;
    }

    private static FutSeason Next(FutSeason old, bool title = false, bool promoted = false, bool relegated = false, int coins = 0)
    {
        RetireSave(old);
        return new FutSeason
        {
            SeasonId = checked(old.SeasonId + 1), TitlesWon = checked(old.TitlesWon + (title ? 1 : 0)),
            Promotions = checked(old.Promotions + (promoted ? 1 : 0)), Relegations = checked(old.Relegations + (relegated ? 1 : 0)),
            Coins = checked(old.Coins + coins), RetiredSaveHashes = new(old.RetiredSaveHashes ?? new())
        };
    }

    internal static void Reset(FutProfile p, bool online = false)
    {
        if (online) throw new InvalidDataException("Ranked online seasons cannot be reset by a client.");
        p.Season = Next(p.Season); p.SeasonSaveBlob = "";
    }

    internal static int Settle(FutProfile p, string reason, bool online, int expectedSeasonId = 0)
    {
        var s = online ? p.OnlineSeason : p.Season;
        int division = online ? p.OnlineDivision : p.OfflineDivision;
        _ = Rules(division, online);
        if (expectedSeasonId != 0 && expectedSeasonId != s.SeasonId) throw new InvalidDataException("Match belongs to an earlier season.");
        if (s.GamesPlayed is < 0 or >= Rounds || s.GamesWon < 0 || s.GamesDraw < 0 || s.GamesLost < 0
            || (long)s.GamesWon + s.GamesDraw + s.GamesLost != s.GamesPlayed || 3L * s.GamesWon + s.GamesDraw != s.Points)
            throw new InvalidDataException("Season record is inconsistent.");
        string result = (reason ?? "").ToUpperInvariant();
        if (result is not ("WIN" or "DRAW" or "TIE" or "LOSS" or "DNF" or "QUIT")) throw new InvalidDataException("Invalid season result.");
        s.GamesPlayed++;
        if (result == "WIN") { s.GamesWon++; s.Points += 3; }
        else if (result is "DRAW" or "TIE") { s.GamesDraw++; s.Points++; }
        else s.GamesLost++;
        RetireSave(s); s.SavedRound = s.GamesPlayed + 1;
        if (!online) p.SeasonSaveBlob = "";
        if (s.GamesPlayed < Rounds) return 0;
        var resolution = Evaluate(division, s.Points, online);
        (p.SeasonHistory ??= new()).Add(new(s.SeasonId, division, online, s.Points, s.GamesWon, s.GamesDraw, s.GamesLost, resolution.Outcome, resolution.Coins, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (p.SeasonHistory.Count > HistoryLimit) p.SeasonHistory.RemoveRange(0, p.SeasonHistory.Count - HistoryLimit);
        var next = Next(s, resolution.Title, resolution.NextDivision < division, resolution.NextDivision > division, resolution.Coins);
        if (online) { p.OnlineDivision = resolution.NextDivision; p.OnlineSeason = next; }
        else { p.OfflineDivision = resolution.NextDivision; p.Season = next; }
        return resolution.Coins;
    }

    internal static string HistoryJson(bool? online = null)
    {
        var entries = (FutProfileStore.Get().SeasonHistory ?? new()).Where(s => !online.HasValue || s.Online == online.Value).Reverse().ToArray();
        return JsonSerializer.Serialize(new
        {
            seasons = entries.Select(s => new { id = s.SeasonId, type = s.Online ? "ONLINE" : "OFFLINE", divisionId = s.Division, numMatches = s.Won + s.Draw + s.Lost, points = s.Points, won = s.Won, draw = s.Draw, lost = s.Lost, prizeLevel = s.Outcome, coins = s.Coins, finishedAt = s.FinishedAt }),
            totalResults = entries.Length
        });
    }

    internal static string ResetJson(int division, bool online = false) => online ? JsonSerializer.Serialize(new { onlineDivision = division }) : JsonSerializer.Serialize(new { offlineDivision = division });
    internal static int ParseResetDivision(string path, NameValueCollection query = null)
    {
        var match = Regex.Match(path ?? "", @"/division/(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string value = match.Success ? match.Groups[1].Value : query?["divisionId"];
        return int.TryParse(value, out int division) && division is >= 1 and <= 11 ? RealDivision(division) : -1;
    }
    internal static string TrophyJson(int entryId) => JsonSerializer.Serialize(new { tournamentId = 8202000 + entryId, tournamentType = 0, assetName = "trophy_1100_gold", silName = "trophy_1100_dark", locString = new[] { new { lang = "ENG_US", label = "Division " + Math.Clamp(entryId % 100, 1, 10) } } });
}
