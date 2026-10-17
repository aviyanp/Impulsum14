using Blaze.Core;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.GameManager;
using Blaze3SDK.Components;
using EATDF.Types;
using EATDF.Serialization;
using static Blaze3SDK.Components.GameManagerBase;
using GamePlayerState = Blaze3SDK.Blaze.GameManager.PlayerState;

namespace Impulsum14;

/// <summary>Opt-in two-player peer lobbies. Carries addresses and setup notifications,
/// never game simulation. Retail gameplay interoperability remains experimental.</summary>
internal sealed class GameManagerComponent : GameManagerBase.Server
{
    internal static GameManagerComponent Instance { get; private set; }
    private sealed record Identity(long Id, string Name, string SessionKey, DateTimeOffset Expires, BlazeRpcConnection Connection);
    private sealed record Member(Identity Identity, ReplicatedGamePlayer Player);
    private sealed record SearchData(Identity Identity, string Name, string Protocol, Dictionary<string, string> GameAttributes,
        Dictionary<string, string> PlayerAttributes, Dictionary<string, string> EntryCriteria, NetworkAddress Network, GameSettings Settings);
    private sealed class Lobby
    {
        internal required ReplicatedGameData Data;
        internal readonly Dictionary<long, Member> Members = new();
        internal long RankedMatchId;
    }
    private sealed class PendingMatch
    {
        internal required MatchmakingQueue<SearchData>.Pair Pair;
        internal required uint GameId;
        internal readonly Dictionary<long, MatchmakingResult> Failures = new();
    }
    private readonly object _sync = new();
    private readonly RankedService _ranked;
    private readonly Dictionary<uint, Lobby> _games = new();
    private readonly Dictionary<long, uint> _membership = new();
    private readonly Dictionary<Guid, long> _memberConnections = new();
    private readonly SortedSet<Member> _memberExpiry = new(Comparer<Member>.Create((a, b) =>
    {
        int time = a.Identity.Expires.CompareTo(b.Identity.Expires);
        return time != 0 ? time : a.Identity.Connection.Id.CompareTo(b.Identity.Connection.Id);
    }));
    private readonly MatchmakingQueue<SearchData> _waiting = new();
    private readonly Dictionary<long, PendingMatch> _pendingAccounts = new();
    private readonly Dictionary<Guid, PendingMatch> _pendingConnections = new();
    private readonly HashSet<long> _abandon = new();
    private readonly Dictionary<long, long> _forfeits = new();
    private bool _cleanupRunning;
    private static int MaxConcurrentSetups => ServerOptions.Current.MatchmakingConcurrentSetups;
    private uint _sequence;

    internal GameManagerComponent(RankedService ranked = null) { _ranked = ranked ?? RankedService.Current; Instance = this; }
    private static void Enabled()
    {
        if (!ServerOptions.Current.EnablePeerLobbies) throw new GameManagerException(ServerError.ERR_COMMAND_NOT_FOUND);
    }
    private static Identity Caller(BlazeRpcContext context)
    {
        var connection = (BlazeRpcConnection)context.Connection;
        var session = PlayerContext.Session;
        if (session.Account.Id <= 0 || session.Expires <= DateTimeOffset.UtcNow ||
            connection.State is not PlayerSession actual || actual.Account.Id != session.Account.Id || actual.Key != session.Key || !PlayerRepository.Instance.IsSessionCurrent(actual))
            throw new GameManagerException(ServerError.ERR_AUTHENTICATION_REQUIRED);
        return new(session.Account.Id, session.Account.Name, session.Key, session.Expires, connection);
    }
    private static bool Live(Identity identity, DateTimeOffset now) => identity.Connection.Connected && identity.Expires > now &&
        identity.Connection.State is PlayerSession current && current.Account.Id == identity.Id && current.Key == identity.SessionKey && PlayerRepository.Instance.IsSessionCurrent(current);
    private static bool Owns(Identity owner, Identity caller) => owner.Id == caller.Id &&
        owner.Connection.Id == caller.Connection.Id && owner.SessionKey == caller.SessionKey;
    private Lobby Find(uint id, Identity caller, bool host = false)
    {
        Enabled();
        if (!_games.TryGetValue(id, out var lobby)) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_ID);
        if (!lobby.Members.TryGetValue(caller.Id, out var member) || !Owns(member.Identity, caller) ||
            (host && lobby.Data.TopologyHostInfo?.PlayerId != caller.Id))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_PERMISSION_DENIED);
        return lobby;
    }
    private void Available(Identity identity)
    {
        if (_membership.ContainsKey(identity.Id) || _memberConnections.ContainsKey(identity.Connection.Id) ||
            _pendingAccounts.ContainsKey(identity.Id) || _pendingConnections.ContainsKey(identity.Connection.Id))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_ALREADY_GAME_MEMBER);
        if (_waiting.TryGet(identity.Id, out _) || _waiting.TryGet(identity.Connection.Id, out _))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_ALREADY_IN_QUEUE);
    }
    private uint NextId()
    {
        // Never wrap into a live or previously issued ID; late cancellations remain harmless.
        if (_sequence == uint.MaxValue) throw new GameManagerException(ServerError.ERR_SYSTEM);
        return ++_sequence;
    }
    private static Dictionary<string, string> Attributes(IDictionary<string, string> source)
    {
        if (source is null || source.Count > 32 || source.Any(p => p.Key is null || p.Value is null || p.Key.Length > 64 || p.Value.Length > 256))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
        return new(source, StringComparer.Ordinal);
    }
    private static void Text(string value, int maximum)
    {
        if (value is null || value.Length > maximum) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
    }
    private static NetworkAddress Address(NetworkAddress source)
    {
        if (source is null) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
        static IpAddress Copy(IpAddress ip) => ip is null ? null : new() { Ip = ip.Ip, Port = ip.Port };
        // Copy the active union member only; never retain a mutable request object.
        switch (source.ActiveIndex)
        {
            case 0 when source.XboxClientAddress is { } xbox:
                if (xbox.XnAddr.Length > 256) break;
                return new() { XboxClientAddress = new() { XnAddr = xbox.XnAddr.ToArray(), Xuid = xbox.Xuid } };
            case 1 when source.XboxServerAddress is { } server:
                Text(server.SiteName, 64);
                return new() { XboxServerAddress = new() { Port = server.Port, SiteName = server.SiteName, Sid = server.Sid } };
            case 2 when source.IpPairAddress is { } pair:
                return new() { IpPairAddress = new() { ExternalAddress = Copy(pair.ExternalAddress), InternalAddress = Copy(pair.InternalAddress) } };
            case 3 when source.IpAddress is { } ip:
                return new() { IpAddress = Copy(ip) };
            case 4 when source.HostNameAddress is { } name:
                Text(name.HostName, 253);
                return new() { HostNameAddress = new() { HostName = name.HostName, Port = name.Port } };
            case 127: return new(); // SDK's unset union: allowed for local protocol tests.
        }
        throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
    }
    private static void Topology(GameNetworkTopology topology)
    {
        if (topology is not (GameNetworkTopology.PEER_TO_PEER_FULL_MESH or GameNetworkTopology.CLIENT_SERVER_PEER_HOSTED))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
    }
    private static ulong ProtocolHash(string protocol)
    {
        // FNV-1 used by the local Blaze reference, including its 64-bit overflow behavior.
        ulong hash = 2166136261;
        foreach (byte value in System.Text.Encoding.UTF8.GetBytes(protocol)) hash = unchecked(hash * 16777619) ^ value;
        return hash;
    }
    private Lobby NewLobby(uint id, Identity host, string name, string protocol, Dictionary<string, string> attributes,
        Dictionary<string, string> entryCriteria, GameSettings settings, IList<NetworkAddress> networks, string type = "", PresenceMode presence = default)
    {
        var hostInfo = new HostInfo { PlayerId = host.Id, SlotId = 0 };
        var lobby = new Lobby { Data = new ReplicatedGameData
        {
            GameId = id, GameReportingId = id, GameName = name, GameTypeName = type, GameProtocolVersionString = protocol,
            GameProtocolVersionHash = ProtocolHash(protocol), GameState = GameState.INITIALIZING, GameAttribs = attributes, GameSettings = settings, EntryCriteriaMap = entryCriteria,
            AdminPlayerList = new List<long> { host.Id }, MaxPlayerCapacity = 2, SlotCapacities = new List<ushort> { 2, 0 },
            TopologyHostInfo = hostInfo, PlatformHostInfo = new HostInfo { PlayerId = host.Id, SlotId = 0 },
            TopologyHostSessionId = unchecked((uint)host.Id), NetworkTopology = GameNetworkTopology.PEER_TO_PEER_FULL_MESH,
            HostNetworkAddressList = networks, TeamCapacity = 1, TeamIds = new List<ushort> { 0, 1 }, PresenceMode = presence,
            VoipNetwork = VoipTopology.VOIP_DISABLED, UUID = Guid.NewGuid().ToString(), SharedSeed = (uint)Random.Shared.Next(), PingSiteAlias = "local"
        } };
        _games.Add(id, lobby);
        return lobby;
    }
    private Member Add(Lobby lobby, Identity identity, NetworkAddress network, Dictionary<string, string> attributes)
    {
        if (_membership.ContainsKey(identity.Id) || _memberConnections.ContainsKey(identity.Connection.Id))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_ALREADY_GAME_MEMBER);
        if (lobby.Members.Count >= 2) throw new GameManagerException(Error.GAMEMANAGER_ERR_GAME_FULL);
        byte slot = lobby.Members.Values.Any(m => m.Player.SlotId == 0) ? (byte)1 : (byte)0;
        var player = new ReplicatedGamePlayer
        {
            GameId = lobby.Data.GameId, PlayerId = identity.Id, PlayerName = identity.Name, AccountLocale = 1701729619,
            PlayerAttribs = attributes, NetworkAddress = network, SlotId = slot, TeamIndex = slot, SlotType = SlotType.SLOT_PUBLIC,
            PlayerState = GamePlayerState.ACTIVE_CONNECTING, JoinedGameTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var member = new Member(identity, player);
        lobby.Members.Add(identity.Id, member);
        _membership.Add(identity.Id, lobby.Data.GameId);
        _memberConnections.Add(identity.Connection.Id, identity.Id);
        _memberExpiry.Add(member);
        return member;
    }
    private static void Setup(Lobby lobby, Member member, uint matchmakingId = 0, List<ReplicatedGamePlayer> roster = null)
    {
        var reason = matchmakingId == 0
            ? new GameSetupReason { DatalessSetupContext = new DatalessSetupContext { SetupContext = lobby.Data.TopologyHostInfo?.PlayerId == member.Identity.Id ? DatalessContext.CREATE_GAME_SETUP_CONTEXT : DatalessContext.JOIN_GAME_SETUP_CONTEXT } }
            : new GameSetupReason { MatchmakingSetupContext = new MatchmakingSetupContext { SessionId = matchmakingId, MatchmakingResult = lobby.Data.TopologyHostInfo?.PlayerId == member.Identity.Id ? MatchmakingResult.SUCCESS_CREATED_GAME : MatchmakingResult.SUCCESS_JOINED_NEW_GAME, FitScore = 1, MaxPossibleFitScore = 1 } };
        // Default SDK enqueue serializes now, then sends after this connection's current RPC reply.
        NotifyNotifyGameSetupAsync(member.Identity.Connection, new NotifyGameSetup { GameData = lobby.Data,
            GameRoster = roster ?? lobby.Members.Values.Select(m => m.Player).OrderBy(p => p.SlotId).ToList(),
            GameQueue = new List<ReplicatedGamePlayer>(), GameSetupReason = reason });
    }
    public override Task<CreateGameResponse> CreateGameAsync(CreateGameRequest request, BlazeRpcContext context)
    {
        Enabled();
        var caller = Caller(context);
        Text(request.GameName, 64); Text(request.GameProtocolVersionString, 128); Text(request.GameTypeName, 64);
        Text(request.GameStatusUrl, 256); Text(request.GamePingSiteAlias, 64); Text(request.PersistedGameId, 128);
        Topology(request.NetworkTopology);
        if (request.MaxPlayerCapacity > 2 || request.HostNetworkAddressList.Count > 2 || request.SlotCapacities.Count > 2 ||
            request.SlotCapacities.Sum(x => (int)x) > 2 || request.TeamIds.Count > 2 || request.AdminPlayerList.Count > 2 ||
            request.ReservedPlayerSeats.Count > 2 || request.SessionIdList.Count > 2 || request.QueueCapacity != 0 || request.PersistedGameIdSecret.Length > 256)
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
        var attributes = Attributes(request.GameAttribs); var playerAttributes = Attributes(request.HostPlayerAttribs);
        var criteria = Attributes(request.EntryCriteriaMap); _ = Attributes(request.MeshAttribs);
        var networks = request.HostNetworkAddressList.Select(Address).ToList();
        lock (_sync)
        {
            Available(caller);
            if (_membership.Count + _pendingAccounts.Count >= ServerOptions.Current.MaxPlayers) throw new GameManagerException(Error.GAMEMANAGER_ERR_GAME_FULL);
            uint id = NextId();
            var lobby = NewLobby(id, caller, request.GameName, request.GameProtocolVersionString, attributes, criteria, request.GameSettings, networks, request.GameTypeName, request.PresenceMode);
            var host = Add(lobby, caller, networks.FirstOrDefault() ?? new NetworkAddress(), playerAttributes);
            Setup(lobby, host);
            return Task.FromResult(new CreateGameResponse { GameId = id });
        }
    }
    public override Task<JoinGameResponse> JoinGameAsync(JoinGameRequest request, BlazeRpcContext context)
    {
        Enabled(); var caller = Caller(context);
        Text(request.GameProtocolVersionString, 128);
        if (request.AdditionalPlayerIdList.Count > 0 || (request.User is { } user && user.BlazeId != 0 && user.BlazeId != caller.Id))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_PLAYER_PASSEDIN);
        var attributes = Attributes(request.PlayerAttribs); var address = Address(request.PlayerNetworkAddress);
        lock (_sync)
        {
            Available(caller);
            if (!_games.TryGetValue(request.GameId, out var lobby)) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_ID);
            if (lobby.Data.GameProtocolVersionString != request.GameProtocolVersionString) throw new GameManagerException(Error.GAMEMANAGER_ERR_GAME_PROTOCOL_VERSION_MISMATCH);
            if (lobby.RankedMatchId != 0 || lobby.Data.GameState is not (GameState.INITIALIZING or GameState.PRE_GAME))
                throw new GameManagerException(Error.GAMEMANAGER_ERR_GAME_IN_PROGRESS);
            if (_membership.Count + _pendingAccounts.Count >= ServerOptions.Current.MaxPlayers) throw new GameManagerException(Error.GAMEMANAGER_ERR_GAME_FULL);
            var existing = lobby.Members.Values.ToArray();
            var joined = Add(lobby, caller, address, attributes);
            Setup(lobby, joined);
            foreach (var member in existing) NotifyNotifyPlayerJoiningAsync(member.Identity.Connection, new NotifyPlayerJoining { GameId = request.GameId, JoiningPlayer = joined.Player });
            return Task.FromResult(new JoinGameResponse { GameId = request.GameId, JoinState = JoinState.JOINED_GAME });
        }
    }
    public override Task<EmptyMessage> AdvanceGameStateAsync(AdvanceGameStateRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller, host: true);
            bool valid = lobby.Data.GameState == request.NewGameState || (lobby.Data.GameState, request.NewGameState) is
                (GameState.INITIALIZING, GameState.PRE_GAME) or (GameState.PRE_GAME, GameState.IN_GAME) or (GameState.IN_GAME, GameState.POST_GAME) ||
                (lobby.RankedMatchId == 0 && lobby.Data.GameState == GameState.POST_GAME && request.NewGameState == GameState.PRE_GAME);
            if (!valid || (request.NewGameState == GameState.IN_GAME && (lobby.Members.Count != 2 || lobby.Members.Values.Any(m => m.Player.PlayerState != GamePlayerState.ACTIVE_CONNECTED))))
                throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_STATE_TRANSITION);
            if (lobby.Data.GameState == request.NewGameState) return Task.FromResult(new EmptyMessage());
            lobby.Data.GameState = request.NewGameState;
            foreach (var member in lobby.Members.Values.ToArray()) NotifyNotifyGameStateChangeAsync(member.Identity.Connection, new NotifyGameStateChange { GameId = request.GameId, NewGameState = request.NewGameState });
            return Task.FromResult(new EmptyMessage());
        }
    }
    public override Task<EmptyMessage> FinalizeGameCreationAsync(UpdateGameSessionRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        if (request.XnetNonce.Length > 256 || request.XnetSession.Length > 1024) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller, host: true);
            lobby.Data.XnetNonce = request.XnetNonce.ToArray(); lobby.Data.XnetSession = request.XnetSession.ToArray();
            foreach (var member in lobby.Members.Values.ToArray())
            {
                NotifyNotifyGameSessionUpdatedAsync(member.Identity.Connection, new GameSessionUpdatedNotification { GameId = request.GameId, XnetNonce = lobby.Data.XnetNonce, XnetSession = lobby.Data.XnetSession });
                NotifyNotifyGameReportingIdChangeAsync(member.Identity.Connection, new NotifyGameReportingIdChange { GameId = request.GameId, GameReportingId = lobby.Data.GameReportingId });
            }
            return Task.FromResult(new EmptyMessage());
        }
    }
    public override Task<EmptyMessage> UpdateMeshConnectionAsync(UpdateMeshConnectionRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller);
            if (request.MeshConnectionStatusList.Count > 2 || request.MeshConnectionStatusList.Select(s => s.TargetPlayer).Distinct().Count() != request.MeshConnectionStatusList.Count ||
                request.MeshConnectionStatusList.Any(s => s.TargetPlayer == caller.Id || !lobby.Members.ContainsKey(s.TargetPlayer) || !Enum.IsDefined(s.PlayerNetConnectionStatus)))
                throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_PLAYER_PASSEDIN);
            var self = lobby.Members[caller.Id];
            if (request.MeshConnectionStatusList.Count != 0)
            {
                // The reporter controls only its own observation. A peer cannot complete or remove its opponent.
                bool connected = lobby.Members.Count == 2 && request.MeshConnectionStatusList[0].PlayerNetConnectionStatus == PlayerNetConnectionStatus.CONNECTED;
                var state = connected ? GamePlayerState.ACTIVE_CONNECTED : GamePlayerState.ACTIVE_CONNECTING;
                if (self.Player.PlayerState != state)
                {
                    self.Player.PlayerState = state;
                    foreach (var member in lobby.Members.Values.ToArray())
                    {
                        NotifyNotifyGamePlayerStateChangeAsync(member.Identity.Connection, new NotifyGamePlayerStateChange { GameId = request.GameId, PlayerId = caller.Id, PlayerState = state });
                        if (connected) NotifyNotifyPlayerJoinCompletedAsync(member.Identity.Connection, new NotifyPlayerJoinCompleted { GameId = request.GameId, PlayerId = caller.Id });
                    }
                }
            }
            return Task.FromResult(new EmptyMessage());
        }
    }
    public override Task<EmptyMessage> SetGameAttributesAsync(SetGameAttributesRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context); var attributes = Attributes(request.GameAttributes);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller, host: true);
            if (attributes.Keys.Union(lobby.Data.GameAttribs.Keys).Count() > 32) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
            foreach (var pair in attributes) lobby.Data.GameAttribs[pair.Key] = pair.Value;
            foreach (var member in lobby.Members.Values.ToArray()) NotifyNotifyGameAttribChangeAsync(member.Identity.Connection, new NotifyGameAttribChange { GameId = request.GameId, GameAttribs = attributes });
            return Task.FromResult(new EmptyMessage());
        }
    }
    public override Task<EmptyMessage> SetPlayerAttributesAsync(SetPlayerAttributesRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context); var attributes = Attributes(request.PlayerAttributes);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller);
            if (request.PlayerId != caller.Id) throw new GameManagerException(Error.GAMEMANAGER_ERR_PERMISSION_DENIED);
            var player = lobby.Members[caller.Id].Player;
            if (attributes.Keys.Union(player.PlayerAttribs.Keys).Count() > 32) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
            foreach (var pair in attributes) player.PlayerAttribs[pair.Key] = pair.Value;
            foreach (var member in lobby.Members.Values.ToArray()) NotifyNotifyPlayerAttribChangeAsync(member.Identity.Connection, new NotifyPlayerAttribChange { GameId = request.GameId, PlayerId = caller.Id, PlayerAttribs = attributes });
            return Task.FromResult(new EmptyMessage());
        }
    }
    public override Task<GetFullGameDataResponse> GetFullGameDataAsync(GetFullGameDataRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        if (request.GameIdList.Count > 32) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_GAME_SETTINGS);
        lock (_sync)
        {
            Enabled();
            var games = request.GameIdList.Distinct().Select(id => Find(id, caller)).Select(l => new ListGameData { Game = l.Data, GameRoster = l.Members.Values.Select(m => m.Player).ToList() }).ToList();
            var response = new GetFullGameDataResponse { Games = games };
            // Responses serialize after leaving this lock; return an independent TDF snapshot.
            var serializer = new Heat2Serializer();
            var frozen = new GetFullGameDataResponse();
            if (!serializer.Deserialize(new MemoryStream(serializer.Serialize(response)), frozen)) throw new InvalidOperationException("Could not snapshot lobby.");
            return Task.FromResult(frozen);
        }
    }
    public override async Task<EmptyMessage> RemovePlayerAsync(RemovePlayerRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        lock (_sync)
        {
            var lobby = Find(request.GameId, caller);
            if (request.PlayerId != caller.Id && lobby.Data.TopologyHostInfo?.PlayerId != caller.Id) throw new GameManagerException(Error.GAMEMANAGER_ERR_PERMISSION_DENIED);
            if (!lobby.Members.ContainsKey(request.PlayerId)) throw new GameManagerException(Error.GAMEMANAGER_ERR_PLAYER_NOT_FOUND);
            if (lobby.RankedMatchId != 0 && lobby.Data.GameState == GameState.IN_GAME && request.PlayerId != caller.Id)
                throw new GameManagerException(Error.GAMEMANAGER_ERR_PERMISSION_DENIED);
            Remove(lobby, request.PlayerId, request.PlayerRemovedReason, request.PlayerId == caller.Id);
        }
        await FlushAbandonedAsync().ConfigureAwait(false);
        return new();
    }
    public override async Task<DestroyGameResponse> DestroyGameAsync(DestroyGameRequest request, BlazeRpcContext context)
    {
        var caller = Caller(context);
        lock (_sync) Destroy(Find(request.GameId, caller, host: true), request.DestructionReason, caller.Id);
        await FlushAbandonedAsync().ConfigureAwait(false);
        return new() { GameId = request.GameId };
    }
    private void Remove(Lobby lobby, long user, PlayerRemovedReason reason, bool selfDeparture = false)
    {
        if (lobby.Data.TopologyHostInfo?.PlayerId == user || lobby.RankedMatchId != 0 || lobby.Data.GameState == GameState.IN_GAME)
        { Destroy(lobby, GameDestructionReason.HOST_LEAVING, selfDeparture ? user : null); return; }
        var removed = lobby.Members[user];
        var recipients = lobby.Members.Values.ToArray();
        lobby.Members.Remove(user); _membership.Remove(user); _memberConnections.Remove(removed.Identity.Connection.Id); _memberExpiry.Remove(removed);
        // A replacement peer must establish a fresh observation in both directions.
        foreach (var member in lobby.Members.Values) member.Player.PlayerState = GamePlayerState.ACTIVE_CONNECTING;
        foreach (var member in recipients) NotifyNotifyPlayerRemovedAsync(member.Identity.Connection, new NotifyPlayerRemoved { GameId = lobby.Data.GameId, PlayerId = user, PlayerRemovedReason = reason });
    }
    private void Destroy(Lobby lobby, GameDestructionReason reason, long? forfeitedBy = null)
    {
        var members = lobby.Members.Values.ToArray();
        _games.Remove(lobby.Data.GameId);
        foreach (var member in members) { _membership.Remove(member.Identity.Id); _memberConnections.Remove(member.Identity.Connection.Id); _memberExpiry.Remove(member); }
        if (lobby.RankedMatchId != 0)
        {
            bool reported = _ranked?.TryGetMatchForServer(lobby.RankedMatchId, out var rankedMatch) == true && rankedMatch is { ReportedA: true } or { ReportedB: true };
            if (lobby.Data.GameState is GameState.INITIALIZING or GameState.PRE_GAME)
            {
                if (reported && forfeitedBy is long reporter) _forfeits.TryAdd(lobby.RankedMatchId, reporter);
                else if (!reported) _abandon.Add(lobby.RankedMatchId);
            }
            else if (lobby.Data.GameState == GameState.IN_GAME && forfeitedBy is long account) _forfeits.TryAdd(lobby.RankedMatchId, account);
        }
        foreach (var member in members) NotifyNotifyGameRemovedAsync(member.Identity.Connection, new NotifyGameRemoved { GameId = lobby.Data.GameId, DestructionReason = reason });
    }
    internal void Disconnected(BlazeRpcConnection connection)
    {
        lock (_sync)
        {
            if (_waiting.TryGet(connection.Id, out var search)) _waiting.Remove(search);
            if (_pendingConnections.TryGetValue(connection.Id, out var pending)) Abort(pending, FindSearch(pending, connection.Id), MatchmakingResult.SESSION_TERMINATED);
            if (_memberConnections.TryGetValue(connection.Id, out long account) && _membership.TryGetValue(account, out uint id) &&
                _games.TryGetValue(id, out var lobby) && lobby.Members.TryGetValue(account, out var member) && member.Identity.Connection.Id == connection.Id)
                Remove(lobby, account, PlayerRemovedReason.BLAZESERVER_CONN_LOST);
        }
        _ = FlushAbandonedAsync();
    }
    internal void SessionRevoked(PlayerSession session)
    {
        lock (_sync)
        {
            long account = session.Account.Id;
            if (_waiting.TryGet(account, out var search) && search.Payload.Identity.SessionKey == session.Key)
            {
                _waiting.Remove(search);
                Failed(search, MatchmakingResult.SESSION_TERMINATED);
            }
            if (_pendingAccounts.TryGetValue(account, out var pending))
            {
                search = pending.Pair.Host.AccountId == account ? pending.Pair.Host : pending.Pair.Guest;
                if (search.Payload.Identity.SessionKey == session.Key) Abort(pending, search, MatchmakingResult.SESSION_TERMINATED);
            }
            if (_membership.TryGetValue(account, out uint id) && _games.TryGetValue(id, out var lobby) &&
                lobby.Members.TryGetValue(account, out var member) && member.Identity.SessionKey == session.Key)
                Remove(lobby, account, PlayerRemovedReason.BLAZESERVER_CONN_LOST);
        }
        _ = FlushAbandonedAsync();
    }
    private static MatchmakingQueue<SearchData>.Search FindSearch(PendingMatch pending, Guid connection) =>
        pending.Pair.Host.ConnectionId == connection ? pending.Pair.Host : pending.Pair.Guest;
    private static void Failed(MatchmakingQueue<SearchData>.Search search, MatchmakingResult result)
    {
        if (search.Payload.Identity.Connection.Connected) NotifyNotifyMatchmakingFailedAsync(search.Payload.Identity.Connection,
            new NotifyMatchmakingFailed { SessionId = search.Id, MatchmakingResult = result });
    }
    private static void Abort(PendingMatch pending, MatchmakingQueue<SearchData>.Search search, MatchmakingResult reason)
    {
        pending.Failures.TryAdd(search.AccountId, reason);
        var other = ReferenceEquals(search, pending.Pair.Host) ? pending.Pair.Guest : pending.Pair.Host;
        pending.Failures.TryAdd(other.AccountId, MatchmakingResult.SESSION_ERROR_GAME_SETUP_FAILED);
    }
    private static MatchmakingPolicy Policy() => new(ServerOptions.Current.MatchmakingInitialEloRange, ServerOptions.Current.MatchmakingEloRangeStep,
        ServerOptions.Current.MatchmakingEloRangeIntervalSeconds, ServerOptions.Current.MatchmakingMaxEloRange);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private int QueueLimit => Math.Min(ServerOptions.Current.MatchmakingQueueLimit, ServerOptions.Current.MaxPlayers);
    private PendingMatch Reserve(MatchmakingPool pool, DateTimeOffset now)
    {
        if (_pendingAccounts.Count >= MaxConcurrentSetups * 2 || _membership.Count + _pendingAccounts.Count + 2 > ServerOptions.Current.MaxPlayers) return null;
        for (int attempts = 0; attempts < 64; attempts++)
        {
            var pair = _waiting.TakePair(pool, now, Policy());
            if (pair is null) return null;
            bool hostLive = Live(pair.Host.Payload.Identity, now) && pair.Host.Deadline > now;
            bool guestLive = Live(pair.Guest.Payload.Identity, now) && pair.Guest.Deadline > now;
            if (!hostLive || !guestLive)
            {
                foreach (var search in new[] { pair.Host, pair.Guest })
                    if (Live(search.Payload.Identity, now) && search.Deadline > now) _waiting.Add(search, QueueLimit);
                    else Failed(search, MatchmakingResult.SESSION_TIMED_OUT);
                continue;
            }
            var pending = new PendingMatch { Pair = pair, GameId = NextId() };
            _pendingAccounts.Add(pair.Host.AccountId, pending); _pendingAccounts.Add(pair.Guest.AccountId, pending);
            _pendingConnections.Add(pair.Host.ConnectionId, pending); _pendingConnections.Add(pair.Guest.ConnectionId, pending);
            return pending;
        }
        return null;
    }
    private void Release(PendingMatch pending)
    {
        _pendingAccounts.Remove(pending.Pair.Host.AccountId); _pendingAccounts.Remove(pending.Pair.Guest.AccountId);
        _pendingConnections.Remove(pending.Pair.Host.ConnectionId); _pendingConnections.Remove(pending.Pair.Guest.ConnectionId);
    }
    private async Task CompletePairAsync(PendingMatch pending)
    {
        long boundId = 0;
        try
        {
            bool aborted;
            lock (_sync) aborted = pending.Failures.Count != 0;
            if (!aborted && pending.Pair.Host.Pool.Ranked)
            {
                // Durable binding runs outside the lobby lock and outside either account's transaction.
                var bound = await Task.Run(() => _ranked.BindMatch(pending.Pair.Host.AccountId, pending.Pair.Guest.AccountId, pending.GameId)).ConfigureAwait(false);
                boundId = bound.Id;
            }
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var search in new[] { pending.Pair.Host, pending.Pair.Guest })
                    if (!Live(search.Payload.Identity, now) || search.Deadline <= now) Abort(pending, search, MatchmakingResult.SESSION_TIMED_OUT);
                if (!ServerOptions.Current.EnablePeerLobbies && pending.Failures.Count == 0) Abort(pending, pending.Pair.Host, MatchmakingResult.SESSION_TERMINATED);
                if (pending.Failures.Count != 0)
                {
                    Release(pending);
                    if (boundId != 0) _abandon.Add(boundId);
                    foreach (var search in new[] { pending.Pair.Host, pending.Pair.Guest }) Failed(search, pending.Failures[search.AccountId]);
                }
                else
                {
                    var hostData = pending.Pair.Host.Payload; var guestData = pending.Pair.Guest.Payload;
                    var lobby = NewLobby(pending.GameId, hostData.Identity, hostData.Name, hostData.Protocol, hostData.GameAttributes,
                        hostData.EntryCriteria, hostData.Settings, new List<NetworkAddress> { hostData.Network });
                    lobby.RankedMatchId = boundId;
                    if (boundId != 0) lobby.Data.GameReportingId = (ulong)boundId;
                    var host = Add(lobby, hostData.Identity, hostData.Network, hostData.PlayerAttributes);
                    var guest = Add(lobby, guestData.Identity, guestData.Network, guestData.PlayerAttributes);
                    Release(pending);
                    // The host learns the game before receiving the incremental joining notification.
                    Setup(lobby, host, pending.Pair.Host.Id, new List<ReplicatedGamePlayer> { host.Player });
                    Setup(lobby, guest, pending.Pair.Guest.Id);
                    NotifyNotifyPlayerJoiningAsync(host.Identity.Connection, new NotifyPlayerJoining { GameId = pending.GameId, JoiningPlayer = guest.Player });
                }
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                Release(pending);
                if (_games.TryGetValue(pending.GameId, out var partial)) Destroy(partial, GameDestructionReason.SYS_CREATION_FAILED);
                if (boundId != 0) _abandon.Add(boundId);
                foreach (var search in new[] { pending.Pair.Host, pending.Pair.Guest }) Failed(search,
                    pending.Failures.GetValueOrDefault(search.AccountId, MatchmakingResult.SESSION_ERROR_GAME_SETUP_FAILED));
            }
            System.Diagnostics.Trace.TraceWarning("Peer match setup failed: {0}", ex.GetType().Name);
        }
        await FlushAbandonedAsync().ConfigureAwait(false);
    }
    private async Task FlushAbandonedAsync()
    {
        lock (_sync)
        {
            if (_ranked is null || _cleanupRunning || (_abandon.Count == 0 && _forfeits.Count == 0)) return;
            _cleanupRunning = true;
        }
        try
        {
            while (true)
            {
                long id; long? forfeitedBy;
                lock (_sync)
                {
                    if (_forfeits.Count != 0) { var first = _forfeits.First(); id = first.Key; forfeitedBy = first.Value; }
                    else if (_abandon.Count != 0) { id = _abandon.First(); forfeitedBy = null; }
                    else return;
                }
                if (!_ranked.TryGetMatchForServer(id, out var receipt) || receipt is null || receipt.Settled || receipt.Cancelled ||
                    (forfeitedBy is null && (receipt.ReportedA || receipt.ReportedB)))
                {
                    // Terminal/retired work is already complete. A mere transport
                    // teardown cannot erase a submitted score or poison cleanup.
                    lock (_sync) { _abandon.Remove(id); _forfeits.Remove(id); }
                    continue;
                }
                try
                {
                    if (forfeitedBy is long account) await Task.Run(() => _ranked.Report(account, id, forfeit: true)).ConfigureAwait(false);
                    else await Task.Run(() => _ranked.AbandonMatch(id)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                {
                    if (!_ranked.TryGetMatchForServer(id, out var terminal) || terminal is { Settled: true } or { Cancelled: true })
                    { lock (_sync) { _abandon.Remove(id); _forfeits.Remove(id); } continue; }
                    System.Diagnostics.Trace.TraceWarning("Ranked cleanup will retry: {0}", ex.GetType().Name); return;
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Ranked cleanup will retry: {0}", ex.GetType().Name); return; }
                lock (_sync) { _abandon.Remove(id); _forfeits.Remove(id); }
            }
        }
        finally { lock (_sync) _cleanupRunning = false; }
    }
    /// <summary>Maintenance also retries widening searches when no new player arrives.</summary>
    internal void ExpireQueue()
    {
        var pairs = new List<PendingMatch>();
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var search in _waiting.Expire(now)) Failed(search, MatchmakingResult.SESSION_TIMED_OUT);
            while (_memberExpiry.Min is { } member && member.Identity.Expires <= now)
            {
                var lobby = _games[_membership[member.Identity.Id]];
                Remove(lobby, member.Identity.Id, PlayerRemovedReason.BLAZESERVER_CONN_LOST);
            }
            if (ServerOptions.Current.EnablePeerLobbies)
            {
                bool progressed;
                do
                {
                    progressed = false;
                    // Oldest pools go first; one pair per pool per round prevents a busy pool monopolizing setup slots.
                    foreach (var pool in _waiting.Pools)
                    {
                        if (_pendingAccounts.Count >= MaxConcurrentSetups * 2) break;
                        var pair = Reserve(pool, now);
                        if (pair is null) continue;
                        pairs.Add(pair);
                        progressed = true;
                    }
                } while (progressed && _pendingAccounts.Count < MaxConcurrentSetups * 2);
            }
        }
        foreach (var pair in pairs) _ = CompletePairAsync(pair);
        _ = FlushAbandonedAsync();
    }
    public override async Task<StartMatchmakingResponse> StartMatchmakingAsync(StartMatchmakingRequest request, BlazeRpcContext context)
    {
        Enabled(); var caller = Caller(context);
        Text(request.GameName, 64); Text(request.GameProtocolVersionString, 128); Topology(request.NetworkTopology);
        if (request.MaxPlayerCapacity > 2 || request.PlayerIdList.Any(id => id != caller.Id) || request.PlayerIdList.Count > 1 || request.SessionIdList.Count > 1 || request.QueueCapacity != 0 || request.GroupId.Id != 0)
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_MATCHMAKING_CRITERIA);
        var criteria = request.CriteriaData;
        if (criteria is not null && (criteria.GenericRulePrefsList.Count > 32 || criteria.SkillRulePrefsList.Count > 32 ||
            criteria.UEDRuleCriteriaMap.Count > 32 || new Heat2Serializer().Serialize(criteria).Length > 8192 ||
            criteria.GenericRulePrefsList.Any(r => r.RuleName.Length > 64 || r.MinFitThresholdName.Length > 64 || r.DesiredValues.Count > 16 || r.DesiredValues.Any(v => v.Length > 256))))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_MATCHMAKING_CRITERIA);
        var modes = criteria?.GenericRulePrefsList.Where(r => r.RuleName == "OSDK_gameMode").ToArray();
        if (modes is { Length: > 1 } || modes is { Length: 1 } && (modes[0].DesiredValues.Count != 1 || modes[0].DesiredValues[0].Length is 0 or > 64))
            throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_MATCHMAKING_CRITERIA);
        string mode = modes is { Length: 1 } ? modes[0].DesiredValues[0] : "default";
        bool ranked = criteria?.RankedGameRulePrefs?.DesiredRankedGameValue == RankedGameRulePrefs.RankedGameDesiredValue.RANKED;
        if (ranked && _ranked is null) throw new GameManagerException(Error.GAMEMANAGER_ERR_INVALID_MATCHMAKING_CRITERIA);
        if (ranked && _ranked.GetActive(caller.Id) is not null) throw new GameManagerException(Error.GAMEMANAGER_ERR_ALREADY_GAME_MEMBER);
        int rating = _ranked?.GetRating(caller.Id).Rating ?? 1000;
        var data = new SearchData(caller, request.GameName, request.GameProtocolVersionString, Attributes(request.GameAttribs),
            Attributes(request.PlayerAttribs), Attributes(request.EntryCriteriaMap), Address(request.PlayerNetworkAddress), request.GameSettings);
        PendingMatch pending;
        uint sessionId;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _waiting.Expire(now)) Failed(expired, MatchmakingResult.SESSION_TIMED_OUT);
            Available(caller);
            if (_waiting.Count + _pendingAccounts.Count >= QueueLimit) throw new GameManagerException(Error.GAMEMANAGER_ERR_QUEUE_FULL);
            var pool = new MatchmakingPool(request.GameProtocolVersionString, mode, ranked);
            sessionId = NextId();
            _waiting.Add(new MatchmakingQueue<SearchData>.Search { Id = sessionId, AccountId = caller.Id, ConnectionId = caller.Connection.Id,
                Pool = pool, Rating = rating, QueuedAt = now,
                Deadline = Min(caller.Expires, now.AddMilliseconds(Math.Clamp((long)request.SessionDurationMS, 1000L, ServerOptions.Current.MatchmakingMaxWaitSeconds * 1000L))), Payload = data }, QueueLimit);
            pending = Reserve(pool, now);
        }
        if (pending is not null) await CompletePairAsync(pending).ConfigureAwait(false);
        return new() { SessionId = sessionId };
    }
    public override Task<EmptyMessage> CancelMatchmakingAsync(CancelMatchmakingRequest request, BlazeRpcContext context)
    {
        Enabled(); var caller = Caller(context);
        lock (_sync)
        {
            if (_waiting.TryGet(caller.Id, out var search) && search.Id == request.MatchmakingSessionId)
            {
                if (!Owns(search.Payload.Identity, caller)) throw new GameManagerException(Error.GAMEMANAGER_ERR_NOT_MATCHMAKING_SESSION_OWNER);
                _waiting.Remove(search); Failed(search, MatchmakingResult.SESSION_CANCELED);
            }
            else if (_pendingAccounts.TryGetValue(caller.Id, out var pending))
            {
                search = pending.Pair.Host.AccountId == caller.Id ? pending.Pair.Host : pending.Pair.Guest;
                if (search.Id == request.MatchmakingSessionId)
                {
                    if (!Owns(search.Payload.Identity, caller)) throw new GameManagerException(Error.GAMEMANAGER_ERR_NOT_MATCHMAKING_SESSION_OWNER);
                    Abort(pending, search, MatchmakingResult.SESSION_CANCELED);
                }
            }
            // Matched, old and repeated cancellations acknowledge without touching a lobby or newer search.
            return Task.FromResult(new EmptyMessage());
        }
    }
}
