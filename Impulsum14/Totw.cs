using System.Text;

namespace Impulsum14;

internal static class Totw
{
    private const int InFormBand = 50;   // resourceId = baseId + band*0x1000000; rareflag 3 = in-form art

    private const long DefaultPersonaId = 9223372036854775806L;

    private static long PersonaId()
    {
        string s = System.Environment.GetEnvironmentVariable("FUT_TOTW_PERSONA");
        return long.TryParse(s, out long v) ? v : DefaultPersonaId;
    }

    internal static long ClubPersona => PersonaId();

    internal static string ClubInfoJson()
    {
        var weeks = Weeks();
        var sb = new StringBuilder();
        if (weeks.Length == 0)
            sb.Append("{\"id\":0,\"squadName\":\"Team of the Week\",\"formation\":\"f442\",\"rating\":")
              .Append(SquadRating()).Append(",\"chemistry\":100,\"squadType\":\"REGULAR_SQUAD\"}");
        for (int i = 0; i < weeks.Length; i++)
        {
            if (i > 0) sb.Append(',');
            int wk = weeks[i];
            sb.Append("{\"id\":").Append(wk).Append(",\"squadName\":\"TOTW ").Append(wk)
              .Append("\",\"formation\":\"").Append(Formation(wk)).Append("\",\"rating\":").Append(WeekRating(wk))
              .Append(",\"chemistry\":100,\"squadType\":\"REGULAR_SQUAD\"}");
        }
        return "{\"user\":[{\"personaId\":" + ClubPersona +
               ",\"clubName\":\"Team of the Week\",\"clubAbbr\":\"TOTW\",\"teamId\":0,\"bidTokens\":{}," +
               "\"established\":\"1774098680\",\"squadList\":{\"squad\":[" + sb + "]," +
               "\"activeSquadId\":" + ActiveWeek() + "},\"badge\":{\"resourceId\":6000654,\"teamId\":111993}," +
               "\"homekit\":{\"resourceId\":6300815,\"teamId\":112393,\"categoryId\":5,\"year\":0}," +
               "\"awaykit\":{\"resourceId\":6400685,\"teamId\":286,\"categoryId\":3,\"year\":0}}]}";
    }

    private static int SquadRating()
    {
        int r = 0;
        foreach (var p in SelectSquad()) if (p.Rating > r) r = p.Rating;
        return r;
    }

    private static readonly (int key, long val)[] ChallengeEntries =
    {
        (1, 3), (2, 1), (3, 2147483647), (4, -2), (5, 1), (6, 12),
        (7, 1398034260), (8, 541150240), (9, 1095190860),
        (10, 1), (11, 3), (12, 0), (13, 1), (14, 0),
    };

    internal static string ChallengeEntriesJson()
    {
        var sb = new StringBuilder("{\"entries\":[");
        for (int i = 0; i < ChallengeEntries.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"key\":").Append(ChallengeEntries[i].key)
              .Append(",\"value\":").Append(ChallengeEntries[i].val).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    internal static string HubSquadJson() => SquadForWeek(ActiveWeek());

    /// <summary>Challenge envelope for a /totw GET (on-enter fetch); the squad is the same XI.</summary>
    internal static string SquadChallengeJson()
    {
        string squad = SquadForWeek(ActiveWeek());
        return "{\"matchDifficulty\":2,\"grantsGameModePrizes\":true,\"squad\":" + squad +
               ",\"squadChallenge\":{\"squad\":" + squad + "}}";
    }

    private readonly record struct Slot(int Order, int Starter, string Pos, int BaseId, int Rating);

    private static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Slot>> Teams = LoadTeams();

    private static System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Slot>> LoadTeams()
    {
        var d = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Slot>>();
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "FUTDB", "totw_teams.tsv");
        try
        {
            bool header = true;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (header) { header = false; continue; }
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 6) continue;
                int wk = int.Parse(c[0]);
                if (!d.TryGetValue(wk, out var l)) { l = new System.Collections.Generic.List<Slot>(); d[wk] = l; }
                l.Add(new Slot(int.Parse(c[1]), int.Parse(c[2]), c[3], int.Parse(c[4]), int.Parse(c[5])));
            }
            ServerDiagnostics.Write($"[TOTW] loaded {d.Count} weekly teams from {path}");
        }
        catch (System.IO.FileNotFoundException) { ServerDiagnostics.Write("[TOTW] no totw_teams.tsv - using top-rated placeholder squad"); }
        catch (System.Exception ex) { ServerDiagnostics.Write($"[TOTW] failed to load teams: {ex.Message}"); }
        return d;
    }

    private static readonly System.Collections.Generic.Dictionary<int, string> Formations = LoadFormations();

    private static System.Collections.Generic.Dictionary<int, string> LoadFormations()
    {
        var d = new System.Collections.Generic.Dictionary<int, string>();
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "FUTDB", "totw_formations.tsv");
        try
        {
            bool header = true;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (header) { header = false; continue; }
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length >= 2 && int.TryParse(c[0], out int wk)) d[wk] = c[1].Trim();
            }
        }
        catch (System.IO.FileNotFoundException) { }
        catch (System.Exception ex) { ServerDiagnostics.Write($"[TOTW] failed to load formations: {ex.Message}"); }
        return d;
    }

    private static string Formation(int week) => Formations.TryGetValue(week, out var f) ? f : "f442";

    internal static int[] Weeks()
    {
        var ks = new System.Collections.Generic.List<int>(Teams.Keys);
        ks.Sort();
        return ks.ToArray();
    }

    internal static int ActiveWeek()
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable("FUT_TOTW_WEEK"), out int w) && Teams.ContainsKey(w))
            return w;
        var wk = Weeks();
        return wk.Length > 0 ? wk[wk.Length - 1] : 1;
    }

    private static int WeekRating(int week)
    {
        if (!Teams.TryGetValue(week, out var slots)) return SquadRating();
        int sum = 0, cnt = 0;
        foreach (var s in slots)
            if (s.Starter == 1 && ResolveCard(s.BaseId, s.Rating) is RealPlayer c) { sum += c.Rating; cnt++; }
        return cnt > 0 ? sum / cnt : SquadRating();
    }

    private static RealPlayer? ResolveCard(int baseId, int rating)
    {
        foreach (var p in SpecialCards.All)
            if (p.Id == baseId && p.Rating == rating && p.Set == "totw") return p;
        foreach (var p in RealPlayers.All)
            if (p.Id == baseId)
                return p with { Rating = rating, Rare = 3, ResourceId = baseId + InFormBand * 0x1000000 };
        return null;
    }

    private static string SlotJson(int idx, string item) =>
        "{\"index\":" + idx + ",\"loyaltyBonus\":1,\"kitNumber\":0,\"chemistry\":10,\"itemData\":" + item + "}";

    private static string EmptySlot(int idx) =>
        "{\"index\":" + idx + ",\"loyaltyBonus\":0,\"kitNumber\":0,\"chemistry\":0,\"itemData\":{\"id\":0}}";

    internal static string SquadForWeek(int week)
    {
        var rnd = new System.Random(1400 + week);
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long persona = PersonaId();

        var players = new StringBuilder("[");
        long captainId = 0;
        int sumR = 0, cntR = 0, topR = 0;

        if (Teams.TryGetValue(week, out var slots) && slots.Count > 0)
        {
            var byOrder = new System.Collections.Generic.Dictionary<int, Slot>();
            foreach (var s in slots) byOrder[s.Order] = s;
            for (int idx = 0; idx < 18; idx++)
            {
                if (idx > 0) players.Append(',');
                long itemId = 700_000_000L + week * 100 + idx;
                if (byOrder.TryGetValue(idx, out var slot) && ResolveCard(slot.BaseId, slot.Rating) is RealPlayer card)
                {
                    players.Append(SlotJson(idx, WebServer.BuildRealPlayerItem(rnd, card, itemId, now, 7)));
                    if (card.Rating > topR) topR = card.Rating;
                    if (slot.Starter == 1) { sumR += card.Rating; cntR++; if (captainId == 0) captainId = itemId; }
                }
                else players.Append(EmptySlot(idx));  
            }
        }
        else
        {
            var picks = SelectSquad();
            for (int idx = 0; idx < picks.Count; idx++)
            {
                if (idx > 0) players.Append(',');
                long itemId = 700_000_000L + idx;
                players.Append(SlotJson(idx, WebServer.BuildRealPlayerItem(rnd, picks[idx], itemId, now, 7)));
                if (picks[idx].Rating > topR) topR = picks[idx].Rating;
                if (idx == 0) captainId = itemId;
            }
        }
        players.Append(']');
        int rating = cntR > 0 ? sumR / cntR : topR;

        string kicktakers =
            "[{\"id\":" + captainId + ",\"index\":0},{\"id\":" + captainId + ",\"index\":1}," +
            "{\"id\":" + captainId + ",\"index\":2},{\"id\":" + captainId + ",\"index\":3}," +
            "{\"id\":" + captainId + ",\"index\":4}]";
        string name = Teams.ContainsKey(week) ? "TOTW " + week : "TOTW";

        return "{\"id\":0,\"valid\":false,\"personaId\":" + persona + ",\"formation\":\"" + Formation(week) + "\"," +
               "\"rating\":" + rating + ",\"chemistry\":100,\"manager\":[{\"id\":0,\"itemType\":\"manager\"}]," +
               "\"players\":" + players + ",\"dreamSquad\":false,\"changed\":0," +
               "\"squadName\":\"" + name + "\",\"starRating\":" + rating + ",\"captain\":" + captainId +
               ",\"kicktakers\":" + kicktakers + ",\"squadType\":\"REGULAR_SQUAD\",\"newSquad\":null,\"custom\":null}";
    }

    private static System.Collections.Generic.List<RealPlayer> SelectSquad()
    {
        var all = RealPlayers.All;
        var chosen = new System.Collections.Generic.List<RealPlayer>();

        RealPlayer gk = default;
        int gkRating = -1;
        foreach (var p in all)
            if (p.Position == "GK" && p.Rating > gkRating) { gk = p; gkRating = p.Rating; }
        if (gkRating >= 0) chosen.Add(gk);

        foreach (var p in SortByRatingDesc(all))
        {
            if (chosen.Count >= 23) break;
            if (p.Position == "GK") continue;
            chosen.Add(p);
        }

        var result = new System.Collections.Generic.List<RealPlayer>();
        foreach (var p in chosen)
        {
            if (result.Count >= 23) break;
            result.Add(p with
            {
                Rating = System.Math.Min(99, p.Rating + 1),
                Rare = 3,
                ResourceId = p.Id + InFormBand * 0x1000000,
            });
        }
        return result;
    }

    private static RealPlayer[] SortByRatingDesc(RealPlayer[] src)
    {
        var copy = (RealPlayer[])src.Clone();
        System.Array.Sort(copy, (a, b) => b.Rating.CompareTo(a.Rating));
        return copy;
    }
}
