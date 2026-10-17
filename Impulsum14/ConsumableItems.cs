namespace Impulsum14;

internal readonly record struct ConsumableItem(
    long ItemId, string ItemType, int SubType, long ResourceId, int RareFlag, string Name);

internal static class ConsumableItems
{
    internal const long ConsumableItemIdBase = 960_000_000L;   // clear of players, specials(900M), cosmetics(950M)

    private const long ConsumableBase = 5_000_000;

    internal static readonly string[] Types =
    {
        "healing", "fitness", "contract", "training",
        "position", "playStyle", "formation", "managerLeagueModifier", "fitnessCoach",
    };

    internal static readonly ConsumableItem[] Catalog = Load();

    private static ConsumableItem[] Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "consumables.tsv");
        string flag = Environment.GetEnvironmentVariable("FUT_PROBE_CONSUMABLES");

        if (flag == "1" || (!File.Exists(path) && flag != "0"))
            return Probe().ToArray();

        var list = new List<ConsumableItem>();
        try
        {
            long id = ConsumableItemIdBase;
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length < 4) continue;
                // itemType  cardsubtypeid  resourceId  name  [rareflag]
                list.Add(new ConsumableItem(id++, c[0].Trim(), int.Parse(c[1]),
                    long.Parse(c[2]), c.Length > 4 && c[4].Length > 0 ? int.Parse(c[4]) : 0,
                    c.Length > 3 ? c[3] : c[0]));
            }
        }
        catch (FileNotFoundException)
        {
            ServerDiagnostics.Write($"[Consumables] no {path}, running without consumables (set FUT_PROBE_CONSUMABLES to probe)");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[Consumables] FAILED to load {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return list.ToArray();
    }

    private static List<ConsumableItem> Probe()
    {
        int from = 5003043, to = 5003100;
        var list = new List<ConsumableItem>();
        long id = ConsumableItemIdBase;
        ServerDiagnostics.Write($"[Consumables] PROBE modifier hunt {from}..{to} (read tab left-to-right):");
        int n = 0;
        for (int r = from; r <= to; r++, n++)
        {
            list.Add(new ConsumableItem(id++, "healing", 0, r, 0, $"probe #{n}"));
            ServerDiagnostics.Write($"   card #{n,2}: resourceId {r}");
        }
        ServerDiagnostics.Write($"[Consumables] PROBE seeded {list.Count} modifier-hunt cards");
        return list;
    }

    internal readonly record struct ConsumableDef(
        string Category, string Kind, int Amount, int Bronze, int Silver, int Gold, int CardSubtypeId);

    internal static readonly Dictionary<long, ConsumableDef> Effects = LoadEffects();

    private static Dictionary<long, ConsumableDef> LoadEffects()
    {
        var dict = new Dictionary<long, ConsumableDef>();
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "consumable_mods.tsv");
        // # resourceId  category  kind  amount  bronze  silver  gold  subtype
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length < 8 || !long.TryParse(c[0], out long rid)) continue;
                dict[rid] = new ConsumableDef(
                    c[1], c[2],
                    int.TryParse(c[3], out int am) ? am : 0,
                    int.TryParse(c[4], out int br) ? br : 0,
                    int.TryParse(c[5], out int si) ? si : 0,
                    int.TryParse(c[6], out int go) ? go : 0,
                    int.TryParse(c[7], out int st) ? st : 0);
            }
        }
        catch (FileNotFoundException)
        {
            ServerDiagnostics.Write($"[Consumables] no {path}; consumable effects will use approximations");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[Consumables] modifier table load failed: {ex.GetType().Name}: {ex.Message}");
        }
        return dict;
    }

    internal static string CanonicalType(string rawItemType)
    {
        string t = rawItemType ?? "";
        if (t.StartsWith("Contract", StringComparison.OrdinalIgnoreCase)) return "contract";
        if (t.StartsWith("Fitness",  StringComparison.OrdinalIgnoreCase)) return "fitness";
        if (t.StartsWith("Health",   StringComparison.OrdinalIgnoreCase)) return "healing";
        if (t.StartsWith("TrainingPlayerPos", StringComparison.OrdinalIgnoreCase)) return "position";
        if (t.StartsWith("Training", StringComparison.OrdinalIgnoreCase)) return "training";
        return t;
    }

    internal static string WireItemType(string rawItemType)
    {
        string t = rawItemType ?? "";
        if (t.StartsWith("Contract", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Fitness", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Health", StringComparison.OrdinalIgnoreCase))
            return "development";
        return "training";
    }

    public static string BuildJson(ConsumableItem it, long timestamp, int pile = 6, string itemState = "free")
    {
        return
            "{\"id\":" + it.ItemId + ",\"timestamp\":" + timestamp + ",\"formation\":\"f442\"," +
            "\"untradeable\":false,\"assetId\":0,\"rating\":0,\"itemType\":\"" + WireItemType(it.ItemType) + "\"," +
            "\"resourceId\":" + it.ResourceId + ",\"owners\":1,\"discardValue\":0," +
            "\"itemState\":\"" + itemState + "\",\"cardsubtypeid\":" + it.SubType + ",\"lastSalePrice\":0," +
            "\"statsList\":[],\"lifetimeStats\":[],\"attributeList\":[],\"teamid\":0," +
            "\"rareflag\":" + it.RareFlag + ",\"leagueId\":0,\"pile\":" + pile + ",\"resourceGameYear\":2014," +
            "\"count\":1,\"consumableCount\":1,\"name\":\"" + Esc(it.Name) + "\"}";
    }

    private static string Esc(string s) => (s ?? "")
        .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
}
