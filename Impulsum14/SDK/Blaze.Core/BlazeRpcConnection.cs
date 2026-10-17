using Blaze.Core.Internal;
using EATDF;
using EATDF.Serialization;
using ProtoFire;
using ProtoFire.Frames;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Blaze.Core;

public class BlazeRpcConnection : IBlazeConnection
{
    private ProtoFireConnection _baseConnection { get; }
    private ITdfSerializer _serializer { get; }
    private readonly object _notificationSync = new();
    private readonly Queue<ProtoFirePacket> _deferredNotifications = new();
    private bool _requestActive;
    private long _deferredBytes;
    public CancellationToken CancellationToken => _baseConnection.CancellationToken;

    public EndPoint? RemoteEndPoint { get; }
    public EndPoint? LocalEndPoint { get; }
    public bool Connected => _baseConnection.Connected;
    public Guid Id => _baseConnection.Id;
    public object? State { get; set; }
    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;


    public BlazeRpcConnection(ProtoFireConnection baseConnection, ITdfSerializer serializer)
    {
        _baseConnection = baseConnection;
        _serializer = serializer;

        // if socket is disposed, we will still be able to get the endpoints
        RemoteEndPoint = _baseConnection.Socket.RemoteEndPoint;
        LocalEndPoint = _baseConnection.Socket.LocalEndPoint;
    }


    public void EnqequeSend(Action<BlazePacket> configure)
    {
        ProtoFirePacket firePacket = createProtoFirePacket(configure);
        EnqequeSend(firePacket);
    }

    public bool Send(Action<BlazePacket> configure)
    {
        ProtoFirePacket firePacket = createProtoFirePacket(configure);
        return Send(firePacket);
    }

    public Task<bool> SendAsync(Action<BlazePacket> configure)
    {
        ProtoFirePacket firePacket = createProtoFirePacket(configure);
        return SendAsync(firePacket);
    }

    public void EnqequeSend(ProtoFirePacket packet)
    {
        if (!Connected) return;
        packet = EnsureMetadata(packet);
        // The generated notification methods serialize the TDF before this point.
        // No application object or unbounded waiting task survives the enqueue.
        long bytes = packet.Data.LongLength + packet.Frame.HeaderSize
            + (packet is ProtoFire2Packet fire2 ? fire2.MetadataData.LongLength : 0);
        bool overflow = false;
        lock (_notificationSync)
        {
            if (_requestActive)
            {
                if (_deferredNotifications.Count >= _baseConnection.OutboundQueueCapacity
                    || bytes > _baseConnection.MaxQueuedOutboundBytes - _deferredBytes)
                    overflow = true;
                else
                {
                    _deferredNotifications.Enqueue(packet);
                    _deferredBytes += bytes;
                }
            }
            else _baseConnection.TryEnqueueSend(packet);
        }
        if (overflow) Disconnect();
    }

    public bool Send(ProtoFirePacket packet) => _baseConnection.Send(EnsureMetadata(packet));
    public Task<bool> SendAsync(ProtoFirePacket packet) => _baseConnection.SendAsync(EnsureMetadata(packet));

    private static ProtoFirePacket EnsureMetadata(ProtoFirePacket packet) => packet.Frame is Fire2Frame frame && packet is not ProtoFire2Packet
        ? new ProtoFire2Packet(frame, Fire2MetadataHelper.Encode(frame.Context, frame.ErrorCode), packet.Data)
        : packet;
    public void Disconnect() => _baseConnection.Disconnect();

    internal void BeginRequest()
    {
        lock (_notificationSync) _requestActive = true;
    }

    internal void EndRequest()
    {
        lock (_notificationSync)
        {
            // The caller has already awaited the reply write. Enqueue every
            // notification before accepting the next RPC, preserving FIFO order.
            while (_deferredNotifications.TryDequeue(out ProtoFirePacket? packet))
                if (Connected) _baseConnection.TryEnqueueSend(packet);
            _deferredBytes = 0;
            _requestActive = false;
        }
    }

    private ProtoFirePacket createProtoFirePacket(Action<BlazePacket> configure)
    {
        IFireFrame frame = _baseConnection.FrameType switch
        {
            FrameType.FireFrame => new FireFrame(),
            FrameType.Fire2Frame => new Fire2Frame(),
            _ => throw new InvalidOperationException("Invalid frame type")
        };

        BlazePacket packet = new BlazePacket()
        {
            Frame = frame
        };

        configure(packet);

        byte[] bytes;

        if (packet.Data == null)
            bytes = Array.Empty<byte>();
        else
            bytes = _serializer.Serialize(packet.Data);

        if (frame is Fire2Frame fire2Frame)
        {
            byte[] metadataBytes = encodeFire2Metadata(fire2Frame);
            return new ProtoFire2Packet(fire2Frame, metadataBytes, bytes);
        }

        return new ProtoFirePacket(frame, bytes);
    }

    private byte[] encodeFire2Metadata(Fire2Frame frame)
    {
        return Fire2MetadataHelper.Encode(frame.Context, frame.ErrorCode);
    }

    public override string ToString()
    {
        if (RemoteEndPoint != null)
            return $"[Remote Ip: {RemoteEndPoint}]";

        return $"[Conn Id: {Id}]";
    }
}
