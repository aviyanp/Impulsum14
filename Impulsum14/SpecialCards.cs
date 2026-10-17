namespace Impulsum14;

internal static class SpecialCards
{
    internal const int Band = 0x1000000;

    private const int ProbeBand = 64;

    internal static readonly RealPlayer[] All = Load();

    private static RealPlayer[] Load()
    {
        var list = new List<RealPlayer>();
        var bases = RealPlayers.All.ToDictionary(p => p.Id);
        LoadFile(Path.Combine(AppContext.BaseDirectory, "FUTDB", "specials.tsv"), list, bases);

        foreach (var clash in list.GroupBy(p => p.ResourceId).Where(g => g.Count() > 1))
            ServerDiagnostics.Write($"[Specials] ERROR duplicate resourceId {clash.Key} on baseId " +
                              $"{clash.First().Id} ({clash.Count()} cards) - give each row its own band");

        return list.Concat(Probe(list)).Concat(Legends.Enabled).ToArray();
    }

    private static void LoadFile(string path, List<RealPlayer> list, Dictionary<int, RealPlayer> bases)
    {
        try
        {
            int lineNo = 1;
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                lineNo++;
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length < 12) continue;
                // set  band  rareflag  baseId  pos  rating  pac  sho  pas  dri  def  phy
                int band = int.Parse(c[1]);
                int rareflag = int.Parse(c[2]);
                int baseId = int.Parse(c[3]);
                if (!bases.TryGetValue(baseId, out var b))
                {
                    ServerDiagnostics.Write($"[Specials] {path}:{lineNo} baseId {baseId} is not in players.tsv, skipping");
                    continue;
                }
                int rating = int.Parse(c[5]);
                list.Add(new RealPlayer(
                    baseId, "", b.TeamId, b.NationId, c[4], rating, rating,
                    int.Parse(c[6]), int.Parse(c[7]), int.Parse(c[8]),
                    int.Parse(c[9]), int.Parse(c[10]), int.Parse(c[11]), rareflag,
                    b.Strength, b.BallControl, b.ShotPower, b.SkillMoves, b.FkAccuracy)
                {
                    ResourceId = baseId + band * Band,
                    Set = c[0],
                });
            }
        }
        catch (FileNotFoundException)
        {
            ServerDiagnostics.Write($"[Specials] no {path}, skipping");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[Specials] FAILED to load {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<RealPlayer> Probe(List<RealPlayer> specials)
    {
        string spec = Environment.GetEnvironmentVariable("FUT_PROBE_RAREFLAG");
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<RealPlayer>();

        var parts = spec.Split('-');
        if (!int.TryParse(parts[0], out int from)) return Array.Empty<RealPlayer>();
        int to = parts.Length > 1 && int.TryParse(parts[1], out int t) ? t : from;

        var taken = new HashSet<int>(specials.Select(p => p.Id));
        var pool = RealPlayers.All.Where(p => p.Rating >= 75 && !taken.Contains(p.Id))
                                  .DistinctBy(p => p.Id).OrderByDescending(p => p.Rating).ToArray();
        var probes = new List<RealPlayer>();
        for (int flag = from; flag <= to && probes.Count < pool.Length; flag++)
        {
            var b = pool[probes.Count];
            int rating = 99 - (flag - from);
            probes.Add(b with
            {
                Rating = rating,
                Potential = rating,
                Rare = flag,
                ResourceId = b.Id + ProbeBand * Band,
                Set = "probe",
            });
            ServerDiagnostics.Write($"[Specials] probe rareflag={flag} -> rating {rating}, base player {b.Id} ({b.Position})");
        }
        ServerDiagnostics.Write($"[Specials] probing rareflag {from}..{to}: read the flag off a card as {99 + from} minus its rating");
        return probes;
    }
}
