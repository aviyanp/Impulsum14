namespace Impulsum14;

/// <summary>Server policy for ranked two-player matches. A shared K is used for
/// each pair, including provisional players, so every rating change is zero-sum.</summary>
internal sealed class RankedOptions
{
    public int InitialRating { get; set; } = 1000;
    public int MinimumRating { get; set; } = 100;
    public int MaximumRating { get; set; } = 3000;
    public int KFactor { get; set; } = 32;
    public int ProvisionalKFactor { get; set; } = 48;
    public int ProvisionalGames { get; set; } = 10;
    public int WinCoins { get; set; } = 500;
    public int DrawCoins { get; set; } = 300;
    public int LossCoins { get; set; } = 200;
    public int GoalCoins { get; set; } = 20;
    public int MaxPlayers { get; set; } = 10000;
    public int MaxActiveMatches { get; set; } = 1000;
    public int RetainedMatches { get; set; } = 2000;
    public int SeasonHistoryLimit { get; set; } = 20;
    public int CheckpointInterval { get; set; } = 128;
    public int MaxJournalBytes { get; set; } = 16 * 1024 * 1024;
    public int MatchTimeoutMinutes { get; set; } = 60;
    public int SettlementWaitMilliseconds { get; set; } = 2500;

    internal void Validate()
    {
        if (MinimumRating < 0 || MaximumRating > 100000 || MinimumRating >= MaximumRating || InitialRating < MinimumRating || InitialRating > MaximumRating)
            throw new InvalidDataException("Ranked rating bounds must contain the initial rating.");
        if (KFactor is < 1 or > 1000 || ProvisionalKFactor is < 1 or > 1000 || ProvisionalGames is < 0 or > 1000)
            throw new InvalidDataException("Invalid ranked K or provisional policy.");
        if (new[] { WinCoins, DrawCoins, LossCoins, GoalCoins }.Any(x => x is < 0 or > 1000000))
            throw new InvalidDataException("Invalid ranked coin rewards.");
        if (MaxPlayers is < 2 or > 100000 || MaxActiveMatches < 1 || MaxActiveMatches > MaxPlayers / 2 || RetainedMatches is < 0 or > 100000 || SeasonHistoryLimit is < 0 or > 100 || CheckpointInterval is < 1 or > 10000 || MaxJournalBytes is < 1048576 or > 268435456 || MatchTimeoutMinutes is < 1 or > 10080 || SettlementWaitMilliseconds is < 0 or > 10000)
            throw new InvalidDataException("Invalid ranked persistence limits.");
    }

    internal RankedOptions Copy() => (RankedOptions)MemberwiseClone();
}

internal sealed record RankedSeasonView(int SeasonId, int GamesPlayed, int GamesWon, int GamesDraw, int GamesLost, int Points, int TitlesWon, int Promotions, int Relegations);

internal sealed record RankedSeasonHistoryView(int SeasonId, int Division, int Points, int Won, int Draw, int Lost, string Outcome, int Coins, long FinishedAt);

internal sealed record RankedRatingView(long AccountId, int Rating, int GamesPlayed, int Wins, int Draws, int Losses, bool Provisional, int Division, RankedSeasonView Season, IReadOnlyList<RankedSeasonHistoryView> History, long CoinsEarned, long Revision);

/// <summary>A receipt for a server-bound pair. Scores always use the fixed A/B
/// orientation; each player's submitted score is normalized before comparison.</summary>
internal sealed record RankedMatchView(long Id, ulong GameId, long PlayerA, long PlayerB, long CreatedAt, long UpdatedAt, string Status, int? GoalsA, int? GoalsB, bool ReportedA, bool ReportedB, long? ForfeitedBy, int RatingBeforeA, int RatingBeforeB, int RatingAfterA, int RatingAfterB, int CoinsA, int CoinsB, int SeasonCoinsA, int SeasonCoinsB)
{
    public bool Settled => Status == "settled";
    public bool Cancelled => Status == "cancelled";
    public int RatingDeltaA => RatingAfterA - RatingBeforeA;
    public int RatingDeltaB => RatingAfterB - RatingBeforeB;
}

internal sealed record RankedReportView(RankedMatchView Match, RankedRatingView Reporter, bool Duplicate)
{
    public bool Settled => Match.Settled;
    public bool Pending => Match.Status is "pending" or "conflict";
    public bool Conflict => Match.Status == "conflict";
}
