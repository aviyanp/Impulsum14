using ProtoFire.Internal;
using ProtoFire.Tls;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ProtoFire;

public class ProtoFireServer
{
    public IPEndPoint LocalEP { get; private set; }
    public bool Started { get; private set; }
    public bool Secure { get; init; }
    public int MaxConnections { get; init; } = 2048;
    public int MaxFrameBytes { get; init; } = 1048576;
    public int IdleTimeoutSeconds { get; init; } = 120;
    public int SendTimeoutSeconds { get; init; } = 15;
    public int InboundQueueCapacity { get; init; } = 32;
    public int OutboundQueueCapacity { get; init; } = 64;
    public FrameType FrameType { get; private set; }
    IProtoFireHandler ConnectionHandler { get; }

    public int Port
    {
        get {

            if (_listenSocket == null)
                return LocalEP.Port;
            
            return _listenSocket.LocalEndPoint switch
            {
                IPEndPoint ep => ep.Port,
                _ => LocalEP.Port
            };
        }
    }

    private Socket? _listenSocket;
    private ConcurrentDictionary<Guid, ProtoFireConnection> _connections;
    private readonly ConcurrentDictionary<Guid, Task> _processingTasks = new();


    public ProtoFireServer(IPEndPoint localEP, IProtoFireHandler handler, FrameType frameType)
    {
        Utilities.ValidateFrameType(frameType);

        FrameType = frameType;
        LocalEP = localEP;


        bool shouldTryWrap = handler is not ProtoFireEventHandlerTryWrapper;

        ConnectionHandler = new ProtoFireServerEventDisconnectHelper(this, handler);
        if (shouldTryWrap)
            ConnectionHandler = new ProtoFireEventHandlerTryWrapper(ConnectionHandler);

        Started = false;

        _connections = new ConcurrentDictionary<Guid, ProtoFireConnection>();
    }

    public async Task StartAsync(int backlog, CancellationToken cancelToken = default)
    {
        if (Started)
            throw new InvalidOperationException("Server is already started");

        try
        {
            _listenSocket = new Socket(LocalEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(LocalEP);
            _listenSocket.Listen(backlog);
            Started = true;
        }
        catch
        {
            _listenSocket?.Close();
            _listenSocket = null;
            throw;
        }

        try
        {
            while (!cancelToken.IsCancellationRequested)
            {
                try
                {
                    Socket clientSocket = await _listenSocket.AcceptAsync(cancelToken).ConfigureAwait(false);
                    if (_connections.Count >= MaxConnections) { clientSocket.Dispose(); continue; }
                    ProtoFireConnection connection = new ProtoFireConnection(clientSocket, ConnectionHandler, FrameType)
                    {
                        MaxFrameBytes = MaxFrameBytes, IdleTimeoutSeconds = IdleTimeoutSeconds,
                        SendTimeoutSeconds = SendTimeoutSeconds, InboundQueueCapacity = InboundQueueCapacity,
                        OutboundQueueCapacity = OutboundQueueCapacity
                    };
                    _connections.TryAdd(connection.Id, connection);
                    Task processing = connection.ProcessAsync(true, Secure);
                    _processingTasks.TryAdd(connection.Id, processing);
                    _ = ObserveConnectionAsync(connection.Id, processing);
                }
                catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cancelToken.IsCancellationRequested) { break; }
                catch (SocketException) when (!cancelToken.IsCancellationRequested)
                {
                    try { await Task.Delay(100, cancelToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { break; }
                }
            }
        }
        finally
        {
            Started = false;
            _listenSocket.Dispose();
            foreach (ProtoFireConnection connection in _connections.Values) connection.Disconnect();
            // Includes application disconnect cleanup, not just closing client sockets.
            await Task.WhenAll(_processingTasks.Values).ConfigureAwait(false);
            _connections.Clear();
        }
    }

    private async Task ObserveConnectionAsync(Guid id, Task processing)
    {
        try { await processing.ConfigureAwait(false); }
        catch { /* ProcessAsync reports errors through the protected event handler. */ }
        finally { _processingTasks.TryRemove(id, out _); }
    }

    internal void OnDisconnected(ProtoFireConnection connection)
    {
        _connections.TryRemove(connection.Id, out _);
    }
}
