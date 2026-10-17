using Blaze3SDK.Components;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blaze.Core;
using Blaze3SDK.Blaze.Authentication;
using EATDF;

namespace Impulsum14;

internal sealed class PlayerState
{
    public int SchemaVersion { get; set; } = 1;
    public FutProfile Profile { get; set; } = new();
    public ClubData Club { get; set; } = new();
    public AccountProfile Account { get; set; } = new();
    public Dictionary<string, string> ClientData { get; set; } = new();
    public Dictionary<string, string> UserSettings { get; set; } = new() { ["FirstTimeFlag"] = "0" };
    public PlayerMarketState Market { get; set; } = new();
    public string LastPackItemList { get; set; } = "";
    public string LastPurchaseResponseBody { get; set; } = "";
    public List<(long Id, string Json)> PendingPackItems { get; set; } = new();
    public List<(long NewId, long OwnedId)> PendingDuplicates { get; set; } = new();
    public long NextPackExtraId { get; set; } = 970000000;
    public int ActiveTournamentId { get; set; } = 1;
    public MatchSession Match { get; set; }
    public long NextMatchId { get; set; } = 1000;
    public string LastCompetition { get; set; } = "";
    public Dictionary<int, ManagerTaskProgress> ManagerTasks { get; set; } = new();
    public int[] NativeManagerQuest { get; set; } = new int[3];
    public bool ManagerCompletionClaimed { get; set; }
}

internal sealed class PlayerMarketState
{
    public ConcurrentDictionary<long, Market.BidTiming> BidTimings { get; set; } = new();
    public ConcurrentDictionary<long, long> MyBids { get; set; } = new();
    public ConcurrentDictionary<long, byte> RefundedBids { get; set; } = new();
    public ConcurrentDictionary<long, byte> Watched { get; set; } = new();
    public ConcurrentDictionary<long, Market.PendingOffer> AcceptedOffers { get; set; } = new();
    public ConcurrentDictionary<long, long> BoughtAt { get; set; } = new();
    public ConcurrentDictionary<long, long> CBoughtAt { get; set; } = new();
    public ConcurrentDictionary<long, long> EBoughtAt { get; set; } = new();
    public ConcurrentDictionary<long, long> FBoughtAt { get; set; } = new();
    public long HeldCoins;
}

internal sealed class AccountDefinition
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
}

internal sealed class PlayerAccount
{
    internal long Id { get; }
    internal string Name { get; }
    internal PlayerState Data { get; set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal object Sync { get; } = new();
    internal bool Dirty { get; set; }
    internal bool Initialized { get; set; }
    internal long Revision { get; set; }
    internal Dictionary<string, CachedHttpReply> HttpCache { get; } = new(StringComparer.Ordinal);
    internal int HttpCacheBytes { get; set; }
    internal string HttpCacheSession { get; set; } = "";
    internal long LastMarketMaintenanceSecond { get; set; } = -1;
    internal Exception PersistenceFailure { get; set; }
    internal byte[] Committed { get; set; }
    internal long LastMaintenance;
    private long _rateState;

    internal PlayerAccount(long id, string name, PlayerState data, byte[] committed = null)
    {
        Id = id; Name = name; Data = data;
        bool sameIdentity = Data.Profile.NucleusId == id && Data.Profile.PersonaName == name;
        Data.Profile.NucleusId = id; Data.Profile.PersonaName = name;
        Committed = sameIdentity && committed is not null ? committed : JsonSerializer.SerializeToUtf8Bytes(Data, PlayerRepository.JsonOptions);
    }

    internal bool AcceptRequest()
    {
        uint second = unchecked((uint)(Environment.TickCount64 / 1000));
        return AccountRequestLimit.TryAccept(ref _rateState, ServerOptions.Current.RequestsPerSecond, second);
    }
}

internal sealed record PlayerSession(string Key, PlayerAccount Account, DateTimeOffset Expires);

internal static class PlayerContext
{
    private static readonly AsyncLocal<PlayerSession> Ambient = new();
    internal static PlayerSession Session => Ambient.Value ?? throw new InvalidOperationException("Player operation outside an authenticated request.");
    internal static PlayerAccount Current => Session.Account;
    internal static void MarkDirty()
    {
        var player = Current;
        player.LastMarketMaintenanceSecond = -1;
        if (player.Dirty) return;
        player.Dirty = true;
        // Maintenance can settle a bid immediately before a GET. Invalidate before
        // building that response, rather than waiting for the durable commit.
        player.HttpCache.Clear(); player.HttpCacheBytes = 0;
    }
    internal static IDisposable Enter(PlayerSession session)
    {
        var previous = Ambient.Value;
        Ambient.Value = session;
        return new Scope(() => Ambient.Value = previous);
    }
    private sealed class Scope(Action exit) : IDisposable { public void Dispose() => exit(); }
}

/// <summary>Serializes each player's transactions, with independent gates across players.
/// Commits the entire wallet/club/progress/escrow as one atomic file replacement.</summary>
internal sealed class PlayerRepository : IDisposable
{
    internal static PlayerRepository Instance { get; private set; }
    internal static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly Dictionary<long, AccountDefinition> _definitions;
    private readonly Dictionary<AccountTokenHash, long> _tokens;
    private readonly ConcurrentDictionary<long, Lazy<PlayerAccount>> _players = new();
    private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PlayerSession> _accountSessions = new();
    private readonly FileStream _processLock;
    private readonly string _dataDirectory;
    private readonly string _playersDirectory;
    private readonly bool _localMode;
    private readonly object _lifecycle = new();
    private int _activeTransactions;
    private int _disposed;
    private int _loadedPlayerCount;
    private long _lastSessionPrune;
    private readonly PlayerAccount _anonymous = new(0, "anonymous", new());
    internal int LoadedPlayerCount => Volatile.Read(ref _loadedPlayerCount);
    internal IEnumerable<PlayerAccount> LoadedPlayers
    {
        get
        {
            // ConcurrentDictionary.Values copies every loaded entry. Maintenance
            // only needs an enumeration and need not allocate a snapshot each tick.
            foreach (var pair in _players)
                if (pair.Value.IsValueCreated) yield return pair.Value.Value;
        }
    }

    internal PlayerRepository()
    {
        var options = ServerOptions.Current;
        _dataDirectory = Path.GetFullPath(options.DataDirectory);
        _playersDirectory = Path.Combine(_dataDirectory, "players");
        _localMode = options.LocalMode;
        Directory.CreateDirectory(_dataDirectory);
        _processLock = new FileStream(Path.Combine(_dataDirectory, ".server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Directory.CreateDirectory(_playersDirectory);
            var definitions = options.LocalMode
                ? new[] { new AccountDefinition { Id = 1000, Name = UserConfig.Username } }
                : JsonSerializer.Deserialize<AccountDefinition[]>(File.ReadAllText(options.AccountsFile), JsonOptions) ?? Array.Empty<AccountDefinition>();
            if (definitions.Length == 0 || definitions.Length > options.MaxPlayers || definitions.Any(d => d is null || d.Id < 1 || d.Name is null || d.Name.Length is < 1 or > 32 || d.Name.Any(char.IsControl)) || definitions.Select(d => d.Id).Distinct().Count() != definitions.Length)
                throw new InvalidDataException("Invalid or duplicate account definitions, or MaxPlayers exceeded.");
            _definitions = definitions.ToDictionary(d => d.Id);
            _tokens = new();
            if (!options.LocalMode)
                foreach (var d in definitions)
                {
                    if (d.TokenSha256 is null || d.TokenSha256.Length != 64 || !d.TokenSha256.All(Uri.IsHexDigit) || !_tokens.TryAdd(AccountTokenHash.FromHex(d.TokenSha256), d.Id)) throw new InvalidDataException("Accounts need unique SHA-256 token hashes (64 hex characters).");
                }
            Instance = this;
        }
        catch { _processLock.Dispose(); throw; }
    }

    internal PlayerAccount Get(long id)
    {
        ThrowIfDisposed();
        if (_players.TryGetValue(id, out var existing)) return existing.Value;
        if (!_definitions.TryGetValue(id, out var definition)) throw new UnauthorizedAccessException("Unknown account.");
        return _players.GetOrAdd(id, _ => new Lazy<PlayerAccount>(() => Load(definition), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal PlayerSession Authenticate(string token)
    {
        ThrowIfDisposed();
        if (_localMode) return SessionFor(Get(1000));
        if (string.IsNullOrEmpty(token) || token.Length > 4096) throw new UnauthorizedAccessException("A private-server access token is required.");
        if (_sessions.TryGetValue(token, out var existing) && existing.Expires > DateTimeOffset.UtcNow) return existing;
        var digest = AccountTokenHash.FromToken(token);
        if (!_tokens.TryGetValue(digest, out long id)) throw new UnauthorizedAccessException("Invalid or expired server credential.");
        return SessionFor(Get(id));
    }

    private PlayerSession SessionFor(PlayerAccount account)
    {
        PlayerSession session, previous;
        lock (account.Sync)
        {
            if (_accountSessions.TryGetValue(account.Id, out var existing) && existing.Expires > DateTimeOffset.UtcNow) return existing;
            session = CreateSession(account, out previous);
        }
        if (previous is not null) GameManagerComponent.Instance?.SessionRevoked(previous);
        return session;
    }

    internal PlayerSession NewSession(PlayerAccount account)
    {
        ThrowIfDisposed();
        PlayerSession session, previous;
        lock (account.Sync)
        {
            session = CreateSession(account, out previous);
        }
        if (previous is not null) GameManagerComponent.Instance?.SessionRevoked(previous);
        return session;
    }

    // Caller holds account.Sync. Notifications are deliberately made by callers
    // after releasing it, since matchmaking has its own synchronization order.
    private PlayerSession CreateSession(PlayerAccount account, out PlayerSession previous)
    {
        if (_accountSessions.TryRemove(account.Id, out previous)) _sessions.TryRemove(previous.Key, out _);
        PlayerSession session;
        do
        {
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            session = new PlayerSession(key, account, DateTimeOffset.UtcNow.AddMinutes(ServerOptions.Current.SessionLifetimeMinutes));
        } while (!_sessions.TryAdd(session.Key, session));
        _accountSessions[account.Id] = session;
        return session;
    }

    internal PlayerSession ResolveWeb(System.Collections.Specialized.NameValueCollection headers)
    {
        string bearer = headers["Authorization"];
        string token = bearer?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? bearer[7..] : null;
        token ??= headers["X-UT-SID"] ?? headers["X-POW-SID"] ?? headers["EASW-Session"] ?? headers["sid"];
        if (!string.IsNullOrEmpty(token) && _sessions.TryGetValue(token, out var session) && session.Expires > DateTimeOffset.UtcNow) return session;
        if (!_localMode) return Authenticate(token);
        return SessionFor(Get(1000));
    }

    internal bool IsSessionCurrent(PlayerSession session) =>
        Volatile.Read(ref _disposed) == 0 && session.Expires > DateTimeOffset.UtcNow &&
        _sessions.TryGetValue(session.Key, out var current) && ReferenceEquals(current, session);

    internal void PruneSessions()
    {
        long tick = Environment.TickCount64;
        long previous = Volatile.Read(ref _lastSessionPrune);
        if (tick - previous < 1000 || Interlocked.CompareExchange(ref _lastSessionPrune, tick, previous) != previous) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _accountSessions)
        {
            if (pair.Value.Expires > now) continue;
            lock (pair.Value.Account.Sync)
            {
                if (!_accountSessions.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value)) continue;
                _accountSessions.TryRemove(pair.Key, out _);
                _sessions.TryRemove(pair.Value.Key, out _);
            }
            GameManagerComponent.Instance?.SessionRevoked(pair.Value);
        }
    }
    internal void Revoke(PlayerSession session)
    {
        PlayerSession removed;
        lock (session.Account.Sync)
        {
            _sessions.TryRemove(session.Key, out removed);
            if (_accountSessions.TryGetValue(session.Account.Id, out var current) && current.Key == session.Key) _accountSessions.TryRemove(session.Account.Id, out _);
        }
        if (removed is not null) GameManagerComponent.Instance?.SessionRevoked(removed);
    }

    internal async Task<T> RunAsync<T>(PlayerSession session, Func<Task<T>> action, CancellationToken cancel = default)
    {
        var player = session.Account;
        BeginTransaction();
        try
        {
            await player.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                cancel.ThrowIfCancellationRequested();
                // A request may have queued before logout, expiry, or session rotation.
                if (session.Key.Length != 0 && !IsSessionCurrent(session)) throw new UnauthorizedAccessException("Invalid or expired session.");
                return await ExecuteAsync(session, action).ConfigureAwait(false);
            }
            finally { player.Gate.Release(); }
        }
        finally { EndTransaction(); }
    }

    internal async Task<bool> TryRunMaintenanceAsync(PlayerAccount player, Func<Task> action, CancellationToken cancel = default)
    {
        BeginTransaction();
        try
        {
            cancel.ThrowIfCancellationRequested();
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref player.LastMaintenance) < ServerOptions.Current.MaintenanceIntervalMs || !player.Gate.Wait(0)) return false;
            try
            {
                if (now - player.LastMaintenance < ServerOptions.Current.MaintenanceIntervalMs) return false;
                player.LastMaintenance = now;
                await ExecuteAsync(new PlayerSession("", player, DateTimeOffset.MaxValue), async () =>
                {
                    await action().ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
                return true;
            }
            finally { player.Gate.Release(); }
        }
        finally { EndTransaction(); }
    }

    private async Task<T> ExecuteAsync<T>(PlayerSession session, Func<Task<T>> action)
    {
        var player = session.Account;
        if (player.PersistenceFailure is not null) throw new IOException("Account requires restart after an uncertain durable commit.", player.PersistenceFailure);
        using var scope = PlayerContext.Enter(session);
        bool initializedBefore = player.Initialized;
        try
        {
            if (!player.Initialized && player.Id > 0)
            {
                // Some migrations change flags without calling a store's Save.
                // Include initialization in the same rollback and commit as action.
                PlayerContext.MarkDirty();
                ClubStore.Initialize();
                player.Initialized = true;
            }
            if (player.Id > 0) RankedProjection.ApplyCurrent();
            var result = await action().ConfigureAwait(false);
            if (player.Dirty && player.Id > 0) await CommitAsync(player).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (player.Dirty) player.Data = JsonSerializer.Deserialize<PlayerState>(player.Committed, JsonOptions)!;
            player.Dirty = false;
            player.LastMarketMaintenanceSecond = -1;
            player.HttpCache.Clear(); player.HttpCacheBytes = 0;
            player.Initialized = initializedBefore;
            throw;
        }
    }

    internal async Task<Tdf> RunRpcAsync(BlazeRpcContext context, Tdf request, Func<Task<Tdf>> invoke)
    {
        var connection = (BlazeRpcConnection)context.Connection;
        string token = request switch { OriginLoginRequest r => r.AuthToken, SilentLoginRequest r => r.AuthToken, LoginRequest r => string.IsNullOrEmpty(r.Token) ? r.Password : r.Token, ExpressLoginRequest r => r.Password, _ => null };
        var session = connection.State as PlayerSession;
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (token is not null)
            {
                var authenticated = Authenticate(token);
                if (session is not null && (session.Account.Id != authenticated.Account.Id || session.Key != authenticated.Key)) GameManagerComponent.Instance?.Disconnected(connection);
                session = authenticated;
                connection.State = session;
            }
            if (session is not null && !IsSessionCurrent(session))
            {
                GameManagerComponent.Instance?.Disconnected(connection);
                connection.State = null;
                throw new UnauthorizedAccessException();
            }
            session ??= _localMode ? ResolveWeb(new()) : new PlayerSession("", _anonymous, DateTimeOffset.MaxValue);
            if (session.Account.Id > 0) connection.State = session;
            if (session.Account.Id == 0 && context.ComponentId is not (1 or 5 or 9)) throw new UnauthorizedAccessException();
            if (!session.Account.AcceptRequest()) throw new BlazeRpcException(Blaze3SDK.Components.ServerError.ERR_TIMEOUT.WithErrorPrefix(), null, "Rate limit exceeded.");
            return await RunAsync(session, invoke, context.CancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { throw new BlazeRpcException(Blaze3SDK.Components.ServerError.ERR_AUTHENTICATION_REQUIRED.WithErrorPrefix(), null, "Authentication required."); }
    }

    private PlayerAccount Load(AccountDefinition definition)
    {
        string path = StatePath(definition.Id);
        PlayerState state;
        byte[] committed = null;
        try { committed = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { }
        // File.Exists also returns false for inaccessible paths and directories.
        // Only an absent canonical file may initialize a fresh account.
        if (committed is not null)
        {
            state = JsonSerializer.Deserialize<PlayerState>(committed, JsonOptions) ?? throw new InvalidDataException($"Empty player state: {definition.Id}");
        }
        else
        {
            state = new();
            // Only the default local account imports legacy profiles; originals remain untouched.
            if (_localMode && definition.Id == 1000)
            {
                T Import<T>(string name, T fallback)
                {
                    try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(_dataDirectory, name)), JsonOptions) ?? throw new InvalidDataException($"Invalid legacy file: {name}"); }
                    catch (FileNotFoundException) { return fallback; }
                }
                state.Profile = Import("fut_profile.json", state.Profile);
                state.Club = Import("club_data.json", state.Club);
                state.Account = Import("account.json", state.Account);
                state.ClientData = Import("clientdata.json", state.ClientData);
            }
        }
        ValidateState(state);
        var account = new PlayerAccount(definition.Id, definition.Name, state, committed);
        Interlocked.Increment(ref _loadedPlayerCount);
        return account;
    }

    private string StatePath(long id) => Path.Combine(_playersDirectory, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private static void ValidateState(PlayerState state)
    {
        if (state.SchemaVersion != 1) throw new InvalidDataException("Unsupported player state version.");
        if (state.Profile is null || state.Club is null || state.Account is null || state.Market is null || state.ClientData is null || state.UserSettings is null || state.ManagerTasks is null || state.NativeManagerQuest is null || state.PendingPackItems is null || state.PendingDuplicates is null)
            throw new InvalidDataException("Incomplete player state.");
        if (state.Profile.Coins < 0 || state.Market.HeldCoins < 0) throw new InvalidDataException("Wallet invariant failed.");
    }

    private async Task CommitAsync(PlayerAccount player)
    {
        long started = Stopwatch.GetTimestamp();
        ValidateState(player.Data);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(player.Data, JsonOptions);
        if (bytes.AsSpan().SequenceEqual(player.Committed)) { player.Dirty = false; return; }
        string path = StatePath(player.Id);
        string temporary = path + ".tmp";
        bool published = false;
        try
        {
            // The full snapshot is already a contiguous byte array; an additional
            // per-write FileStream buffer only duplicates it for small accounts.
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            DurableFile.Replace(temporary, path, _playersDirectory, ref published);
            player.Committed = bytes;
            player.Revision++;
            player.HttpCache.Clear(); player.HttpCacheBytes = 0;
            player.Dirty = false;
            ServerMetrics.RecordDurableCommit(bytes.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // A successful rename followed by failed directory persistence has an
            // uncertain durable outcome. Never serve the rolled-back wallet again
            // in this process; recovery must reload the canonical snapshot.
            if (published) player.PersistenceFailure = ex;
            ServerMetrics.RecordCommitFailure(); throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void BeginTransaction()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _activeTransactions);
        if (Volatile.Read(ref _disposed) != 0)
        {
            EndTransaction();
            ThrowIfDisposed();
        }
    }

    private void EndTransaction()
    {
        if (Interlocked.Decrement(ref _activeTransactions) == 0 && Volatile.Read(ref _disposed) == 1)
            lock (_lifecycle) Monitor.PulseAll(_lifecycle);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed != 0)
            {
                while (_disposed != 2) Monitor.Wait(_lifecycle);
                return;
            }
            Volatile.Write(ref _disposed, 1);
            // Retain exclusive ownership until in-flight writes have finished.
            // Releasing it first would permit a second store to overwrite them.
            while (Volatile.Read(ref _activeTransactions) != 0) Monitor.Wait(_lifecycle);
            _processLock.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
            Volatile.Write(ref _disposed, 2);
            Monitor.PulseAll(_lifecycle);
        }
    }
}
