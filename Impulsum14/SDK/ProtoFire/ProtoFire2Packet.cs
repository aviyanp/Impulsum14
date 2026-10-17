using ProtoFire.Frames;

namespace ProtoFire;

/// <summary>
/// Fire2 packet: 16-byte header + metadata + payload.
/// Metadata is Heat2-encoded Fire2Metadata TDF (contains error code, context, etc.).
/// Data is the Heat2-encoded TDF payload.
/// </summary>
public class ProtoFire2Packet : ProtoFirePacket
{
    public byte[] MetadataData { get; set; }

    public new Fire2Frame Frame => (Fire2Frame)base.Frame;

    public ProtoFire2Packet(Fire2Frame frame, byte[] metadataData, byte[] data)
        : base(frame, data)
    {
        MetadataData = metadataData;
    }

    public static ProtoFire2Packet? ReadFrom(Stream stream, int maxFrameBytes = 1048576)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        try
        {
            Fire2Frame frame = new Fire2Frame();
            frame.Initialize(stream);

            if ((long)frame.PayloadSize + frame.MetadataSize > maxFrameBytes) throw new InvalidDataException("Frame exceeds limit.");
            byte[] metadataData = Array.Empty<byte>();
            if (frame.MetadataSize > 0)
            {
                metadataData = new byte[frame.MetadataSize];
                stream.ReadExactly(metadataData, 0, frame.MetadataSize);
            }

            byte[] data = Array.Empty<byte>();
            if (frame.PayloadSize > 0)
            {
                data = new byte[frame.PayloadSize];
                stream.ReadExactly(data, 0, (int)frame.PayloadSize);
            }

            return new ProtoFire2Packet(frame, metadataData, data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public static async Task<ProtoFire2Packet?> ReadFromAsync(Stream stream, int maxFrameBytes = 1048576, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        try
        {
            Fire2Frame frame = new Fire2Frame();
            await frame.InitializeAsync(stream, cancellationToken).ConfigureAwait(false);

            if ((long)frame.PayloadSize + frame.MetadataSize > maxFrameBytes) throw new InvalidDataException("Frame exceeds limit.");
            byte[] metadataData = Array.Empty<byte>();
            if (frame.MetadataSize > 0)
            {
                metadataData = new byte[frame.MetadataSize];
                await stream.ReadExactlyAsync(metadataData.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            byte[] data = Array.Empty<byte>();
            if (frame.PayloadSize > 0)
            {
                data = new byte[frame.PayloadSize];
                await stream.ReadExactlyAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            return new ProtoFire2Packet(frame, metadataData, data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public override void WriteTo(Stream stream)
    {
        if (MetadataData.Length > ushort.MaxValue) throw new InvalidDataException("Fire2 metadata exceeds its wire length.");
        Frame.PayloadSize = (uint)Data.Length;
        Frame.MetadataSize = (ushort)MetadataData.Length;

        Frame.WriteTo(stream);

        if (MetadataData.Length > 0)
            stream.Write(MetadataData, 0, MetadataData.Length);

        if (Data.Length > 0)
            stream.Write(Data, 0, Data.Length);
    }

    public override async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (MetadataData.Length > ushort.MaxValue) throw new InvalidDataException("Fire2 metadata exceeds its wire length.");
        Frame.PayloadSize = (uint)Data.Length;
        Frame.MetadataSize = (ushort)MetadataData.Length;

        await Frame.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);

        if (MetadataData.Length > 0)
            await stream.WriteAsync(MetadataData.AsMemory(), cancellationToken).ConfigureAwait(false);

        if (Data.Length > 0)
            await stream.WriteAsync(Data.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
