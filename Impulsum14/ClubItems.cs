namespace Impulsum14;

internal readonly record struct CosmeticItem(
    long ItemId, string Type, int AssetId, long ResourceId, int SubType, string Name, int Rating,
    int Rare = 0, int Category = 0, int TeamId = 0);

internal static class ClubItems
{
    internal const long BallIdFloor = 8_120_091L;

    private const long BallBase    = 8120000;
    private const long StadiumBase = 6200000;
    private const long KitBase     = 6300000;
    private const long BadgeBase   = 6000000;

    internal const long CosmeticItemIdBase = 950_000_000L;   // clear of players (id) and specials (900M)

    private static readonly (string Type, int SubType, long Base)[] TypeInfo =
    {
        ("ball",    30, BallBase),
        ("stadium", 10, StadiumBase),
        ("kit",      9, KitBase),
        ("badge",   11, BadgeBase),
    };

    internal static readonly CosmeticItem[] Catalog = Load();

    private static (int SubType, long Base) Info(string type)
    {
        foreach (var t in TypeInfo) if (t.Type == type) return (t.SubType, t.Base);
        return (0, 0);
    }

    private static CosmeticItem[] Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "items.tsv");
        var list = new List<CosmeticItem>();
        try
        {
            long id = CosmeticItemIdBase;
            int lineNo = 1;
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                lineNo++;
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length < 3) continue;
                // type  assetId  name  [resourceId]  [rating]  [rare]  [category]  [teamid]
                string type = c[0].Trim();
                var (subType, baseRes) = Info(type);
                if (subType == 0)
                {
                    ServerDiagnostics.Write($"[Items] {path}:{lineNo} unknown type '{type}', skipping");
                    continue;
                }
                int assetId = int.Parse(c[1]);
                string name = c.Length > 2 ? c[2] : type;
                long resId = c.Length > 3 && c[3].Length > 0 && c[3] != "-" ? long.Parse(c[3]) : baseRes + assetId;
                int F(int i, int fallback) =>
                    c.Length > i && c[i].Length > 0 && int.TryParse(c[i], out int v) ? v : fallback;
                list.Add(new CosmeticItem(id++, type, assetId, resId, subType, name,
                    F(4, 75), F(5, 0), F(6, 0), F(7, 0)));
            }
        }
        catch (FileNotFoundException)
        {
            ServerDiagnostics.Write($"[Items] no {path}, running without club items");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[Items] FAILED to load {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return list.Concat(Probe(list.Count)).ToArray();
    }

    private static IEnumerable<CosmeticItem> Probe(int startCount)
    {
        string spec = Environment.GetEnvironmentVariable("FUT_PROBE_ITEMS");
        if (string.IsNullOrWhiteSpace(spec)) yield break;

        long id = CosmeticItemIdBase + 500_000 + startCount;   // clear of the real catalog ids
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split(':');
            if (kv.Length != 2) continue;
            string type = kv[0].Trim();
            var (subType, baseRes) = Info(type);
            if (subType == 0) { ServerDiagnostics.Write($"[Items] probe: unknown type '{type}'"); continue; }

            string[] range = kv[1].Split('-');
            if (!int.TryParse(range[0], out int from)) continue;
            int to = range.Length > 1 && int.TryParse(range[1], out int t) ? t : from;
            ServerDiagnostics.Write($"[Items] probing {type} assetId {from}..{to}");
            for (int a = from; a <= to; a++)
                yield return new CosmeticItem(id++, type, a, baseRes + a, subType, $"{type} {a}", 75);
        }
    }

    internal const long ActiveItemIdBase = 800_001;   // ids of the squad "actives" entries (one per equipment type)

    internal static bool TryResolveCatalogId(long id, out CosmeticItem def)
    {
        def = default;
        long idx = id - CosmeticItemIdBase;
        if (idx < 0 || idx >= Catalog.Length) return false;
        var candidate = Catalog[(int)idx];
        if (candidate.ItemId != id) return false;   // id falls in the linear block only if the entry confirms it
        def = candidate;
        return true;
    }

    internal static string ActiveStateName(string type, string slot) => type switch
    {
        "stadium" => "activeStadium",
        "ball"    => "activeBall",
        "kit"     => slot == "102" ? "activeAwayKit" : "activeHomeKit",
        _         => "activeBadge",
    };

    public static string BuildJson(CosmeticItem it, long timestamp, string itemState = "free", int pile = 6)
    {
        int discard = it.Rating;
        string head =
            "{\"id\":" + it.ItemId + ",\"timestamp\":" + timestamp + ",\"formation\":\"f442\"," +
            "\"untradeable\":false,\"assetId\":" + it.AssetId + ",\"rating\":" + it.Rating + "," +
            "\"itemType\":\"" + (it.Type == "badge" ? "custom" : it.Type) + "\"," +
            "\"resourceId\":" + it.ResourceId + ",\"owners\":1,\"discardValue\":" + discard + "," +
            "\"itemState\":\"" + itemState + "\",\"cardsubtypeid\":" + it.SubType + ",\"lastSalePrice\":0," +
            "\"statsList\":[],\"lifetimeStats\":[],\"attributeList\":[],\"teamid\":" + it.TeamId +
            ",\"rareflag\":" + it.Rare + "," +
            "\"leagueId\":0,\"pile\":" + pile + ",\"resourceGameYear\":2014";
        int category = it.Category;

        string name = Esc(it.Name);
        string tail = it.Type switch
        {
            "stadium" => ",\"cardassetid\":36,\"category\":" + category + ",\"name\":\"" + name +
                         "\",\"description\":\"StadiumDesc_" + it.AssetId + "\"," +
                         "\"biodescription\":\"StadiumDetailDesc\"," +
                         "\"stadiumid\":" + it.AssetId + ",\"value\":" + it.Rating +
                         ",\"capacity\":30000}",
            "ball"    => ",\"cardassetid\":37,\"category\":" + category + ",\"name\":\"" + name +
                         "\",\"value\":" + it.Rating + ",\"manufacturer\":\"ManufacturerGeneric\"}",
            "kit"     => ",\"category\":" + category + ",\"value\":" + it.Rating + ",\"year\":0}",
            "badge"   => ",\"category\":" + category + ",\"value\":" + it.Rating +
                         ",\"weightrare\":" + (it.Rare * 10) + ",\"header\":\"Badge\"}",
            _         => "}",
        };
        return head + tail;
    }

    private static string Esc(string s) => (s ?? "")
        .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
}
