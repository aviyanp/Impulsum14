using ProtoFire.Frames;

namespace ProtoFire;

public class ProtoFirePacket
{
    public IFireFrame Frame { get; set; }
    public byte[] Data { get; set; }

    public ProtoFirePacket(IFireFrame frame, byte[] data)
    {
        Frame = frame;
        Data = data;
    }

    public MemoryStream GetDataStream()
    {
        return new MemoryStream(Data);
    }

    public static ProtoFirePacket? ReadFrom<TFrame>(Stream stream, int maxFrameBytes = 1048576) where TFrame : IFireFrame, new()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        try
        {
            TFrame frame = new TFrame();
            frame.Initialize(stream);

            if (frame.Size > maxFrameBytes) throw new InvalidDataException("Frame exceeds limit.");

            // Reading the data
            byte[] data = new byte[frame.Size];

            if (frame.Size != 0)
                stream.ReadExactly(data, 0, (int)frame.Size);

            return new ProtoFirePacket(frame, data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public virtual void WriteTo(Stream stream)
    {
        Frame.Size = (uint)Data.Length;
        Frame.WriteTo(stream);
        if (Data.Length != 0)
            stream.Write(Data, 0, Data.Length);
    }

    public static async Task<ProtoFirePacket?> ReadFromAsync<TFrame>(Stream stream, int maxFrameBytes = 1048576, CancellationToken cancellationToken = default) where TFrame : IFireFrame, new()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        try
        {
            TFrame frame = new TFrame();
            await frame.InitializeAsync(stream, cancellationToken).ConfigureAwait(false);

            if (frame.Size > maxFrameBytes) throw new InvalidDataException("Frame exceeds limit.");

            // Reading the data
            byte[] data = new byte[frame.Size];

            if (frame.Size != 0)
                await stream.ReadExactlyAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);

            return new ProtoFirePacket(frame, data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public virtual async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Frame.Size = (uint)Data.Length;
        await Frame.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        if (Data.Length != 0)
            await stream.WriteAsync(Data.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
