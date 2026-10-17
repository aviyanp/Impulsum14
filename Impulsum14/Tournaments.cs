namespace Impulsum14;

internal static class Tournaments
{
    private static readonly Dictionary<int, int[]> TeamPools = new()
    {
        [ 1] = new[] { 422, 1572, 357, 294, 922, 1914, 1744, 873, 697, 689, 1939, 110, 696, 12, 15005 },
        [ 2] = new[] { 1926, 162, 2023, 433, 94, 298, 165, 1910, 256, 1871, 570, 97, 3, 8, 1880 },
        [ 3] = new[] { 1887, 1902, 62, 1807, 1915, 417, 614, 191, 91, 665, 200, 459, 95, 14, 1884 },
        [ 4] = new[] { 57, 378, 605, 2007, 1888, 472, 190, 674, 1913, 10020, 226, 1882, 29, 1795, 71 },
        [ 5] = new[] { 896, 1906, 1903, 31, 673, 1881, 1808, 58, 1844, 379, 242, 1838, 1901, 4, 1876 },
        [ 6] = new[] { 203, 1861, 1799, 1793, 78, 171, 453, 1893, 1837, 1909, 10029, 1878, 232, 1908, 15029 },
        [ 7] = new[] { 229, 1961, 217, 744, 1598, 1952, 231, 246, 1892, 468, 244, 189, 1032, 192, 166 },
        [ 8] = new[] { 72, 479, 1917, 206, 1970, 1879, 169, 1039, 819, 1853, 1843, 25, 1891, 483, 54 },
        [ 9] = new[] { 2, 38, 1013, 1719, 109, 450, 485, 1792, 70, 106, 59, 1824, 1809, 28, 1877 },
        [10] = new[] { 19, 247, 1028, 15, 1806, 66, 569, 452, 23, 312, 32, 36, 383, 1842, 245 },
        [11] = new[] { 17, 39, 1819, 393, 65, 462, 517, 315, 480, 567, 1053, 219, 1860, 50, 9 },
        [12] = new[] { 1960, 568, 280, 74, 1629, 598, 237, 1043, 69, 175, 449, 448, 573, 1, 481 },
        [13] = new[] { 13, 7, 144, 1896, 34, 1048, 55, 1035, 1041, 461, 22, 18, 457, 44, 48 },
        [14] = new[] { 52, 234, 325, 236, 47, 46, 10, 5, 11, 21, 73, 240, 45, 243, 241 },
    };

    private static readonly int[] DefaultTeamIds =
        { 52, 234, 325, 236, 47, 46, 10, 5, 11, 21, 73, 240, 45, 243, 241 };

    private static readonly Dictionary<int, int[]> NativeTeamPools = BuildNativeTeamPools();
    private static Dictionary<int, int[]> BuildNativeTeamPools()
    {
        var eligible = Seasons.NativeTeamIds.ToHashSet();
        var strength = RealPlayers.All.GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Rating).Take(18).Average(p => p.Rating));
        return TeamPools.ToDictionary(entry => entry.Key, entry =>
        {
            var pool = entry.Value.Where(eligible.Contains).Distinct().ToList();
            double target = pool.Count > 0 ? pool.Average(id => strength[id]) : 75;
            pool.AddRange(eligible.Except(pool).OrderBy(id => Math.Abs(strength[id] - target)).ThenBy(id => strength[id]).ThenBy(id => id).Take(15 - pool.Count));
            return pool.Count == 15 ? pool.ToArray() : DefaultTeamIds.ToArray();
        });
    }

    internal static int[] GetTeamIds(int tournamentId) =>
        (NativeTeamPools.TryGetValue(tournamentId, out var pool) ? pool : DefaultTeamIds).ToArray();

    // Coin-only server presets; historical first-clear packs are not advertised or inferred from client blobs.
    internal static readonly (int Id, string Name, int Design, int Diff, int Coins, int Unlock)[] Defs =
    {
        ( 1, "Starter Cup",                1100, 1,  300,  0),
        ( 2, "Midlands Invitational",      1104, 2,  500,  0),
        ( 3, "Gold Challenge",             1108, 3,  700,  0),
        ( 4, "Quad-League Classic",        1112, 2,  600,  1),
        ( 5, "Managers Cup",               1116, 3,  700,  1),
        ( 6, "Bronze International Shield",1120, 4, 1000,  2),
        ( 7, "Trio Showcase",              1124, 2,  300,  2),
        ( 8, "Unified Cup",                1128, 3, 1000,  2),
        ( 9, "Pyramid Invitational",       1132, 4, 1000,  3),
        (10, "Silver Links Cup",           1136, 3,  700,  3),
        (11, "Federation Cup",             1140, 4,  200,  4),
        (12, "Champions Trophy",           1144, 5, 2500,  4),
        (13, "Premier Clash",              1148, 3, 1200,  5),
        (14, "Ultimate Cup",               1152, 5, 3000, 10),
    };

    internal static string CatalogJson()
    {
        var profile = FutProfileStore.Get();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            tournament = Defs.Select(d => new
            {
                id = d.Id, type = "offline", treeType = "knockout", aigroup = d.Id,
                eligibilityOperation = "AND", elgReq = Array.Empty<object>(), numTeams = 16, numRounds = NumRounds, matchlength = 6,
                rounds = Enumerable.Range(1, NumRounds).Select(round => new { id = round, difficulty = d.Diff, rewardMultiplier = 1, coins = ServerOptions.Current.MatchWinCoins }),
                awardSet = new { awards = new[] { new { awardType = 1, value = d.Coins, halid = 0 } } },
                trophyResourceId = 8200000 + d.Id, trophyUserCount = (profile.TournamentWins ?? new()).GetValueOrDefault(d.Id),
                @lock = d.Unlock > profile.TrophiesWon ? "LOCKED_TROPHIES" : "UNLOCKED", unlockreq = d.Unlock,
                triesMax = 0, triesPeriod = 0, triesRemaining = 0, nextReset = 0, starttime = 0, endtime = int.MaxValue,
                timeUntilStart = 0, timeUntilEnd = 315360000, visStart = 3650, visEnd = 3650
            })
        });
    }

    internal static string TrophyJson(int tourneyId)
    {
        string name = "Cup"; int design = 1100;
        foreach (var d in Defs)
            if (d.Id == tourneyId) { name = d.Name; design = d.Design; break; }
        return "{\"tournamentId\":" + tourneyId + ",\"tournamentType\":0,\"assetName\":\"trophy_" + design +
               "_gold\",\"silName\":\"trophy_" + design + "_dark\",\"locString\":[{\"lang\":\"ENG_US\",\"label\":\"" +
               name + "\"}]}";
    }

    internal static int ActiveTournamentId
    {
        get => PlayerContext.Current.Data.ActiveTournamentId;
        set
        {
            if (!Defs.Any(d => d.Id == value)) throw new InvalidDataException("Unknown tournament.");
            PlayerContext.Current.Data.ActiveTournamentId = value; PlayerContext.MarkDirty();
        }
    }

    internal static int TrophiesWon => FutProfileStore.Get().TrophiesWon;

    internal const int NumRounds = 4;                       // every fixed cup is a 16-team, 4-round bracket


    internal static int AwardCoins(int tournamentId)
    {
        foreach (var d in Defs) if (d.Id == tournamentId) return d.Coins;
        return 0;
    }

    internal static string TeamsJson(int tournamentId = 0, int count = 15)
    {
        int id = tournamentId > 0 ? tournamentId : ActiveTournamentId;
        if (!Defs.Any(d => d.Id == id) || count is < 0 or > 15) throw new InvalidDataException("Invalid tournament opponent request.");
        // The player's FUT club occupies the sixteenth bracket slot. This exact
        // member is consumed by FutGetTournamentTeams; alternate guessed arrays are unnecessary.
        return System.Text.Json.JsonSerializer.Serialize(new { teamId = GetTeamIds(id).Take(count).ToArray() });
    }

    internal static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }

    internal static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                switch (n)
                {
                    case '"':  sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/':  sb.Append('/'); break;
                    case 'b':  sb.Append('\b'); break;
                    case 'f':  sb.Append('\f'); break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case 'u' when i + 4 < s.Length &&
                                   int.TryParse(s.Substring(i + 1, 4),
                                       System.Globalization.NumberStyles.HexNumber,
                                       System.Globalization.CultureInfo.InvariantCulture, out int uv):
                        sb.Append((char)uv);
                        i += 4;
                        break;
                    default:   sb.Append(n); break;
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string CaptureString(string body, string field)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body ?? "{}");
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Invalid tournament save.");
        return CompetitionSave.ReadString(document.RootElement, field);
    }

    private static bool CanResume(int id, SavedTournament saved) => Defs.Any(d => d.Id == id && d.Unlock <= TrophiesWon)
        && saved is not null && saved.Active && ServerOptions.Current.ResumeCompetitionBlobs && saved.RunId > 0 && saved.SavedRunId == saved.RunId
        && saved.CompletedRounds is >= 0 and < NumRounds && saved.Round == saved.CompletedRounds + 1
        && saved.DataVersion > 0 && saved.ProgressDataVersion > 0
        && CompetitionSave.Resumable(saved.TournamentData, saved.ProgressData, saved.Round);

    internal static string UserTournamentJson(int id)
    {
        var saved = FutProfileStore.Get().SavedTournaments.GetValueOrDefault(id);
        return CanResume(id, saved) ? WireSave(id, saved) : MinimalSave(id);
    }

    internal static string UserListJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        tournamentId = FutProfileStore.Get().SavedTournaments.Where(p => CanResume(p.Key, p.Value)).Select(p => p.Key).OrderBy(id => id).ToArray()
    });

    private static string MinimalSave(int id) => System.Text.Json.JsonSerializer.Serialize(new { tournamentId = id });

    private static string WireSave(int id, SavedTournament saved)
    {
        var document = new Dictionary<string, object>
        {
            ["tournamentId"] = id, ["round"] = saved.Round,
            ["tournamentData"] = saved.TournamentData, ["dataVersion"] = saved.DataVersion
        };
        if (!string.IsNullOrEmpty(saved.ProgressData))
        {
            document["progressdata"] = saved.ProgressData; document["progressDataVersion"] = saved.ProgressDataVersion;
        }
        return System.Text.Json.JsonSerializer.Serialize(document);
    }

    private static void EnsureAvailable(int id, FutProfile profile)
    {
        if (!Defs.Any(d => d.Id == id && d.Unlock <= profile.TrophiesWon)) throw new InvalidDataException("Unknown or locked tournament.");
    }

    private static SavedTournament GetRun(FutProfile profile, int id)
    {
        profile.SavedTournaments ??= new();
        if (!profile.SavedTournaments.TryGetValue(id, out var saved)) profile.SavedTournaments[id] = saved = new();
        if (saved.RunId < 1 || saved.CompletedRounds is < 0 or >= NumRounds || saved.Round != saved.CompletedRounds + 1)
            throw new InvalidDataException("Tournament record is inconsistent.");
        return saved;
    }

    internal static (long RunId, int Round) BeginMatch(int id)
    {
        (long RunId, int Round) result = default;
        FutProfileStore.Mutate(profile =>
        {
            EnsureAvailable(id, profile);
            var saved = GetRun(profile, id);
            saved.Active = true; saved.Won = false;
            result = (saved.RunId, saved.CompletedRounds + 1);
        });
        return result;
    }

    internal static string SaveProgress(int id, string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Invalid tournament save.");
        if (root.TryGetProperty("tournamentId", out _) && CompetitionSave.ReadInt(root, "tournamentId", -1) != id)
            throw new InvalidDataException("Tournament identity does not match the save target.");
        string progress = CompetitionSave.ReadString(root, "progressData");
        if (progress.Length == 0) progress = CompetitionSave.ReadString(root, "progressdata");
        return SaveProgress(id, CompetitionSave.ReadInt(root, "round", 1), CompetitionSave.ReadInt(root, "dataVersion", 1),
            CompetitionSave.ReadString(root, "tournamentData"), CompetitionSave.ReadInt(root, "progressDataVersion", 1), progress);
    }

    internal static string SaveProgress(int id, int round, int dataVersion, string tournamentData, int progressDataVersion, string progressData)
    {
        if (round is < 1 or > NumRounds || dataVersion < 1 || progressDataVersion < 1
            || (tournamentData?.Length ?? 0) > CompetitionSave.MaxBlobLength || (progressData?.Length ?? 0) > CompetitionSave.MaxBlobLength)
            throw new InvalidDataException("Invalid tournament save.");
        tournamentData ??= ""; progressData ??= "";
        FutProfileStore.Mutate(profile =>
        {
            EnsureAvailable(id, profile);
            var saved = GetRun(profile, id);
            // Native blobs cannot skip, rewind or complete authoritative rounds.
            if (round != saved.CompletedRounds + 1) return;
            string fingerprint = CompetitionSave.Fingerprint(round, tournamentData, progressData);
            if ((saved.RetiredSaveHashes ??= new()).Contains(fingerprint, StringComparer.Ordinal)) return;
            if (saved.TournamentData != tournamentData || saved.ProgressData != progressData || saved.Round != round) RetireSave(saved);
            saved.Round = round; saved.DataVersion = dataVersion; saved.TournamentData = tournamentData;
            saved.ProgressDataVersion = progressDataVersion; saved.ProgressData = progressData;
            saved.SavedRunId = saved.RunId; saved.Active = true; saved.Won = false;
        });
        // Never reflect a rejected, malformed or kickoff-stub save as resumable.
        return UserTournamentJson(id);
    }

    private static void RetireSave(SavedTournament saved)
    {
        if (!string.IsNullOrEmpty(saved.TournamentData) || !string.IsNullOrEmpty(saved.ProgressData))
            CompetitionSave.Retire(saved.RetiredSaveHashes ??= new(), CompetitionSave.Fingerprint(saved.Round, saved.TournamentData, saved.ProgressData));
        saved.TournamentData = ""; saved.ProgressData = ""; saved.SavedRunId = 0;
    }

    private static void EndRun(SavedTournament saved, bool won)
    {
        RetireSave(saved);
        saved.RunId = checked(saved.RunId + 1); saved.CompletedRounds = 0; saved.Round = 1;
        saved.Active = false; saved.Won = won;
    }

    internal static void Reset(int id)
    {
        FutProfileStore.Mutate(profile =>
        {
            EnsureAvailable(id, profile);
            EndRun(GetRun(profile, id), false);
        });
    }

    internal static (int Prize, bool WonFinal) SettleTournamentMatch(int id, string? endReason, long expectedRunId = 0, int expectedRound = 0)
    {
        string reason = (endReason ?? "").ToUpperInvariant();
        // A knockout draw is unresolved until extra time/penalties identifies a winner.
        if (reason is not ("WIN" or "LOSS" or "DNF" or "QUIT")) throw new InvalidDataException("Knockout matches require a winner or an abandonment.");
        int prize = 0;
        bool wonFinal = false;
        FutProfileStore.Mutate(profile =>
        {
            EnsureAvailable(id, profile);
            var saved = GetRun(profile, id);
            int round = saved.CompletedRounds + 1;
            if (expectedRunId != 0 && saved.RunId != expectedRunId || expectedRound != 0 && round != expectedRound)
                throw new InvalidDataException("Match belongs to an earlier tournament round.");
            if (reason != "WIN") { EndRun(saved, false); return; }
            if (round < NumRounds)
            {
                RetireSave(saved);
                saved.CompletedRounds = round; saved.Round = round + 1; saved.Active = true; saved.Won = false;
                return;
            }
            prize = AwardCoins(id); wonFinal = true;
            (profile.TournamentWins ??= new())[id] = checked(profile.TournamentWins.GetValueOrDefault(id) + 1);
            profile.TrophiesWon = checked(profile.TrophiesWon + 1);
            // Retain a bounded tombstone: delayed old saves cannot reopen the won bracket.
            EndRun(saved, true);
        });
        return (prize, wonFinal);
    }
}
