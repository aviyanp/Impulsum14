namespace Impulsum14;

internal readonly record struct Manager(int ResourceId, string Name, int Rating, int NationId, int LeagueId);

internal static class Managers
{
    internal static readonly Manager[] All = Load();

    private static Manager[] Load()
    {
        if (Environment.GetEnvironmentVariable("FUT_SERVE_MANAGERS") == "0")
        {
            ServerDiagnostics.Write("[Managers] not served (FUT_SERVE_MANAGERS=0).");
            return System.Array.Empty<Manager>();
        }

        var path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "managers.tsv");
        var list = new System.Collections.Generic.List<Manager>();
        try
        {
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 5) continue;
                if (!int.TryParse(c[0], out int rid)) continue;   // skips the header row
                list.Add(new Manager(rid, c[1],
                    int.TryParse(c[2], out int rt) ? rt : 75,
                    int.TryParse(c[3], out int nat) ? nat : 0,
                    int.TryParse(c[4], out int lg) ? lg : 0));
            }
        }
        catch (Exception ex) { ServerDiagnostics.Write($"[Managers] failed: {ex.Message}"); }
        return list.ToArray();
    }
}
