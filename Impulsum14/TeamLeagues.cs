namespace Impulsum14;

internal static class TeamLeagues
{
    private static readonly Dictionary<int, int> Map = Load();

    private static Dictionary<int, int> Load()
    {
        var map = new Dictionary<int, int>();
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "teamleagues.tsv");
        try
        {
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                if (c.Length < 2) continue;
                if (int.TryParse(c[0], out int tid) && int.TryParse(c[1], out int lid))
                    map[tid] = lid;
            }
            ServerDiagnostics.Write($"[TeamLeagues] loaded {map.Count} team->league rows from {path}");
        }
        catch (FileNotFoundException)
        {
            ServerDiagnostics.Write($"[TeamLeagues] no {path}, running without league filter");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[TeamLeagues] FAILED to load {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return map;
    }

    internal static int LeagueOf(int teamId) => Map.TryGetValue(teamId, out int lid) ? lid : 0;

    private static readonly int[] Leagues = Map.Values.Distinct().OrderBy(v => v).ToArray();
    internal static int[] AllLeagues => Leagues;
}