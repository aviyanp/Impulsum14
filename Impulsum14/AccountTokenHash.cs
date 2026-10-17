using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Impulsum14;

/// <summary>A fixed-size credential digest. Never retains the supplied token.</summary>
internal readonly record struct AccountTokenHash(ulong A, ulong B, ulong C, ulong D)
{
    internal static AccountTokenHash FromHex(string hex) => FromBytes(Convert.FromHexString(hex));

    internal static AccountTokenHash FromToken(string token)
    {
        // Three bytes per UTF-16 code unit also accommodates replacement of an
        // unpaired surrogate. Long credentials remain bounded by Authenticate.
        Span<byte> utf8 = token.Length <= 128 ? stackalloc byte[384] : new byte[Encoding.UTF8.GetByteCount(token)];
        int length = Encoding.UTF8.GetBytes(token, utf8);
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(utf8[..length], digest);
            return FromBytes(digest);
        }
        finally { CryptographicOperations.ZeroMemory(utf8[..length]); }
    }

    private static AccountTokenHash FromBytes(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
}
