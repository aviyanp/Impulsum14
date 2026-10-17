using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text;

namespace Impulsum14;

internal static class Market
{
    // Only deterministic catalogue calculations are shared. Account purchases,
    // watches, escrow, bids and auction time are always evaluated by the caller.
    private readonly record struct StartKey(byte Family, long Listing, long Cycle);
    private const int StartCacheLimit = 8192;
    private static readonly ConcurrentDictionary<StartKey, long> StartCache = new();
    private static readonly Queue<StartKey> StartCacheOrder = new();
    private static readonly object StartCacheGate = new();

    private static long RememberStart(StartKey key, long value)
    {
        lock (StartCacheGate)
        {
            if (StartCache.TryGetValue(key, out long existing)) return existing;
            while (StartCache.Count >= StartCacheLimit)
                StartCache.TryRemove(StartCacheOrder.Dequeue(), out _);
            StartCache[key] = value;
            StartCacheOrder.Enqueue(key);
            return value;
        }
    }

    internal readonly record struct PlayerFilterKey(int MinimumRating, int MaximumRating,
        int Nation, int League, int Team, int BaseDefinition, int Definition, int Level)
    {
        internal bool IsUnfiltered => MinimumRating < 0 && MaximumRating < 0 && Nation < 0
            && League < 0 && Team < 0 && BaseDefinition < 0 && Definition < 0 && Level == 0;

        internal bool Matches(RealPlayer card) =>
            (MinimumRating < 0 || card.Rating >= MinimumRating)
            && (MaximumRating < 0 || card.Rating <= MaximumRating)
            && (Nation < 0 || card.NationId == Nation)
            && (League < 0 || TeamLeagues.LeagueOf(card.TeamId) == League)
            && (Team < 0 || card.TeamId == Team)
            && (BaseDefinition < 0 || card.Id == BaseDefinition)
            && (Definition < 0 || card.CardId == Definition)
            && (Level == 0 || (Level == 1 ? card.Rating < 65
                : Level == 2 ? card.Rating is >= 65 and < 75 : card.Rating >= 75));
    }

    internal static PlayerFilterKey ParsePlayerFilter(NameValueCollection query)
    {
        int Nonnegative(string name) => int.TryParse(query[name], out int value) && value >= 0 ? value : -1;
        int Positive(string name) => int.TryParse(query[name], out int value) && value > 0 ? value : -1;
        int masked = Positive("maskedDefId");
        int level = (query["lev"] ?? "").ToLowerInvariant() switch { "bronze" => 1, "silver" => 2, "gold" => 3, _ => 0 };
        return new(Nonnegative("minRating"), Nonnegative("maxRating"), Nonnegative("nat"),
            Nonnegative("leag"), Nonnegative("team"), masked < 0 ? -1 : masked % SpecialCards.Band,
            Positive("definitionId"), level);
    }

    private readonly record struct SelectionKey(byte Family, PlayerFilterKey Player = default,
        string Category = "", string Level = "", string Position = "", string WireType = "",
        int Style = 0, int Nation = 0, int League = 0, int Team = 0);
    private sealed record SelectionView(int[] Indices, long[] Prefix, long Total, int HalfBits)
    {
        internal long Bytes => 64L + Indices.LongLength * sizeof(int) + Prefix.LongLength * sizeof(long);
    }

    private const int SelectionCacheLimit = 64;
    private const long SelectionCacheByteLimit = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<SelectionKey, SelectionView> SelectionCache = new();
    private static readonly Queue<SelectionKey> SelectionCacheOrder = new();
    private static readonly object SelectionCacheGate = new();
    private static long SelectionCacheBytes;

    private static SelectionView CreateSelection(int[] counts, Func<int, bool> matches)
    {
        var selected = new List<int>();
        for (int i = 0; i < counts.Length; i++) if (matches(i)) selected.Add(i);
        var prefix = new long[selected.Count + 1];
        long total = 0;
        for (int i = 0; i < selected.Count; i++) prefix[i + 1] = total += counts[selected[i]];
        return new(selected.ToArray(), prefix, total, HalfBitsFor(total));
    }

    private static SelectionView RememberSelection(SelectionKey key, SelectionView view)
    {
        // Avoid retaining oversized untrusted query text, even for an empty view.
        int textLength = key.Category.Length + key.Level.Length + key.Position.Length + key.WireType.Length;
        long bytes = view.Bytes + textLength * sizeof(char);
        if (textLength > 512 || bytes > SelectionCacheByteLimit) return view;
        lock (SelectionCacheGate)
        {
            if (SelectionCache.TryGetValue(key, out var existing)) return existing;
            while (SelectionCache.Count >= SelectionCacheLimit || SelectionCacheBytes + bytes > SelectionCacheByteLimit)
            {
                var oldest = SelectionCacheOrder.Dequeue();
                if (SelectionCache.TryRemove(oldest, out var removed))
                    SelectionCacheBytes -= removed.Bytes + (oldest.Category.Length + oldest.Level.Length
                        + oldest.Position.Length + oldest.WireType.Length) * sizeof(char);
            }
            SelectionCache[key] = view;
            SelectionCacheOrder.Enqueue(key);
            SelectionCacheBytes += bytes;
            return view;
        }
    }

    private static readonly RealPlayer[] Cards;  
    private static readonly int[] Counts;   
    private static readonly long[] Prefix;  
    internal static long Total { get; }
    private static readonly int HalfBits;       

    private static int TargetTotal => ServerOptions.Current.MarketPlayers;
    internal const long TradeIdBase = 2_000_000_000L; 
    private const long ItemIdBase = 3_000_000_000L;  

    internal const long ConsumableTradeIdBase = 2_010_000_000L; 
    private const long ConsumableItemIdBaseMkt = 3_010_000_000L;  
    private static int TargetCTotal => ServerOptions.Current.MarketConsumables;
    private static readonly ConsumableItem[] CCards = Array.Empty<ConsumableItem>();
    private static readonly int[] CTier = Array.Empty<int>();   
    private static readonly long[] CBaseP = Array.Empty<long>(); 
    private static readonly int[] CCounts = Array.Empty<int>();
    private static readonly long[] CPrefix = new long[1];
    internal static long CTotal { get; }
    private static readonly int CHalfBits = 1;

    internal const long ClubItemTradeIdBase = 2_020_000_000L;   
    private const long ClubItemItemIdBaseMkt = 3_020_000_000L;    
    private static int TargetETotal => ServerOptions.Current.MarketClubItems;
    private static readonly CosmeticItem[] ECards = Array.Empty<CosmeticItem>();
    private static readonly long[] EBaseP = Array.Empty<long>();
    private static readonly int[] ECounts = Array.Empty<int>();
    private static readonly long[] EPrefix = new long[1];
    internal static long ETotal { get; }
    private static readonly int EHalfBits = 1;

    internal const long StaffTradeIdBase = 2_030_000_000L;     
    private const long StaffItemIdBaseMkt = 3_030_000_000L;   
    private static int TargetSTotal => ServerOptions.Current.MarketStaff;
    private static readonly bool[] FIsManager = Array.Empty<bool>();
    private static readonly Manager[] FManager = Array.Empty<Manager>();
    private static readonly StaffCard[] FStaff = Array.Empty<StaffCard>();
    private static readonly long[] FBaseP = Array.Empty<long>();
    private static readonly int[] FCounts = Array.Empty<int>();
    private static readonly long[] FPrefix = new long[1];
    internal static long STotal { get; }
    private static readonly int SHalfBits = 1;  

    static Market()
    {
        var pool = SpecialCards.All.Concat(RealPlayers.All).ToArray();
        Cards = pool.OrderBy(c => Hash((uint)c.CardId, 0x9E3779B1u)).ToArray();

        var raw = new int[Cards.Length];
        long rawSum = 0;
        for (int i = 0; i < Cards.Length; i++) { raw[i] = BaseCount(Cards[i]); rawSum += raw[i]; }
        double calib = rawSum > 0 ? (double)TargetTotal / rawSum : 1.0;

        Counts = new int[Cards.Length];
        Prefix = new long[Cards.Length + 1];
        long acc = 0;
        for (int i = 0; i < Cards.Length; i++)
        {
            int n = Math.Max(1, (int)Math.Round(raw[i] * calib));
            Counts[i] = n;
            acc += n;
            Prefix[i + 1] = acc;
        }
        Total = acc;

        HalfBits = HalfBitsFor(Total);

        FeedTierCards = new int[FeedTiers.Length][];
        for (int t = 0; t < FeedTiers.Length; t++)
        FeedTierCards[t] = Enumerable.Range(0, Cards.Length)
            .Where(i => FeedTiers[t].pick(Cards[i])).ToArray();

        var rawC = ConsumableItems.Catalog;
        if (rawC.Length > 0)
        {
            CCards = rawC;
            CTier = new int[rawC.Length];
            CBaseP = new long[rawC.Length];
            var famN = new Dictionary<string, int>();
            for (int i = 0; i < rawC.Length; i++)
                famN[rawC[i].ItemType] = famN.GetValueOrDefault(rawC[i].ItemType) + 1;

            var famPos = new Dictionary<string, int>();
            var cRaw = new long[rawC.Length];
            long cSum = 0;
            for (int i = 0; i < rawC.Length; i++)
            {
                var c = rawC[i];
                int pos = famPos.GetValueOrDefault(c.ItemType);
                famPos[c.ItemType] = pos + 1;
                int fam = famN[c.ItemType];
                int tier = c.ItemType.Equals("playStyle", StringComparison.OrdinalIgnoreCase) ? 2
                    : c.ItemType.StartsWith("TrainingPlayerPos", StringComparison.OrdinalIgnoreCase) ? 2
                    : c.ItemType.Equals("managerLeagueModifier", StringComparison.OrdinalIgnoreCase) ? 1
                    : fam >= 3 ? pos % 3
                    : 0;
                CTier[i] = tier;
                CBaseP[i] = ConsumableBasePrice(c, tier);

                double wt = c.ItemType.StartsWith("Contract", StringComparison.OrdinalIgnoreCase)
                    ? c.ItemType.Contains("Staff") ? 4.0 : 14.0
                    : c.ItemType.StartsWith("Fitness", StringComparison.OrdinalIgnoreCase) ? 10.0
                    : c.ItemType.StartsWith("Health", StringComparison.OrdinalIgnoreCase) ? 9.0
                    : c.ItemType.StartsWith("TrainingPlayerPos", StringComparison.OrdinalIgnoreCase) ? 7.0
                    : c.ItemType.Equals("playStyle", StringComparison.OrdinalIgnoreCase) ? 9.0
                    : c.ItemType.Equals("managerLeagueModifier", StringComparison.OrdinalIgnoreCase) ? 6.0
                    : 8.0;
                double tierW = tier == 0 ? 2.0 : tier == 1 ? 1.5 : 1.0;
                cRaw[i] = (long)(wt * tierW * (c.RareFlag != 0 ? 1.15 : 1.0));
                cSum += cRaw[i];
            }

            double cCalib = cSum > 0 ? (double)TargetCTotal / cSum : 1.0;
            CCounts = new int[rawC.Length];
            CPrefix = new long[rawC.Length + 1];
            long cAcc = 0;
            for (int i = 0; i < rawC.Length; i++)
            {
                int n = Math.Max(1, (int)Math.Round(cRaw[i] * cCalib));
                CCounts[i] = n;
                cAcc += n;
                CPrefix[i + 1] = cAcc;
            }
            CTotal = cAcc;
            CHalfBits = HalfBitsFor(CTotal);
        }

        var rawE = ClubItems.Catalog;
        if (rawE.Length > 0)
        {
            ECards = rawE;
            EBaseP = new long[rawE.Length];
            var eRaw = new long[rawE.Length];
            long eSum = 0;
            for (int i = 0; i < rawE.Length; i++)
            {
                var it = rawE[i];
                long bp = it.Type switch { "kit" => 400, "badge" => 350, "ball" => 800, _ => 8000 };
                double rareMult = it.Rare != 0 ? it.Type switch { "badge" => 4.0, "kit" => 3.0, "ball" => 2.0, _ => 1.5 } : 1.0;
                EBaseP[i] = (long)(bp * rareMult);
                double wt = it.Type switch { "kit" => 10.0, "badge" => 8.0, "ball" => 6.0, _ => 3.0 };
                eRaw[i] = (long)(wt * (it.Rare != 0 ? 1.2 : 1.0));
                eSum += eRaw[i];
            }

            double eCalib = eSum > 0 ? (double)TargetETotal / eSum : 1.0;
            ECounts = new int[rawE.Length];
            EPrefix = new long[rawE.Length + 1];
            long eAcc = 0;
            for (int i = 0; i < rawE.Length; i++)
            {
                int n = Math.Max(1, (int)Math.Round(eRaw[i] * eCalib));
                ECounts[i] = n;
                eAcc += n;
                EPrefix[i + 1] = eAcc;
            }
            ETotal = eAcc;
            EHalfBits = HalfBitsFor(ETotal);
        }

        var rawM = Managers.All;
        var rawS = Staff.All;
        if (rawM.Length + rawS.Length > 0)
        {
            int n = rawM.Length + rawS.Length;
            FIsManager = new bool[n];
            FManager = new Manager[n];
            FStaff = new StaffCard[n];
            FBaseP = new long[n];
            var fRaw = new long[n];
            long fSum = 0;
            for (int i = 0; i < rawM.Length; i++)
            {
                FIsManager[i] = true;
                FManager[i] = rawM[i];
                int r = rawM[i].Rating;
                FBaseP[i] = r >= 80 ? 2500 + (r - 79) * 1500 : 300 + r * 10;
                fRaw[i] = 8;
                fSum += fRaw[i];
            }
            for (int j = 0; j < rawS.Length; j++)
            {
                int i = rawM.Length + j;
                var s = rawS[j];
                FIsManager[i] = false;
                FStaff[i] = s;
                long bp = s.ItemType switch
                {
                    "headCoach" => 250 + s.Rating * 5,
                    "gkCoach" => 250 + s.Rating * 5,
                    "physio" => 350 + s.Rating * 6,
                    _ => 600 + s.Rating * 10,
                };
                FBaseP[i] = (long)(bp * (s.Rare != 0 ? 2.5 : 1.0));
                fRaw[i] = s.ItemType switch { "headCoach" => 5, "gkCoach" => 4, "physio" => 5, _ => 5 };
                fSum += fRaw[i];
            }

            double fCalib = fSum > 0 ? (double)TargetSTotal / fSum : 1.0;
            FCounts = new int[n];
            FPrefix = new long[n + 1];
            long fAcc = 0;
            for (int i = 0; i < n; i++)
            {
                int cn = Math.Max(1, (int)Math.Round(fRaw[i] * fCalib));
                FCounts[i] = cn;
                fAcc += cn;
                FPrefix[i + 1] = fAcc;
            }
            STotal = fAcc;
            SHalfBits = HalfBitsFor(STotal);
        }
    }

    private static long ConsumableBasePrice(ConsumableItem c, int tier)
    {
        const StringComparison OI = StringComparison.OrdinalIgnoreCase;
        string t = c.ItemType ?? "";
        long b;
        if (t.StartsWith("Contract", OI))
            b = t.Contains("Staff") ? tier switch { 0 => 300, 1 => 500, _ => 900 }
                                    : tier switch { 0 => 200, 1 => 350, _ => 600 };
        else if (t.StartsWith("Fitness", OI))
            b = tier switch { 0 => 250, 1 => 450, _ => 900 };
        else if (t.StartsWith("Health", OI))
            b = tier switch { 0 => 150, 1 => 250, _ => 450 };
        else if (t.StartsWith("TrainingPlayerPos", OI))
            b = 4200;
        else if (t.Equals("playStyle", OI))
            b = 2500 + 150L * (c.ResourceId % 10);
        else if (t.Equals("managerLeagueModifier", OI))
            b = tier switch { 0 => 500, 1 => 900, _ => 1400 };
        else if (t.StartsWith("TrainingGk", OI))
            b = tier switch { 0 => 250, 1 => 550, _ => 1200 };
        else
            b = tier switch { 0 => 350, 1 => 800, _ => 1800 };
        return b;
    }

    private static int BaseCount(RealPlayer c)
    {
        int r = c.Rating;
        int b = r <= 64 ? 220 : r <= 74 ? 280 : r <= 79 ? 190 : r <= 82 ? 115
              : r <= 84 ? 65 : r <= 86 ? 90 : r <= 88 ? 80 : r <= 90 ? 60 : r <= 93 ? 45 : 40;
        if (r >= 88) b = Math.Max(b, 12);
        if (c.IsSpecial) return Math.Max(1, (int)(b * 0.10));
        if (c.Rare != 0 && r >= 75) return (int)(b * 1.15);
        return b;
    }

    private static long BasePrice(RealPlayer c)
    {
        long p = RatingPrice(c.Rating);
        if (c.IsSpecial) return (long)(p * SpecialMult(c.Rare));
        if (c.Rare != 0 && c.Rating >= 75) return (long)(p * 1.25);
        return p;
    }

    private static long RatingPrice(int r) => r switch
    {
        <= 63 => 150,   64 => 170,   65 => 200,   66 => 220,   67 => 250,   68 => 280,
        69 => 320,      70 => 360,   71 => 420,   72 => 500,   73 => 600,   74 => 750,
        75 => 950,      76 => 1200,  77 => 1600,  78 => 2100,  79 => 2800,  80 => 3800,
        81 => 5200,     82 => 7000,  83 => 9500,  84 => 13000, 85 => 19000, 86 => 28000,
        87 => 42000,    88 => 65000, 89 => 100000, 90 => 160000, 91 => 260000, 92 => 430000,
        93 => 700000,   _ => 1100000,
    };

    private static double SpecialMult(int rareflag) => rareflag switch
    {
        5 => 8.0,    // TOTY
        11 => 5.0,   // TOTS
        3 => 2.0,    // in-form / TOTW
        _ => 2.5,
    };

    private static (int startingBid, int buyNow) Price(RealPlayer c, long g, long k)
    {
        long baseP = BasePrice(c);
        var rng = new Rng(Hash((uint)c.CardId, (uint)g) ^ 0x51ED2C0Bu);
        uint w = Hash((uint)g, (uint)(k * 0x85EBCA6Bu) ^ 0x5F356495u);
        double wig = 0.70 + (w % 61) / 100.0;                     // each listing cycle gets its own price
        long buy = Snap((long)(baseP * wig * (0.80 + rng.NextDouble() * 0.55)));   // 0.80 .. 1.35
        long start = Snap((long)(buy * (0.55 + rng.NextDouble() * 0.30)));         // 0.55 .. 0.85
        if (start < 150) start = 150;
        if (start >= buy) start = Math.Max(150, buy - Step(buy));
        return ((int)start, (int)buy);
    }


    private static (long dur, long gap, long phase) Lifecycle(long g)
    {
        uint s = Hash((uint)g, 0x51515EA7u);
        int r = Cards[Locate(g)].Rating;
        long dur = (s & 3) switch
        {
            0 => r >= 85 ? 1800L : r >= 80 ? 3600L : 7200L,
            1 => r >= 85 ? 3600L : r >= 80 ? 7200L : 10800L,
            2 => r >= 80 ? 3600L : 10800L,
            _ => r >= 85 ? 3600L : 14400L,
        };
        long gap = 30 + ((s >> 2) % (r >= 85 ? 150 : 300));       // hot cards relist quickly
        long phase = (s >> 4) % dur;
        return (dur, gap, phase);
    }

    private static (long k, long start, long dur, long gap, long local) Cycle(long g, long now)
    {
        var (dur, gap, phase) = Lifecycle(g);
        long period = dur + gap;
        long k = (now - phase) / period;
        long start = phase + k * period;
        return (k, start, dur, gap, now - start);
    }

    internal static bool LiveAt(long g, long now)
    {
        var c = Cycle(g, now);
        return c.local < c.dur;
    }

    internal static RealPlayer? ListingCard(long tradeId)
    {
        long g = tradeId - TradeIdBase;
        if (g < 0 || g >= Total) return null;
        return Cards[Locate(g)];
    }

    private static (bool hasBids, long simBid, int offers, long finalBid) SimBids(
        RealPlayer card, long g, long k, long dur, int startBid, int buyNow, long elapsed)
    {
        uint s = Hash((uint)g, (uint)(k * 0x9E3779B1u) ^ 0x6D2B79F5u);
        bool hot = card.Rating >= 85 || card.IsSpecial || card.Rating >= 80 && card.Rare != 0;
        int chance = hot ? 62 : card.Rating >= 80 ? 45 : 28;
        if (s % 100 >= chance) return (false, startBid, 0, startBid);
        if (buyNow - Step(buyNow) < startBid) return (false, startBid, 0, startBid);   // no room to bid above BIN

        long bidGap = hot ? 40 + ((s >> 7) % 130) : 45 + ((s >> 7) % 300);         // hot: ~40-170s
        long firstDelay = hot ? 20 + ((s >> 15) % Math.Max(1, dur / 10))
                              : 40 + ((s >> 15) % Math.Max(1, dur / 8));           // first bid arrives fast on stars
        long incr = startBid * (hot ? 4 + (int)((s >> 23) % 5) : 3 + (int)((s >> 23) % 5)) / 100;
        incr = Math.Max(50, (incr + Step(incr) - 1) / Step(incr) * Step(incr));
        long cap = startBid * (hot ? 150 + ((s >> 26) % 60) : 115 + ((s >> 26) % 60)) / 100;
        if (cap > buyNow) cap = buyNow;   // bot may push to BIN, never past it
        if (cap <= startBid) return (false, startBid, 0, startBid);   // no room to bid

        long finalN = Math.Max(1, (dur - firstDelay) / bidGap);
        long finalBid = Math.Min(cap, startBid + finalN * incr);
        if (elapsed <= firstDelay) return (true, startBid, 0, finalBid);

long n = Math.Min(finalN, (elapsed - firstDelay) / bidGap + 1);
        long cur = Math.Min(cap, startBid + n * incr);
        if (cur >= buyNow) return (true, buyNow, (int)Math.Min(n, 99), buyNow);  
        return (true, cur, (int)Math.Min(n, 99), finalBid);
    }

    internal static long LiveTotal(long now)
    {
        // UI estimate: a bounded, evenly spaced sample avoids a full-market scan.
        // The catalogue is virtual and private purchases are a tiny fraction of its slots.
        if (_liveTotalCachedAt > 0 && now - _liveTotalCachedAt < 10) return _liveTotalCachedValue;
        int samples = (int)Math.Min(4096, Total);
        if (samples == 0) return 0;
        long active = 0;
        for (int i = 0; i < samples; i++)
        {
            long g = (long)i * Total / samples;
            var cycle = Cycle(g, now);
            if (cycle.local < cycle.dur) active++;
        }
        _liveTotalCachedValue = active * Total / samples;
        _liveTotalCachedAt = now;
        return _liveTotalCachedValue;
    }

    private static long _liveTotalCachedAt;
    private static long _liveTotalCachedValue;

    private static long EffStart(RealPlayer card, long g, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        var key = new StartKey(0, g, k);
        return StartCache.TryGetValue(key, out long value) ? value
            : RememberStart(key, EffStartUncached(card, g, k, dur, s0, buy));
    }

    private static long EffStartUncached(RealPlayer card, long g, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        const int Window = 40;
        long k0 = Math.Max(0, k - Window);
        long eff = s0, prevEff = 0;
        var (previousStart, previousBuy) = Price(card, g, k0);
        for (long kk = k0 + 1; kk <= k; kk++)
        {
            var (sp, bp) = Price(card, g, kk);             // rolled start/buy of this cycle
            long sPrevOp = (kk - 1 > k0) ? prevEff : previousStart;
            var (hasBids, _, _, finalP) = SimBids(card, g, kk - 1, dur, (int)sPrevOp, previousBuy, dur);
            eff = (hasBids && finalP < previousBuy) ? Math.Max(sp, finalP) : sp;   // BIN'd sale -> fresh start
            if (eff > bp - Step(bp)) eff = Math.Max(sp, (long)bp - Step(bp));
            prevEff = eff;
            previousBuy = bp;
        }
        return eff;
    }


    private static (long dur, long gap, long phase) CLifecycle(long cg)
    {
        uint s = Hash((uint)cg, 0x51515EA7u ^ 0xABCD1234u);
        long dur = 1800 + (s % 5) * 900;             // 30 min .. 1h30
        long gap = 20 + ((s >> 3) % 90);
        long phase = (s >> 6) % dur;
        return (dur, gap, phase);
    }

    private static (long k, long start, long dur, long gap, long local) CCycle(long cg, long now)
    {
        var (dur, gap, phase) = CLifecycle(cg);
        long period = dur + gap;
        long k = (now - phase) / period;
        long start = phase + k * period;
        return (k, start, dur, gap, now - start);
    }

    private static bool LiveConsumableAt(long cg, long now)
    {
        var c = CCycle(cg, now);
        return c.local < c.dur && !CBoughtThisCycle(cg, now);
    }

    private static (int startingBid, int buyNow) CPrice(long cg, long k)
    {
        int i = LocateIn(CPrefix, cg);
        long baseP = CBaseP[i];
        var rng = new Rng(Hash((uint)cg, (uint)(i * 0x9E3779B1u)) ^ 0x51ED2C0Bu);
        uint w = Hash((uint)cg, (uint)(k * 0x85EBCA6Bu) ^ 0x5F356495u);
        double wig = 0.80 + (w % 41) / 100.0;
        long buy = Snap((long)(baseP * wig * (0.90 + rng.NextDouble() * 0.35)));
        long start = Snap((long)(buy * (0.55 + rng.NextDouble() * 0.30)));
        if (start < 150) start = 150;
        if (start >= buy) start = Math.Max(150, buy - Step(buy));
        return ((int)start, (int)buy);
    }

    private static (bool hasBids, long simBid, int offers, long finalBid) CSimBids(
        int tier, long cg, long k, long dur, int startBid, int buyNow, long elapsed)
    {
        uint s = Hash((uint)cg, (uint)(k * 0x9E3779B1u) ^ 0x6D2B79F5u);
        bool hot = tier == 2;
        int chance = hot ? 30 : 18;
        if (s % 100 >= chance) return (false, startBid, 0, startBid);
        if (buyNow - Step(buyNow) < startBid) return (false, startBid, 0, startBid);   // no room to bid above BIN
        long bidGap = 60 + ((s >> 7) % 200);
        long firstDelay = 30 + ((s >> 15) % Math.Max(1, dur / 8));
        long incr = Math.Max(50, startBid * 3 / 100 + Step(startBid));
        long cap = startBid * (hot ? 125 : 110) / 100;
        if (cap > buyNow) cap = buyNow;   // bot may push to BIN, never past it
        if (cap <= startBid) return (false, startBid, 0, startBid);   // no room to bid
        long finalN = Math.Max(1, (dur - firstDelay) / bidGap);
        long finalBid = Math.Min(cap, startBid + finalN * incr);
        if (elapsed <= firstDelay) return (true, startBid, 0, finalBid);
        long n = Math.Min(finalN, (elapsed - firstDelay) / bidGap + 1);
        long cur = Math.Min(cap, startBid + n * incr);
        if (cur >= buyNow) return (true, buyNow, (int)Math.Min(n, 99), buyNow);   // Bot hits BIN -> buys it now
        return (true, cur, (int)Math.Min(n, 99), finalBid);
    }

    private static long CEffStart(long cg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        var key = new StartKey(1, cg, k);
        return StartCache.TryGetValue(key, out long value) ? value
            : RememberStart(key, CEffStartUncached(cg, k, dur, s0, buy));
    }

    private static long CEffStartUncached(long cg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        const int Window = 40;
        long k0 = Math.Max(0, k - Window);
        int tier = CTier[LocateIn(CPrefix, cg)];
        long eff = s0, prevEff = 0;
        var (previousStart, previousBuy) = CPrice(cg, k0);
        for (long kk = k0 + 1; kk <= k; kk++)
        {
            var (sp, bp) = CPrice(cg, kk);
            long sPrevOp = (kk - 1 > k0) ? prevEff : previousStart;
            var (hasBids, _, _, finalP) = CSimBids(tier, cg, kk - 1, dur, (int)sPrevOp, previousBuy, dur);
            eff = (hasBids && finalP < previousBuy) ? Math.Max(sp, finalP) : sp;   // BIN'd sale -> fresh start
            if (eff > bp - Step(bp)) eff = Math.Max(sp, (long)bp - Step(bp));
            prevEff = eff;
            previousBuy = bp;
        }
        return eff;
    }

    internal static string ConsumableBucket(string itemType)
    {
        const StringComparison OI = StringComparison.OrdinalIgnoreCase;
        string t = itemType ?? "";
        if (t.StartsWith("Contract", OI)) return "contract";
        if (t.StartsWith("Health", OI)) return "healing";
        if (t.StartsWith("Fitness", OI)) return "fitness";
        if (t.StartsWith("TrainingPlayerPos", OI)) return "position";
        if (t.StartsWith("TrainingGk", OI)) return "gk";
        if (t.StartsWith("Training", OI)) return "training";      // attribute training (Pace/Shooting/...)
        if (t.Equals("playStyle", OI)) return "playstyle";
        if (t.Equals("managerLeagueModifier", OI)) return "managerleague";
        return t.ToLowerInvariant();
    }

    internal static string RequestedConsumableBucket(string cat)
    {
        string c = (cat ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0 || c == "all" || c == "any") return "";
        if (c.Contains("contract")) return "contract";
        if (c.Contains("heal") || c.Contains("health") || c.Contains("injur")) return "healing";
        if (c.Contains("fit")) return "fitness";
        if (c.Contains("position") || c.Contains("pos")) return "position";
        if (c.Contains("gk") || c.Contains("keeper") || c.Contains("goalkeep")) return "gk";
        if (c.Contains("chem") || c.Contains("style") || c.Contains("playstyle")) return "playstyle";
        if (c.Contains("manager") || c.Contains("league")) return "managerleague";
        if (c.Contains("train")) return "training";
        return "";
    }

    private static bool IsDevelopmentBucket(string bucket)
        => bucket is "contract" or "fitness" or "healing";

    internal static bool ConsumableCatMatches(string itemType, string cat, string wireType = "")
    {
        string want = RequestedConsumableBucket(cat);
        string has = ConsumableBucket(itemType);
        if (want.Length > 0) return has == want;
        string wt = (wireType ?? "").Trim().ToLowerInvariant();
        if (wt == "development") return IsDevelopmentBucket(has);
        if (wt == "training") return !IsDevelopmentBucket(has);
        return true;
    }

    internal static bool IsConsumableCat(string cat) => RequestedConsumableBucket(cat).Length > 0;

    internal static string ConsumablePageJson(int start, int num, long now, string cat, string lev,
        string pos = "", int playStyle = 0, int minBuyNow = 0, int maxBuyNow = 0,
        int minCurrent = 0, int maxCurrent = 0, string sig = null, string wireType = "")
    {

        if (start < 0) start = 0;
        num = Math.Clamp(num, 1, 60);
        var rnd = Random.Shared;
        var sb = new StringBuilder("[");
        int written = 0;
        uint key = MarketKey; long scanFrom = start;
        int wantTier = lev switch { "bronze" => 0, "silver" => 1, "gold" => 2, _ => -1 };
        string wantPos = (pos ?? "").Trim();

        bool filtered = playStyle > 0 || minBuyNow > 0 || maxBuyNow > 0
                        || minCurrent > 0 || maxCurrent > 0 || wantPos.Length > 0;
        long block = Math.Min(ServerOptions.Current.MarketScanLimit, (long)num * (filtered ? 96 : 16));
        long winStart = (scanFrom / Math.Max(1L, (long)num)) * block;

        bool TierOk(int i) => wantTier < 0 || CTier[i] == wantTier;
        bool StyleOk(int i) => playStyle <= 0 || CCards[i].SubType == playStyle;
        bool PosOk(int i)
        {
            if (wantPos.Length == 0) return true;
            string t = CCards[i].ItemType ?? "";
            if (!t.StartsWith("TrainingPlayerPos", StringComparison.OrdinalIgnoreCase)) return false;
            return (CCards[i].Name ?? "").Contains(wantPos, StringComparison.OrdinalIgnoreCase);
        }
        var viewKey = new SelectionKey(1, Category: cat ?? "", Level: lev ?? "", Position: wantPos,
            WireType: wireType ?? "", Style: playStyle);
        if (!SelectionCache.TryGetValue(viewKey, out var view))
            view = RememberSelection(viewKey, CreateSelection(CCounts,
                i => ConsumableCatMatches(CCards[i].ItemType, cat, wireType) && TierOk(i) && StyleOk(i) && PosOk(i)));
        var mc = view.Indices;
        if (mc.Length > 0)
        {
            var vPrefix = view.Prefix;
            long vTotal = view.Total;
            int vHalf = view.HalfBits;
            long scanCap = Math.Min(winStart + block, vTotal);   // stay inside the view -> no repeats
            for (long p = winStart; written < num && p < scanCap; p++)
            {
                long fg = PermuteView(p, vTotal, vHalf, key);
                int k = LocateIn(vPrefix, fg);
                int ci = mc[k];
                long cg = CPrefix[ci] + (fg - vPrefix[k]);
                if (!LiveConsumableAt(cg, now) || CSimBinnedThisCycle(cg, now)) continue;   // sold / owned / not yet relisted
                if (!CByPrice(cg, now, minBuyNow, maxBuyNow, minCurrent, maxCurrent)) continue;
                if (written > 0) sb.Append(',');
                sb.Append(CEntry(cg, now, rnd));
                written++;
            }
        }
        
        sb.Append(']');
        return sb.ToString();
    }

    private static string CEntry(long cg, long now, Random rnd)
    {
        long itemId = ConsumableItemIdBaseMkt + cg;
        long tradeId = ConsumableTradeIdBase + cg;
        var cyc = CCycle(cg, now);
        int ci = LocateIn(CPrefix, cg);
        var card = CCards[ci];
        var (start, buy) = CPrice(cg, cyc.k);
        long effStart = CEffStart(cg, cyc.k, cyc.dur, start, buy);
        bool bought = CBoughtThisCycle(cg, now);
        bool live = cyc.local < cyc.dur && !bought;

        long currentBid; int offers; string bidState; string tradeState; long expiresOut;
        if (live)
        {
            long remaining = cyc.dur - cyc.local;
            var (_, sim, simOff, _) = CSimBids(CTier[ci], cg, cyc.k, cyc.dur, (int)effStart, buy, cyc.local);
            if (sim >= buy)
            {
                currentBid = buy; offers = simOff;
                bidState = "none"; tradeState = "closed"; expiresOut = 0;   // bot BIN'd it
            }
            else
            {
                currentBid = sim; offers = simOff; bidState = "none";
                tradeState = "active";
                expiresOut = remaining;
            }
        }
        else if (bought)
        {
            currentBid = buy; offers = 0; bidState = "none";
            tradeState = "closed"; expiresOut = 0;
        }
        else
        {
            var (hasBids, _, _, _) = CSimBids(CTier[ci], cg, cyc.k, cyc.dur, (int)effStart, buy, cyc.dur);
            currentBid = effStart; offers = hasBids ? 1 : 0;
            bidState = "none";
            tradeState = hasBids ? "closed" : "expired";
            expiresOut = 0;
        }

        string seller = SellerFor(cg, cyc.k);
        string item = ConsumableItems.BuildJson(card, now, 5, "forSale");
        return "{\"tradeId\":" + tradeId + ",\"itemData\":" + item +
               ",\"tradeState\":\"" + tradeState + "\",\"buyNowPrice\":" + buy +
               ",\"currentBid\":" + currentBid + ",\"offers\":" + offers +
               ",\"watched\":false" +
               ",\"bidState\":\"" + bidState + "\",\"startingBid\":" + effStart + ",\"confidenceValue\":100" +
               ",\"expires\":" + expiresOut +
               ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013," +
               "\"sellerId\":0,\"tradeOwner\":false,\"tradeIdStr\":\"" + tradeId +
               "\",\"lastSalePrice\":0,\"coinsProcessed\":false}";
    }

    internal static bool CResolveTradeId(long tradeId, out ConsumableItem item, out int startingBid, out int buyNow)
    {
        item = default; startingBid = 0; buyNow = 0;
        long cg = tradeId - ConsumableTradeIdBase;
        if (cg < 0 || cg >= CTotal) return false;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cyc = CCycle(cg, nowUtc);
        if (cyc.local >= cyc.dur || CBoughtThisCycle(cg, nowUtc) || CSimBinnedThisCycle(cg, nowUtc)) return false;   // just sold / not relisted
        item = CCards[LocateIn(CPrefix, cg)];
        var (cStart, cBuy) = CPrice(cg, cyc.k);
        startingBid = (int)CEffStart(cg, cyc.k, cyc.dur, cStart, cBuy);
        buyNow = cBuy;
        return true;
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<long, long> CBoughtAt => PlayerContext.Current.Data.Market.CBoughtAt;

    internal static void CMarkBought(long tradeId, long now)
    {
        long cg = tradeId - ConsumableTradeIdBase;
        if (cg < 0 || cg >= CTotal) return;
        CBoughtAt[cg] = CCycle(cg, now).k;
    }

    private static bool CBoughtThisCycle(long cg, long now)
        => CBoughtAt.TryGetValue(cg, out long k) && k == CCycle(cg, now).k;

    private static bool CSimBinnedThisCycle(long cg, long now)
    {
        var cyc = CCycle(cg, now);
        if (cyc.local >= cyc.dur) return false;
        int ci = LocateIn(CPrefix, cg);
        var (start, buy) = CPrice(cg, cyc.k);
        long eff = CEffStart(cg, cyc.k, cyc.dur, start, buy);
        var (_, sim, _, _) = CSimBids(CTier[ci], cg, cyc.k, cyc.dur, (int)eff, buy, cyc.local);
        return sim >= buy;
    }

    internal static string CSellerFor(long cg, long now) => SellerFor(cg, CCycle(cg, now).k);


    private static (long dur, long gap, long phase) ELifecycle(long eg)
    {
        uint s = Hash((uint)eg, 0x51515EA7u ^ 0xC0FFEE11u);
        long dur = 1200 + (s % 7) * 600;             // 20 min .. 1h20
        long gap = 15 + ((s >> 3) % 75);
        long phase = (s >> 6) % dur;
        return (dur, gap, phase);
    }

    private static (long k, long start, long dur, long gap, long local) ECycle(long eg, long now)
    {
        var (dur, gap, phase) = ELifecycle(eg);
        long period = dur + gap;
        long k = (now - phase) / period;
        long start = phase + k * period;
        return (k, start, dur, gap, now - start);
    }

    private static bool LiveClubItemAt(long eg, long now)
    {
        var c = ECycle(eg, now);
        return c.local < c.dur && !EBoughtThisCycle(eg, now);
    }

    private static (int startingBid, int buyNow) EPrice(long eg, long k)
    {
        int i = LocateIn(EPrefix, eg);
        long baseP = EBaseP[i];
        var rng = new Rng(Hash((uint)eg, (uint)(i * 0x9E3779B1u)) ^ 0x51ED2C0Bu);
        uint w = Hash((uint)eg, (uint)(k * 0x85EBCA6Bu) ^ 0x5F356495u);
        double wig = 0.80 + (w % 41) / 100.0;
        long buy = Snap((long)(baseP * wig * (0.90 + rng.NextDouble() * 0.35)));
        long start = Snap((long)(buy * (0.55 + rng.NextDouble() * 0.30)));
        if (start < 150) start = 150;
        if (start >= buy) start = Math.Max(150, buy - Step(buy));
        return ((int)start, (int)buy);
    }

    private static (bool hasBids, long simBid, int offers, long finalBid) ESimBids(
        int rare, int rating, long eg, long k, long dur, int startBid, int buyNow, long elapsed)
    {
        uint s = Hash((uint)eg, (uint)(k * 0x9E3779B1u) ^ 0x6D2B79F5u);
        bool hot = rare != 0 || rating >= 85;
        int chance = hot ? 26 : 12;
        if (s % 100 >= chance) return (false, startBid, 0, startBid);
        if (buyNow - Step(buyNow) < startBid) return (false, startBid, 0, startBid);   // no room to bid above BIN
        long bidGap = 60 + ((s >> 7) % 180);
        long firstDelay = 30 + ((s >> 15) % Math.Max(1, dur / 8));
        long incr = Math.Max(50, startBid * 3 / 100 + Step(startBid));
        long cap = startBid * (hot ? 125 : 110) / 100;
        if (cap > buyNow) cap = buyNow;   // bot may push to BIN, never past it
        if (cap <= startBid) return (false, startBid, 0, startBid);   // no room to bid
        long finalN = Math.Max(1, (dur - firstDelay) / bidGap);
        long finalBid = Math.Min(cap, startBid + finalN * incr);
        if (elapsed <= firstDelay) return (true, startBid, 0, finalBid);
        long n = Math.Min(finalN, (elapsed - firstDelay) / bidGap + 1);
        long cur = Math.Min(cap, startBid + n * incr);
        if (cur >= buyNow) return (true, buyNow, (int)Math.Min(n, 99), buyNow);   // Bot hits BIN -> buys it now
        return (true, cur, (int)Math.Min(n, 99), finalBid);
    }

    private static long EEffStart(long eg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        var key = new StartKey(2, eg, k);
        return StartCache.TryGetValue(key, out long value) ? value
            : RememberStart(key, EEffStartUncached(eg, k, dur, s0, buy));
    }

    private static long EEffStartUncached(long eg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        const int Window = 40;
        long k0 = Math.Max(0, k - Window);
        var card = ECards[LocateIn(EPrefix, eg)];
        long eff = s0, prevEff = 0;
        var (previousStart, previousBuy) = EPrice(eg, k0);
        for (long kk = k0 + 1; kk <= k; kk++)
        {
            var (sp, bp) = EPrice(eg, kk);
            long sPrevOp = (kk - 1 > k0) ? prevEff : previousStart;
            var (hasBids, _, _, finalP) = ESimBids(card.Rare, card.Rating, eg, kk - 1, dur, (int)sPrevOp, previousBuy, dur);
            eff = (hasBids && finalP < previousBuy) ? Math.Max(sp, finalP) : sp;   // BIN'd sale -> fresh start
            if (eff > bp - Step(bp)) eff = Math.Max(sp, (long)bp - Step(bp));
            prevEff = eff;
            previousBuy = bp;
        }
        return eff;
    }

    internal static bool ClubItemCatMatches(string type, string cat)
    {
        string t = type ?? "";
        string c = (cat ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0 || c.Equals("all") || c.Equals("any") || c.Equals("clubinfo")) return true;
        if (t == "badge") return c.Contains("badge") || c.Contains("crest") || c.Contains("custom");
        if (t == "kit") return c.Contains("kit");
        if (t == "ball") return c.Contains("ball");
        if (t == "stadium") return c.Contains("stadium") || c.Contains("stadiums") || c.Contains("tifo");
        return true;
    }

    internal static string ClubItemPageJson(int start, int num, long now, string cat, string lev,
        int leag = 0, int team = 0, int minBuyNow = 0, int maxBuyNow = 0,
        int minCurrent = 0, int maxCurrent = 0, string sig = null)
    {

        if (start < 0) start = 0;
        num = Math.Clamp(num, 1, 60);
        var rnd = Random.Shared;
        var sb = new StringBuilder("[");
        int written = 0;
        uint key = MarketKey; long scanFrom = start;
        bool TierOk(int i)
        {
            if (lev is not ("bronze" or "silver" or "gold")) return true;
            int r = ECards[i].Rating;
            return lev switch { "bronze" => r < 65, "silver" => r is >= 65 and < 75, _ => r >= 75 };
        }
        bool TeamOk(int i)
        {
            if (team <= 0) return true;
            int tid = ECards[i].TeamId;
            if (tid == 0) tid = ECards[i].AssetId;
            return tid == team;
        }
        bool LeagueOk(int i)
        {
            if (leag <= 0) return true;
            int tid = ECards[i].TeamId;
            if (tid == 0) tid = ECards[i].AssetId;
            return TeamLeagues.LeagueOf(tid) == leag;
        }

        bool filteredX = leag > 0 || team > 0 || minBuyNow > 0 || maxBuyNow > 0
                         || minCurrent > 0 || maxCurrent > 0;
        long block = Math.Min(ServerOptions.Current.MarketScanLimit, (long)num * (filteredX ? 96 : 16));
        long winStart = (scanFrom / Math.Max(1L, (long)num)) * block;
        var viewKey = new SelectionKey(2, Category: cat ?? "", Level: lev ?? "", League: leag, Team: team);
        if (!SelectionCache.TryGetValue(viewKey, out var view))
            view = RememberSelection(viewKey, CreateSelection(ECounts,
                i => ClubItemCatMatches(ECards[i].Type, cat) && TierOk(i) && TeamOk(i) && LeagueOk(i)));
        var mc = view.Indices;
        if (mc.Length > 0)
        {
            var vPrefix = view.Prefix;
            long vTotal = view.Total;
            int vHalf = view.HalfBits;
            long scanCap = Math.Min(winStart + block, vTotal);   // stay inside the view -> no repeats
            for (long p = winStart; written < num && p < scanCap; p++)
            {
                long fg = PermuteView(p, vTotal, vHalf, key);
                int k = LocateIn(vPrefix, fg);
                int ci = mc[k];
                long eg = EPrefix[ci] + (fg - vPrefix[k]);
                if (!LiveClubItemAt(eg, now) || ESimBinnedThisCycle(eg, now)) continue;   // sold / owned / not yet relisted
                if (!EByPrice(eg, now, minBuyNow, maxBuyNow, minCurrent, maxCurrent)) continue;
                if (written > 0) sb.Append(',');
                sb.Append(EEntry(eg, now, rnd));
                written++;
            }
        }
        
        sb.Append(']');
        return sb.ToString();
    }

    private static string EEntry(long eg, long now, Random rnd)
    {
        long itemId = ClubItemItemIdBaseMkt + eg;
        long tradeId = ClubItemTradeIdBase + eg;
        var cyc = ECycle(eg, now);
        int ci = LocateIn(EPrefix, eg);
        var card = ECards[ci];
        var (start, buy) = EPrice(eg, cyc.k);
        long effStart = EEffStart(eg, cyc.k, cyc.dur, start, buy);
        bool bought = EBoughtThisCycle(eg, now);
        bool live = cyc.local < cyc.dur && !bought;

        long currentBid; int offers; string bidState; string tradeState; long expiresOut;
        if (live)
        {
            long remaining = cyc.dur - cyc.local;
            var (_, sim, simOff, _) = ESimBids(card.Rare, card.Rating, eg, cyc.k, cyc.dur, (int)effStart, buy, cyc.local);
            if (sim >= buy)
            {
                currentBid = buy; offers = simOff;
                bidState = "none"; tradeState = "closed"; expiresOut = 0;   // bot BIN'd it
            }
            else
            {
                currentBid = sim; offers = simOff; bidState = "none";
                tradeState = "active";
                expiresOut = remaining;
            }
        }
        else if (bought)
        {
            currentBid = buy; offers = 0; bidState = "none";
            tradeState = "closed"; expiresOut = 0;
        }
        else
        {
            var (hasBids, _, _, _) = ESimBids(card.Rare, card.Rating, eg, cyc.k, cyc.dur, (int)effStart, buy, cyc.dur);
            currentBid = effStart; offers = hasBids ? 1 : 0;
            bidState = "none";
            tradeState = hasBids ? "closed" : "expired";
            expiresOut = 0;
        }

        string seller = SellerFor(eg, cyc.k);
        string item = ClubItems.BuildJson(card, now, "forSale", 5);
        return "{\"tradeId\":" + tradeId + ",\"itemData\":" + item +
               ",\"tradeState\":\"" + tradeState + "\",\"buyNowPrice\":" + buy +
               ",\"currentBid\":" + currentBid + ",\"offers\":" + offers +
               ",\"watched\":false" +
               ",\"bidState\":\"" + bidState + "\",\"startingBid\":" + effStart + ",\"confidenceValue\":100" +
               ",\"expires\":" + expiresOut +
               ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013," +
               "\"sellerId\":0,\"tradeOwner\":false,\"tradeIdStr\":\"" + tradeId +
               "\",\"lastSalePrice\":0,\"coinsProcessed\":false}";
    }

    internal static bool EResolveTradeId(long tradeId, out CosmeticItem item, out int startingBid, out int buyNow)
    {
        item = default; startingBid = 0; buyNow = 0;
        long eg = tradeId - ClubItemTradeIdBase;
        if (eg < 0 || eg >= ETotal) return false;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cyc = ECycle(eg, nowUtc);
        if (cyc.local >= cyc.dur || EBoughtThisCycle(eg, nowUtc) || ESimBinnedThisCycle(eg, nowUtc)) return false;
        item = ECards[LocateIn(EPrefix, eg)];
        var (eStart, eBuy) = EPrice(eg, cyc.k);
        startingBid = (int)EEffStart(eg, cyc.k, cyc.dur, eStart, eBuy);
        buyNow = eBuy;
        return true;
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<long, long> EBoughtAt => PlayerContext.Current.Data.Market.EBoughtAt;

    internal static void EMarkBought(long tradeId, long now)
    {
        long eg = tradeId - ClubItemTradeIdBase;
        if (eg < 0 || eg >= ETotal) return;
        EBoughtAt[eg] = ECycle(eg, now).k;
    }

    private static bool EBoughtThisCycle(long eg, long now)
        => EBoughtAt.TryGetValue(eg, out long k) && k == ECycle(eg, now).k;

    private static bool ESimBinnedThisCycle(long eg, long now)
    {
        var cyc = ECycle(eg, now);
        if (cyc.local >= cyc.dur) return false;
        int ci = LocateIn(EPrefix, eg);
        var (start, buy) = EPrice(eg, cyc.k);
        long eff = EEffStart(eg, cyc.k, cyc.dur, start, buy);
        var (_, sim, _, _) = ESimBids(ECards[ci].Rare, ECards[ci].Rating, eg, cyc.k, cyc.dur, (int)eff, buy, cyc.local);
        return sim >= buy;
    }

    internal static string ESellerFor(long eg, long now) => SellerFor(eg, ECycle(eg, now).k);


    private static (long dur, long gap, long phase) FLifecycle(long fg)
    {
        uint s = Hash((uint)fg, 0x51515EA7u ^ 0xF00DF00Du);
        long dur = 1500 + (s % 6) * 750;             // 25 min .. 1h20
        long gap = 20 + ((s >> 3) % 90);
        long phase = (s >> 6) % dur;
        return (dur, gap, phase);
    }

    private static (long k, long start, long dur, long gap, long local) FCycle(long fg, long now)
    {
        var (dur, gap, phase) = FLifecycle(fg);
        long period = dur + gap;
        long k = (now - phase) / period;
        long start = phase + k * period;
        return (k, start, dur, gap, now - start);
    }

    private static bool LiveStaffAt(long fg, long now)
    {
        var c = FCycle(fg, now);
        return c.local < c.dur && !FBoughtThisCycle(fg, now);
    }

    private static (int startingBid, int buyNow) FPrice(long fg, long k)
    {
        int i = LocateIn(FPrefix, fg);
        long baseP = FBaseP[i];
        var rng = new Rng(Hash((uint)fg, (uint)(i * 0x9E3779B1u)) ^ 0x51ED2C0Bu);
        uint w = Hash((uint)fg, (uint)(k * 0x85EBCA6Bu) ^ 0x5F356495u);
        double wig = 0.80 + (w % 41) / 100.0;
        long buy = Snap((long)(baseP * wig * (0.90 + rng.NextDouble() * 0.35)));
        long start = Snap((long)(buy * (0.55 + rng.NextDouble() * 0.30)));
        if (start < 150) start = 150;
        if (start >= buy) start = Math.Max(150, buy - Step(buy));
        return ((int)start, (int)buy);
    }

    private static (bool hasBids, long simBid, int offers, long finalBid) FSimBids(
        int rating, bool isManager, long fg, long k, long dur, int startBid, int buyNow, long elapsed)
    {
        uint s = Hash((uint)fg, (uint)(k * 0x9E3779B1u) ^ 0x6D2B79F5u);
        bool hot = isManager && rating >= 80;
        int chance = hot ? 30 : 12;
        if (s % 100 >= chance) return (false, startBid, 0, startBid);
        if (buyNow - Step(buyNow) < startBid) return (false, startBid, 0, startBid);   // no room to bid above BIN
        long bidGap = 60 + ((s >> 7) % 180);
        long firstDelay = 30 + ((s >> 15) % Math.Max(1, dur / 8));
        long incr = Math.Max(50, startBid * 3 / 100 + Step(startBid));
        long cap = startBid * (hot ? 125 : 110) / 100;
        if (cap > buyNow) cap = buyNow;   // bot may push to BIN, never past it
        if (cap <= startBid) return (false, startBid, 0, startBid);   // no room to bid
        long finalN = Math.Max(1, (dur - firstDelay) / bidGap);
        long finalBid = Math.Min(cap, startBid + finalN * incr);
        if (elapsed <= firstDelay) return (true, startBid, 0, finalBid);
        long n = Math.Min(finalN, (elapsed - firstDelay) / bidGap + 1);
        long cur = Math.Min(cap, startBid + n * incr);
        if (cur >= buyNow) return (true, buyNow, (int)Math.Min(n, 99), buyNow);   // Bot hits BIN -> buys it now
        return (true, cur, (int)Math.Min(n, 99), finalBid);
    }

    private static long FEffStart(long fg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        var key = new StartKey(3, fg, k);
        return StartCache.TryGetValue(key, out long value) ? value
            : RememberStart(key, FEffStartUncached(fg, k, dur, s0, buy));
    }

    private static long FEffStartUncached(long fg, long k, long dur, int s0, int buy)
    {
        if (k <= 0) return s0;
        const int Window = 40;
        long k0 = Math.Max(0, k - Window);
        int ci = LocateIn(FPrefix, fg);
        bool isMgr = FIsManager[ci];
        int rp = isMgr ? FManager[ci].Rating : FStaff[ci].Rating;
        long eff = s0, prevEff = 0;
        var (previousStart, previousBuy) = FPrice(fg, k0);
        for (long kk = k0 + 1; kk <= k; kk++)
        {
            var (sp, bp) = FPrice(fg, kk);
            long sPrevOp = (kk - 1 > k0) ? prevEff : previousStart;
            var (hasBids, _, _, finalP) = FSimBids(rp, isMgr, fg, kk - 1, dur, (int)sPrevOp, previousBuy, dur);
            eff = (hasBids && finalP < previousBuy) ? Math.Max(sp, finalP) : sp;   // BIN'd sale -> fresh start
            if (eff > bp - Step(bp)) eff = Math.Max(sp, (long)bp - Step(bp));
            prevEff = eff;
            previousBuy = bp;
        }
        return eff;
    }

    internal static bool StaffCatMatches(bool isManager, StaffCard s, string cat)
    {
        string c = (cat ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0 || c is "all" or "any" or "staff") return true;
        if (isManager) return c.Contains("manager") || c.Contains("mgr");
        string t = s.ItemType ?? "";
        if (c.Contains("gk") || c.Contains("goalkeeper") || c.Contains("keeper"))
            return t == "gkCoach";                              // "GKCoach" must not pull in head coaches
        if (c.Contains("physio") || c.Contains("heal") || c.Contains("health"))
            return t == "physio";
        if (c.Contains("fitness"))
            return t == "fitnessCoach";
        if (c.Contains("head"))
            return t == "headCoach";
        if (c.Contains("coach") || c.Contains("training") || c.Contains("tactics"))
            return t == "headCoach";                            // generic coach search
        return false;                                           // unknown cat (e.g. "manager") matches nothing
    }

    internal static string StaffPageJson(int start, int num, long now, string cat, string lev,
        int nat = 0, int leag = 0, int minBuyNow = 0, int maxBuyNow = 0,
        int minCurrent = 0, int maxCurrent = 0, string sig = null)
    {

        if (start < 0) start = 0;
        num = Math.Clamp(num, 1, 60);
        var rnd = Random.Shared;
        var sb = new StringBuilder("[");
        int written = 0;
        uint key = MarketKey; long scanFrom = start;
        bool TierOk(int i)
        {
            if (lev is not ("bronze" or "silver" or "gold")) return true;
            int r = FIsManager[i] ? FManager[i].Rating : FStaff[i].Rating;
            return lev switch { "bronze" => r < 65, "silver" => r is >= 65 and < 75, _ => r >= 75 };
        }
        bool NationOk(int i) => nat <= 0 || (FIsManager[i] && FManager[i].NationId == nat);
        // A manager's advertised league may be modified for this listing cycle;
        // only immutable manager/staff eligibility belongs in the cached view.
        bool LeagueEligible(int i) => leag <= 0 || FIsManager[i];

        bool filteredY = nat > 0 || leag > 0 || minBuyNow > 0 || maxBuyNow > 0
                         || minCurrent > 0 || maxCurrent > 0;
        long block = Math.Min(ServerOptions.Current.MarketScanLimit, (long)num * (filteredY ? 96 : 16));
        long winStart = (scanFrom / Math.Max(1L, (long)num)) * block;

        var viewKey = new SelectionKey(3, Category: cat ?? "", Level: lev ?? "", Nation: nat, League: leag > 0 ? 1 : 0);
        if (!SelectionCache.TryGetValue(viewKey, out var view))
            view = RememberSelection(viewKey, CreateSelection(FCounts,
                i => StaffCatMatches(FIsManager[i], FStaff[i], cat) && TierOk(i) && NationOk(i) && LeagueEligible(i)));
        var mc = view.Indices;
        if (mc.Length > 0)
        {
            var vPrefix = view.Prefix;
            long vTotal = view.Total;
            int vHalf = view.HalfBits;
            long scanCap = Math.Min(winStart + block, vTotal);   // stay inside the view -> no repeats
            for (long p = winStart; written < num && p < scanCap; p++)
            {
                long fg = PermuteView(p, vTotal, vHalf, key);
                int k = LocateIn(vPrefix, fg);
                int ci = mc[k];
                long fs = FPrefix[ci] + (fg - vPrefix[k]);
                if (!LiveStaffAt(fs, now) || FSimBinnedThisCycle(fs, now)) continue;
                if (leag > 0 && ManagerListingState(fs, FCycle(fs, now).k, FManager[ci]).League != leag) continue;
                if (!FByPrice(fs, now, minBuyNow, maxBuyNow, minCurrent, maxCurrent)) continue;
                if (written > 0) sb.Append(',');
                sb.Append(FEntry(fs, now, rnd));
                written++;
            }
        }
        
        sb.Append(']');
        return sb.ToString();
    }

    private static string FEntry(long fg, long now, Random rnd)
    {
        long itemId = StaffItemIdBaseMkt + fg;
        long tradeId = StaffTradeIdBase + fg;
        var cyc = FCycle(fg, now);
        int ci = LocateIn(FPrefix, fg);
        var (start, buy) = FPrice(fg, cyc.k);
        long effStart = FEffStart(fg, cyc.k, cyc.dur, start, buy);
        bool bought = FBoughtThisCycle(fg, now);
        bool live = cyc.local < cyc.dur && !bought;
        int rating = FIsManager[ci] ? FManager[ci].Rating : FStaff[ci].Rating;
        int rareFlag = FIsManager[ci] ? (rating >= 80 ? 1 : 0) : FStaff[ci].Rare;

        long currentBid; int offers; string bidState; string tradeState; long expiresOut;
        if (live)
        {
            long remaining = cyc.dur - cyc.local;
            var (_, sim, simOff, _) = FSimBids(rating, FIsManager[ci], fg, cyc.k, cyc.dur, (int)effStart, buy, cyc.local);
            if (sim >= buy)
            {
                currentBid = buy; offers = simOff;
                bidState = "none"; tradeState = "closed"; expiresOut = 0;   // bot BIN'd it
            }
            else
            {
                currentBid = sim; offers = simOff; bidState = "none";
                tradeState = "active";
                expiresOut = remaining;
            }
        }
        else if (bought)
        {
            currentBid = buy; offers = 0; bidState = "none";
            tradeState = "closed"; expiresOut = 0;
        }
        else
        {
            var (hasBids, _, _, _) = FSimBids(rating, FIsManager[ci], fg, cyc.k, cyc.dur, (int)effStart, buy, cyc.dur);
            currentBid = effStart; offers = hasBids ? 1 : 0;
            bidState = "none";
            tradeState = hasBids ? "closed" : "expired";
            expiresOut = 0;
        }

        string seller = SellerFor(fg, cyc.k);
        int mgrLeague = -1, mgrContract = 7;
        if (FIsManager[ci])
        {
            (mgrContract, mgrLeague) = ManagerListingState(fg, cyc.k, FManager[ci]);
        }
        string item = FIsManager[ci]
            ? WebServer.BuildManagerItem(FManager[ci], itemId, now, 5, rareFlag, "forSale", mgrLeague, mgrContract)
            : WebServer.BuildStaffItem(FStaff[ci], itemId, now, 5, "forSale");
        return "{\"tradeId\":" + tradeId + ",\"itemData\":" + item +
               ",\"tradeState\":\"" + tradeState + "\",\"buyNowPrice\":" + buy +
               ",\"currentBid\":" + currentBid + ",\"offers\":" + offers +
               ",\"watched\":false" +
               ",\"bidState\":\"" + bidState + "\",\"startingBid\":" + effStart + ",\"confidenceValue\":100" +
               ",\"expires\":" + expiresOut +
               ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013," +
               "\"sellerId\":0,\"tradeOwner\":false,\"tradeIdStr\":" + tradeId +
               ",\"lastSalePrice\":0,\"coinsProcessed\":false}";
    }

    private static (int Contract, int League) ManagerListingState(long listing, long cycle, Manager manager)
    {
        int contract = 7, league = manager.LeagueId;
        uint mr = Hash((uint)listing, (uint)(cycle * 0x9E3779B1u)) ^ 0x2F6E2B57u;
        if (mr % 100 >= 78 && TeamLeagues.AllLeagues.Length > 0)
            league = TeamLeagues.AllLeagues[(int)((mr >> 7) % (uint)TeamLeagues.AllLeagues.Length)];
        if ((mr >> 14) % 100 >= 45) contract = (int)((mr >> 21) % 7);
        return (contract, league);
    }

    internal static ManagerMod? ManagerListingMods(long tradeId, long now)
    {
        long listing = tradeId - StaffTradeIdBase;
        if (listing < 0 || listing >= STotal) return null;
        int index = LocateIn(FPrefix, listing);
        if (!FIsManager[index]) return null;
        var state = ManagerListingState(listing, FCycle(listing, now).k, FManager[index]);
        return new ManagerMod { Contract = state.Contract, LeagueId = state.League };
    }

    internal static bool FResolveTradeId(long tradeId, out bool isManager, out Manager mgr, out StaffCard stf,
        out int startingBid, out int buyNow)
    {
        isManager = false; mgr = default; stf = default; startingBid = 0; buyNow = 0;
        long fg = tradeId - StaffTradeIdBase;
        if (fg < 0 || fg >= STotal) return false;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cyc = FCycle(fg, nowUtc);
        if (cyc.local >= cyc.dur || FBoughtThisCycle(fg, nowUtc) || FSimBinnedThisCycle(fg, nowUtc)) return false;
        int ci = LocateIn(FPrefix, fg);
        isManager = FIsManager[ci];
        if (isManager) mgr = FManager[ci]; else stf = FStaff[ci];
        var (start, buy) = FPrice(fg, cyc.k);
        startingBid = (int)FEffStart(fg, cyc.k, cyc.dur, start, buy);
        buyNow = buy;
        return true;
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<long, long> FBoughtAt => PlayerContext.Current.Data.Market.FBoughtAt;

    internal static void FMarkBought(long tradeId, long now)
    {
        long fg = tradeId - StaffTradeIdBase;
        if (fg < 0 || fg >= STotal) return;
        FBoughtAt[fg] = FCycle(fg, now).k;
    }

    private static bool FBoughtThisCycle(long fg, long now)
        => FBoughtAt.TryGetValue(fg, out long k) && k == FCycle(fg, now).k;

    private static bool FSimBinnedThisCycle(long fg, long now)
    {
        var cyc = FCycle(fg, now);
        if (cyc.local >= cyc.dur) return false;
        int ci = LocateIn(FPrefix, fg);
        var (start, buy) = FPrice(fg, cyc.k);
        long eff = FEffStart(fg, cyc.k, cyc.dur, start, buy);
        int r = FIsManager[ci] ? FManager[ci].Rating : FStaff[ci].Rating;
        var (_, sim, _, _) = FSimBids(r, FIsManager[ci], fg, cyc.k, cyc.dur, (int)eff, buy, cyc.local);
        return sim >= buy;
    }

    internal static string FSellerFor(long fg, long now) => SellerFor(fg, FCycle(fg, now).k);


    private static readonly object _feedLock = new();
    private static readonly List<string> _feedEvents = new();
    private static long _lastFeedTick;
    private const int FeedSampleSlots = 6000;
    private const long FeedTickSeconds = 4;

    private static readonly (string key, int weight, Func<RealPlayer, bool> pick)[]
        FeedTiers =
    {
        ("bronze", 16, p => p.Rating <= 64),
        ("silver", 22, p => p.Rating is >= 65 and <= 74),
        ("goldL",  20, p => p.Rating is >= 75 and <= 79),
        ("goldM",  15, p => p.Rating is >= 80 and <= 84),
        ("goldH",  11, p => p.Rating is >= 85 and <= 87),
        ("elite",  10, p => p.Rating >= 88),
    };

    private static readonly int[][] FeedTierCards;   // card indices per tier, filled in the static ctor once Cards is ready

    internal static void TryAdvanceFeed(long now)
    {
        lock (_feedLock)
        {
            if (_lastFeedTick > 0 && now - _lastFeedTick < FeedTickSeconds) return;
            long prev = _lastFeedTick == 0 ? now - FeedTickSeconds : _lastFeedTick;
            _lastFeedTick = now;

            var rnd = new Random((int)(now & 0x7FFFFFFF));
            for (int i = 0; i < FeedSampleSlots; i++)
            {
                long g = SampleG(rnd);
                var cyc = Cycle(g, now);
                long elapsed = Math.Max(0, cyc.local);
                var card = Cards[Locate(g)];
                var (start, buy) = Price(card, g, cyc.k);
                long eff = EffStart(card, g, cyc.k, cyc.dur, start, buy);
                var (hasBids, sim, _, finalBid) = SimBids(card, g, cyc.k, cyc.dur, (int)eff, buy, elapsed);
string seller = SellerFor(g, cyc.k);
                long tradeId = TradeIdBase + g;

                long saleAt = cyc.start + cyc.dur;              // when the listing sells
                if (saleAt > prev && saleAt <= now && hasBids)
                    PushEvent("sale", card, finalBid, 0, seller, tradeId);

                long relistAt = saleAt + cyc.gap;               // when a fresh card appears
                if (relistAt > prev && relistAt <= now)
                    PushEvent("new", card, eff, buy, seller, tradeId);

                if (card.Rating >= 85 && hasBids)               // headline bids only, keeps the feed readable
                {
                    long prevElapsed = Math.Max(0, prev - cyc.start);
                    var (_, simPrev, _, _) = SimBids(card, g, cyc.k, cyc.dur, (int)eff, buy, prevElapsed);
                    if (sim > simPrev)
                        PushEvent("bid", card, sim, 0, seller, tradeId);
                }
            }

            if (CTotal > 0)   // consumables shift as people BIN them - surface a few
            {
                const int FeedConsumableSlots = 3000;
                for (int i = 0; i < FeedConsumableSlots; i++)
                {
                    long cg = (long)(rnd.NextDouble() * CTotal);
                    var cyc = CCycle(cg, now);
                    int ci = LocateIn(CPrefix, cg);
                    var card = CCards[ci];
                    var (start, buy) = CPrice(cg, cyc.k);
                    string seller = SellerFor(cg, cyc.k);
                    long tradeId = ConsumableTradeIdBase + cg;
                    long saleAt = cyc.start + cyc.dur;              // BIN usually takes it
                    if (saleAt > prev && saleAt <= now)
                        PushEventC("sale", card.Name, buy, 0, seller, tradeId);
                    long relistAt = saleAt + cyc.gap;               // a fresh card appears
                    if (relistAt > prev && relistAt <= now)
                        PushEventC("new", card.Name, start, buy, seller, tradeId);
                }
            }

            if (ETotal > 0)   // badges, kits and balls come and go constantly
            {
                const int FeedClubItemSlots = 1500;
                for (int i = 0; i < FeedClubItemSlots; i++)
                {
                    long eg = (long)(rnd.NextDouble() * ETotal);
                    var cyc = ECycle(eg, now);
                    int ci = LocateIn(EPrefix, eg);
                    var card = ECards[ci];
                    var (start, buy) = EPrice(eg, cyc.k);
                    string seller = SellerFor(eg, cyc.k);
                    long tradeId = ClubItemTradeIdBase + eg;
                    long saleAt = cyc.start + cyc.dur;
                    if (saleAt > prev && saleAt <= now)
                        PushEventC("sale", card.Name, buy, 0, seller, tradeId, card.Rating);
                    long relistAt = saleAt + cyc.gap;
                    if (relistAt > prev && relistAt <= now)
                        PushEventC("new", card.Name, start, buy, seller, tradeId, card.Rating);
                }
            }

            if (STotal > 0)   // managers and staff change hands constantly
            {
                const int FeedStaffSlots = 1200;
                for (int i = 0; i < FeedStaffSlots; i++)
                {
                    long fg = (long)(rnd.NextDouble() * STotal);
                    var cyc = FCycle(fg, now);
                    int ci = LocateIn(FPrefix, fg);
                    string name = FIsManager[ci] ? FManager[ci].Name : FStaff[ci].Name;
                    int rating = FIsManager[ci] ? FManager[ci].Rating : FStaff[ci].Rating;
                    var (start, buy) = FPrice(fg, cyc.k);
                    string seller = SellerFor(fg, cyc.k);
                    long tradeId = StaffTradeIdBase + fg;
                    long saleAt = cyc.start + cyc.dur;
                    if (saleAt > prev && saleAt <= now)
                        PushEventC("sale", name, buy, 0, seller, tradeId, rating);
                    long relistAt = saleAt + cyc.gap;
                    if (relistAt > prev && relistAt <= now)
                        PushEventC("new", name, start, buy, seller, tradeId, rating);
                }
            }
        }
    }

    private static long SampleG(Random rnd)
    {
        int totalW = 0;
        for (int t = 0; t < FeedTiers.Length; t++) totalW += FeedTiers[t].weight;
        int roll = rnd.Next(totalW);
        for (int t = 0; t < FeedTiers.Length; t++)
        {
            roll -= FeedTiers[t].weight;
            if (roll < 0)
            {
                var pool = FeedTierCards[t];
                if (pool.Length > 0)
                {
                    int ci = pool[rnd.Next(pool.Length)];
                    long lo = Prefix[ci], hi = Prefix[ci + 1];
                    return lo + (long)(rnd.NextDouble() * (hi - lo));
                }
            }
        }
        return (long)(rnd.NextDouble() * Total);   // fallback (shouldn't happen)
    }

    private static void PushEvent(string kind, RealPlayer card, long price, long bin, string seller, long tradeId)
    {
        string safe = card.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string ev = "{\"type\":\"" + kind + "\",\"name\":\"" + safe + "\",\"rating\":" + card.Rating +
                    ",\"price\":" + price + ",\"bin\":" + bin + ",\"seller\":\"" + seller +
                    "\",\"tradeId\":" + tradeId + "}";
        _feedEvents.Add(ev);
        const int MaxEvents = 60;
        while (_feedEvents.Count > MaxEvents) _feedEvents.RemoveAt(0);
    }

    internal static void PushEventC(string kind, string name, long price, long bin, string seller, long tradeId,
        int rating = 0)
    {
        string safe = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string ev = "{\"type\":\"" + kind + "\",\"name\":\"" + safe + "\",\"rating\":" + rating +
                    ",\"price\":" + price + ",\"bin\":" + bin + ",\"seller\":\"" + seller +
                    "\",\"tradeId\":" + tradeId + "}";
        _feedEvents.Add(ev);
        const int MaxEvents = 60;
        while (_feedEvents.Count > MaxEvents) _feedEvents.RemoveAt(0);
    }

    internal static void PushUserSale(string name, int rating, long price, long tradeId)
    {
        string safe = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string ev = "{\"type\":\"sale\",\"name\":\"" + safe + "\",\"rating\":" + rating +
                    ",\"price\":" + price + ",\"bin\":0,\"seller\":\"You (bot buyer)\",\"tradeId\":" + tradeId + "}";
        lock (_feedLock)
        {
            _feedEvents.Add(ev);
            const int MaxEvents = 60;
            while (_feedEvents.Count > MaxEvents) _feedEvents.RemoveAt(0);
        }
    }

    internal static string FeedJson()
    {
        TryAdvanceFeed(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        lock (_feedLock)
        {
            var sb = new StringBuilder("{\"total\":").Append(LiveTotal(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                .Append(",\"events\":[");
            for (int i = _feedEvents.Count - 1; i >= 0; i--)
            {
                if (sb[^1] != '[') sb.Append(',');
                sb.Append(_feedEvents[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    internal readonly record struct BidTiming(long Cycle, long Duration, long EndsAt);
    internal static BidTiming Timing(long tradeId, long now) { var c = Cycle(tradeId - TradeIdBase, now); return new(c.k, c.dur, c.start + c.dur); }

    internal readonly record struct BidOutcome(
        long TradeId, RealPlayer Card, int StartingBid, int BuyNow, long MyBid, bool Won);

    // Runs when a user bid's auction has ended: determines whether the user's bid
    // stood against the bots, and reports the outcome for the server to settle.
    internal static List<BidOutcome> CollectMyBidResults(long now)
    {
        var outs = new List<BidOutcome>();
        foreach (var kv in MyBids)
        {
            if (AcceptedOffers.ContainsKey(kv.Key)) continue;   // the seller already accepted this one
            long g = kv.Key - TradeIdBase;
            if (g < 0 || g >= Total) continue;
            var cyc = Cycle(g, now);
            var timing = PlayerContext.Current.Data.Market.BidTimings.GetValueOrDefault(kv.Key, new BidTiming(cyc.k, cyc.dur, cyc.start + cyc.dur));
            if (now < timing.EndsAt) continue;
            var card = Cards[Locate(g)];
            var (start, buy) = Price(card, g, timing.Cycle);
            int effectiveStart = (int)EffStart(card, g, timing.Cycle, timing.Duration, start, buy);
            var (_, _, _, finalBid) = SimBids(card, g, timing.Cycle, timing.Duration, effectiveStart, buy, timing.Duration);
            outs.Add(new BidOutcome(kv.Key, card, effectiveStart, buy, kv.Value, kv.Value >= finalBid));
        }
        return outs;
    }

    internal static void RemoveMyBid(long tradeId)
    {
        MyBids.TryRemove(tradeId, out _);
        PlayerContext.Current.Data.Market.BidTimings.TryRemove(tradeId, out _);
        RefundedBids.TryRemove(tradeId, out _);
        PlayerContext.MarkDirty();
    }

    internal static (int Count, int Winning, int Outbid) WatchlistCounts(long now)
    {
        int winning = 0, outbid = 0;
        foreach (var kv in MyBids)
        {
            if (AcceptedOffers.ContainsKey(kv.Key)) continue;   // counted once below
            long g = kv.Key - TradeIdBase;
            if (g < 0 || g >= Total) continue;
            var cyc = Cycle(g, now);
            if (cyc.local >= cyc.dur) continue;   // ended -> settled on next poll
            var card = Cards[Locate(g)];
            var (start, buy) = Price(card, g, cyc.k);
            int effectiveStart = (int)EffStart(card, g, cyc.k, cyc.dur, start, buy);
            var (_, sim, _, _) = SimBids(card, g, cyc.k, cyc.dur, effectiveStart, buy, cyc.local);
            if (kv.Value >= sim) winning++; else outbid++;
        }
        foreach (var kv in AcceptedOffers) winning++;   // accepted offers count as wins in the watchlist
        int pureWatched = 0;
        foreach (var kv in Watched)
        {
            long g = kv.Key - TradeIdBase;
            if (g < 0 || g >= Total) continue;
            if (MyBids.ContainsKey(kv.Key) || AcceptedOffers.ContainsKey(kv.Key)) continue;   // already counted
            var cyc = Cycle(g, now);
            if (cyc.local >= cyc.dur) continue;
            pureWatched++;   // watched without bidding: count only, no win/outbid badge
        }
        return (winning + outbid + pureWatched, winning, outbid);
    }

internal static (long CurrentBid, int Offers, string BidState) AuctionState(long tradeId, long now)
    {
        long g = tradeId - TradeIdBase;
        if (g < 0 || g >= Total) return (0, 0, "none");
        var cyc = Cycle(g, now);
        if (cyc.local >= cyc.dur || BoughtThisCycle(g, now)) return (0, 0, "none");
        var card = Cards[Locate(g)];
        var (start, buy) = Price(card, g, cyc.k);
        long eff = EffStart(card, g, cyc.k, cyc.dur, start, buy);
        var (_, sim, simOff, _) = SimBids(card, g, cyc.k, cyc.dur, (int)eff, buy, cyc.local);
        long myBid = MyBids.TryGetValue(tradeId, out long mb) ? mb : 0;
        int offers = simOff + (myBid > 0 ? 1 : 0);
        if (AcceptedOffers.TryGetValue(tradeId, out var accOff)) return (accOff.Bid, simOff + 1, "highest");
        if (myBid > 0 && myBid >= sim) return (myBid, offers, "highest");
        if (myBid > 0) return (sim, offers, "outbid");
        return (sim, offers, "none");
    }

    internal static long Step(long p) => p < 1000 ? 50 : p < 10000 ? 100 : p < 50000 ? 250 : p < 100000 ? 500 : 1000;
    internal static long Snap(long p) => Math.Max(150, (p + Step(p) / 2) / Step(p) * Step(p));

    internal static readonly uint MarketKey = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);

    internal static void WarmUp()
    {
        _ = Total;
        _ = CTotal;
        _ = ETotal;
        _ = STotal;
    }

    internal static string SearchSignature(System.Collections.Specialized.NameValueCollection q)
    {
        var parts = new List<string>();
        foreach (string k in q.AllKeys)
            if (k != null && k != "start" && k != "num")
                parts.Add(k + "=" + (q[k] ?? ""));
        parts.Sort(StringComparer.Ordinal);
        return string.Join("&", parts);
    }

    private static bool ModsMatch(RealPlayer c, long g, long now, int playStyle, string[] wantPos)
    {
        bool checkPosition = wantPos != null && wantPos.Length > 0;
        if (playStyle <= 0 && !checkPosition) return true;
        var mods = SimCardState(c, g, Cycle(g, now).k, includeTrainingBoost: false);
        if (playStyle > 0 && mods.Style != playStyle) return false;
        if (!checkPosition) return true;
        string eff = mods.Pos ?? c.Position;
        for (int i = 0; i < wantPos.Length; i++)
            if (string.Equals(eff, wantPos[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ByPriceK(RealPlayer c, long g, long k, long dur, long local,
        int startBid, int buyNow, int mnB, int mxB, int mnC, int mxC)
    {
        if (mnB <= 0 && mxB <= 0 && mnC <= 0 && mxC <= 0) return true;
        if (mnB > 0 && buyNow < mnB) return false;
        if (mxB > 0 && buyNow > mxB) return false;
        if (mnC > 0 || mxC > 0)
        {
            long cur = startBid;
            if (local < dur)
            {
                var (_, sim, _, _) = SimBids(c, g, k, dur, startBid, buyNow, local);
                if (MyBids.TryGetValue(TradeIdBase + g, out long mb) && mb > sim) cur = mb;
                else cur = sim;
            }
            if (mnC > 0 && cur < mnC) return false;
            if (mxC > 0 && cur > mxC) return false;
        }
        return true;
    }

    private static bool ByPrice(RealPlayer c, long g, long now2,
        int mnB, int mxB, int mnC, int mxC)
    {
        if (mnB <= 0 && mxB <= 0 && mnC <= 0 && mxC <= 0) return true;
        var cyc = Cycle(g, now2);
        var (startBid, buyNow) = Price(c, g, cyc.k);
        if (mnB > 0 && buyNow < mnB) return false;
        if (mxB > 0 && buyNow > mxB) return false;
        if (mnC > 0 || mxC > 0)
        {
            long cur = EffStart(c, g, cyc.k, cyc.dur, startBid, buyNow);
            if (cyc.local < cyc.dur)
            {
                var (_, sim, _, _) = SimBids(c, g, cyc.k, cyc.dur, (int)cur, buyNow, cyc.local);
                if (AcceptedOffers.TryGetValue(TradeIdBase + g, out var offer)) cur = offer.Bid;
                else if (MyBids.TryGetValue(TradeIdBase + g, out long mb) && mb > sim) cur = mb;
                else cur = sim;
            }
            if (mnC > 0 && cur < mnC) return false;
            if (mxC > 0 && cur > mxC) return false;
        }
        return true;
    }

    private static bool CByPrice(long cg, long now, int mnB, int mxB, int mnC, int mxC)
    {
        if (mnB <= 0 && mxB <= 0 && mnC <= 0 && mxC <= 0) return true;
        int ci = LocateIn(CPrefix, cg);
        var cyc = CCycle(cg, now);
        var (startBid, buyNow) = CPrice(cg, cyc.k);
        if (mnB > 0 && buyNow < mnB) return false;
        if (mxB > 0 && buyNow > mxB) return false;
        if (mnC > 0 || mxC > 0)
        {
            long cur = CEffStart(cg, cyc.k, cyc.dur, startBid, buyNow);
            if (cyc.local < cyc.dur)
            {
                var (_, sim, _, _) = CSimBids(CTier[ci], cg, cyc.k, cyc.dur, (int)cur, buyNow, cyc.local);
                cur = sim;
            }
            if (mnC > 0 && cur < mnC) return false;
            if (mxC > 0 && cur > mxC) return false;
        }
        return true;
    }

    private static bool EByPrice(long eg, long now, int mnB, int mxB, int mnC, int mxC)
    {
        if (mnB <= 0 && mxB <= 0 && mnC <= 0 && mxC <= 0) return true;
        var cyc = ECycle(eg, now);
        int i = LocateIn(EPrefix, eg);
        var (startBid, buyNow) = EPrice(eg, cyc.k);
        if (mnB > 0 && buyNow < mnB) return false;
        if (mxB > 0 && buyNow > mxB) return false;
        if (mnC > 0 || mxC > 0)
        {
            long cur = EEffStart(eg, cyc.k, cyc.dur, startBid, buyNow);
            if (cyc.local < cyc.dur)
            {
                var (_, sim, _, _) = ESimBids(ECards[i].Rare, ECards[i].Rating, eg, cyc.k, cyc.dur, (int)cur, buyNow, cyc.local);
                cur = sim;
            }
            if (mnC > 0 && cur < mnC) return false;
            if (mxC > 0 && cur > mxC) return false;
        }
        return true;
    }

    private static bool FByPrice(long fg, long now, int mnB, int mxB, int mnC, int mxC)
    {
        if (mnB <= 0 && mxB <= 0 && mnC <= 0 && mxC <= 0) return true;
        var cyc = FCycle(fg, now);
        int i = LocateIn(FPrefix, fg);
        var (startBid, buyNow) = FPrice(fg, cyc.k);
        if (mnB > 0 && buyNow < mnB) return false;
        if (mxB > 0 && buyNow > mxB) return false;
        if (mnC > 0 || mxC > 0)
        {
            long cur = FEffStart(fg, cyc.k, cyc.dur, startBid, buyNow);
            if (cyc.local < cyc.dur)
            {
                int r = FIsManager[i] ? FManager[i].Rating : FStaff[i].Rating;
                var (_, sim, _, _) = FSimBids(r, FIsManager[i], fg, cyc.k, cyc.dur, (int)cur, buyNow, cyc.local);
                cur = sim;
            }
            if (mnC > 0 && cur < mnC) return false;
            if (mxC > 0 && cur > mxC) return false;
        }
        return true;
    }

    internal static string PageJson(int start, int num, long now, Func<RealPlayer, bool> match = null,
        int minBuyNow = 0, int maxBuyNow = 0, int minCurrent = 0, int maxCurrent = 0, int playStyle = 0,
        string sig = null, string[] wantPos = null, PlayerFilterKey? selection = null)
    {

        if (start < 0) start = 0;
        num = Math.Clamp(num, 1, 60);
        var rnd = Random.Shared;
        var sb = new StringBuilder("[");
        int written = 0;
        uint key = MarketKey; long scanFrom = start;

        bool filtered = playStyle > 0 || minBuyNow > 0 || maxBuyNow > 0
                        || minCurrent > 0 || maxCurrent > 0 || (wantPos != null && wantPos.Length > 0);
        long block = Math.Min(ServerOptions.Current.MarketScanLimit, (long)num * (filtered ? 96 : 16));
        long winStart = (scanFrom / Math.Max(1L, (long)num)) * block;

        if (match == null && (selection == null || selection.Value.IsUnfiltered))
        {
            {
                long scanCap = Math.Min(winStart + block, Total);   // never re-roll past the market domain
                for (long p = winStart; written < num && p < scanCap; p++)
                {
                    long g = Permute(p, key);
                    if (!LiveAt(g, now) || BoughtThisCycle(g, now) || SimBinnedThisCycle(g, now)) continue;   // sold / owned / not yet relisted
                    int i = Locate(g);
                    if (!ByPrice(Cards[i], g, now, minBuyNow, maxBuyNow, minCurrent, maxCurrent)) continue;
                    if (!ModsMatch(Cards[i], g, now, playStyle, wantPos)) continue;
                    if (written > 0) sb.Append(',');
                    sb.Append(Entry(Cards[i], g, now, rnd));
                    written++;
                }
            }
        }
        else
        {
            {
                SelectionView view;
                if (selection is { } filter)
                {
                    var viewKey = new SelectionKey(0, filter);
                    if (!SelectionCache.TryGetValue(viewKey, out view))
                        view = RememberSelection(viewKey, CreateSelection(Counts, i => filter.Matches(Cards[i])));
                }
                else view = CreateSelection(Counts, i => match(Cards[i]));
                var mc = view.Indices;
                if (mc.Length > 0)
                {
                    var vPrefix = view.Prefix;
                    long vTotal = view.Total;
                    int vHalf = view.HalfBits;
                    long scanCap = Math.Min(winStart + block, vTotal);   // stay inside the view -> no repeats
                    for (long p = winStart; written < num && p < scanCap; p++)
                    {
                        long fg = PermuteView(p, vTotal, vHalf, key);
                        int k = LocateIn(vPrefix, fg);
                        int ci = mc[k];
                        long g = Prefix[ci] + (fg - vPrefix[k]);   // real global index -> stable tradeId
                        if (!LiveAt(g, now) || BoughtThisCycle(g, now) || SimBinnedThisCycle(g, now)) continue;   // sold / owned / not yet relisted
                        if (!ByPrice(Cards[ci], g, now, minBuyNow, maxBuyNow, minCurrent, maxCurrent)) continue;
                        if (!ModsMatch(Cards[ci], g, now, playStyle, wantPos)) continue;
                        if (written > 0) sb.Append(',');
                        sb.Append(Entry(Cards[ci], g, now, rnd));
                        written++;
                    }
                }
            }
        }
        
        sb.Append(']');
        return sb.ToString();
    }

    internal static string EntryByTradeId(long tradeId, long now)
    {
        long g = tradeId - TradeIdBase;
        if (g >= 0 && g < Total) return Entry(Cards[Locate(g)], g, now, new Random());
        long cg = tradeId - ConsumableTradeIdBase;
        if (cg >= 0 && cg < CTotal) return CEntry(cg, now, new Random());
        long eg = tradeId - ClubItemTradeIdBase;
        if (eg >= 0 && eg < ETotal) return EEntry(eg, now, new Random());
        long fg = tradeId - StaffTradeIdBase;
        if (fg >= 0 && fg < STotal) return FEntry(fg, now, new Random());
        return null;
    }

    private static readonly string[] SellerNames = MakeSellerNames(250_000);

    private static string[] MakeSellerNames(int count)
    {
        string[] descriptors =
        {
            "Crimson", "Azure", "Shadow", "Phantom", "Night", "Cyber", "Neon", "Chaos", "Frost", "Storm",
            "Blaze", "Toxic", "Silent", "Dark", "Savage", "Swift", "Venom", "Volt", "Zenith", "Mystic",
            "Onyx", "Lunar", "Solar", "Cosmic", "Atomic", "Quantum", "Inferno", "Ember", "Echo", "Hyper",
            "Omega", "Alpha", "Prime", "Ultra", "Turbo", "Rogue", "Fallen", "Frozen", "Mythic", "Legendary",
            "Rapid", "Blood", "Ghostly", "Cryptic", "Glacial", "Midnight", "Twilight", "Golden", "Silver", "Emerald",
            "Sapphire", "Crimson", "Solar", "Plasma", "Radiant", "Void", "Haunted", "Grim", "Iron", "Steel",
        };
        string[] nouns =
        {
            "Falcon", "Wolf", "Viper", "Ninja", "Warrior", "Titan", "Hunter", "Dragon", "Comet", "Reaper",
            "Ghost", "Razor", "Saber", "Knight", "Ronin", "Valkyrie", "Oracle", "Rogue", "Apex", "Juggernaut",
            "Phoenix", "Griffin", "Wraith", "Strider", "Slayer", "Guardian", "Sentinel", "Cobra", "Panther", "Lion",
            "Tiger", "Eagle", "Hawk", "Fox", "Owl", "Shark", "Puma", "Lynx", "Jackal", "Mammoth",
            "Hydra", "Basilisk", "Chimera", "Kraken", "Cyclops", "Wizard", "Mage", "Berserker", "Spectre", "Wisp",
            "Yeti", "Ogre", "Demon", "Angel", "Celestial", "Revenant", "Lancer", "Rider", "Blade", "Kage",
        };
        string[] prefixes =
        {
            "xX", "Mr", "The", "iTs", "Itz", "Dr", "Sir", "King", "Big", "Pro",
            "Da", "Iam", "Just", "Im", "Elite", "Mega", "Not", "Too", "Very", "AV",
        };
        string[] suffixes =
        {
            "YT", "HD", "OP", "GX", "xX", "gamer", "07", "22", "14", "77",
            "99", "x", "69", "21", "10", "11", "88", "00", "zz", "eZ",
        };

        var rng = new Rng(0xF17A14u);
        var set = new HashSet<string>();
        int guard = 0;
        while (set.Count < count)
        {
            if (++guard > count * 60) { set.Add("Gamer" + rng.NextDouble().ToString().Substring(2, 6)); continue; }
            string d = descriptors[(int)(rng.NextDouble() * descriptors.Length)];
            string n = nouns[(int)(rng.NextDouble() * nouns.Length)];
            string core = d + n;
            string name;
            double roll = rng.NextDouble();
            if (roll < 0.05)      name = "xX_" + core + "_Xx";
            else if (roll < 0.13) name = "Xx" + core + "xX";
            else if (roll < 0.23) name = prefixes[(int)(rng.NextDouble() * prefixes.Length)] + core;
            else if (roll < 0.35) name = core + suffixes[(int)(rng.NextDouble() * suffixes.Length)];
            else if (roll < 0.95)
            {
                string digits = ((int)(rng.NextDouble() * 100)).ToString("00");
                name = core + digits;
                if (rng.NextDouble() < 0.15) name = name.ToUpperInvariant();
                if (rng.NextDouble() < 0.10) name = name.ToLowerInvariant();
            }
            else
                name = core;
            if (name.Length > 18) continue;   // don't emit chopped tails, just roll again
            set.Add(name);
        }
        return set.ToArray();
    }

    internal static string SellerFor(long g, long k)
        => SellerNames[(int)(Hash((uint)g, (uint)(k * 0x9E3779B1u)) % (uint)SellerNames.Length)];

    internal static System.Collections.Concurrent.ConcurrentDictionary<long, long> MyBids => PlayerContext.Current.Data.Market.MyBids;

    internal static long HeldCoins { get => PlayerContext.Current.Data.Market.HeldCoins; set => PlayerContext.Current.Data.Market.HeldCoins = value; }
    internal static System.Collections.Concurrent.ConcurrentDictionary<long, byte> RefundedBids => PlayerContext.Current.Data.Market.RefundedBids;
    internal static System.Collections.Concurrent.ConcurrentDictionary<long, byte> Watched => PlayerContext.Current.Data.Market.Watched;

    internal static void ChangeHeld(long delta)
    {
        HeldCoins += delta;
        PlayerContext.MarkDirty();
        if (HeldCoins < 0) HeldCoins = 0;
    }

    internal static long EscrowHeld(long tradeId) =>
        RefundedBids.ContainsKey(tradeId)
            ? 0
            : (MyBids.TryGetValue(tradeId, out long mb) ? mb : 0);

    internal static long RefundEscrow(long tradeId)
    {
        long esc = EscrowHeld(tradeId);
        if (esc > 0) ChangeHeld(-esc);
        RefundedBids.TryAdd(tradeId, 0);
        return esc;
    }

    internal static List<long> CollectRefundableBids(long now)
    {
        var refunds = new List<long>();
        foreach (var kv in MyBids)
        {
            long tid = kv.Key;
            if (AcceptedOffers.ContainsKey(tid) || RefundedBids.ContainsKey(tid)) continue;
            long g = tid - TradeIdBase;
            if (g < 0 || g >= Total) { refunds.Add(tid); continue; }
            var st = AuctionState(tid, now);
            if (st.BidState == "outbid" || st.BidState == "none") refunds.Add(tid);
        }
        return refunds;
    }

    internal readonly record struct PendingOffer(long Bid, long OfferedItemId, long AcceptAtUnix, long SettledAt = 0);
    internal static System.Collections.Concurrent.ConcurrentDictionary<long, PendingOffer> AcceptedOffers => PlayerContext.Current.Data.Market.AcceptedOffers;

    private static System.Collections.Concurrent.ConcurrentDictionary<long, long> BoughtAt => PlayerContext.Current.Data.Market.BoughtAt;

    internal static void MarkBought(long tradeId, long now)
    {
        long g = tradeId - TradeIdBase;
        if (g < 0 || g >= Total) return;
        BoughtAt[g] = Cycle(g, now).k;
    }

    private static bool BoughtThisCycle(long g, long now)
        => BoughtAt.TryGetValue(g, out long k) && k == Cycle(g, now).k;

    internal static bool Bought(long tradeId, long now)
    {
        long g = tradeId - TradeIdBase;
        return g >= 0 && g < Total && BoughtThisCycle(g, now);
    }

    private static bool SimBinnedThisCycle(long g, long now)
    {
        var cyc = Cycle(g, now);
        if (cyc.local >= cyc.dur) return false;
        var card = Cards[Locate(g)];
        var (start, buy) = Price(card, g, cyc.k);
        long eff = EffStart(card, g, cyc.k, cyc.dur, start, buy);
        var (_, sim, _, _) = SimBids(card, g, cyc.k, cyc.dur, (int)eff, buy, cyc.local);
        return sim >= buy;
    }

    private static string Entry(RealPlayer card, long g, long now, Random rnd)
    {
        long itemId = ItemIdBase + g;
        long tradeId = TradeIdBase + g;
        var cyc = Cycle(g, now);
        var (start, buy) = Price(card, g, cyc.k);
        long effStart = EffStart(card, g, cyc.k, cyc.dur, start, buy);
        long myBid = MyBids.TryGetValue(tradeId, out long mb) ? mb : 0;
        bool offerPending = AcceptedOffers.TryGetValue(tradeId, out var myOffer);
        if (offerPending) myBid = myOffer.Bid;
        bool bought = BoughtThisCycle(g, now);
        bool live = cyc.local < cyc.dur && !bought;

        long currentBid; int offers; string bidState; string tradeState; long expiresOut;
        if (live)
        {
            long remaining = cyc.dur - cyc.local;
            var (_, sim, simOff, _) = SimBids(card, g, cyc.k, cyc.dur, (int)effStart, buy, cyc.local);
            if (offerPending)
            {
                currentBid = myBid; offers = simOff + 1;
                bidState = "highest"; tradeState = "active"; expiresOut = remaining;   // seller accepted your offer
            }
            else if (sim >= buy)
            {
                currentBid = buy; offers = simOff + (myBid > 0 ? 1 : 0);
                bidState = "none"; tradeState = "closed"; expiresOut = 0;   // bot BIN'd it
            }
            else
            {
                offers = simOff + (myBid > 0 ? 1 : 0);
                if (myBid > 0 && myBid >= sim) { currentBid = myBid; bidState = "highest"; }
                else { currentBid = sim; bidState = myBid > 0 ? "outbid" : "none"; }
                tradeState = "active";
                expiresOut = remaining;
            }
        }
        else if (bought)
        {
            if (offerPending)
            {
                var (_, _, simOff2, _) = SimBids(card, g, cyc.k, cyc.dur, (int)effStart, buy, cyc.dur);
                currentBid = myOffer.Bid; offers = simOff2 + 1; bidState = "highest";
                tradeState = "closed"; expiresOut = 0;   // accepted offer settled - reads as a won purchase
            }
            else
            {
                currentBid = buy; offers = 0; bidState = "none";
                tradeState = "closed"; expiresOut = 0;
            }
        }
        else
        {
            var (hasBids, _, _, _) = SimBids(card, g, cyc.k, cyc.dur, (int)effStart, buy, cyc.dur);
            currentBid = Math.Max(myBid, effStart);
            offers = (hasBids ? 1 : 0) + (myBid > 0 ? 1 : 0);
            bidState = "none";
            tradeState = hasBids || myBid > 0 ? "closed" : "expired";
            expiresOut = 0;
        }

        string seller = SellerFor(g, cyc.k);
        string item = MktPlayerItem(card, g, cyc.k, itemId, now);
        return "{\"tradeId\":" + tradeId + ",\"itemData\":" + item +
               ",\"tradeState\":\"" + tradeState + "\",\"buyNowPrice\":" + buy +
               ",\"currentBid\":" + currentBid + ",\"offers\":" + offers +
               ",\"watched\":" + (myBid > 0 || Watched.ContainsKey(tradeId) ? "true" : "false") +
               ",\"bidState\":\"" + bidState + "\",\"startingBid\":" + effStart + ",\"confidenceValue\":100" +
               ",\"expires\":" + expiresOut +
               ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013," +
               "\"sellerId\":0,\"tradeOwner\":false,\"tradeIdStr\":\"" + tradeId +
               "\",\"lastSalePrice\":0,\"coinsProcessed\":false}";
    }

    private static readonly int[] PlayerStyles =
    {
        251, 252, 253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266,
        267, 268,
    };
    private static readonly int[] GkStyles =
    {
        269, 270, 271, 272, 273,
    };
    private static readonly (string From, string To)[] PositionChanges =
    {
        ("ST", "CF"), ("CF", "ST"), ("LW", "LF"), ("LF", "LW"), ("RW", "RF"), ("RF", "RW"),
        ("LM", "LW"), ("LW", "LM"), ("RM", "RW"), ("RW", "RM"), ("CM", "CAM"), ("CAM", "CM"),
        ("CDM", "CM"), ("CM", "CDM"), ("CAM", "CDM"), ("CDM", "CAM"), ("RWB", "RB"), ("RB", "RWB"),
        ("LWB", "LB"), ("LB", "LWB"),
    };
    private static readonly Dictionary<string, string[]> PositionTargets = PositionChanges
        .GroupBy(pair => pair.From, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.To).ToArray(), StringComparer.Ordinal);

    private static string MktPlayerItem(RealPlayer card, long g, long k, long itemId, long now)
    {
        var (contract, fitness, morale, style, pos, trainingFlag, boost) = SimCardState(card, g, k);
        return WebServer.BuildRealPlayerItem(null, card, itemId, now, 5, "forSale",
            contract, fitness, morale, style, pos, trainingFlag, boost, null, 0);
    }

    private static (int Contract, int Fitness, int Morale, int Style, string Pos, int TrainingFlag, int[] Boost)
        SimCardState(RealPlayer card, long g, long k, bool includeTrainingBoost = true)
    {
        var r = new Rng(Hash((uint)g, (uint)(k * 0x9E3779B1u)) ^ 0x2F6E2B57u);
        int contract = 7, fitness = 99, morale = 50, style = 250, trainingFlag = -1;
        string pos = null; int[] boost = null;

        if (r.NextDouble() >= 0.45) contract = (int)(r.NextDouble() * 7.0);        // ~55% full contract
        if (r.NextDouble() >= 0.45) fitness = 45 + (int)(r.NextDouble() * 54.0);   // ~55% fully fit
        if (r.NextDouble() >= 0.35) morale = 1 + (int)(r.NextDouble() * 99.0);
        if (r.NextDouble() >= 0.62)
        {
            var pool = string.Equals(card.Position, "GK", StringComparison.OrdinalIgnoreCase)
                ? GkStyles : PlayerStyles;
            style = pool[(int)(r.NextDouble() * pool.Length)];
        }
        if (r.NextDouble() >= 0.80)
        {
            if (PositionTargets.TryGetValue(card.Position, out var to))
                pos = to[(int)(r.NextDouble() * to.Length)];
        }
        if (r.NextDouble() >= 0.85)
        {
            trainingFlag = 1;
            if (includeTrainingBoost)
            {
                boost = new int[6];
                boost[(int)(r.NextDouble() * 6)] += 6;
            }
        }
        return (contract, fitness, morale, style, pos, trainingFlag, boost);
    }

    internal static PlayerMod ListingMods(long tradeId, RealPlayer card, long now)
    {
        long g = tradeId - TradeIdBase;
        if (g < 0 || g >= Total) return null;
        var cyc = Cycle(g, now);
        var (contract, fitness, _, style, pos, trainingFlag, boost) = SimCardState(card, g, cyc.k);
        var mod = new PlayerMod
        {
            PlayStyle = style,
            Contract = contract,
            Fitness = fitness,
        };
        if (!string.IsNullOrEmpty(pos)) mod.Position = pos;
        if (boost != null)
        {
            mod.TrainingFlag = trainingFlag > 0 ? trainingFlag : 0;
            System.Array.Copy(boost, mod.AttrBoost, boost.Length);
        }
        return mod;
    }

    internal static bool ResolveTradeId(long tradeId, out RealPlayer card, out int startingBid, out int buyNow)
    {
        card = default; startingBid = 0; buyNow = 0;
        long g = tradeId - TradeIdBase;
        if (g < 0 || g >= Total) return false;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cyc = Cycle(g, nowUtc);
        if (cyc.local >= cyc.dur || BoughtThisCycle(g, nowUtc) || SimBinnedThisCycle(g, nowUtc)) return false;   // just sold / not relisted
        card = Cards[Locate(g)];
        var (start, buy) = Price(card, g, cyc.k);
        startingBid = (int)EffStart(card, g, cyc.k, cyc.dur, start, buy);
        buyNow = buy;
        return true;
    }


    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> CardIndexByCardId = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long MinBuy, long At)> CheapCache = new();
    private const long CheapCacheTtlSec = 8;

    internal static long CheapestLiveBuyNow(RealPlayer card, long now)
    {
        if (CheapCache.TryGetValue(card.CardId, out var e) && now >= e.At && now - e.At < CheapCacheTtlSec) return e.MinBuy;

        if (!CardIndexByCardId.TryGetValue(card.CardId, out int ci))
        {
            ci = -1;
            for (int j = 0; j < Cards.Length; j++)
                if (Cards[j].CardId == card.CardId) { ci = j; break; }
            CardIndexByCardId[card.CardId] = ci;
        }
        long min = long.MaxValue;
        if (ci >= 0)
        {
            for (long g = Prefix[ci]; g < Prefix[ci + 1]; g++)
            {
                // This shared estimate values user listings and trade offers against
                // the virtual market. Private account purchases must not change
                // another account's valuation or enter a cross-account cache.
                if (!LiveAt(g, now) || SimBinnedThisCycle(g, now)) continue;
                var cyc = Cycle(g, now);
                var (_, buy) = Price(Cards[ci], g, cyc.k);
                if (buy < min) min = buy;
            }
        }
        long result = min == long.MaxValue ? 0 : min;
        CheapCache[card.CardId] = (result, now);
        return result;
    }

    internal static long MarketValue(RealPlayer card) => BasePrice(card);   // fallback when no live comps

    internal static long OfferAcceptDelay(long total, long buyNow, Random rnd)
    {
        double ratio = (double)total / buyNow;
        if (ratio >= 1.00) return 5 + rnd.Next(0, 26);         // seconds
        if (ratio >= 0.90) return 30 + rnd.Next(0, 61);
        if (ratio >= 0.80) return 90 + rnd.Next(0, 121);
        if (ratio >= 0.70) return 180 + rnd.Next(0, 181);
        if (ratio >= 0.60) return 300 + rnd.Next(0, 301);
        return -1;                                             // declined
    }

    internal static long UserSaleDelay(long price, long floor, Random rnd)
    {
        if (floor <= 0) floor = Math.Max(1, price);          // no comps: assume fair pricing
        if (price < floor)
        {
            double under = (double)price / floor;
            if (under <= 0.25) return rnd.Next(2, 5);        // absurdly cheap -> almost instant
            if (under <= 0.50) return rnd.Next(5, 12);
            return rnd.Next(15, 45);                         // a good deal -> under a minute
        }
        if (price == floor) return rnd.Next(150, 420);       // same as the cheapest copy: a player has to browse and spot it
        double ratio = (double)price / floor;
        if (ratio <= 1.10) return rnd.Next(300, 750);        // ~cheapest -> several minutes
        if (ratio <= 1.25) return rnd.Next(900, 2100);       // a bit high -> 15-35 min
        if (ratio <= 1.50) return rnd.Next(1800, 7200);      // noticeably high -> 30 min - 2 h
        if (ratio <= 2.00 && rnd.NextDouble() < 0.5) return rnd.Next(7200, 43200);   // doubled: half the time, eventually
        return 0;                                            // 2x+ the floor -> nobody bites
    }

    private static int Locate(long g) => LocateIn(Prefix, g);

    private static int LocateIn(long[] prefix, long g)
    {
        int lo = 0, hi = prefix.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (prefix[mid] <= g) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private static int HalfBitsFor(long total)
    {
        int hb = 1;
        while ((1L << (2 * hb)) < total) hb++;
        return hb;
    }

    private static long Permute(long p, uint key) => PermuteView(p, Total, HalfBits, key);

    private static long PermuteView(long p, long total, int half, uint key)
    {
        long g = Feistel(p, half, key);
        while (g >= total) g = Feistel(g, half, key);
        return g;
    }

    private static long Feistel(long x, int half, uint key)
    {
        long mask = (1L << half) - 1;
        long l = (x >> half) & mask;
        long r = x & mask;
        for (int i = 0; i < 4; i++)
        {
            long f = Hash((uint)r, (uint)(i * 0x9E3779B1u) ^ key) & mask;
            (l, r) = (r, l ^ f);
        }
        return (l << half) | r;
    }

    private static uint Hash(uint a, uint b)
    {
        uint h = a * 2654435761u ^ (b + 0x9E3779B9u + (a << 6) + (a >> 2));
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
        return h;
    }

    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }
        private uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
        public double NextDouble() => (NextU() & 0xFFFFFF) / (double)0x1000000;
    }
}
