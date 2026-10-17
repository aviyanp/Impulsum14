using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.Util;
using Blaze3SDK.Components;
using EATDF.Types;
using Microsoft.Extensions.Logging;

namespace Impulsum14;

internal sealed class UtilComponent : UtilComponentBase.Server
{
    private readonly ILogger _log;
    private static string GameIp => ServerOptions.Current.AdvertisedAddress;
    private readonly string _web; // base URL

    // EA's real FIFA 14 asset CDN. Keep static content here until we mirror it locally.
    private const string CdnBase = "https://fifa17.content.easports.com/fifa/fltOnlineAssets/C74DDF38-0B11-49b0-B199-2E2A11D1CC13/2014";
    public UtilComponent(ILogger log, string webBaseUrl) { _log = log; _web = webBaseUrl.TrimEnd('/'); }

    private static uint Now() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public override Task<PreAuthResponse> PreAuthAsync(PreAuthRequest request, BlazeRpcContext context)
    {
        _log.LogInformation("preAuth received");
        return Task.FromResult(new PreAuthResponse
        {
            AuthenticationSource = "303107",
            RegistrationSource = "303107",
            ComponentIds = new List<ushort> { 1, 4, 5, 7, 9, 10, 11, 12, 13, 15, 21, 25, 28, 2049, 2050, 30722 },
            Config = new FetchConfigResponse
            {
                Config = new Dictionary<string, string>
                {
                    { "connIdleTimeout", "120s" },
                    { "defaultRequestTimeout", "80s" },
                    { "pingPeriod", "20s" },
                }
            },
            UnderageSupported = false,
            PersonaNamespace = "cem_ea_id",
            LegalDocGameIdentifier = "fifa-2014-pc",
            Platform = "pc",
            InstanceName = "fifa-2014-pc",
            QosSettings = new QosConfigInfo
            {
                NumLatencyProbes = 10,
                ServiceId = 0,
                BandwidthPingSiteInfo = new QosPingSiteInfo { Address = GameIp, Port = 17502, SiteName = "ea-sjc" },
                PingSiteInfoByAliasMap = new Dictionary<string, QosPingSiteInfo>
                {
                    { "ea-sjc", new QosPingSiteInfo { Address = GameIp, Port = 17502, SiteName = "ea-sjc" } },
                },
            },
            ServerVersion = "FIFA14 Blaze 13.3.0.5.0",
        });
    }

    public override Task<PingResponse> PingAsync(EmptyMessage request, BlazeRpcContext context)
        => Task.FromResult(new PingResponse { ServerTime = Now() });

    private static Dictionary<string, string> UserSettings => PlayerContext.Current.Data.UserSettings;

    public override Task<UserSettingsLoadAllResponse> UserSettingsLoadAllAsync(UserSettingsLoadAllRequest request, BlazeRpcContext context)
        => Task.FromResult(new UserSettingsLoadAllResponse { DataMap = new Dictionary<string, string>(UserSettings) });

    public override Task<UserSettingsResponse> UserSettingsLoadAsync(UserSettingsLoadRequest request, BlazeRpcContext context)
        => Task.FromResult(new UserSettingsResponse { Key = request.Key, Data = UserSettings.TryGetValue(request.Key ?? "", out var v) ? v : "" });

    public override Task<EmptyMessage> UserSettingsSaveAsync(UserSettingsSaveRequest request, BlazeRpcContext context)
    {
        if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 || (request.Data?.Length ?? 0) > 65536 || (UserSettings.Count >= 128 && !UserSettings.ContainsKey(request.Key)))
            throw new BlazeRpcException(4, null, "User settings limit exceeded.");
        UserSettings[request.Key] = request.Data ?? "";
        PlayerContext.MarkDirty();
        return Task.FromResult(new EmptyMessage());
    }

    public override Task<EmptyMessage> SetClientMetricsAsync(ClientMetrics request, BlazeRpcContext context) => Task.FromResult(new EmptyMessage());
    public override Task<LocalizeStringsResponse> LocalizeStringsAsync(LocalizeStringsRequest request, BlazeRpcContext context)
    {
        var map = new Dictionary<string, string>();
        foreach (var id in request.StringIds)
            map[id] = id;
        return Task.FromResult(new LocalizeStringsResponse { LocalizedStrings = map });
    }

    public override Task<EmptyMessage> SetClientDataAsync(ClientData request, BlazeRpcContext context) => Task.FromResult(new EmptyMessage());

    public override Task<FetchConfigResponse> FetchClientConfigAsync(FetchClientConfigRequest request, BlazeRpcContext context)
    {
        //some of these may be useless, ill keep them cause it works and i dont want to break anything
        _log.LogInformation("fetchClientConfig section='{0}'", request.ConfigSection);

        var cfg = new Dictionary<string, string>();
        int webPort;
        try { webPort = new Uri(_web).Port; } catch { webPort = 9988; }

        cfg["OSDK_PEERBUFFERSIZE"]             = "16384";
        cfg["OSDK_DISTBUFFERSIZE_IN"]          = "16384";
        cfg["OSDK_DISTBUFFERSIZE_OUT"]         = "16384";
        cfg["OSDK_MAXGAMES"]                   = "16";
        cfg["OSDK_MAXROOMS"]                   = "16";
        cfg["OSDK_USERROOM_PREFIX"]            = "room";
        cfg["OSDK_MATCHUP_TIMEOUT"]            = "30";
        cfg["OSDK_KEEPALIVEINTERVAL"]          = "30";
        cfg["OSDK_STATS_EMPTY_CELL"]           = "-1";
        cfg["OSDK_TICKER_COUNT"]               = "10";
        cfg["JOIN_GAME_TIMEOUT"]               = "30";
        cfg["OSDK_USERLIST_REQUEST_MAX_USERS"] = "50";
        cfg["POW_MDL_MAX_IMAGESIZE"]           = "1048576";
        cfg["POW_MDL_DELAYNEWSDOWNLOAD"]       = "0";
        string liveContent = $"{_web}/fifa/fltOnlineAssets/C74DDF38-0B11-49b0-B199-2E2A11D1CC13/2014";
        cfg["LIVE_CONTENT_HOST"]               = $"{liveContent}/";
        cfg["ROSTERUPDATE_URL"]                = $"{liveContent}/rosterupdate";
        cfg["ROSTER_URL"]                      = $"{liveContent}/roster";
        cfg["EASW/ENABLED"]                    = "1";
        cfg["OSDK_EASW_REQ_URL"]               = $"{_web}/easw/req";
        cfg["OSDK_EASW_AUTH_URL"]              = $"{_web}/easw/auth";
        cfg["OSDK_EASW_EVENT_URL"]             = $"{_web}/easw/event";
        cfg["OSDK_EASW_MEDIA_URL"]             = $"{_web}/easw/media";
        cfg["OSDK_EASW_GF_FILE_URL"]           = $"{_web}/easw/gf";
        cfg["OSDK_EASW_ALLOWED_LOCALES"]       = "en_US,en_GB,en_US.UTF-8,enUS,enGB,fr_FR,de_DE,es_ES,it_IT,pt_BR,nl_NL";
        cfg["OSDK_EASW_CONNECT_RETRY_PERIOD"]  = "30";
        cfg["CMS_BASE_URL"]                    = $"{_web}/cms";
        cfg["CMS_APIKEY"]                      = "fifa14";
        cfg["CMS_SKUID"]                       = "FFA14PCC";
        cfg["FUT_URI"]                         = $"{_web}/fut";
        cfg["FUT_RS4_BASE_URL"]                = $"{_web}/fut/rs4";
        cfg["FUT/ROSTERUPDATE_URL"]            = $"{_web}/fut/rosterupdate";
        cfg["FUTDYNAMICMESSAGES_URL_BASE"]     = $"{_web}/fut/dynamicmessages";
        cfg["FUTBOOTCFGFILE_URL"]              = $"{_web}/fut/";
        cfg["ONLINE/SERVER_RS4"]               = $"{_web}";
        cfg["FIFA_RS4_URL"]                    = $"{_web}";
        cfg["FIFA_RS4_TIMEOUT"]                = "30";
        cfg["FIFALEADERBOARD_BASE_URL"]        = $"{_web}/leaderboard";
        cfg["ROUTINGCFGFILE_URL"]              = $"{_web}/dime/dimerouting.xml";
        cfg["DIME_FILES_PATH"]                 = $"{_web}/fifa/dime/gen4/";
        cfg["DIME_IMG_FILES_PATH"]             = $"{_web}/fifa/dime/gen4/";
        cfg["DOWNLOADER_PATH"]                 = $"{_web}/fifa/dl/gen4/";
        cfg["nucleusHost"]    = GameIp;
        cfg["nucleusPort"]    = webPort.ToString();
        cfg["nucleusPortSSL"] = "443";
        cfg["nucleusBaseUri"] = "/";
        cfg["environment"]    = "prod";
        cfg["useNucleusSSL"]  = "0";
        cfg["AUTH_TYPE"]           = "NUCLEUS";
        cfg["CLIENT_TIMEOUT"]      = "90";
        cfg["OSDK_AUTH_REQUIRED"]  = "1";
        cfg["OSDK_ONLINE_ENABLED"] = "1";
        cfg["OSDK_SERVER_VERSION"] = "3.15.08.0";
        cfg["REQUEST_TIMEOUT"]     = "80";
        cfg["USE_TOKEN_AUTH"]      = "1";
        cfg["ALLOW_OFFLINE"]                = "1";
        cfg["AUTOLOGIN"]                    = "1";
        cfg["EMAIL_OPT_IN"]                 = "0";
        cfg["FIFA_POW_CONTENT_SERVER_URL"]  = $"{_web}/";
        cfg["FIFA_POW_MMM_URI"]             = $"{_web}/";
        cfg["FIFA_POW_NUCLEUS_PROXY_URL"]   = $"{_web}/";
        cfg["FIFA_POW_URL"]                 = $"{_web}/";
        cfg["FUT/ALWAYS_SHOW_QUESTS_PANEL"]    = "0";
        cfg["FUT/ALWAYS_SHOW_SMART_TUTORIALS"] = "0";
        cfg["FUT/ALWAYS_SHOW_TUTORIALS"]       = "0";
        cfg["FUT/DAY60_FIX_ENABLED"]           = "0";
        cfg["FUT/ENABLED"]                     = "1";
        cfg["FUT/FAKE_CARDS0_FAIL"]            = "0";
        cfg["FUT/FUT_STAT_TUNING"]             = "";
        cfg["FUT/HTTP_FRAME_DELAY"]            = "0";
        cfg["FUT/LOG_RPUPS"]                   = "0";
        cfg["FUT/OVERRIDE_VERSION"]            = "";
        cfg["FUT/ROSTERUPDATE_URL"]            = "";
        cfg["NUCLEUS_ADDED_URL"]             = $"{_web}/nucleus/added";
        cfg["NUCLEUS_CREATE_INFO_URL"]       = $"{_web}/nucleus/create_info";
        cfg["NUCLEUS_CREATE_URL"]            = $"{_web}/nucleus/create";
        cfg["NUCLEUS_DEACTIVATED_INFO_URL"]  = $"{_web}/nucleus/deactivated_info";
        cfg["NUCLEUS_DUPACCT_INFO_URL"]      = $"{_web}/nucleus/dupacct_info";
        cfg["NUCLEUS_INCOMPLETE_URL"]        = $"{_web}/nucleus/incomplete";
        cfg["NUCLEUS_LOGIN_ENABLED"]         = "1";
        cfg["ONLINE/POW_CUSTOMCONTENTURL"]   = $"{GameIp}:{webPort}";
        cfg["ONLINE/POW_CUSTOMURL"]          = $"{GameIp}:{webPort}";
        cfg["ORIGIN_LOGIN_ENABLED"]          = "1";
        cfg["POW/ASSERT_POW_ERROR"]              = "0";
        cfg["POW/ENABLE_ALL_UNLOCKABLES"]        = "1";
        cfg["POW/ENABLE_RPUPS"]                  = "0";
        cfg["POW/ENABLE_USER_NEWS"]              = "0";
        cfg["POW/FIRST_BOOT_ACTIVITY"]           = "0";
        cfg["POW/FORCE_SCENARIO_COMPLETE"]       = "1";
        cfg["POW/POW_DISABLE_ERROR_BACKOUT"]     = "1";
        cfg["POW/POW_WIDGET"]                    = "1";
        cfg["POW/SEND_ACTIVITIES"]               = "0";
        cfg["POW/SKIP_SCENARIO_ROSTER_DOWNLOAD"] = "1";
        cfg["POW/STORE_CUSTOM_CATALOG"]          = "0";
        cfg["PRIVATE_BETA"]                = "0";
        cfg["SKIP_LEGAL_DOC"]              = "1";

        string futAssets = $"{_web}/onlineAssets/2014/fut/";
        foreach (var m in new[] { "ACADEMY","AUCTIONS","BOOTSTRAP","CARDS","CLUB","DEFAULT",
            "EVENTS","GAMEHUB","ITEMS","LEADERBOARDS","MAIN","PACKS","QUESTS","ROOT","RS4",
            "SQUAD","STORE","TUTORIAL","WEBCONTENT","WEBSESSION" })
        {
            cfg[$"FUT/MODULE_BASEURL_{m}"] = futAssets;
            cfg[$"FUT/SINGLE_BASEURL_{m}"] = futAssets;
        }

        cfg["WEBOFFER_BASE_URI"] = "/";
        cfg["WEBOFFER_ENABLED"]  = "0";
        cfg["WEBOFFER_HOST"]     = GameIp;
        cfg["WEBOFFER_PORT"]     = webPort.ToString();
        cfg["ABUSE_REPORTING_ENABLED"] = "0";
        cfg["EVENTS_URL"]              = "fifa/sponsoredevents/events_list.xml";
        cfg["SPONSORED_EVENT_ENABLED"] = "1";
        cfg["SPONSORED_EVENT_URL"]     = "fifa/sponsoredevents/events_list.xml";
        cfg["ROSTER_UPDATE_ENABLED"] = "0";
        cfg["ROSTER_UPDATE_URL"]     = "fifa/fifalive/rosterupdate.xml";
        cfg["BASE_URL"]         = $"{_web}/fifa/fltOnlineAssets/C74DDF38-0B11-49b0-B199-2E2A11D1CC13/2014";
        cfg["CONTENT_BASE_URL"] = $"{_web}/fifa/fltOnlineAssets/C74DDF38-0B11-49b0-B199-2E2A11D1CC13/2014";

        return Task.FromResult(new FetchConfigResponse { Config = cfg });
    }

    public override Task<GetTelemetryServerResponse> GetTelemetryServerAsync(GetTelemetryServerRequest request, BlazeRpcContext context)
        => Task.FromResult(Tele());

    public override Task<PostAuthResponse> PostAuthAsync(EmptyMessage request, BlazeRpcContext context)
    {
        _log.LogInformation("postAuth received");
        return Task.FromResult(new PostAuthResponse
        {
            TelemetryServer = Tele(),
            TickerServer = new GetTickerServerResponse { Address = GameIp, Port = 6776, Key = "key" },
            UserOptions = new UserOptions { TelemetryOpt = TelemetryOpt.TELEMETRY_OPT_OUT },
        });
    }

    private static GetTelemetryServerResponse Tele() => new()
    {
        Address = GameIp,
        Disable = "disa",
        Filter = "filt",
        IsAnonymous = false,
        Key = "key",
        Locale = 1701729619,
        NoToggleOk = "nook",
        Port = 6767,
        SendDelay = 10,
        SendPercentage = 10,
        SessionID = "id",
        UseServerTime = "0",
    };
}
