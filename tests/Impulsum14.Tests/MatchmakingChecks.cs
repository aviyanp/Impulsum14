using System.Net;
using System.Net.Sockets;
using Blaze3SDK.Blaze;
using Blaze3SDK.Blaze.Authentication;
using Blaze3SDK.Blaze.GameManager;
using Blaze3SDK.Components;
using EATDF;
using EATDF.Serialization;
using Impulsum14;
using ProtoFire;
using ProtoFire.Frames;
using GamePlayerState = Blaze3SDK.Blaze.GameManager.PlayerState;

/// <summary>Isolated helpers; the executable runner chooses when to register them.</summary>
internal static class MatchmakingChecks
{
    internal static Task QueueInvariantsAsync()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(100);
        var policy = new MatchmakingPolicy(100, 100, 10, 500);
        var pool = new MatchmakingPool("pc-v1", "seasons", false);
        var queue = new MatchmakingQueue<int>();
        MatchmakingQueue<int>.Search Search(int id, int rating = 1000, MatchmakingPool? key = null, Guid? connection = null, int age = 0) => new()
        {
            Id = (uint)id, AccountId = id, ConnectionId = connection ?? Guid.NewGuid(), Pool = key ?? pool,
            Rating = rating, QueuedAt = now.AddSeconds(-age), Deadline = now.AddMinutes(5), Payload = id
        };
        var oldest = Search(1, 900, age: 10); var closer = Search(2, 1000); var arriving = Search(3, 1050);
        queue.Add(oldest, 100); queue.Add(closer, 100); queue.Add(arriving, 100);
        var pair = queue.TakePair(pool, now, policy)!;
        Equal(1L, pair.Host.AccountId); Equal(2L, pair.Guest.AccountId);
        Equal(1, queue.Count);
        Check(queue.TryGet(3L, out var remaining) && ReferenceEquals(remaining, arriving), "Queue membership changed for the unpaired search.");
        Check(!queue.Remove(oldest), "A completed search was removed twice.");
        Throws(() => queue.Add(Search(3), 100));
        Throws(() => queue.Add(Search(4, connection: arriving.ConnectionId), 100));
        Throws(() => queue.Add(Search(5), 1));
        queue.Remove(arriving);
        var replacement = Search(3); queue.Add(replacement, 100);
        Check(!queue.Remove(arriving) && queue.TryGet(3L, out remaining) && ReferenceEquals(remaining, replacement), "A delayed removal deleted a replacement session.");
        queue.Remove(replacement);

        // Tuple keys prevent delimiter collisions, and ranked/unranked searches remain separate.
        var keys = new[] { new MatchmakingPool("p|m", "x", false), new MatchmakingPool("p", "m|x", false),
            new MatchmakingPool("p", "x", false), new MatchmakingPool("p", "x", true) };
        for (int i = 0; i < keys.Length; i++) queue.Add(Search(10 + i, key: keys[i]), 100);
        foreach (var key in keys) Check(queue.TakePair(key, now, policy) is null, "Incompatible pools matched.");
        foreach (var search in queue.Expire(now.AddMinutes(5))) Check(search.Id is >= 10 and <= 13, "Unexpected expired search.");
        Equal(0, queue.Count); Equal(0, queue.Pools.Length);

        queue.Add(Search(20, 1000), 100); queue.Add(Search(21, 1300), 100);
        Check(queue.TakePair(pool, now, policy) is null, "Elo widened before its configured interval.");
        Check(queue.TakePair(pool, now.AddSeconds(19), policy) is null, "Elo widened too far.");
        pair = queue.TakePair(pool, now.AddSeconds(20), policy)!;
        Equal(20L, pair.Host.AccountId); Equal(21L, pair.Guest.AccountId);
        Equal(500, policy.Range(now.AddDays(-100), now));

        // Re-inserting a live peer after its partner disconnects preserves its original wait order.
        var preserved = Search(30); queue.Add(preserved, 100); queue.Add(Search(31), 100);
        pair = queue.TakePair(pool, now, policy)!; queue.Add(pair.Host, 100); queue.Add(Search(32), 100);
        pair = queue.TakePair(pool, now, policy)!;
        Equal(30L, pair.Host.AccountId); Equal(32L, pair.Guest.AccountId);

        // Compare the indexed selector with a direct fairness oracle over varied ratings and removals.
        var random = new Random(14014);
        for (int round = 0; round < 30; round++)
        {
            queue = new();
            var expected = new List<MatchmakingQueue<int>.Search>();
            for (int i = 0; i < 150; i++)
            {
                var search = Search(1000 + i, random.Next(0, 10001), age: 300 - i);
                queue.Add(search, 1000); expected.Add(search);
            }
            for (int i = 0; i < 20; i++)
            {
                int index = random.Next(expected.Count);
                queue.Remove(expected[index]); expected.RemoveAt(index);
            }
            while (true)
            {
                MatchmakingQueue<int>.Search? host = null, guest = null;
                foreach (var candidate in expected.OrderBy(s => s.Order))
                {
                    guest = expected.Where(s => s != candidate && Math.Abs(s.Rating - candidate.Rating) <= policy.Range(candidate.QueuedAt, now)).OrderBy(s => s.Order).FirstOrDefault();
                    if (guest is not null) { host = candidate; break; }
                }
                pair = queue.TakePair(pool, now, policy);
                if (host is null) { Check(pair is null, "Indexed selector produced an ineligible pair."); break; }
                Check(pair is not null, "Indexed selector missed an eligible pair.");
                Equal(host.AccountId, pair!.Host.AccountId); Equal(guest!.AccountId, pair.Guest.AccountId);
                expected.Remove(host); expected.Remove(guest);
            }
            Equal(expected.Count, queue.Count);
            queue.Expire(now.AddDays(1)); Equal(0, queue.Count);
        }
        // Thousands of unrelated pools exercise sparse indexes and complete index reclamation.
        queue = new();
        var large = Enumerable.Range(1, 10000).Select(i => Search(i, i % 10001, new("v" + i, "mode", false))).ToArray();
        foreach (var search in large) queue.Add(search, large.Length);
        foreach (var search in large.Reverse()) Check(queue.Remove(search), "Indexed removal lost an account.");
        Equal(0, queue.Count); Equal(0, queue.Pools.Length);
        return Task.CompletedTask;
    }

    internal static async Task LobbyProtocolAsync(int port, string token1, string token2, ServerOptions options)
    {
        bool enabled = options.EnablePeerLobbies;
        options.EnablePeerLobbies = true;
        try
        {
            await using (var host = await Wire.Connect(port, token1))
            await using (var guest = await Wire.Connect(port, token2))
            await using (var otherConnection = await Wire.Connect(port, token1))
            {
                var rejected = await host.Call(GameManagerBase.GameManagerCommand.createGame, new CreateGameRequest
                {
                    GameName = "bounds", HostNetworkAddressList = new List<NetworkAddress> { new(), new(), new() }
                });
                Check(rejected.Frame.ErrorCode != 0, "Oversized address list was silently truncated.");
                var createdPacket = await host.Call(GameManagerBase.GameManagerCommand.createGame, new CreateGameRequest
                {
                    GameName = "protocol checks", GameProtocolVersionString = "mm-check", MaxPlayerCapacity = 2,
                    NetworkTopology = GameNetworkTopology.PEER_TO_PEER_FULL_MESH
                });
                Success(createdPacket); Check(!host.SetupBeforeReply, "Game setup preceded create response.");
                uint game = Decode<CreateGameResponse>(createdPacket).GameId;
                var hostSetup = Decode<NotifyGameSetup>(await host.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                Equal(host.AccountId, hostSetup.GameData!.TopologyHostInfo!.PlayerId);
                Equal(1, hostSetup.GameRoster.Count);
                Check((await otherConnection.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = game })).Frame.ErrorCode != 0,
                    "A second connection hijacked the same account's lobby.");
                Check((await otherConnection.Call(GameManagerBase.GameManagerCommand.getFullGameData, new GetFullGameDataRequest { GameIdList = new List<uint> { game } })).Frame.ErrorCode != 0,
                    "A second connection read the same account's lobby membership.");
                Check((await guest.Call(GameManagerBase.GameManagerCommand.joinGame, new JoinGameRequest { GameId = game, GameProtocolVersionString = "wrong" })).Frame.ErrorCode != 0, "Protocol mismatch joined.");
                Success(await guest.Call(GameManagerBase.GameManagerCommand.joinGame, new JoinGameRequest { GameId = game, GameProtocolVersionString = "mm-check" }));
                Check(!guest.SetupBeforeReply, "Game setup preceded join response.");
                var guestSetup = Decode<NotifyGameSetup>(await guest.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                Equal(2, guestSetup.GameRoster.Count);
                var joining = Decode<NotifyPlayerJoining>(await host.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyPlayerJoining));
                Equal(guest.AccountId, joining.JoiningPlayer!.PlayerId);
                Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.PRE_GAME }));
                Check((await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME })).Frame.ErrorCode != 0, "An unconnected full mesh entered gameplay.");
                Check((await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, host.AccountId))).Frame.ErrorCode != 0, "A self mesh observation completed joining.");
                Check((await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, long.MaxValue))).Frame.ErrorCode != 0, "An unknown mesh target was accepted.");
                Success(await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, guest.AccountId)));
                var state = Decode<GetFullGameDataResponse>(await host.Call(GameManagerBase.GameManagerCommand.getFullGameData, new GetFullGameDataRequest { GameIdList = new List<uint> { game } }));
                Equal(GamePlayerState.ACTIVE_CONNECTED, state.Games[0].GameRoster.Single(p => p.PlayerId == host.AccountId).PlayerState);
                Equal(GamePlayerState.ACTIVE_CONNECTING, state.Games[0].GameRoster.Single(p => p.PlayerId == guest.AccountId).PlayerState);
                var lostMesh = Mesh(game, guest.AccountId); lostMesh.MeshConnectionStatusList[0].PlayerNetConnectionStatus = PlayerNetConnectionStatus.DISCONNECTED;
                Success(await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, lostMesh));
                Check((await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME })).Frame.ErrorCode != 0, "An obsolete connected observation allowed gameplay.");
                Success(await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, guest.AccountId)));
                Success(await guest.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, host.AccountId)));
                Check((await guest.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME })).Frame.ErrorCode != 0, "Guest advanced host-owned game state.");
                Success(await host.Call(GameManagerBase.GameManagerCommand.finalizeGameCreation, new UpdateGameSessionRequest { GameId = game, XnetNonce = new byte[] { 1, 2 }, XnetSession = new byte[] { 3, 4 } }));
                var session = Decode<GameSessionUpdatedNotification>(await guest.FindNotification(GameManagerBase.GameManagerNotification.NotifyGameSessionUpdated));
                Check(session.XnetNonce.SequenceEqual(new byte[] { 1, 2 }) && session.XnetSession.SequenceEqual(new byte[] { 3, 4 }), "Finalize omitted the session update payload.");
                Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME }));
                Check((await host.Call(GameManagerBase.GameManagerCommand.getFullGameData, new GetFullGameDataRequest { GameIdList = Enumerable.Repeat(game, 33).ToList() })).Frame.ErrorCode != 0, "Full game data silently truncated an oversized request.");
                Success(await host.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = game }));
            }

            await using (var host = await Wire.Connect(port, token1))
            await using (var guest = await Wire.Connect(port, token2))
            await using (var outsider = await Wire.Connect(port, token1))
            {
                var first = Decode<StartMatchmakingResponse>(await host.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("queue-version", "one")));
                Check((await host.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("queue-version", "one"))).Frame.ErrorCode != 0, "A duplicate search replaced the first session.");
                Check((await outsider.Call(GameManagerBase.GameManagerCommand.cancelMatchmaking, new CancelMatchmakingRequest { MatchmakingSessionId = first.SessionId })).Frame.ErrorCode != 0, "A second connection cancelled the first connection's search.");
                var second = Decode<StartMatchmakingResponse>(await guest.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("queue-version", "one")));
                Check(!guest.SetupBeforeReply, "Matched setup preceded the start-matchmaking response.");
                var hostSetup = Decode<NotifyGameSetup>(await host.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                var guestSetup = Decode<NotifyGameSetup>(await guest.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                Equal(host.AccountId, hostSetup.GameData!.TopologyHostInfo!.PlayerId);
                Equal(first.SessionId, hostSetup.GameSetupReason!.MatchmakingSetupContext!.SessionId);
                Equal(second.SessionId, guestSetup.GameSetupReason!.MatchmakingSetupContext!.SessionId);
                Equal(1, hostSetup.GameRoster.Count); Equal(2, guestSetup.GameRoster.Count);
                await host.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyPlayerJoining);
                Success(await host.Call(GameManagerBase.GameManagerCommand.cancelMatchmaking, new CancelMatchmakingRequest { MatchmakingSessionId = first.SessionId }));
                Success(await host.Call(GameManagerBase.GameManagerCommand.getFullGameData, new GetFullGameDataRequest { GameIdList = new List<uint> { hostSetup.GameData.GameId } }));
                Success(await host.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = hostSetup.GameData.GameId }));
            }
            foreach (bool differentProtocol in new[] { true, false })
            {
                await using var first = await Wire.Connect(port, token1);
                await using var second = await Wire.Connect(port, token2);
                uint one = Decode<StartMatchmakingResponse>(await first.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("sep-a", "mode-a"))).SessionId;
                uint two = Decode<StartMatchmakingResponse>(await second.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search(differentProtocol ? "sep-b" : "sep-a", differentProtocol ? "mode-a" : "mode-b"))).SessionId;
                Success(await first.Call(GameManagerBase.GameManagerCommand.cancelMatchmaking, new CancelMatchmakingRequest { MatchmakingSessionId = one }));
                Success(await second.Call(GameManagerBase.GameManagerCommand.cancelMatchmaking, new CancelMatchmakingRequest { MatchmakingSessionId = two }));
                Equal(MatchmakingResult.SESSION_CANCELED, Decode<NotifyMatchmakingFailed>(await first.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyMatchmakingFailed)).MatchmakingResult);
                Equal(MatchmakingResult.SESSION_CANCELED, Decode<NotifyMatchmakingFailed>(await second.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyMatchmakingFailed)).MatchmakingResult);
            }
            await using (var one = await Wire.Connect(port, token1))
            {
                var shortSearch = Search("timeout", "mode"); shortSearch.SessionDurationMS = 1000;
                uint id = Decode<StartMatchmakingResponse>(await one.Call(GameManagerBase.GameManagerCommand.startMatchmaking, shortSearch)).SessionId;
                await Task.Delay(1100); GameManagerComponent.Instance.ExpireQueue();
                var failed = Decode<NotifyMatchmakingFailed>(await one.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyMatchmakingFailed));
                Equal(id, failed.SessionId); Equal(MatchmakingResult.SESSION_TIMED_OUT, failed.MatchmakingResult);
                Success(await one.Call(GameManagerBase.GameManagerCommand.cancelMatchmaking, new CancelMatchmakingRequest { MatchmakingSessionId = id }));
            }
            var disconnected = await Wire.Connect(port, token1);
            await disconnected.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("disconnect", "mode"));
            await disconnected.DisposeAsync();
            await using (var replacement = await Wire.Connect(port, token1))
            {
                await Eventually(async () => (await replacement.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("disconnect", "mode"))).Frame.ErrorCode == 0);
                await using var peer = await Wire.Connect(port, token2);
                Success(await peer.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("disconnect", "mode")));
                var setup = Decode<NotifyGameSetup>(await replacement.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                Equal(replacement.AccountId, setup.GameData!.TopologyHostInfo!.PlayerId);
                Success(await replacement.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = setup.GameData.GameId }));
            }
        }
        finally { options.EnablePeerLobbies = enabled; }
    }

    internal static async Task RankedLifecycleAsync(int port, string token1, string token2, ServerOptions options, RankedService ranked)
    {
        bool enabled = options.EnablePeerLobbies; int range = options.MatchmakingInitialEloRange, maximum = options.MatchmakingMaxEloRange;
        options.EnablePeerLobbies = true; options.MatchmakingInitialEloRange = options.MatchmakingMaxEloRange = 10000;
        try
        {
            await using (var one = await Wire.Connect(port, token1))
            await using (var two = await Wire.Connect(port, token2))
            {
                int before = ranked.GetRating(one.AccountId).GamesPlayed;
                await one.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-check", "one", true));
                Success(await two.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-check", "one", true)));
                var setup = Decode<NotifyGameSetup>(await one.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                long match = checked((long)setup.GameData!.GameReportingId);
                Equal(match, ranked.GetActive(one.AccountId)!.Id); Equal(match, ranked.GetActive(two.AccountId)!.Id);
                Equal((ulong)setup.GameData.GameId, ranked.GetActive(one.AccountId)!.GameId);
                Success(await one.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = setup.GameData.GameId }));
                await Eventually(() => Task.FromResult(ranked.GetActive(one.AccountId) is null && ranked.GetActive(two.AccountId) is null));
                Equal("cancelled", ranked.GetMatch(one.AccountId, match).Status);
                Equal(before, ranked.GetRating(one.AccountId).GamesPlayed);
            }
            await using var host = await Wire.Connect(port, token1);
            var guest = await Wire.Connect(port, token2);
            try
            {
                await host.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-forfeit", "one", true));
                Success(await guest.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-forfeit", "one", true)));
                var setup = Decode<NotifyGameSetup>(await host.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                uint game = setup.GameData!.GameId; long match = checked((long)setup.GameData.GameReportingId);
                Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.PRE_GAME }));
                Success(await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, guest.AccountId)));
                Success(await guest.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, host.AccountId)));
                Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME }));
                Check((await host.Call(GameManagerBase.GameManagerCommand.removePlayer, new RemovePlayerRequest { GameId = game, PlayerId = guest.AccountId })).Frame.ErrorCode != 0, "Ranked host could kick an opponent to force a win.");
                Success(await guest.Call(GameManagerBase.GameManagerCommand.removePlayer, new RemovePlayerRequest { GameId = game, PlayerId = guest.AccountId }));
                await Eventually(() => Task.FromResult(ranked.GetMatch(host.AccountId, match).Status == "settled"));
                Equal(guest.AccountId, ranked.GetMatch(host.AccountId, match).ForfeitedBy!.Value);
                Check(ranked.GetActive(host.AccountId) is null && ranked.GetActive(guest.AccountId) is null, "Explicit self departure left both ranked players busy.");
                // An already cancelled/retired cleanup job must never block the
                // next legitimate result. Reproduce an expiry racing self-leave.
                for (int cleanup = 0; cleanup < 2; cleanup++)
                {
                    await host.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("cleanup-recovery-" + cleanup, "one", true));
                    Success(await guest.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("cleanup-recovery-" + cleanup, "one", true)));
                    var nextSetup = Decode<NotifyGameSetup>(await host.FindNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                    uint nextGame = nextSetup.GameData!.GameId; long nextMatch = checked((long)nextSetup.GameData.GameReportingId);
                    Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = nextGame, NewGameState = GameState.PRE_GAME }));
                    Success(await host.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(nextGame, guest.AccountId)));
                    Success(await guest.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(nextGame, host.AccountId)));
                    Success(await host.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = nextGame, NewGameState = GameState.IN_GAME }));
                    int gamesBefore = ranked.GetRating(host.AccountId).GamesPlayed;
                    if (cleanup == 0) ranked.ExpireMatches(DateTimeOffset.UtcNow.AddSeconds(1));
                    Success(await guest.Call(GameManagerBase.GameManagerCommand.removePlayer, new RemovePlayerRequest { GameId = nextGame, PlayerId = guest.AccountId }));
                    await Eventually(() => Task.FromResult(ranked.GetMatch(host.AccountId, nextMatch).Status == (cleanup == 0 ? "cancelled" : "settled")));
                    Equal(gamesBefore + (cleanup == 0 ? 0 : 1), ranked.GetRating(host.AccountId).GamesPlayed);
                }

            }
            finally { await guest.DisposeAsync(); }
            await using (var dropHost = await Wire.Connect(port, token1))
            {
                var dropGuest = await Wire.Connect(port, token2);
                try
                {
                    int gamesBefore = ranked.GetRating(dropHost.AccountId).GamesPlayed;
                    await dropHost.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-disconnect", "one", true));
                    Success(await dropGuest.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("ranked-disconnect", "one", true)));
                    var setup = Decode<NotifyGameSetup>(await dropHost.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                    uint game = setup.GameData!.GameId; long match = checked((long)setup.GameData.GameReportingId);
                    Success(await dropHost.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.PRE_GAME }));
                    Success(await dropHost.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, dropGuest.AccountId)));
                    Success(await dropGuest.Call(GameManagerBase.GameManagerCommand.updateMeshConnection, Mesh(game, dropHost.AccountId)));
                    Success(await dropHost.Call(GameManagerBase.GameManagerCommand.advanceGameState, new AdvanceGameStateRequest { GameId = game, NewGameState = GameState.IN_GAME }));
                    await dropGuest.DisposeAsync();
                    await dropHost.FindNotification(GameManagerBase.GameManagerNotification.NotifyGameRemoved);
                    Equal("pending", ranked.GetMatch(dropHost.AccountId, match).Status);
                    Check(ranked.GetMatch(dropHost.AccountId, match).ForfeitedBy is null, "An abrupt network loss invented a ranked winner.");
                    Equal(gamesBefore, ranked.GetRating(dropHost.AccountId).GamesPlayed);
                    Equal(match, ranked.GetActive(dropHost.AccountId)!.Id);
                    // Test cleanup follows the same no-rewards policy as the configured unresolved-match expiry.
                    ranked.AbandonMatch(match);
                }
                finally { await dropGuest.DisposeAsync(); }
            }
            int step = options.MatchmakingEloRangeStep, interval = options.MatchmakingEloRangeIntervalSeconds;
            options.MatchmakingInitialEloRange = 0; options.MatchmakingEloRangeStep = 10000; options.MatchmakingEloRangeIntervalSeconds = 1;
            try
            {
                await using var waitingHost = await Wire.Connect(port, token1);
                await using var waitingGuest = await Wire.Connect(port, token2);
                Check(ranked.GetRating(waitingHost.AccountId).Rating != ranked.GetRating(waitingGuest.AccountId).Rating, "Forfeit fixture did not produce distinct ratings.");
                await waitingHost.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("heartbeat-widening", "one"));
                Success(await waitingGuest.Call(GameManagerBase.GameManagerCommand.startMatchmaking, Search("heartbeat-widening", "one")));
                await Task.Delay(1100); GameManagerComponent.Instance.ExpireQueue();
                var setup = Decode<NotifyGameSetup>(await waitingHost.NextGameNotification(GameManagerBase.GameManagerNotification.NotifyGameSetup));
                Equal(waitingHost.AccountId, setup.GameData!.TopologyHostInfo!.PlayerId);
                Success(await waitingHost.Call(GameManagerBase.GameManagerCommand.destroyGame, new DestroyGameRequest { GameId = setup.GameData.GameId }));
            }
            finally { options.MatchmakingEloRangeStep = step; options.MatchmakingEloRangeIntervalSeconds = interval; }
        }
        finally { options.EnablePeerLobbies = enabled; options.MatchmakingInitialEloRange = range; options.MatchmakingMaxEloRange = maximum; }
    }

    private static StartMatchmakingRequest Search(string protocol, string mode, bool ranked = false) => new()
    {
        GameName = "matchmaking checks", GameProtocolVersionString = protocol, SessionDurationMS = 30000,
        MaxPlayerCapacity = 2, NetworkTopology = GameNetworkTopology.PEER_TO_PEER_FULL_MESH,
        CriteriaData = new MatchmakingCriteriaData
        {
            GenericRulePrefsList = new List<GenericRulePrefs> { new() { RuleName = "OSDK_gameMode", DesiredValues = new List<string> { mode } } },
            RankedGameRulePrefs = new RankedGameRulePrefs { DesiredRankedGameValue = ranked ? RankedGameRulePrefs.RankedGameDesiredValue.RANKED : RankedGameRulePrefs.RankedGameDesiredValue.UNRANKED }
        }
    };
    private static UpdateMeshConnectionRequest Mesh(uint game, long target) => new()
    {
        GameId = game, MeshConnectionStatusList = new List<PlayerConnectionStatus>
        { new() { TargetPlayer = target, PlayerNetConnectionStatus = PlayerNetConnectionStatus.CONNECTED } }
    };
    private static T Decode<T>(ProtoFirePacket packet) where T : Tdf, new()
    {
        Success(packet); var value = new T();
        Check(new Heat2Serializer().Deserialize(packet.GetDataStream(), value), "Invalid test response TDF."); return value;
    }
    private static void Success(ProtoFirePacket packet) => Equal((ushort)0, packet.Frame.ErrorCode);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected a bounded queue rejection."); }
    private static async Task Eventually(Func<Task<bool>> check)
    {
        for (int i = 0; i < 100; i++) { if (await check()) return; await Task.Delay(25); }
        throw new Exception("Asynchronous matchmaking cleanup did not complete.");
    }

    private sealed class Wire : IAsyncDisposable
    {
        private readonly TcpClient _socket = new();
        private readonly List<ProtoFirePacket> _notifications = new();
        private uint _sequence;
        internal long AccountId { get; private set; }
        internal bool SetupBeforeReply { get; private set; }
        internal static async Task<Wire> Connect(int port, string token)
        {
            var wire = new Wire(); await wire._socket.ConnectAsync(IPAddress.Loopback, port);
            var login = await wire.Send(1, (ushort)AuthenticationComponentBase.AuthenticationComponentCommand.originLogin, new OriginLoginRequest { AuthToken = token });
            wire.AccountId = Decode<FullLoginResponse>(login).SessionInfo!.UserId;
            return wire;
        }
        internal Task<ProtoFirePacket> Call(GameManagerBase.GameManagerCommand command, Tdf body) => Send(4, (ushort)command, body);
        private async Task<ProtoFirePacket> Send(ushort component, ushort command, Tdf body)
        {
            uint sequence = ++_sequence; SetupBeforeReply = false;
            await new ProtoFirePacket(new FireFrame { Component = component, Command = command, MessageNum = sequence }, new Heat2Serializer().Serialize(body)).WriteToAsync(_socket.GetStream());
            while (true)
            {
                var packet = await Read();
                if (packet.Frame.MessageType == MessageType.Notification)
                {
                    if (packet.Frame.Component == 4 && packet.Frame.Command == (ushort)GameManagerBase.GameManagerNotification.NotifyGameSetup) SetupBeforeReply = true;
                    _notifications.Add(packet); continue;
                }
                Equal(sequence, packet.Frame.MessageNum); return packet;
            }
        }
        internal async Task<ProtoFirePacket> NextGameNotification(GameManagerBase.GameManagerNotification expected)
        {
            while (true)
            {
                int index = _notifications.FindIndex(p => p.Frame.Component == 4);
                if (index >= 0)
                {
                    var packet = _notifications[index]; _notifications.RemoveAt(index);
                    Equal((ushort)expected, packet.Frame.Command); return packet;
                }
                _notifications.Add(await Read());
            }
        }
        internal async Task<ProtoFirePacket> FindNotification(GameManagerBase.GameManagerNotification expected)
        {
            while (true)
            {
                int index = _notifications.FindIndex(p => p.Frame.Component == 4 && p.Frame.Command == (ushort)expected);
                if (index >= 0) { var packet = _notifications[index]; _notifications.RemoveAt(index); return packet; }
                _notifications.Add(await Read());
            }
        }
        private async Task<ProtoFirePacket> Read() => await ProtoFirePacket.ReadFromAsync<FireFrame>(_socket.GetStream()).WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Test Blaze connection closed.");
        public ValueTask DisposeAsync() { _socket.Dispose(); return ValueTask.CompletedTask; }
    }
}
