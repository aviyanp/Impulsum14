using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Blaze.Core;
using EATDF.Serialization;
using Microsoft.Extensions.Logging;
using ProtoFire.Tls;

namespace Impulsum14;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--hash-token"))
            {
                Console.WriteLine(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Console.ReadLine() ?? ""))));
                return 0;
            }
            ServerOptions.Current = ServerOptions.Load();
            var options = ServerOptions.Current;
            if (args.Contains("--validate-config")) { Console.WriteLine("Configuration valid."); return 0; }
            using var loggerFactory = LoggerFactory.Create(b => b
                .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                .SetMinimumLevel(Enum.Parse<LogLevel>(options.LogLevel, true)));
            var log = loggerFactory.CreateLogger("Impulsum14");
            if (args.Contains("--bridge")) return await RunBridgeAsync(options, loggerFactory);
            using var repository = new PlayerRepository();
            using var ranked = new RankedService(options.DataDirectory, options.Ranked);
            RankedService.Current = ranked;
            using var cert = new X509Certificate2(options.CertificatePath, Environment.GetEnvironmentVariable("FIFA14_CERTIFICATE_PASSWORD"), X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            var protoCert = ProtoSSLCertificate.FromX509Certificate2(cert);
            var callbacks = new ServerCallbacks(protoCert, loggerFactory.CreateLogger("Blaze"));
            var encoding = Environment.GetEnvironmentVariable("FIFA14_FIRE2") == "1" ? FrameEncoding.Fire2 : FrameEncoding.Fire;
            TdfSerializer serializer = Environment.GetEnvironmentVariable("FIFA14_HEAT1") == "1" ? new HeatSerializer() : new Heat2Serializer();
            var redirector = new BlazeRouter();
            redirector.AddComponent(new RedirectorComponent(options.BlazePort, loggerFactory.CreateLogger("Redirector")));
            var redirServer = new BlazeServer(MakeConfig(options.RedirectorPort, true, redirector), loggerFactory.CreateLogger<BlazeServer>());
            var router = new BlazeRouter();
            router.AddComponent(new UtilComponent(loggerFactory.CreateLogger("Util"), options.PublicWebUrl));
            router.AddComponent<AuthenticationComponent>();
            router.AddComponent<UserSessionsComponent>();
            router.AddComponent<CensusDataComponent>();
            router.AddComponent<AssociationListsComponent>();
            router.AddComponent<MessagingComponent>();
            router.AddComponent<RoomsComponent>();
            router.AddComponent<StatsComponent>();
            router.AddComponent<ClubsComponent>();
            router.AddComponent(new SponsoredEventsComponent());
            router.AddComponent(new OsdkSettingsComponent());
            router.AddComponent<GameReportingComponent>();
            router.AddComponent(new GameManagerComponent(ranked));
            var blazeServer = new BlazeServer(MakeConfig(options.BlazePort, false, router), loggerFactory.CreateLogger<BlazeServer>());
            var web = new WebServer(options.WebPort, loggerFactory.CreateLogger("Web"));
            Legends.ValidateClientManifest();
            if (RealPlayers.All.Length == 0) throw new InvalidDataException("Player database is empty or missing.");
            Market.WarmUp();
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            AssemblyLoadContext.Default.Unloading += _ => SafeCancel(shutdown);
            using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
            var tasks = new[] { redirServer.StartAsync(shutdown.Token), blazeServer.StartAsync(shutdown.Token), web.StartAsync(shutdown.Token) };
            log.LogInformation("Listening on {Bind}; advertising {Host}, web {Web}. Maintenance scheduler {TickRate} Hz.", options.BindAddress, options.AdvertisedAddress, options.PublicWebUrl, options.TickRate);
            await Task.WhenAny(tasks);
            shutdown.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            return 0;

            BlazeServerConfig MakeConfig(int port, bool secure, BlazeRouter r) => new()
            {
                LocalEndpoint = new IPEndPoint(IPAddress.Parse(options.BindAddress), port),
                PacketFrameEncoding = encoding, Serializer = serializer, Secure = secure,
                Certificate = secure ? protoCert : null, CallbackHandler = callbacks, Router = r,
                Backlog = 512, MaxConnections = options.MaxConnections, MaxFrameBytes = options.MaxBodyBytes,
                IdleTimeoutSeconds = options.IdleTimeoutSeconds, SendTimeoutSeconds = options.SendTimeoutSeconds,
                InboundQueueCapacity = options.InboundQueueCapacity, OutboundQueueCapacity = options.OutboundQueueCapacity,
                CommandMiddleware = repository.RunRpcAsync
            };
        }
        catch (Exception ex) { Console.Error.WriteLine($"Server stopped: {ex.Message}"); return 1; }
    }
    private static async Task<int> RunBridgeAsync(ServerOptions options, ILoggerFactory logging)
    {
        if (Environment.GetEnvironmentVariable("FIFA14_FIRE2") == "1" || Environment.GetEnvironmentVariable("FIFA14_HEAT1") == "1") throw new InvalidDataException("PC bridge uses Fire + Heat2.");
        string tokenFile = Environment.GetEnvironmentVariable("FIFA14_UPSTREAM_TOKEN_FILE");
        string token = tokenFile is not null ? File.ReadAllText(tokenFile).Trim() : Environment.GetEnvironmentVariable("FIFA14_UPSTREAM_TOKEN");
        string upstreamWeb = Environment.GetEnvironmentVariable("FIFA14_UPSTREAM_WEB_URL") ?? throw new InvalidDataException("Set FIFA14_UPSTREAM_WEB_URL.");
        string upstreamBlaze = Environment.GetEnvironmentVariable("FIFA14_UPSTREAM_BLAZE_HOST") ?? new Uri(upstreamWeb).Host;
        int upstreamPort = int.TryParse(Environment.GetEnvironmentVariable("FIFA14_UPSTREAM_BLAZE_PORT"), out int p) ? p : 10000;
        var bridge = new ClientBridge(options, upstreamWeb, upstreamBlaze, upstreamPort, token);
        using var cert = new X509Certificate2(options.CertificatePath, Environment.GetEnvironmentVariable("FIFA14_CERTIFICATE_PASSWORD"), X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        var proto = ProtoSSLCertificate.FromX509Certificate2(cert);
        var router = new BlazeRouter(); router.AddComponent(new RedirectorComponent(options.BlazePort, logging.CreateLogger("Redirector")));
        var redirector = new BlazeServer(new BlazeServerConfig { LocalEndpoint = new IPEndPoint(IPAddress.Parse(options.BindAddress), options.RedirectorPort), Router = router, Secure = true, CallbackHandler = new ServerCallbacks(proto, logging.CreateLogger("Bridge")), MaxConnections = 64 }, logging.CreateLogger<BlazeServer>());
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AssemblyLoadContext.Default.Unloading += _ => SafeCancel(stop);
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
        var tasks = new[] { redirector.StartAsync(stop.Token), bridge.StartBlazeAsync(stop.Token), bridge.StartWebAsync(stop.Token) };
        await Task.WhenAny(tasks); stop.Cancel();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return 0;
    }

    private static void SafeCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); } catch (ObjectDisposedException) { }
    }

}
