using System.Net;
using System.Text.Json;

namespace Impulsum14;

/// <summary>One validated configuration used by listeners, advertised URLs and services.</summary>
internal sealed class ServerOptions
{
    internal static ServerOptions Current { get; set; } = new();
    public string BindAddress { get; set; } = "127.0.0.1";
    public string AdvertisedAddress { get; set; } = "127.0.0.1";
    public string PublicWebUrl { get; set; } = "http://127.0.0.1:9988";
    public int RedirectorPort { get; set; } = 42127;
    public int BlazePort { get; set; } = 10000;
    public int WebPort { get; set; } = 9988;
    public string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Profile");
    public string CertificatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "certs", "server.pfx");
    public string AccountsFile { get; set; } = "";
    public bool LocalMode { get; set; } = true;
    public int TickRate { get; set; } = 30;
    public int MaintenanceIntervalMs { get; set; } = 1000;
    public int MaxConnections { get; set; } = 2048;
    public int MaxPlayers { get; set; } = 1000;
    public int MaxBodyBytes { get; set; } = 1024 * 1024;
    public int ResponseCacheBytes { get; set; } = 131072;
    public int ResponseCacheEntries { get; set; } = 8;
    public int MaxPageSize { get; set; } = 500;
    public int RequestsPerSecond { get; set; } = 120;
    public int SessionLifetimeMinutes { get; set; } = 720;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int IdleTimeoutSeconds { get; set; } = 120;
    public int SendTimeoutSeconds { get; set; } = 15;
    public int InboundQueueCapacity { get; set; } = 32;
    public int OutboundQueueCapacity { get; set; } = 64;
    public int MatchmakingQueueLimit { get; set; } = 1000;
    public int MatchmakingConcurrentSetups { get; set; } = 16;
    public int MatchmakingInitialEloRange { get; set; } = 100;
    public int MatchmakingEloRangeStep { get; set; } = 50;
    public int MatchmakingEloRangeIntervalSeconds { get; set; } = 10;
    public int MatchmakingMaxEloRange { get; set; } = 1000;
    public int MatchmakingMaxWaitSeconds { get; set; } = 120;
    public RankedOptions Ranked { get; set; } = new();
    public SeasonRule[] OfflineDivisionRules { get; set; } = Array.Empty<SeasonRule>();
    public SeasonRule[] OnlineDivisionRules { get; set; } = Array.Empty<SeasonRule>();
    public int MarketPlayers { get; set; } = 230000;
    public int MarketConsumables { get; set; } = 50000;
    public int MarketClubItems { get; set; } = 100000;
    public int MarketStaff { get; set; } = 60000;
    public int MarketScanLimit { get; set; } = 25000;
    public int MatchWinCoins { get; set; } = 500;
    public int MatchDrawCoins { get; set; } = 300;
    public int MatchLossCoins { get; set; } = 200;
    public int GoalCoins { get; set; } = 20;
    public int ManagerTaskCoins { get; set; } = 150;
    public int ManagerCompletionCoins { get; set; } = 1000;
    public bool EnablePeerLobbies { get; set; }
    public bool EnableLegends { get; set; }
    public string LegendClientManifest { get; set; } = "";
    public double LegendChance { get; set; } = 0.001;
    public bool ResumeCompetitionBlobs { get; set; } = true;
    public string LogLevel { get; set; } = "Warning";

    internal static ServerOptions Load()
    {
        string path = Environment.GetEnvironmentVariable("FIFA14_CONFIG") ?? Path.Combine(AppContext.BaseDirectory, "server.json");
        if (Environment.GetEnvironmentVariable("FIFA14_CONFIG") is not null && !File.Exists(path)) throw new FileNotFoundException("Configured server file not found.", path);
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("Empty server configuration.")
            : new ServerOptions();
        // Environment wins over the file. Existing launcher variables remain supported.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["REDIR_PORT"] = nameof(RedirectorPort), ["BLAZE_PORT"] = nameof(BlazePort), ["WEB_PORT"] = nameof(WebPort),
          ["BIND_ADDRESS"] = nameof(BindAddress), ["ADVERTISED_ADDRESS"] = nameof(AdvertisedAddress),
          ["PUBLIC_WEB_URL"] = nameof(PublicWebUrl), ["DATA_DIRECTORY"] = nameof(DataDirectory),
          ["ACCOUNTS_FILE"] = nameof(AccountsFile), ["LOCAL_MODE"] = nameof(LocalMode),
          ["TICK_RATE"] = nameof(TickRate), ["LOG_LEVEL"] = nameof(LogLevel) };
        foreach (var pair in env)
        {
            string value = Environment.GetEnvironmentVariable("FIFA14_" + pair.Key);
            if (value is null) continue;
            var prop = typeof(ServerOptions).GetProperty(pair.Value)!;
            prop.SetValue(options, prop.PropertyType == typeof(bool) ? value == "1" || (value != "0" && bool.Parse(value)) : Convert.ChangeType(value, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
        }
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out var bind) || !IPAddress.TryParse(AdvertisedAddress, out var advertised) || advertised.Equals(IPAddress.Any) || advertised.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("BindAddress must be an IP; AdvertisedAddress must be a reachable IPv4 address.");
        if (!Uri.TryCreate(PublicWebUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || url.AbsolutePath != "/")
            throw new InvalidDataException("PublicWebUrl must be an HTTP(S) origin, including the externally reachable port.");
        foreach (int port in new[] { RedirectorPort, BlazePort, WebPort })
            if (port is < 1 or > 65535) throw new InvalidDataException("Listener ports must be 1..65535.");
        if (new[] { RedirectorPort, BlazePort, WebPort }.Distinct().Count() != 3) throw new InvalidDataException("Listener ports must differ.");
        if (LocalMode && !IPAddress.IsLoopback(bind)) throw new InvalidDataException("LocalMode requires a loopback bind. Set LocalMode=false and provision accounts for LAN/hosted operation.");
        if (!LocalMode && (string.IsNullOrWhiteSpace(AccountsFile) || !File.Exists(AccountsFile))) throw new InvalidDataException("Hosted mode requires an AccountsFile.");
        if (TickRate is not (30 or 60)) throw new InvalidDataException("TickRate must be 30 or 60.");
        if (SendTimeoutSeconds is < 1 or > 300 || InboundQueueCapacity is < 1 or > 1024 || OutboundQueueCapacity is < 1 or > 1024)
            throw new InvalidDataException("Invalid transport queue or timeout limits.");
        if (MatchmakingConcurrentSetups is < 1 or > 256 || MatchmakingQueueLimit is < 1 or > 100000 || MatchmakingInitialEloRange is < 0 or > 10000 || MatchmakingEloRangeStep is < 0 or > 10000 || MatchmakingEloRangeIntervalSeconds is < 1 or > 300 || MatchmakingMaxEloRange < MatchmakingInitialEloRange || MatchmakingMaxEloRange > 10000 || MatchmakingMaxWaitSeconds is < 1 or > 600)
            throw new InvalidDataException("Invalid matchmaking limits.");
        ValidateDivisionRules(OfflineDivisionRules); ValidateDivisionRules(OnlineDivisionRules);
        if (Ranked is null) throw new InvalidDataException("Ranked configuration cannot be null.");
        Ranked.Validate();
        if (ResponseCacheBytes is < 0 or > 1048576 || ResponseCacheEntries is < 0 or > 64) throw new InvalidDataException("Invalid response cache limits.");
        if (MaxConnections is < 1 or > 100000 || MaxPlayers is < 1 or > 100000 || RequestsPerSecond is < 1 or > 10000 || MaxBodyBytes is < 1024 or > 16777216 || MaxPageSize is < 1 or > 2000 || MarketScanLimit is < 100 or > 500000 || MaintenanceIntervalMs is < 100 or > 60000 || SessionLifetimeMinutes is < 1 or > 10080 || RequestTimeoutSeconds is < 1 or > 300 || IdleTimeoutSeconds is < 5 or > 3600)
            throw new InvalidDataException("Server limits are outside their supported range.");
        if (MarketPlayers is < 1000 or > 5000000 || MarketConsumables is < 100 or > 5000000 || MarketClubItems is < 100 or > 5000000 || MarketStaff is < 100 or > 5000000) throw new InvalidDataException("Invalid synthetic market size.");
        if (new[] { MatchWinCoins, MatchDrawCoins, MatchLossCoins, GoalCoins, ManagerTaskCoins, ManagerCompletionCoins }.Any(x => x < 0 || x > 1000000) || !double.IsFinite(LegendChance) || LegendChance is < 0 or > 1) throw new InvalidDataException("Invalid economy configuration.");
        if (!Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(LogLevel, true, out _)) throw new InvalidDataException("Invalid LogLevel.");
        DataDirectory = Path.GetFullPath(DataDirectory);
    }

    private static void ValidateDivisionRules(SeasonRule[] rules)
    {
        if (rules is null || rules.Length is not (0 or 10)) throw new InvalidDataException("Division rules require ten rows ordered Division 1 to 10, or an empty default table.");
        for (int i = 0; i < rules.Length; i++)
        {
            var row = rules[i];
            if (row is null || row.Title is < 1 or > 30 || row.Hold < 0 || row.Hold > row.Title ||
                (i == 0 ? row.Promotion != 0 : row.Promotion <= 0 || row.Promotion > row.Title || row.Promotion < row.Hold) ||
                (i == 9 && row.Hold != 0) || new[] { row.TitleCoins, row.PromotionCoins, row.HoldCoins, row.RelegationCoins }.Any(c => c is < 0 or > 1000000))
                throw new InvalidDataException("Invalid division thresholds or rewards.");
        }
    }
}
