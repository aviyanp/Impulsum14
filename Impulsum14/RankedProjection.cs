namespace Impulsum14;

/// <summary>The ranked ledger is authority for both opponents. Project its cumulative
/// receipts into this account's atomic snapshot; retrying after a crash only credits
/// the difference, including when the other player settled while this user was away.</summary>
internal static class RankedProjection
{
    internal static void ApplyCurrent()
    {
        var ranked = RankedService.Current;
        if (ranked is null || PlayerContext.Current.Id <= 0) return;
        if (!ranked.TryGetRating(PlayerContext.Current.Id, out var view)) return;
        var profile = FutProfileStore.Get();
        if (profile.RankedRevisionApplied == view.Revision) return;
        if (view.CoinsEarned < profile.RankedCoinsApplied || view.Wins < profile.RankedWinsApplied || view.Draws < profile.RankedDrawsApplied || view.Losses < profile.RankedLossesApplied)
            throw new InvalidDataException("Ranked ledger is older than this account's applied receipts.");
        PlayerContext.MarkDirty();
        checked
        {
            profile.Coins += view.CoinsEarned - profile.RankedCoinsApplied;
            profile.Wins += view.Wins - profile.RankedWinsApplied;
            profile.Draws += view.Draws - profile.RankedDrawsApplied;
            profile.Losses += view.Losses - profile.RankedLossesApplied;
        }
        profile.RankedCoinsApplied = view.CoinsEarned;
        profile.RankedWinsApplied = view.Wins; profile.RankedDrawsApplied = view.Draws; profile.RankedLossesApplied = view.Losses;
        profile.RankedRevisionApplied = view.Revision;
        profile.OnlineDivision = view.Division;
        var season = profile.OnlineSeason;
        if (season.SeasonId != view.Season.SeasonId)
        {
            // Opaque data from the old season can never resume a new one.
            if (season.Data.Length > 0 || season.ProgressData.Length > 0)
                CompetitionSave.Retire(season.RetiredSaveHashes, CompetitionSave.Fingerprint(season.SavedRound, season.Data, season.ProgressData));
            season = new FutSeason { SeasonId = view.Season.SeasonId, RetiredSaveHashes = new(season.RetiredSaveHashes) };
            profile.OnlineSeason = season;
        }
        season.GamesPlayed = view.Season.GamesPlayed; season.GamesWon = view.Season.GamesWon;
        season.GamesDraw = view.Season.GamesDraw; season.GamesLost = view.Season.GamesLost; season.Points = view.Season.Points;
        season.TitlesWon = view.Season.TitlesWon; season.Promotions = view.Season.Promotions; season.Relegations = view.Season.Relegations;
        profile.SeasonHistory.RemoveAll(h => h.Online);
        profile.SeasonHistory.AddRange(view.History.Select(h => new SeasonHistoryEntry(h.SeasonId, h.Division, true, h.Points, h.Won, h.Draw, h.Lost, h.Outcome, h.Coins, h.FinishedAt)));
        if (profile.SeasonHistory.Count > 100) profile.SeasonHistory = profile.SeasonHistory.OrderByDescending(h => h.FinishedAt).Take(100).OrderBy(h => h.FinishedAt).ToList();
    }
}
