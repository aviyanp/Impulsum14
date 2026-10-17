using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Impulsum14;

internal static class CompetitionSave
{
    internal const int MaxBlobLength = 262144;
    internal const int RetiredHashLimit = 2048;

    // This is a conservative structural guard, not authentication of opaque native
    // data. Saves carry presentation state only; authoritative match receipts own awards.
    internal static bool Resumable(string data, string progress, int round, bool season = false)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxBlobLength || (progress?.Length ?? 0) > MaxBlobLength
            || round < 1 || round > (season ? Seasons.Rounds : Tournaments.NumRounds) || season && round <= 1) return false;
        try
        {
            byte[] bytes = Convert.FromBase64String(data);
            if (bytes.Length < 4) return false;
            // Cup data is native opaque bracket state. Its progress is zero at the
            // dangerous kickoff stub; a subsequent authoritative round still needs data.
            if (!season)
            {
                if (!string.IsNullOrEmpty(progress))
                {
                    byte[] progressBytes = Convert.FromBase64String(progress);
                    return progressBytes.Length >= 4 && (round > 1 || progressBytes.Any(b => b != 0));
                }
                return round > 1;
            }
            if (!string.IsNullOrEmpty(progress) && Convert.FromBase64String(progress).Length < 4) return false;
            var payload = bytes.AsSpan(4).ToArray();
            if (payload.Length >= 2 && payload[0] == 0x1f && payload[1] == 0x8b)
            {
                using var input = new MemoryStream(payload);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = gzip.Read(buffer)) > 0)
                {
                    if (output.Length + read > MaxBlobLength) return false;
                    output.Write(buffer, 0, read);
                }
                payload = output.ToArray();
            }
            // Captured season stubs have no player records; real saves have a
            // nonzero player-table count at offset 8 after the 4-byte envelope.
            return payload.Length > 32 && payload[8] != 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or ArgumentException) { return false; }
    }

    internal static int ReadInt(JsonElement root, string field, int fallback)
    {
        if (!root.TryGetProperty(field, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) throw new InvalidDataException("Invalid competition save integer.");
        return result;
    }

    internal static string ReadString(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid competition save buffer.");
        string result = value.GetString() ?? "";
        if (result.Length > MaxBlobLength) throw new InvalidDataException("Competition save exceeds limit.");
        return result;
    }

    internal static string Fingerprint(int round, string data, string progress)
    {
        string Canonical(string value)
        {
            try { return Convert.ToBase64String(Convert.FromBase64String(value ?? "")); }
            catch (FormatException) { return value ?? ""; }
        }
        data = Canonical(data); progress = Canonical(progress);
        // Length delimiters keep different pairs from producing the same input.
        // Canonical base64 also blocks whitespace-only re-encodings of retired blobs.
        string text = round.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + data.Length + ":" + data + ":" + progress;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal static void Retire(List<string> hashes, string fingerprint)
    {
        if (!hashes.Contains(fingerprint, StringComparer.Ordinal)) hashes.Add(fingerprint);
        if (hashes.Count > RetiredHashLimit) hashes.RemoveRange(0, hashes.Count - RetiredHashLimit);
    }
}
