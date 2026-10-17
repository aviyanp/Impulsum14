using System.Text.Json;

namespace Impulsum14;

internal static class Legends
{
    internal static readonly RealPlayer[] Catalog = Load();
    internal static IEnumerable<RealPlayer> Enabled => ServerOptions.Current.EnableLegends ? Catalog : Array.Empty<RealPlayer>();
    internal static void ValidateClientManifest()
    {
        if (!ServerOptions.Current.EnableLegends) return;
        if (!File.Exists(ServerOptions.Current.LegendClientManifest)) throw new InvalidDataException("Legends require a client identity manifest. See tools/legend_manifest.py.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(ServerOptions.Current.LegendClientManifest));
        if (!doc.RootElement.TryGetProperty("sourceSha256", out var hash) || hash.GetString()?.Length != 64 || !doc.RootElement.TryGetProperty("assetIds", out var ids)) throw new InvalidDataException("Invalid legend client manifest.");
        var installed = ids.EnumerateArray().Select(id => id.GetInt32()).ToHashSet();
        if (Catalog.Length == 0 || Catalog.Any(p => !installed.Contains(p.Id))) throw new InvalidDataException("Legend identities are missing from the client database. Stock PC data cannot safely render these cards.");
    }
    private static RealPlayer[] Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "legends.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var players = new List<RealPlayer>();
        foreach (var row in document.RootElement.GetProperty("players").EnumerateArray())
        {
            var a = row.GetProperty("attributes").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            players.Add(new RealPlayer(row.GetProperty("assetId").GetInt32(), row.GetProperty("name").GetString()!, row.GetProperty("teamId").GetInt32(), row.GetProperty("nation").GetInt32(), row.GetProperty("position").GetString()!, row.GetProperty("rating").GetInt32(), row.GetProperty("rating").GetInt32(), a[0], a[1], a[2], a[3], a[4], a[5], row.GetProperty("rareFlag").GetInt32()) { ResourceId = row.GetProperty("resourceId").GetInt32(), Set = "legend" });
        }
        if (players.Count != 42 || players.Select(p => p.CardId).Distinct().Count() != players.Count) throw new InvalidDataException("Invalid legend catalogue.");
        return players.ToArray();
    }
    internal static string Json() => JsonSerializer.Serialize(new { enabled = ServerOptions.Current.EnableLegends, players = Catalog.Select(p => new { assetId = p.Id, resourceId = p.CardId, name = p.Name, rating = p.Rating, position = p.Position, rareflag = p.Rare }) });
}
