using ProtoFire.Internal;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace ProtoFire;

public class ProtoFireClient
{
    private uint _nextReqNum;
    private int _pendingCount;
    internal readonly ConcurrentDictionary<uint, PendingRequest> replyTasks = new();
    internal sealed record PendingRequest(ushort Component, ushort Command, TaskCompletionSource<ProtoFirePacket> Completion);
    public ProtoFireConnection Connection { get; }
    public Task ProcessingTask { get; }
    public int RequestTimeout { get; set; } = 15000;
    public int MaxPendingRequests { get; set; } = 128;

    private ProtoFireClient(Socket socket, FrameType frameType, bool secure)
        : this(socket, new NullProtoFireEventHandler(), frameType, secure) { }

    private ProtoFireClient(Socket socket, IProtoFireHandler eventHandler, FrameType frameType, bool secure)
    {
        Connection = new ProtoFireConnection(socket, new ProtoFireClientEventHandler(this, eventHandler), frameType);
        ProcessingTask = Connection.ProcessAsync(false, secure);
    }

    public uint GetNextMsgNum() => Interlocked.Increment(ref _nextReqNum)
        & (Connection.FrameType == FrameType.FireFrame ? 0x000FFFFFu : 0x00FFFFFFu);

    public ProtoFirePacket SendRequest(ProtoFirePacket packet) => SendRequestAsync(packet).GetAwaiter().GetResult();

    public async Task<ProtoFirePacket> SendRequestAsync(ProtoFirePacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RequestTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingRequests);
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new InvalidOperationException("Too many pending ProtoFire requests.");
        }
        var completion = new TaskCompletionSource<ProtoFirePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(packet.Frame.Component, packet.Frame.Command, completion);
        bool registered = false;
        try
        {
            if (!replyTasks.TryAdd(packet.Frame.MessageNum, pending))
                throw new InvalidOperationException("A request with this message number is already pending.");
            registered = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                if (!await Connection.SendAsync(packet, timeout.Token).ConfigureAwait(false))
                    throw new IOException("Failed to send ProtoFire packet.");
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Connection.CancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The ProtoFire connection closed before a reply arrived.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The request timed out.");
            }
        }
        finally
        {
            if (registered) ((ICollection<KeyValuePair<uint, PendingRequest>>)replyTasks).Remove(new(packet.Frame.MessageNum, pending));
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    internal void FailPendingRequests()
    {
        foreach (var entry in replyTasks)
            if (((ICollection<KeyValuePair<uint, PendingRequest>>)replyTasks).Remove(entry))
                entry.Value.Completion.TrySetCanceled(Connection.CancellationToken);
    }

    internal void TryCompleteReply(ProtoFirePacket packet)
    {
        if (replyTasks.TryGetValue(packet.Frame.MessageNum, out PendingRequest? pending)
            && pending.Component == packet.Frame.Component && pending.Command == packet.Frame.Command
            && ((ICollection<KeyValuePair<uint, PendingRequest>>)replyTasks).Remove(new(packet.Frame.MessageNum, pending)))
            pending.Completion.TrySetResult(packet);
    }

    public static ProtoFireClient Connect(string host, int port, bool secure, IProtoFireHandler eventHandler, FrameType frameType)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(host, port); return new ProtoFireClient(socket, eventHandler, frameType, secure); }
        catch { socket.Dispose(); throw; }
    }

    public static async Task<ProtoFireClient> ConnectAsync(string host, int port, bool secure, IProtoFireHandler eventHandler, FrameType frameType, CancellationToken cancellationToken = default)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new ProtoFireClient(socket, eventHandler, frameType, secure);
        }
        catch { socket.Dispose(); throw; }
    }

    public static ProtoFireClient Connect(string host, int port, bool secure, FrameType frameType)
        => Connect(host, port, secure, new NullProtoFireEventHandler(), frameType);

    public static Task<ProtoFireClient> ConnectAsync(string host, int port, bool secure, FrameType frameType, CancellationToken cancellationToken = default)
        => ConnectAsync(host, port, secure, new NullProtoFireEventHandler(), frameType, cancellationToken);
}
