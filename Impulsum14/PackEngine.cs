namespace Impulsum14;
internal readonly record struct PackPick(
    PackPick.ItemKind Kind,
    RealPlayer Player,
    ConsumableItem Consumable,
    CosmeticItem Cosmetic,
    Manager Manager,
    int ManagerRareFlag,
    StaffCard Staff)
{
    internal enum ItemKind { Player, Consumable, Cosmetic, Manager, Staff }

    internal static PackPick OfPlayer(RealPlayer p) =>
        new(ItemKind.Player, p, default, default, default, 0, default);
    internal static PackPick OfConsumable(ConsumableItem c) =>
        new(ItemKind.Consumable, default, c, default, default, 0, default);
    internal static PackPick OfCosmetic(CosmeticItem c) =>
        new(ItemKind.Cosmetic, default, default, c, default, 0, default);
    internal static PackPick OfManager(Manager m, int rareFlag) =>
        new(ItemKind.Manager, default, default, default, m, rareFlag, default);
    internal static PackPick OfStaff(StaffCard s) =>
        new(ItemKind.Staff, default, default, default, default, 0, s);
}

internal static class PackEngine
{

    private static readonly Dictionary<(int Tier, bool Rare), RealPlayer[]> PlayerPools =
        BuildPlayerPools();

    private static Dictionary<(int, bool), RealPlayer[]> BuildPlayerPools()
    {
        var pools = new Dictionary<(int, bool), RealPlayer[]>();
        for (int tier = 0; tier <= 2; tier++)
            foreach (bool rare in new[] { false, true })
                pools[(tier, rare)] = RealPlayers.All
                    .Where(p => PackConfig.TierOf(p.Rating) == tier && (p.Rare != 0) == rare)
                    .ToArray();
        return pools;
    }

    private sealed class Slot
    {
        public string Type = ItemTypes.Player;
        public int Tier;
        public bool Rare;
        public int? ForcedTier;
    }

    private const int SubK = 25;
    private const int SpecialFloor = 5;

    private static int SpecialWeight(int rating) => System.Math.Max(SpecialFloor, 99 - rating);

    private static readonly Dictionary<int, RealPlayer[]> SpecialsByBase =
        SpecialCards.All.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.ToArray());

    private static (RealPlayer Card, bool IsSpecial) Substitute(RealPlayer basePlayer, int tier, System.Random rnd)
    {
        if (!SpecialsByBase.TryGetValue(basePlayer.Id, out var all)) return (basePlayer, false);
        var options = all.Where(s => PackConfig.TierOf(s.Rating) == tier).ToArray();
        if (options.Length == 0) return (basePlayer, false);

        int stayBase = SubK * PackWeights.Of(basePlayer);   
        int total = stayBase;
        foreach (var s in options) total += SpecialWeight(s.Rating);
        int roll = rnd.Next(total) - stayBase;
        if (roll < 0) return (basePlayer, false);
        foreach (var s in options)
        {
            roll -= SpecialWeight(s.Rating);
            if (roll < 0) return (s, true);
        }
        return (options[options.Length - 1], true);
    }

    internal static List<PackPick> Open(int packId, System.Random rnd, out bool gotSpecial)
    {
        gotSpecial = false;
        if (!PackConfig.Packs.TryGetValue(packId, out var def))
            def = PackConfig.Packs[300];  

        var level = WeightedPick(def.Levels, v => v.Weight, rnd);
        var cls   = WeightedPick(def.Classes, v => v.Weight, rnd);
        var type  = WeightedPick(def.Types, v => v.Weight, rnd);

        var slots = BuildSlots(def, type, level, cls, rnd);

        ApplyGuarantees(def, slots, rnd);

        var drawnPlayerIds = new HashSet<int>();
        var drawnOther = new HashSet<(string, long)>();
        var picks = new PackPick[slots.Count];
        var isSpec = new bool[slots.Count];

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Type == ItemTypes.Player)
            {
                var (pick, isSpecial) = FillPlayer(def, slots[i], drawnPlayerIds, rnd);
                if (isSpecial) { gotSpecial = true; isSpec[i] = true; }
                drawnPlayerIds.Add(pick.Id);
                picks[i] = PackPick.OfPlayer(pick);
            }
            else
            {
                picks[i] = FillNonPlayer(slots[i], drawnOther, rnd);
            }
        }

        if (ServerOptions.Current.EnableLegends && rnd.NextDouble() < ServerOptions.Current.LegendChance)
        {
            var candidates = Enumerable.Range(0, slots.Count).Where(i => slots[i].Type == ItemTypes.Player && (slots[i].ForcedTier ?? slots[i].Tier) == PackConfig.Gold && slots[i].Rare).ToArray();
            if (candidates.Length > 0)
            {
                int target = candidates[rnd.Next(candidates.Length)];
                picks[target] = PackPick.OfPlayer(PackLottery.Draw(Legends.Catalog, p => Math.Max(1, 100 - p.Rating), rnd));
                isSpec[target] = true; gotSpecial = true;
            }
        }

        if (gotSpecial)
        {
            int cap = int.MaxValue;
            for (int i = 0; i < slots.Count; i++)
                if (isSpec[i]) cap = System.Math.Min(cap, picks[i].Player.Rating);

            for (int i = 0; i < slots.Count; i++)
            {
                if (isSpec[i] || slots[i].Type != ItemTypes.Player || picks[i].Player.Rating <= cap)
                    continue;
                int tier = slots[i].ForcedTier ?? slots[i].Tier;
                var capped = PlayerPool(tier, slots[i].Rare, def.MinPlayerRating)
                    .Where(p => p.Rating <= cap && !drawnPlayerIds.Contains(p.Id)).ToArray();
                if (capped.Length == 0) continue;   // can't satisfy the cap; leave the card as-is
                drawnPlayerIds.Remove(picks[i].Player.Id);
                var repl = PackLottery.Draw(capped, PackWeights.Of, rnd);
                drawnPlayerIds.Add(repl.Id);
                picks[i] = PackPick.OfPlayer(repl);
            }
        }

        return new List<PackPick>(picks);
    }

    private static List<Slot> BuildSlots(PackDefinition def, TypeVariant type, LevelVariant level, ClassVariant cls, System.Random rnd)
    {
        var slots = new List<Slot>(def.Size);
        foreach (var (t, count) in type.Fixed)
            for (int i = 0; i < count; i++) slots.Add(new Slot { Type = t });

        WildcardPool pool = def.Wildcards.FirstOrDefault(w => w.Name == type.WildcardPool);
        for (int i = 0; i < type.Wildcard; i++)
        {
            string picked = pool.Options is { Length: > 0 }
                ? WeightedPick(pool.Options, o => o.Weight, rnd).Type
                : ItemTypes.Kit;
            slots.Add(new Slot { Type = picked });
        }

        // Deal tier tokens. Tokens are the level variant's gold/silver/bronze counts; each slot draws
        // one, weighted by how many of each remain (the design's "dealt like cards from a deck"). All
        // item types here resolve at every tier (the non-player pickers relax), so no look-ahead is
        // needed; players exist at every tier too.
        var tokens = new List<int>(def.Size);
        for (int i = 0; i < level.Gold; i++)   tokens.Add(PackConfig.Gold);
        for (int i = 0; i < level.Silver; i++) tokens.Add(PackConfig.Silver);
        for (int i = 0; i < level.Bronze; i++) tokens.Add(PackConfig.Bronze);
        while (tokens.Count < slots.Count) tokens.Add(PackConfig.Gold);
        Shuffle(tokens, rnd);
        for (int i = 0; i < slots.Count; i++) slots[i].Tier = tokens[i];

        var eligible = slots.Where(s => RareEligible(s.Type)).OrderBy(_ => rnd.Next()).ToList();
        int rares = System.Math.Min(cls.Rare, eligible.Count);
        for (int i = 0; i < rares; i++) eligible[i].Rare = true;
        if (rares < cls.Rare)
            ServerDiagnostics.Write($"[Pack] {def.Size}-item pack wanted {cls.Rare} rares but only " +
                                     $"{eligible.Count} slots are rare-eligible");

        Shuffle(slots, rnd);
        return slots;
    }
    private static bool RareEligible(string type) => type is ItemTypes.Player or ItemTypes.Contract
        or ItemTypes.Fitness or ItemTypes.Training or ItemTypes.Healing;

    private static void ApplyGuarantees(PackDefinition def, List<Slot> slots, System.Random rnd)
    {
        foreach (var g in def.Guarantees)
        {
            if (rnd.NextDouble() * 100.0 >= g.ChancePct) continue;

            var typeSlots = slots.Where(s => s.Type == g.ItemType).ToList();
            if (typeSlots.Count == 0) continue;

            if (g.Rare && !typeSlots.Any(s => s.Rare))
            {
                var target = typeSlots[rnd.Next(typeSlots.Count)];
                var donor = slots.FirstOrDefault(s => s.Rare && s.Type != g.ItemType);
                if (donor != null) donor.Rare = false;
                target.Rare = true;
            }
            if (g.Tier is int t)
            {
                var target = typeSlots.FirstOrDefault(s => s.Rare) ?? typeSlots[0];
                target.ForcedTier = t;
            }
        }
    }

    private static (RealPlayer Pick, bool IsSpecial) FillPlayer(PackDefinition def, Slot slot, HashSet<int> drawnIds, System.Random rnd)
    {
        int tier = slot.ForcedTier ?? slot.Tier;

        var pool = PlayerPool(tier, slot.Rare, def.MinPlayerRating);
        var pick = PackLottery.DrawExcluding(pool, PackWeights.Of, p => drawnIds.Contains(p.Id), rnd);

        return Substitute(pick, tier, rnd);
    }

    private static RealPlayer[] PlayerPool(int tier, bool rare, int minRating)
    {
        var pool = PlayerPools[(tier, rare)];
        if (pool.Length == 0) pool = PlayerPools[(tier, false)];
        if (pool.Length == 0) pool = RealPlayers.All;
        if (minRating > 0)
        {
            var floored = pool.Where(p => p.Rating >= minRating).ToArray();
            if (floored.Length >= 8) pool = floored;
        }
        return pool;
    }

    private static PackPick FillNonPlayer(Slot slot, HashSet<(string, long)> drawnOther, System.Random rnd)
    {
        var (lo, hi) = PackConfig.Band(slot.Tier);
        string tierWord = PackConfig.TierWord(slot.Tier);

        switch (slot.Type)
        {
            case ItemTypes.Manager:
            {
                var m = PickManager(lo, hi, drawnOther, rnd);
                drawnOther.Add((ItemTypes.Manager, m.ResourceId));
                return PackPick.OfManager(m, slot.Rare ? 1 : 0);
            }
            case ItemTypes.Staff:
            {
                var s = PickStaff(lo, hi, drawnOther, rnd);
                drawnOther.Add((ItemTypes.Staff, s.ResourceId));
                return PackPick.OfStaff(s);
            }
            case ItemTypes.Kit:
            case ItemTypes.Badge:
            case ItemTypes.Ball:
            case ItemTypes.Stadium:
            {
                var c = PickCosmetic(slot.Type, lo, hi, slot.Rare, drawnOther, rnd);
                drawnOther.Add((slot.Type, c.ResourceId));
                return PackPick.OfCosmetic(c);
            }
            case ItemTypes.Position:
            {
                var inst = PickConsumable("TrainingPlayerPos", tierWord, false, rnd) with { RareFlag = 1 };
                return PackPick.OfConsumable(inst);
            }
            default:   // contract / fitness / training / healing
            {
                string family = slot.Type switch
                {
                    ItemTypes.Contract => rnd.NextDouble() < 0.90 ? "ContractPlayer" : "ContractStaff",
                    ItemTypes.Fitness  => "Fitness",
                    ItemTypes.Training => "Training",
                    ItemTypes.Healing  => "Health",
                    _                  => "ContractPlayer",
                };
                var inst = PickConsumable(family, tierWord, slot.Rare, rnd);
                return PackPick.OfConsumable(inst);
            }
        }
    }

    private static Manager PickManager(int lo, int hi, HashSet<(string, long)> drawn, System.Random rnd)
    {
        if (Managers.All.Length == 0) return default;
        var pool = Managers.All.Where(m => m.Rating >= lo && m.Rating <= hi
                                           && !drawn.Contains((ItemTypes.Manager, m.ResourceId))).ToArray();
        if (pool.Length == 0) pool = Managers.All.Where(m => m.Rating >= lo && m.Rating <= hi).ToArray();
        if (pool.Length == 0) pool = Managers.All;
        return pool[rnd.Next(pool.Length)];
    }

    private static StaffCard PickStaff(int lo, int hi, HashSet<(string, long)> drawn, System.Random rnd)
    {
        if (Staff.All.Length == 0) return default;
        var pool = Staff.All.Where(s => s.Rating >= lo && s.Rating <= hi
                                        && !drawn.Contains((ItemTypes.Staff, s.ResourceId))).ToArray();
        if (pool.Length == 0) pool = Staff.All.Where(s => s.Rating >= lo && s.Rating <= hi).ToArray();
        if (pool.Length == 0) pool = Staff.All;
        return pool[rnd.Next(pool.Length)];
    }

    private static CosmeticItem PickCosmetic(string type, int lo, int hi, bool wantRare,
                                             HashSet<(string, long)> drawn, System.Random rnd)
    {
        var ofType = ClubItems.Catalog.Where(c => c.Type == type).ToArray();
        if (ofType.Length == 0) ofType = ClubItems.Catalog;
        CosmeticItem[] Narrow(System.Func<CosmeticItem, bool> f)
        {
            var byDedup = ofType.Where(c => !drawn.Contains((type, c.ResourceId))).ToArray();
            var src = byDedup.Length > 0 ? byDedup : ofType;
            return src.Where(f).ToArray();
        }
        var pool = Narrow(c => (c.Rare != 0) == wantRare && c.Rating >= lo && c.Rating <= hi);
        if (pool.Length == 0) pool = Narrow(c => (c.Rare != 0) == wantRare);
        if (pool.Length == 0) pool = Narrow(c => c.Rating >= lo && c.Rating <= hi);
        if (pool.Length == 0) pool = ofType;
        return pool[rnd.Next(pool.Length)];
    }

    private static ConsumableItem PickConsumable(string family, string tierWord, bool rare, System.Random rnd)
    {
        var all = ConsumableItems.Catalog
            .Where(c => c.ItemType.StartsWith(family, System.StringComparison.OrdinalIgnoreCase)).ToArray();
        if (all.Length == 0)
            throw new System.InvalidOperationException(
                $"no '{family}' consumables in the catalog (consumables.tsv incomplete)");
        var pool = all.Where(c => c.Name.Contains(tierWord, System.StringComparison.OrdinalIgnoreCase)
                                  && (c.RareFlag != 0) == rare).ToArray();
        if (pool.Length == 0) pool = all.Where(c => (c.RareFlag != 0) == rare).ToArray();
        if (pool.Length == 0) pool = all.Where(c => c.Name.Contains(tierWord, System.StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pool.Length == 0) pool = all;
        return pool[rnd.Next(pool.Length)];
    }

    private static T WeightedPick<T>(IReadOnlyList<T> options, System.Func<T, int> weight, System.Random rnd)
    {
        int total = 0;
        for (int i = 0; i < options.Count; i++) total += System.Math.Max(0, weight(options[i]));
        if (total <= 0) return options[rnd.Next(options.Count)];
        int roll = rnd.Next(total);
        for (int i = 0; i < options.Count; i++)
        {
            roll -= System.Math.Max(0, weight(options[i]));
            if (roll < 0) return options[i];
        }
        return options[options.Count - 1];
    }

    private static void Shuffle<T>(IList<T> list, System.Random rnd)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
