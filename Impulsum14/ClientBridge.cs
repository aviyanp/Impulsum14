using System.Net;
using System.Net.Sockets;
using Blaze3SDK.Blaze.Authentication;
using Blaze3SDK.Blaze.Util;
using Blaze3SDK.Components;
using EATDF.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtoFire;
using ProtoFire.Frames;

namespace Impulsum14;

/// <summary>Loopback adapter for the existing PC launcher. Keeps the private server token
/// out of game files; rewrites the advertised web origin to the local authenticated proxy.</summary>
internal sealed class ClientBridge
{
    private readonly ServerOptions _local;
    private readonly string _token;
    private readonly Uri _upstream;
    private readonly string _blazeHost;
    private readonly int _blazePort;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _webRequests;
    internal ClientBridge(ServerOptions local, string webUrl, string blazeHost, int blazePort, string token)
    {
        if (!IPAddress.IsLoopback(IPAddress.Parse(local.BindAddress)) || string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(webUrl, UriKind.Absolute, out var upstream) || upstream.Scheme is not ("http" or "https") || upstream.AbsolutePath != "/" || !string.IsNullOrEmpty(upstream.UserInfo) || upstream.Query.Length != 0 || upstream.Fragment.Length != 0 || string.IsNullOrWhiteSpace(blazeHost) || token.Any(char.IsControl) || token.Length > 8192 || blazePort is < 1 or > 65535) throw new InvalidDataException("Bridge needs a loopback bind, an upstream HTTP(S) origin, a Blaze host/port, and FIFA14_UPSTREAM_TOKEN.");
        _local = local; _token = token; _upstream = upstream; _blazeHost = blazeHost; _blazePort = blazePort;
        int capacity = Math.Min(256, local.MaxConnections);
        _webRequests = new SemaphoreSlim(capacity, capacity);
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = capacity, ConnectTimeout = TimeSpan.FromSeconds(local.SendTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal async Task StartWebAsync(CancellationToken cancel)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Parse(_local.BindAddress), _local.WebPort); k.Limits.MaxRequestBodySize = _local.MaxBodyBytes; k.Limits.MaxConcurrentConnections = _local.MaxConnections; k.AddServerHeader = false; });
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (!_webRequests.Wait(0)) { context.Response.StatusCode = 503; return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(_local.RequestTimeoutSeconds));
            try
            {
                string target = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal)) { context.Response.StatusCode = 400; return; }
                var destination = new Uri(_upstream, target);
                if (destination.Authority != _upstream.Authority || destination.Scheme != _upstream.Scheme) { context.Response.StatusCode = 400; return; }
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), destination);
                if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(context.Request.Body);
                foreach (var header in context.Request.Headers)
                {
                    if (HopHeader(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                try
                {
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                        if (!HopHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
                    await response.Content.CopyToAsync(context.Response.Body, deadline.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                { if (!context.Response.HasStarted) context.Response.StatusCode = 502; else context.Abort(); }
            }
            finally { _webRequests.Release(); }
        });
        try { await app.RunAsync(cancel); }
        finally { _http.Dispose(); }
    }
    private static bool HopHeader(string key) => key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Connection", StringComparison.OrdinalIgnoreCase) || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || key.Equals("TE", StringComparison.OrdinalIgnoreCase) || key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) || key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) || key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) || key.Equals("Trailer", StringComparison.OrdinalIgnoreCase);

    internal async Task StartBlazeAsync(CancellationToken cancel)
    {
        var listener = new TcpListener(IPAddress.Parse(_local.BindAddress), _local.BlazePort);
        listener.Start(128);
        var active = new HashSet<Task>();
        using var limit = new SemaphoreSlim(_local.MaxConnections, _local.MaxConnections);
        using var relays = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        async Task RelayLimited(TcpClient client)
        {
            try { await Relay(client, relays.Token); }
            finally { limit.Release(); }
        }
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancel);
                if (!limit.Wait(0)) { client.Dispose(); continue; }
                var work = RelayLimited(client);
                foreach (Task completed in active.Where(t => t.IsCompleted).ToArray())
                {
                    await completed; // Observe unexpected relay failures before pruning.
                    active.Remove(completed);
                }
                active.Add(work);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { listener.Stop(); relays.Cancel(); await Task.WhenAll(active); }
    }
    private async Task Relay(TcpClient client, CancellationToken cancel)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            client.NoDelay = true; upstream.NoDelay = true;
            using var registration = connection.Token.Register(() => { client.Dispose(); upstream.Dispose(); });
            try
            {
                using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                {
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(_local.SendTimeoutSeconds));
                    await upstream.ConnectAsync(_blazeHost, _blazePort, connectDeadline.Token);
                }
                var toServer = Pump(client.GetStream(), upstream.GetStream(), true, connection);
                var toClient = Pump(upstream.GetStream(), client.GetStream(), false, connection);
                await Task.WhenAny(toServer, toClient); connection.Cancel();
                await Task.WhenAll(toServer, toClient);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException or InvalidDataException or ArgumentException or InvalidOperationException) { }
        }
    }
    private async Task Pump(Stream input, Stream output, bool outbound, CancellationTokenSource connection)
    {
        var serializer = new Heat2Serializer();
        while (true)
        {
            ProtoFirePacket? packet;
            using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
            {
                readDeadline.CancelAfter(TimeSpan.FromSeconds(_local.IdleTimeoutSeconds));
                packet = await ProtoFirePacket.ReadFromAsync<FireFrame>(input, _local.MaxBodyBytes, readDeadline.Token);
            }
            if (packet is null) return;
            if (outbound && packet.Frame.Component == 1)
            {
                if (packet.Frame.Command == (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin)
                { var login = new OriginLoginRequest(); if (!serializer.Deserialize(packet.GetDataStream(), login)) return; login.AuthToken = _token; packet.Data = serializer.Serialize(login); }
                else if (packet.Frame.Command == (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.silentLogin)
                { var login = new SilentLoginRequest(); if (!serializer.Deserialize(packet.GetDataStream(), login)) return; login.AuthToken = _token; packet.Data = serializer.Serialize(login); }
                else if (packet.Frame.Command == (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.login)
                { var login = new LoginRequest(); if (!serializer.Deserialize(packet.GetDataStream(), login)) return; login.Token = _token; login.Password = ""; packet.Data = serializer.Serialize(login); }
                else if (packet.Frame.Command == (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.expressLogin)
                { var login = new ExpressLoginRequest(); if (!serializer.Deserialize(packet.GetDataStream(), login)) return; login.Password = _token; packet.Data = serializer.Serialize(login); }
            }
            if (!outbound && packet.Frame.Component == 9 && packet.Frame.Command == (ushort)UtilComponentBase.UtilComponentCommand.fetchClientConfig && packet.Frame.ErrorCode == 0)
            {
                var config = new FetchConfigResponse();
                if (!serializer.Deserialize(packet.GetDataStream(), config)) return;
                foreach (string key in config.Config.Keys.ToArray()) config.Config[key] = config.Config[key].Replace(_upstream.GetLeftPart(UriPartial.Authority), _local.PublicWebUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                packet.Data = serializer.Serialize(config);
            }
            if (packet.Data.Length > _local.MaxBodyBytes) throw new InvalidDataException("Rewritten Blaze frame exceeds limit.");
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(_local.SendTimeoutSeconds));
            await packet.WriteToAsync(output, writeDeadline.Token);
        }
    }
}
