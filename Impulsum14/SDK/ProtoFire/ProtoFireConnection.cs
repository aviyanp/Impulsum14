using Org.BouncyCastle.Tls;
using ProtoFire.Frames;
using ProtoFire.Internal;
using ProtoFire.Tls;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.Channels;

namespace ProtoFire;

public class ProtoFireConnection
{
    public Socket Socket { get; }
    public IProtoFireHandler EventHandler { get; }
    public FrameType FrameType { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public bool Connected => Volatile.Read(ref _disconnected) == 0;
    public object? State { get; set; }
    public int MaxFrameBytes { get; init; } = 1048576;
    public int IdleTimeoutSeconds { get; init; } = 120;
    public int SendTimeoutSeconds { get; init; } = 15;
    public int InboundQueueCapacity { get; init; } = 32;
    public int OutboundQueueCapacity { get; init; } = 64;
    public long MaxQueuedOutboundBytes => Math.Max((long)MaxFrameBytes + FireFrame.MAX_HEADER_SIZE, 4L * 1024 * 1024);
    public long MaxQueuedInboundBytes => MaxQueuedOutboundBytes;
    public CancellationToken CancellationToken => _lifetime.Token;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _disconnectedCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _queueSync = new();
    private Channel<ProtoFirePacket>? _incoming;
    private Channel<OutboundPacket>? _outgoing;
    private long _queuedOutboundBytes;
    private long _queuedInboundBytes;
    private readonly SemaphoreSlim _incomingSpace = new(0, 1);
    private int _disconnected;
    private int _processing;
    private bool _secure;
    private Stream _stream;
    private Task? _writerTask;

    private sealed record OutboundPacket(ProtoFirePacket Packet, long Bytes, long Deadline, TaskCompletionSource<bool>? Completion);

    internal ProtoFireConnection(Socket socket, IProtoFireHandler eventHandler, FrameType frameType)
    {
        Utilities.ValidateFrameType(frameType);
        Socket = socket;
        _stream = new NetworkStream(socket, true);
        FrameType = frameType;
        EventHandler = eventHandler is ProtoFireEventHandlerTryWrapper ? eventHandler : new ProtoFireEventHandlerTryWrapper(eventHandler);
        Socket.NoDelay = true;
    }

    private void EnsureQueues()
    {
        lock (_queueSync)
        {
            _incoming ??= Channel.CreateBounded<ProtoFirePacket>(new BoundedChannelOptions(InboundQueueCapacity)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            _outgoing ??= Channel.CreateBounded<OutboundPacket>(new BoundedChannelOptions(OutboundQueueCapacity)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        }
    }

    public async Task ProcessAsync(bool isServer, bool secure)
    {
        if (Interlocked.Exchange(ref _processing, 1) != 0)
            throw new InvalidOperationException("Connection is already being processed.");
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrameBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(IdleTimeoutSeconds);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SendTimeoutSeconds);
            EnsureQueues();
            if (!Connected || !await EventHandler.OnConnectedAsync(this).WaitAsync(_lifetime.Token).ConfigureAwait(false))
                return;
            _secure = secure;
            if (secure)
                _stream = await AuthenticateConnectionAsync(_stream, isServer).ConfigureAwait(false);

            // One writer owns the stream, including while an RPC callback is running.
            _writerTask = WritePacketsAsync();
            await EventHandler.OnAuthenticatedAsync(this, secure).WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(ReadPacketsAsync(), ProcessPacketsAsync()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            if (Connected) await EventHandler.OnErrorAsync(this, ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EventHandler.OnErrorAsync(this, ex).ConfigureAwait(false);
        }
        finally
        {
            Disconnect();
            if (_writerTask != null) await _writerTask.ConfigureAwait(false);
            else FailQueuedSends();
            if (_incoming != null)
                while (_incoming.Reader.TryRead(out _)) { }
            await _disconnectedCallback.Task.ConfigureAwait(false);
            try { _stream.Dispose(); } catch { }
        }
    }

    private async Task<Stream> AuthenticateConnectionAsync(Stream baseStream, bool isServer)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // BouncyCastle 2.4's blocking TLS API has no cancellable handshake. Closing
        // the transport interrupts it without changing the FIFA TLS/RC4 protocol.
        using var registration = deadline.Token.Register(Disconnect);
        return await Task.Run(() =>
        {
            if (isServer)
            {
                ProtoSSLCertificate certificate = EventHandler.SelectCertificate(this)
                    ?? throw new IOException("No TLS certificate was selected.");
                return new ProtoSslServerProtocol(certificate, baseStream).Stream;
            }
            var protocol = new TlsClientProtocol(baseStream);
            protocol.Connect(new ProtoFireTlsClient());
            return protocol.Stream;
        }).ConfigureAwait(false);
    }

    private async Task ReadPacketsAsync()
    {
        try
        {
            while (Connected)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                using var registration = deadline.Token.Register(Disconnect);
                ProtoFirePacket? packet = FrameType switch
                {
                    FrameType.FireFrame => await ProtoFirePacket.ReadFromAsync<FireFrame>(_stream, MaxFrameBytes, deadline.Token).ConfigureAwait(false),
                    FrameType.Fire2Frame => await ProtoFire2Packet.ReadFromAsync(_stream, MaxFrameBytes, deadline.Token).ConfigureAwait(false),
                    _ => null
                };
                if (packet is null) break;
                long bytes = PacketBytes(packet);
                await ReserveIncomingBytesAsync(bytes).ConfigureAwait(false);
                try { await _incoming!.Writer.WriteAsync(packet, _lifetime.Token).ConfigureAwait(false); }
                catch { ReleaseIncomingBytes(bytes); throw; }
            }
        }
        finally
        {
            _incoming!.Writer.TryComplete();
            Disconnect();
        }
    }

    private async Task ProcessPacketsAsync()
    {
        await foreach (ProtoFirePacket packet in _incoming!.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            ReleaseIncomingBytes(PacketBytes(packet));
            await EventHandler.OnPacketReceivedAsync(this, packet).WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private static long PacketBytes(ProtoFirePacket packet) => packet.Data.LongLength + packet.Frame.HeaderSize
        + (packet is ProtoFire2Packet fire2 ? fire2.MetadataData.LongLength : 0);

    private async Task ReserveIncomingBytesAsync(long bytes)
    {
        while (true)
        {
            lock (_queueSync)
            {
                if (bytes <= MaxQueuedInboundBytes - _queuedInboundBytes)
                {
                    _queuedInboundBytes += bytes;
                    return;
                }
            }
            await _incomingSpace.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private void ReleaseIncomingBytes(long bytes)
    {
        lock (_queueSync) _queuedInboundBytes -= bytes;
        try { _incomingSpace.Release(); } catch (SemaphoreFullException) { }
    }

    public bool Send(ProtoFirePacket packet) => SendAsync(packet).GetAwaiter().GetResult();

    public Task<bool> SendAsync(ProtoFirePacket packet, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryQueue(packet, completion)) return Task.FromResult(false);
        return cancellationToken.CanBeCanceled ? completion.Task.WaitAsync(cancellationToken) : completion.Task;
    }

    /// <summary>Transfers ownership of a packet to the bounded writer without waiting for network I/O.
    /// A full queue closes the slow connection; packets are never silently dropped.</summary>
    public bool TryEnqueueSend(ProtoFirePacket packet) => TryQueue(packet, null);

    private bool TryQueue(ProtoFirePacket packet, TaskCompletionSource<bool>? completion)
    {
        if (!Connected || packet.Frame.Type != FrameType) return false;
        long bytes = packet.Data.LongLength + (packet is ProtoFire2Packet fire2 ? fire2.MetadataData.LongLength : 0);
        if (bytes > MaxFrameBytes || (FrameType == FrameType.Fire2Frame && packet is not ProtoFire2Packet))
        {
            Disconnect();
            return false;
        }
        bytes += packet.Frame.HeaderSize;
        EnsureQueues();
        bool accepted;
        lock (_queueSync)
        {
            accepted = Connected && bytes <= MaxQueuedOutboundBytes - _queuedOutboundBytes
                && _outgoing!.Writer.TryWrite(new OutboundPacket(packet, bytes, Stopwatch.GetTimestamp() + (long)SendTimeoutSeconds * Stopwatch.Frequency, completion));
            if (accepted) _queuedOutboundBytes += bytes;
        }
        if (!accepted) Disconnect();
        return accepted;
    }

    private async Task WritePacketsAsync()
    {
        try
        {
            await foreach (OutboundPacket item in _outgoing!.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                bool sent = false;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), item.Deadline);
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Queued ProtoFire send timed out.");
                    deadline.CancelAfter(remaining);
                    using var registration = deadline.Token.Register(Disconnect);
                    Stream stream = _stream;
                    if (_secure)
                    {
                        // The blocking TLS stream shares Stream's async read/write gate.
                        // Use its synchronous write on a worker so a pending read cannot
                        // prevent a reply. There is at most one such worker per connection.
                        await Task.Run(() => { item.Packet.WriteTo(stream); stream.Flush(); }).ConfigureAwait(false);
                    }
                    else
                    {
                        await item.Packet.WriteToAsync(stream, deadline.Token).ConfigureAwait(false);
                        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                    await EventHandler.OnPacketSentAsync(this, item.Packet).WaitAsync(deadline.Token).ConfigureAwait(false);
                    sent = Connected;
                }
                finally
                {
                    ReleaseBytes(item.Bytes);
                    item.Completion?.TrySetResult(sent);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
        catch (Exception ex) { await EventHandler.OnErrorAsync(this, ex).ConfigureAwait(false); }
        finally { Disconnect(); FailQueuedSends(); }
    }

    private void ReleaseBytes(long bytes)
    {
        lock (_queueSync) _queuedOutboundBytes -= bytes;
    }

    private void FailQueuedSends()
    {
        if (_outgoing == null) return;
        while (_outgoing.Reader.TryRead(out OutboundPacket? item))
        {
            ReleaseBytes(item.Bytes);
            item.Completion?.TrySetResult(false);
        }
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        try { _lifetime.Cancel(); } catch (AggregateException) { }
        _incoming?.Writer.TryComplete();
        _outgoing?.Writer.TryComplete();
        // Close the socket first: disposing a TLS stream can synchronously emit an
        // alert or wait for its reader. Socket close also interrupts blocked TLS I/O.
        try { Socket.Dispose(); } catch { }
        _ = NotifyDisconnectedAsync();
    }

    private async Task NotifyDisconnectedAsync()
    {
        try
        {
            // Never run application disconnect callbacks under a caller's game lock.
            await Task.Run(() => EventHandler.OnDisconnectedAsync(this)).ConfigureAwait(false);
        }
        catch (Exception ex) { await EventHandler.OnErrorAsync(this, ex).ConfigureAwait(false); }
        finally { _disconnectedCallback.TrySetResult(); }
    }
}
