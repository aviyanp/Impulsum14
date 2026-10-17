namespace Impulsum14;

internal readonly record struct MatchmakingPool(string Protocol, string Mode, bool Ranked);

/// <summary>The older search controls the widening window. Arrival order breaks every tie.</summary>
internal readonly record struct MatchmakingPolicy(int InitialRange, int RangeStep, int IntervalSeconds, int MaxRange)
{
    internal int Range(DateTimeOffset queuedAt, DateTimeOffset now)
    {
        long steps = Math.Max(0, (long)(now - queuedAt).TotalSeconds) / Math.Max(1, IntervalSeconds);
        return (int)Math.Min(MaxRange, InitialRange + steps * RangeStep);
    }
}

/// <summary>
/// Account/connection membership and expiry are indexed. Pools index the oldest search at
/// each exact rating; a sparse range-minimum tree finds the oldest compatible opponent
/// without scanning unrelated pools or every queued player. The owner supplies its lock.
/// </summary>
internal sealed class MatchmakingQueue<T>
{
    internal sealed class Search
    {
        internal required uint Id;
        internal required long AccountId;
        internal required Guid ConnectionId;
        internal required MatchmakingPool Pool;
        internal required int Rating;
        internal required DateTimeOffset QueuedAt;
        internal required DateTimeOffset Deadline;
        internal required T Payload;
        internal long Order;
    }
    internal sealed record Pair(Search Host, Search Guest);
    private sealed class RangeNode
    {
        internal RangeNode Left, Right;
        internal Search Oldest;
    }
    private sealed class PoolIndex
    {
        internal readonly Dictionary<int, SortedSet<Search>> Ratings = new();
        internal readonly SortedSet<Search> Heads = new(OrderComparer);
        internal RangeNode Root;
    }
    private const int MaximumRating = 100000;
    private static readonly IComparer<Search> OrderComparer = Comparer<Search>.Create((a, b) => a.Order.CompareTo(b.Order));
    private static readonly IComparer<Search> ExpiryComparer = Comparer<Search>.Create((a, b) =>
    {
        int order = a.Deadline.CompareTo(b.Deadline);
        return order != 0 ? order : a.Order.CompareTo(b.Order);
    });
    private readonly Dictionary<long, Search> _accounts = new();
    private readonly Dictionary<Guid, Search> _connections = new();
    private readonly Dictionary<MatchmakingPool, PoolIndex> _pools = new();
    private readonly SortedSet<Search> _expiry = new(ExpiryComparer);
    private readonly SortedSet<Search> _poolHeads = new(OrderComparer);
    private long _order;
    internal int Count => _accounts.Count;
    internal MatchmakingPool[] Pools => _poolHeads.Select(s => s.Pool).ToArray();
    internal bool TryGet(long account, out Search search) => _accounts.TryGetValue(account, out search);
    internal bool TryGet(Guid connection, out Search search) => _connections.TryGetValue(connection, out search);

    internal void Add(Search search, int capacity)
    {
        if (Count >= capacity) throw new InvalidOperationException("Matchmaking queue is full.");
        if (_accounts.ContainsKey(search.AccountId) || _connections.ContainsKey(search.ConnectionId))
            throw new InvalidOperationException("Account or connection already queued.");
        if (search.Rating is < 0 or > MaximumRating || search.Deadline <= search.QueuedAt)
            throw new ArgumentOutOfRangeException(nameof(search));
        if (search.Order == 0) search.Order = checked(++_order);
        if (!_pools.TryGetValue(search.Pool, out var pool)) _pools.Add(search.Pool, pool = new());
        if (pool.Heads.Min is { } oldPoolHead) _poolHeads.Remove(oldPoolHead);
        if (!pool.Ratings.TryGetValue(search.Rating, out var bucket)) pool.Ratings.Add(search.Rating, bucket = new(OrderComparer));
        var oldHead = bucket.Min;
        bucket.Add(search);
        if (!ReferenceEquals(oldHead, bucket.Min))
        {
            if (oldHead is not null) pool.Heads.Remove(oldHead);
            pool.Heads.Add(bucket.Min);
            pool.Root = Set(pool.Root, 0, MaximumRating, search.Rating, bucket.Min);
        }
        _poolHeads.Add(pool.Heads.Min);
        _accounts.Add(search.AccountId, search);
        _connections.Add(search.ConnectionId, search);
        _expiry.Add(search);
    }

    internal bool Remove(Search search)
    {
        // A delayed disconnect/cancel must never remove a newer search for this account.
        if (!_accounts.TryGetValue(search.AccountId, out var current) || !ReferenceEquals(current, search)) return false;
        _accounts.Remove(search.AccountId);
        _connections.Remove(search.ConnectionId);
        _expiry.Remove(search);
        var pool = _pools[search.Pool];
        _poolHeads.Remove(pool.Heads.Min);
        var bucket = pool.Ratings[search.Rating];
        bool head = ReferenceEquals(bucket.Min, search);
        bucket.Remove(search);
        if (head)
        {
            pool.Heads.Remove(search);
            var next = bucket.Min;
            if (next is not null) pool.Heads.Add(next);
            pool.Root = Set(pool.Root, 0, MaximumRating, search.Rating, next);
        }
        if (bucket.Count == 0) pool.Ratings.Remove(search.Rating);
        if (pool.Ratings.Count == 0) _pools.Remove(search.Pool);
        else _poolHeads.Add(pool.Heads.Min);
        return true;
    }

    internal List<Search> Expire(DateTimeOffset now)
    {
        var expired = new List<Search>();
        while (_expiry.Min is { } search && search.Deadline <= now)
        {
            Remove(search);
            expired.Add(search);
        }
        return expired;
    }

    internal Pair TakePair(MatchmakingPool key, DateTimeOffset now, MatchmakingPolicy policy)
    {
        if (!_pools.TryGetValue(key, out var pool)) return null;
        foreach (var host in pool.Heads)
        {
            int range = policy.Range(host.QueuedAt, now);
            Search guest = Older(
                Query(pool.Root, 0, MaximumRating, Math.Max(0, host.Rating - range), host.Rating - 1),
                Query(pool.Root, 0, MaximumRating, host.Rating + 1, Math.Min(MaximumRating, host.Rating + range)));
            // The next search at the same rating is necessarily the fairest same-rating peer.
            guest = Older(guest, pool.Ratings[host.Rating].Skip(1).FirstOrDefault());
            if (guest is null) continue;
            var pair = host.Order < guest.Order ? new Pair(host, guest) : new Pair(guest, host);
            Remove(pair.Host);
            Remove(pair.Guest);
            return pair;
        }
        return null;
    }

    private static Search Older(Search a, Search b) => a is null ? b : b is null || a.Order < b.Order ? a : b;
    private static RangeNode Set(RangeNode node, int lo, int hi, int rating, Search search)
    {
        if (node is null && search is null) return null;
        node ??= new();
        if (lo == hi) node.Oldest = search;
        else
        {
            int middle = (lo + hi) / 2;
            if (rating <= middle) node.Left = Set(node.Left, lo, middle, rating, search);
            else node.Right = Set(node.Right, middle + 1, hi, rating, search);
            node.Oldest = Older(node.Left?.Oldest, node.Right?.Oldest);
        }
        return node.Oldest is null ? null : node;
    }
    private static Search Query(RangeNode node, int lo, int hi, int from, int to)
    {
        if (node is null || from > hi || to < lo || from > to) return null;
        if (from <= lo && hi <= to) return node.Oldest;
        int middle = (lo + hi) / 2;
        return Older(Query(node.Left, lo, middle, from, to), Query(node.Right, middle + 1, hi, from, to));
    }
}
