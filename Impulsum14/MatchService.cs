using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Impulsum14;

internal sealed class MatchSession
{
    public long Id { get; set; }
    public long CreatedAt { get; set; }
    public string RequestHash { get; set; } = "";
    public string Competition { get; set; } = "";
    public bool Online { get; set; }
    public long RankedMatchId { get; set; }
    public bool RankedFinalized { get; set; }
    public int TournamentId { get; set; }
    public long TournamentRunId { get; set; }
    public int TournamentRound { get; set; }
    public int SeasonInstanceId { get; set; }
    public int SquadId { get; set; }
    public long ManagerId { get; set; }
    public List<long> StartingXiIds { get; set; } = new();
    public List<long> RosterIds { get; set; } = new();
    public bool Ready { get; set; }
    public bool Settled { get; set; }
    public string Response { get; set; } = "";
    public int Difficulty { get; set; } = 3;
}

internal static class MatchService
{
    private static readonly string[] StatKeys = { "goals", "shotsOnTarget", "successfulTackles", "corners", "cleansheets", "passingPercentage", "possessionPercentage", "manOfTheMatch", "fouls", "yellowCards", "redCards", "offsides" };
    private static readonly string[] ItemKeys = { "shots", "goals", "yellowCards", "redCards", "suspension", "injuryGames", "fitness", "assists" };

    internal static MatchSession Begin(string body)
    {
        using var doc = Parse(body);
        var root = doc.RootElement;
        var data = PlayerContext.Current.Data;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (data.Match is { Settled: false } active)
        {
            if (active.RequestHash == hash) return active;
            throw new BadHttpRequestException("Finish or reset the active match first.", 409);
        }
        if (data.Match is { RankedMatchId: > 0, RankedFinalized: false } previous)
        {
            var receipt = FindRankedReceipt(previous);
            if (receipt is { Settled: false, Cancelled: false }) throw new BadHttpRequestException("The submitted ranked result is still pending.", 409);
            RefreshRankedReceipt(previous, receipt);
        }
        int tournament = Integer(root, "tournamentId", 0, 0, int.MaxValue);
        var ranked = RankedService.Current?.GetActive(PlayerContext.Current.Id);
        // Browsing an offline cup leaves a presentation hint. A subsequent
        // server-bound online game takes precedence over that stale hint;
        // explicitly requesting a cup still conflicts with the paired game.
        if (ranked is null && tournament == 0 && data.LastCompetition == "tournament") tournament = data.ActiveTournamentId;
        if (ranked is not null && (ranked.PlayerA == PlayerContext.Current.Id ? ranked.ReportedA : ranked.ReportedB))
            throw new BadHttpRequestException("This ranked score has already been submitted.", 409);
        bool requestedOnline = Text(root, "type", 32).Equals("online", StringComparison.OrdinalIgnoreCase);
        if (requestedOnline && ranked is null) throw new BadHttpRequestException("Online results require a server-bound opponent.", 409);
        if (ranked is not null && tournament > 0) throw new BadHttpRequestException("Finish the paired online match first.", 409);
        bool online = ranked is not null;
        string competition = online ? "ranked" : tournament > 0 ? "tournament" : root.TryGetProperty("seasonId", out _) || data.LastCompetition == "season" ? "season" : "friendly";
        int div = online ? data.Profile.OnlineDivision : data.Profile.OfflineDivision;
        var season = online ? data.Profile.OnlineSeason : data.Profile.Season;
        int squadId = Integer(root, "squadId", data.Club.ActiveSquadId, 0, int.MaxValue);
        var squad = data.Club.Squads.FirstOrDefault(s => s.Id == squadId);
        if (squad is null && root.TryGetProperty("squadId", out _)) throw new BadHttpRequestException("The selected squad does not exist.", 409);
        squad ??= data.Club.Squads.FirstOrDefault();
        PlayerContext.MarkDirty();
        var run = tournament > 0 ? Tournaments.BeginMatch(tournament) : (RunId: 0L, Round: 0);
        var match = new MatchSession
        {
            Id = checked(++data.NextMatchId), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RequestHash = hash,
            TournamentId = tournament, TournamentRunId = run.RunId, TournamentRound = run.Round, SeasonInstanceId = season.SeasonId,
            Competition = competition, Online = online, RankedMatchId = ranked?.Id ?? 0, SquadId = squad?.Id ?? squadId, ManagerId = squad?.ManagerId ?? 0,
            StartingXiIds = squad?.Slots.Where(s => s.Key is >= 0 and < 11 && s.Value > 0).OrderBy(s => s.Key).Select(s => s.Value).Distinct().ToList() ?? new(),
            RosterIds = squad?.Slots.Where(s => s.Key is >= 0 and < 23 && s.Value > 0).Select(s => s.Value).Distinct().ToList() ?? new(),
            Difficulty = tournament > 0 ? Tournaments.Defs.First(d => d.Id == tournament).Diff : competition == "season" ? Seasons.Difficulty(div, season.GamesPlayed + 1) : 3
        };
        data.Match = match; data.LastCompetition = "";
        return match;
    }

    internal static Squad Ready(string body)
    {
        var data = PlayerContext.Current.Data;
        var match = data.Match;
        if (match is null || match.Settled) throw new BadHttpRequestException("No active match.", 409);
        using var document = Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("matchId", out _) && Long(root, "matchId") != match.Id) throw new BadHttpRequestException("Match identity does not match.", 409);
        var ids = new List<long>();
        if (root.TryGetProperty("items", out var items))
        {
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 11) throw new BadHttpRequestException("MatchReady accepts at most eleven starters.", 400);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid match item.");
                long id = item.TryGetProperty("id", out _) ? Long(item, "id") : Long(item, "itemId");
                if (!match.RosterIds.Contains(id) || ids.Contains(id)) throw new BadHttpRequestException("Invalid starting player.", 409);
                ids.Add(id);
            }
        }
        if (ids.Count == 0) ids = match.StartingXiIds.ToList();
        if (match.Ready)
        {
            // Ready identifies participants, not formation slots. Retrying the
            // same XI in another order must not rewrite or reject a bound game.
            if (ids.Count != match.StartingXiIds.Count || ids.Any(id => !match.StartingXiIds.Contains(id)))
                throw new BadHttpRequestException("MatchReady starters are already bound.", 409);
            return data.Club.Squads.FirstOrDefault(s => s.Id == match.SquadId) ?? new Squad { Id = match.SquadId };
        }
        // Validate even the implicit XI. Building an index of a potentially
        // large club for eleven identifiers wastes memory on every Ready.
        var selected = ids.ToHashSet();
        int owned = 0;
        foreach (var card in data.Club.Inventory)
            if (selected.Contains(card.ItemId))
            {
                if (card.Pile is 0 or 3) throw new BadHttpRequestException("Invalid starting player.", 409);
                owned++;
            }
        if (owned != ids.Count) throw new BadHttpRequestException("A starting player is no longer available.", 409);
        PlayerContext.MarkDirty();
        match.StartingXiIds = ids;
        match.Ready = true;
        return data.Club.Squads.FirstOrDefault(s => s.Id == match.SquadId) ?? new Squad { Id = match.SquadId };
    }

    internal static void Reset()
    {
        var match = PlayerContext.Current.Data.Match;
        if (match is { RankedMatchId: > 0, RankedFinalized: false } && RankedService.Current is { } ranked)
        {
            // Once the client created a paired match, its own reset is a
            // concession. Its optional Ready flag cannot erase a peer's score.
            if (FindRankedReceipt(match) is { Settled: false, Cancelled: false })
                ranked.Report(PlayerContext.Current.Id, match.RankedMatchId, forfeit: true);
            RankedProjection.ApplyCurrent();
        }
        PlayerContext.MarkDirty();
        PlayerContext.Current.Data.Match = null;
    }

    internal static async Task<string> EndAsync(string body, Action<string> consequences, CancellationToken cancel = default)
    {
        string response = End(body, consequences);
        var match = PlayerContext.Current.Data.Match!;
        if (match.RankedMatchId == 0 || match.RankedFinalized || RankedService.Current is not { } ranked) return response;
        RankedMatchView? receipt;
        try
        {
            receipt = await ranked.WaitForSettlementAsync(PlayerContext.Current.Id, match.RankedMatchId,
                TimeSpan.FromMilliseconds(ServerOptions.Current.Ranked.SettlementWaitMilliseconds), cancel).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { receipt = null; }
        if (receipt is null || receipt.Settled || receipt.Cancelled) response = RefreshRankedReceipt(match, receipt);
        return response;
    }

    internal static string End(string body, Action<string> consequences)
    {
        var data = PlayerContext.Current.Data;
        var match = data.Match ?? throw new BadHttpRequestException("No active match.", 409);
        using var document = Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("matchId", out _) && Long(root, "matchId") != match.Id) throw new BadHttpRequestException("Match ID does not belong to this session.", 409);
        if (match.Settled) return match.Response;
        string reason = Text(root, "endReason", 32).ToUpperInvariant();
        reason = reason switch { "TIE" => "DRAW", "FORFEIT" => "QUIT", _ => reason };
        if (reason is not ("WIN" or "DRAW" or "LOSS" or "DNF" or "QUIT" or "NO_CONTEST")) throw new InvalidDataException("Invalid match result.");
        bool abandoned = reason is "DNF" or "QUIT";
        bool noContest = reason == "NO_CONTEST";
        var myStats = Stats(root, "myMatchStats");
        var opponentStats = Stats(root, "opponentMatchStats");
        var itemStats = Items(root, match);
        string matchData = Text(root, "matchData", 262144);
        int seconds = Integer(root, "secondsPlayed", 0, 0, 10800);
        if (seconds == 0 && !abandoned && !noContest) seconds = 5400;
        int goals = myStats["goals"];
        var options = ServerOptions.Current;
        int coins = abandoned || noContest ? 0 : (reason == "WIN" ? options.MatchWinCoins : reason == "DRAW" ? options.MatchDrawCoins : options.MatchLossCoins) + goals * options.GoalCoins;
        int prize = 0;
        RankedMatchView rankedReceipt = null;
        PlayerContext.MarkDirty();
        if (match.RankedMatchId > 0)
        {
            var ranked = RankedService.Current ?? throw new InvalidOperationException("Ranked service unavailable.");
            rankedReceipt = FindRankedReceipt(match);
            if (rankedReceipt is { Settled: false, Cancelled: false })
            {
                if (noContest || abandoned) rankedReceipt = ranked.Report(PlayerContext.Current.Id, match.RankedMatchId, forfeit: true).Match;
                else
                {
                    if (!root.TryGetProperty("myMatchStats", out var mine) || !mine.TryGetProperty("goals", out _) || !root.TryGetProperty("opponentMatchStats", out var theirs) || !theirs.TryGetProperty("goals", out _))
                        throw new BadHttpRequestException("Ranked results require both score blocks.", 400);
                    if ((goals > opponentStats["goals"] ? "WIN" : goals < opponentStats["goals"] ? "LOSS" : "DRAW") != reason)
                        throw new BadHttpRequestException("Result disagrees with score.", 409);
                    rankedReceipt = ranked.Report(PlayerContext.Current.Id, match.RankedMatchId, goals, opponentStats["goals"]).Match;
                }
            }
            coins = 0;
            if (rankedReceipt is { Settled: true })
            {
                bool isA = rankedReceipt.PlayerA == PlayerContext.Current.Id;
                prize = isA ? rankedReceipt.SeasonCoinsA : rankedReceipt.SeasonCoinsB;
                coins = (isA ? rankedReceipt.CoinsA : rankedReceipt.CoinsB) - prize;
            }
            RankedProjection.ApplyCurrent();
        }
        else if (!noContest)
        {
            if (match.TournamentId > 0) prize = Tournaments.SettleTournamentMatch(match.TournamentId, reason, match.TournamentRunId, match.TournamentRound).Prize;
            if (match.Competition == "season") prize += Seasons.Settle(data.Profile, reason, false, match.SeasonInstanceId);
            checked { data.Profile.Coins += coins + prize; }
            if (reason == "WIN") data.Profile.Wins++;
            else if (reason == "DRAW") data.Profile.Draws++;
            else data.Profile.Losses++;
        }
        if (!noContest && (!abandoned || match.Ready)) consequences(body);
        if (!abandoned && !noContest) ManagerTasks.Observe("play_match");
        match.Settled = true;
        long balance = data.Profile.Coins;
        var reply = new Dictionary<string, object>
        {
            ["matchId"] = match.Id, ["allCoins"] = balance, ["credits"] = balance, ["coins"] = balance,
            ["coinsEarned"] = coins + prize, ["matchCoins"] = coins, ["tournamentCoins"] = match.Competition == "tournament" ? prize : 0,
            ["currencies"] = new[] { new { name = "COINS", funds = balance, finalFunds = balance } },
            ["completionAward"] = coins, ["skillAward"] = 0, ["totalAward"] = coins + prize, ["rewardCoins"] = coins, ["totalCoins"] = coins + prize,
            ["seasonCoins"] = match.Competition is "season" or "ranked" ? prize : 0, ["boostConis"] = 0, ["boostCountLeft"] = 0,
            ["participationAward"] = coins, ["matchCoinPartials"] = Array.Empty<object>(), ["matchCoinMultipliers"] = Array.Empty<object>(),
            ["matchParamsKeyValues"] = new Dictionary<string, string>(), ["endReason"] = RankedReason(rankedReceipt, reason), ["secondsPlayed"] = seconds,
            ["matchDifficulty"] = match.Difficulty, ["items"] = itemStats, ["matchData"] = matchData, ["dnfModifier"] = abandoned ? 0 : 1,
            ["userData"] = new { credits = balance, coins = balance }, ["rankedMatchId"] = match.RankedMatchId,
            ["rankedReceiptStatus"] = match.RankedMatchId > 0 ? rankedReceipt?.Status ?? "retired" : "offline",
            ["settlementPending"] = rankedReceipt is { Settled: false, Cancelled: false }
        };
        if (!abandoned) { reply["myMatchStats"] = myStats; reply["opponentMatchStats"] = opponentStats; }
        if (rankedReceipt is { Settled: true })
        {
            bool isA = rankedReceipt.PlayerA == PlayerContext.Current.Id;
            reply["elo"] = isA ? rankedReceipt.RatingAfterA : rankedReceipt.RatingAfterB;
            reply["eloChange"] = isA ? rankedReceipt.RatingDeltaA : rankedReceipt.RatingDeltaB;
        }
        match.RankedFinalized = match.RankedMatchId > 0 && (rankedReceipt is null || rankedReceipt.Settled || rankedReceipt.Cancelled);
        match.Response = JsonSerializer.Serialize(reply);
        return match.Response;
    }

    private static RankedMatchView? FindRankedReceipt(MatchSession match)
    {
        try { return RankedService.Current?.GetMatch(PlayerContext.Current.Id, match.RankedMatchId); }
        catch (KeyNotFoundException) { return null; }
    }

    private static string RefreshRankedReceipt(MatchSession match, RankedMatchView? receipt)
    {
        if (match.RankedFinalized) return match.Response;
        RankedProjection.ApplyCurrent();
        bool isA = receipt?.PlayerA == PlayerContext.Current.Id;
        int total = receipt is { Settled: true } ? isA ? receipt.CoinsA : receipt.CoinsB : 0;
        int season = receipt is { Settled: true } ? isA ? receipt.SeasonCoinsA : receipt.SeasonCoinsB : 0;
        long balance = FutProfileStore.Get().Coins;
        var response = JsonNode.Parse(match.Response)!.AsObject();
        foreach (string key in new[] { "credits", "coins", "allCoins" }) response[key] = balance;
        foreach (string key in new[] { "coinsEarned", "totalAward", "totalCoins" }) response[key] = total;
        foreach (string key in new[] { "matchCoins", "completionAward", "participationAward", "rewardCoins" }) response[key] = total - season;
        response["seasonCoins"] = season; response["settlementPending"] = false;
        response["currencies"] = JsonSerializer.SerializeToNode(new[] { new { name = "COINS", funds = balance, finalFunds = balance } });
        response["userData"] = JsonSerializer.SerializeToNode(new { credits = balance, coins = balance });
        response["rankedReceiptStatus"] = receipt?.Status ?? "retired";
        response["endReason"] = RankedReason(receipt, response["endReason"]?.GetValue<string>() ?? "NO_CONTEST");
        if (receipt is { Settled: true })
        {
            response["elo"] = isA ? receipt.RatingAfterA : receipt.RatingAfterB;
            response["eloChange"] = isA ? receipt.RatingDeltaA : receipt.RatingDeltaB;
        }
        PlayerContext.MarkDirty(); match.RankedFinalized = true; match.Response = response.ToJsonString(); return match.Response;
    }

    private static string RankedReason(RankedMatchView? receipt, string reported)
    {
        if (receipt is { Cancelled: true }) return "NO_CONTEST";
        if (receipt is not { Settled: true }) return reported;
        if (receipt.ForfeitedBy == PlayerContext.Current.Id) return reported == "DNF" ? "DNF" : "QUIT";
        bool isA = receipt.PlayerA == PlayerContext.Current.Id;
        int comparison = (isA ? receipt.GoalsA!.Value : receipt.GoalsB!.Value).CompareTo(isA ? receipt.GoalsB!.Value : receipt.GoalsA!.Value);
        return comparison > 0 ? "WIN" : comparison < 0 ? "LOSS" : "DRAW";
    }

    private static Dictionary<string, int> Stats(JsonElement root, string property)
    {
        bool present = root.TryGetProperty(property, out var stats);
        if (present && stats.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid match stats.");
        return StatKeys.ToDictionary(key => key, key => present ? Integer(stats, key, 0, 0, key == "goals" ? 50 : 100000) : 0);
    }
    private static List<Dictionary<string, object>> Items(JsonElement root, MatchSession match)
    {
        var result = new List<Dictionary<string, object>>();
        if (!root.TryGetProperty("items", out var items)) return result;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 23) throw new InvalidDataException("Invalid match item list.");
        var allowed = match.RosterIds.ToHashSet(); var seen = new HashSet<long>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid match item.");
            long id = item.TryGetProperty("id", out _) ? Long(item, "id") : Long(item, "itemId");
            if (!allowed.Contains(id) || !seen.Add(id)) throw new BadHttpRequestException("Match item does not belong to the bound squad.", 409);
            var row = new Dictionary<string, object> { ["id"] = id };
            foreach (string key in ItemKeys)
                if (item.TryGetProperty(key, out _) || key is "redCards" or "suspension") row[key] = Integer(item, key, 0, 0, key == "fitness" ? 99 : 100000);
            if (item.TryGetProperty("injuryType", out _)) row["injuryType"] = Text(item, "injuryType", 64);
            result.Add(row);
        }
        return result;
    }
    private static JsonDocument Parse(string body)
    {
        var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose(); throw new InvalidDataException("Expected an object.");
    }
    private static int Integer(JsonElement root, string key, int fallback, int min, int max)
    {
        if (!root.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < min || number > max) throw new InvalidDataException("Invalid " + key + ".");
        return number;
    }
    private static long Long(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 0) throw new InvalidDataException("Invalid " + key + ".");
        return number;
    }
    private static string Text(JsonElement root, string key, int maximum)
    {
        if (!root.TryGetProperty(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum) throw new InvalidDataException("Invalid " + key + ".");
        return value.GetString()!;
    }
}
