using System.Text.Json;

namespace Impulsum14;

internal sealed class FutClub
{
    private sealed record VisualCatalog(CosmeticItem[] Badges, CosmeticItem[] HomeKits, CosmeticItem[] AwayKits, CosmeticItem[] Stadiums);
    private static readonly Lazy<VisualCatalog> Visuals = new(() => new(
        ClubItems.Catalog.Where(c => c.Type == "badge").ToArray(),
        ClubItems.Catalog.Where(c => c.Type == "kit" && c.Rare == 0).ToArray(),
        ClubItems.Catalog.Where(c => c.Type == "kit" && c.Rare == 1).ToArray(),
        ClubItems.Catalog.Where(c => c.Type == "stadium" && (c.Rating < 75 || c.Rare == 0)).ToArray()));
    public bool Established { get; set; } = false;   // false => new player, no club yet
    public long EstablishedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // unix seconds when the club was created
    public long TeamId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Abbr { get; set; } = "";
    public int BadgeId { get; set; } = 0;
    public int StadiumId { get; set; } = 0;
    public int KitId { get; set; } = 0;

    public long ActiveStadiumId { get; set; } = 6200047;
    public long ActiveBallId { get; set; } = 8120092;
    public long ActiveHomeKitId { get; set; } = 6300070;
    public long ActiveAwayKitId { get; set; } = 6400073;
    public long ActiveBadgeId { get; set; } = 6000070;

    public FutClub()
    {
        try
        {
            var rnd = Random.Shared;
            var visuals = Visuals.Value;
            var badges = visuals.Badges;
            if (badges.Length > 0)
            {
                var b = badges[rnd.Next(badges.Length)];
                ActiveBadgeId = b.ResourceId;
                BadgeId = b.AssetId;
                TeamId = b.TeamId != 0 ? b.TeamId : b.AssetId;
            }
            var nonRareKits = visuals.HomeKits;
            if (nonRareKits.Length > 0) ActiveHomeKitId = nonRareKits[rnd.Next(nonRareKits.Length)].ResourceId;
            var rareKits = visuals.AwayKits;
            if (rareKits.Length > 0) ActiveAwayKitId = rareKits[rnd.Next(rareKits.Length)].ResourceId;
            var stadiums = visuals.Stadiums;
            if (stadiums.Length > 0) ActiveStadiumId = stadiums[rnd.Next(stadiums.Length)].ResourceId;
        }
        catch { }
    }
}

internal sealed class FutSeason
{
    public int SeasonId { get; set; } = 1;
    public int CatalogId { get; set; } = 1;
    public int CatalogDivision { get; set; } = 10;
    public int SavedRound { get; set; } = 1;
    public int SavedSeasonId { get; set; }
    public int CatalogSeasonId { get; set; }
    public List<string> RetiredSaveHashes { get; set; } = new();
    public string Data { get; set; } = "";
    public string ProgressData { get; set; } = "";
    public int DataVersion { get; set; } = 1;
    public int ProgressDataVersion { get; set; } = 1;
    public int Points { get; set; } = 0;
    public int GamesPlayed { get; set; } = 0;
    public int GamesWon { get; set; } = 0;
    public int GamesLost { get; set; } = 0;
    public int GamesDraw { get; set; } = 0;
    public int TitlesWon { get; set; } = 0;
    public int Promotions { get; set; } = 0;
    public int Relegations { get; set; } = 0;
    public int Coins { get; set; } = 0;
    public bool Completed { get; set; } = false;
}

internal sealed class SavedTournament
{
    public int CompletedRounds { get; set; }
    public long RunId { get; set; } = 1;
    public long SavedRunId { get; set; }
    public List<string> RetiredSaveHashes { get; set; } = new();
    public int Round { get; set; } = 1;             
    public int DataVersion { get; set; } = 1;
    public string TournamentData { get; set; } = "";
    public int ProgressDataVersion { get; set; } = 1;
    public string ProgressData { get; set; } = "";
    public bool Active { get; set; } = false;         
    public bool Won { get; set; } = false;         
}

internal sealed class FutProfile
{
    public long NucleusId { get; set; } = 1000;
    public string PersonaName { get; set; } = UserConfig.Username;
    public bool IsReturningUser { get; set; } = false;   // new player by default
    public long Coins { get; set; } = 0;
    public long FifaPoints { get; set; } = 0;
    public FutClub Club { get; set; } = new();
    public int OfflineDivision { get; set; } = 10;
    public int OnlineDivision { get; set; } = 10;
    public int TrophiesWon { get; set; } = 0;            // FUT offline tournament cups won (unlocks higher cups)
    public Dictionary<int, int> TournamentWins { get; set; } = new();
    public long RankedCoinsApplied { get; set; }
    public long RankedRevisionApplied { get; set; }
    public int RankedWinsApplied { get; set; }
    public int RankedDrawsApplied { get; set; }
    public int RankedLossesApplied { get; set; }
    public int Wins { get; set; } = 0;        
    public int Draws { get; set; } = 0;
    public int Losses { get; set; } = 0;
    public FutSeason Season { get; set; } = new();
    public FutSeason OnlineSeason { get; set; } = new();
    public List<SeasonHistoryEntry> SeasonHistory { get; set; } = new();
    public string SeasonSaveBlob { get; set; } = "";     // client's encoded season save (captured, never advertised back)
    // In-progress offline tournaments, keyed by tournament id -> the client's saved bracket state.
    public Dictionary<int, SavedTournament> SavedTournaments { get; set; } = new();
    public Dictionary<int, int> PacksSinceSpecial { get; set; } = new();
}

internal static class FutProfileStore
{
    private static object _lock => PlayerContext.Current.Sync;
    private static FutProfile _profile => PlayerContext.Current.Data.Profile;

    public static FutProfile Get()
    {
        lock (_lock) return _profile;
    }

    public static void Mutate(Action<FutProfile> change)
    {
        lock (_lock)
        {
            PlayerContext.MarkDirty();
            change(_profile);
            Save();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _profile.IsReturningUser = false;
            _profile.Coins = 0;
            _profile.FifaPoints = 0;
            _profile.Club = new FutClub();
            _profile.OfflineDivision = 10;
            _profile.OnlineDivision = 10;
            _profile.TrophiesWon = 0;
            _profile.TournamentWins.Clear();
            // Ranked receipts remain account-scoped across club deletion. Rebuild the
            // season projection next time, without paying previous rewards again.
            _profile.RankedRevisionApplied = -1;
            _profile.Wins = 0;
            _profile.Draws = 0;
            _profile.Losses = 0;
            _profile.Season = new FutSeason();
            _profile.OnlineSeason = new FutSeason();
            _profile.SeasonSaveBlob = "";
            _profile.SavedTournaments = new();
            _profile.SeasonHistory.Clear();
            _profile.PacksSinceSpecial.Clear();
            Save();
        }
    }

    private static void Save() => PlayerContext.MarkDirty();
}
