using EATDF.Members;
using EATDF.Serialization;
using EATDF.Types;

namespace Blaze.Core.Internal;

/// <summary>
/// Encodes/decodes Fire2 metadata (Heat2-encoded TDF with context and error code).
/// Avoids circular dependency on Blaze15SDK by encoding the raw Heat2 bytes directly.
///
/// Fire2Metadata TDF fields (relevant subset):
///   CNTX (0x8EED3800) - UInt64 (Heat2 Integer type 0x00)
///   ERRC (0x972CA300) - Int32  (Heat2 Integer type 0x00)
/// </summary>
internal static class Fire2MetadataHelper
{
    // Heat2 tag bytes for CNTX field (tag 0x8EED3800, type Integer=0x00)
    private static readonly byte[] CNTX_HEADER = [0x8E, 0xED, 0x38, 0x00];

    // Heat2 tag bytes for ERRC field (tag 0x972CA300, type Integer=0x00)
    private static readonly byte[] ERRC_HEADER = [0x97, 0x2C, 0xA3, 0x00];

    public static byte[] Encode(ulong context, ushort errorCode)
    {
        if (context == 0 && errorCode == 0)
            return Array.Empty<byte>();

        MemoryStream ms = new MemoryStream(32);

        if (context != 0)
        {
            ms.Write(CNTX_HEADER);
            WriteVarInt(ms, unchecked((long)context));
        }

        if (errorCode != 0)
        {
            ms.Write(ERRC_HEADER);
            WriteVarInt(ms, errorCode);
        }

        byte[] result = ms.ToArray();
        ms.Dispose();
        return result;
    }

    public static void Decode(byte[] data, out ulong context, out int errorCode)
    {
        context = 0;
        errorCode = 0;
        var metadata = new EmptyMessage();
        if (!new Heat2Serializer().Deserialize(new MemoryStream(data, false), metadata))
            throw new InvalidDataException("Invalid Fire2 metadata.");
        bool hasContext = false, hasError = false;
        foreach (var field in metadata.GetUnknownMembers())
        {
            if (field.TdfInfo.Tag == 0x8EED3800)
            {
                if (hasContext || field is not TdfInt64 contextField)
                    throw new InvalidDataException("Invalid Fire2 context metadata.");
                context = unchecked((ulong)contextField.Value);
                hasContext = true;
            }
            else if (field.TdfInfo.Tag == 0x972CA300)
            {
                if (hasError || field is not TdfInt64 errorField || errorField.Value < int.MinValue || errorField.Value > uint.MaxValue)
                    throw new InvalidDataException("Invalid Fire2 error metadata.");
                errorCode = unchecked((int)errorField.Value);
                hasError = true;
            }
        }
    }

    private static void WriteVarInt(Stream stream, long value)
    {
        if (value == 0)
        {
            stream.WriteByte(0);
            return;
        }

        byte curByte;
        if (value > 0)
            curByte = (byte)(value & 0x3F | 0x80);
        else
        {
            value = -value;
            curByte = (byte)(value & 0x3F | 0xC0);
        }

        for (long i = value >> 6; i > 0; i >>= 7)
        {
            stream.WriteByte(curByte);
            curByte = (byte)((i | 0x80) & 0xFF);
        }

        stream.WriteByte((byte)(curByte & 0x7F));
    }

}
