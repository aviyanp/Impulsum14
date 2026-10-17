using Blaze3SDK.Components;
using Blaze.Core;
using EATDF;
using Microsoft.Extensions.Logging;
using ProtoFire;
using ProtoFire.Frames;
using ProtoFire.Tls;

namespace Impulsum14;

internal sealed class ServerCallbacks : IBlazeServerCallbacks
{
    private readonly ProtoSSLCertificate _cert;
    private readonly ILogger _log;
    public ServerCallbacks(ProtoSSLCertificate cert, ILogger log) { _cert = cert; _log = log; }

    public ProtoSSLCertificate SelectCertificate(BlazeRpcConnection connection) => _cert;

    public Task<bool> OnConnectedAsync(BlazeRpcConnection connection)
    { _log.LogInformation("client connected"); return Task.FromResult(true); }

    public Task OnAuthenticatedAsync(BlazeRpcConnection connection, bool secure)
    { _log.LogInformation("TLS authenticated (secure={0})", secure); return Task.CompletedTask; }

    public Task OnDisconnectedAsync(BlazeRpcConnection connection)
    { GameManagerComponent.Instance?.Disconnected(connection); _log.LogInformation("client disconnected"); return Task.CompletedTask; }

    public Task OnErrorAsync(BlazeRpcConnection connection, Exception exception)
    { _log.LogWarning("connection error: {0}", exception.Message); return Task.CompletedTask; }

    public Task OnRpcCommandErrorAsync(BlazeRpcConnection connection, ProtoFirePacket packet, Tdf request, Exception exception)
    { _log.LogWarning("rpc command error: {0}", exception.Message); return Task.CompletedTask; }

    public Task OnUnhandledAsync(BlazeRpcConnection connection, ProtoFirePacket packet, bool corrupted)
    {
        _log.LogWarning("UNHANDLED command 0x{0:x4}::0x{1:x4} (corrupted={2}) bodyLen={3}",
            packet.Frame.Component, packet.Frame.Command, corrupted, packet.Data?.Length ?? 0);
        if (packet.Frame.MessageType == MessageType.Message)
        {
            ushort code = corrupted ? (ushort)0x4001 : Blaze3SDK.Components.ServerError.ERR_COMMAND_NOT_FOUND.WithErrorPrefix();
            IFireFrame respFrame = packet.Frame.CreateResponseFrame(code);
            return connection.SendAsync(new ProtoFirePacket(respFrame, Array.Empty<byte>()));
        }
        return Task.CompletedTask;
    }
}
