using System.Buffers.Binary;
using Impulsum14;

internal static class RankedChecks
{
    internal static void PairingAndCorroboration(string root)
    {
        using var service = new RankedService(DirectoryFor(root, "ranked-pairing"));
        True(!service.TryGetRating(1, out var absent) && absent is null, "An unseen account was allocated durable ranked state.");
        Equal(1000, service.GetRating(1).Rating);
        Throws<InvalidOperationException>(() => service.BindMatch(1, 1));
        var match = service.BindMatch(1, 2, 900);
        True(service.TryGetRating(1, out var boundRating) && boundRating.Rating == 1000, "Bound account is missing its immutable lookup.");
        Equal(match.Id, service.BindMatch(2, 1, 900).Id);
        Throws<InvalidOperationException>(() => service.BindMatch(1, 3));
        Throws<UnauthorizedAccessException>(() => service.Report(3, match.Id, 50, 0));
        Throws<InvalidDataException>(() => service.Report(1, match.Id, 51, 0));
        Throws<InvalidDataException>(() => service.Report(1, match.Id));
        Throws<InvalidDataException>(() => service.Report(1, match.Id, 0, 0, forfeit: true));
        var pending = service.Report(1, match.Id, 2, 1);
        True(pending.Pending && !pending.Conflict, "First report must await its opponent.");
        Throws<InvalidOperationException>(() => service.AbandonMatch(match.Id));
        Equal(0, service.GetRating(1).GamesPlayed);
        Equal(0L, service.GetRating(1).CoinsEarned);
        var conflict = service.Report(2, match.Id, 4, 0);
        True(conflict.Conflict && !conflict.Settled, "Contradictory normalized scores settled a match.");
        Equal(1000, service.GetRating(1).Rating);
        Throws<InvalidOperationException>(() => service.Report(2, match.Id, 1, 2));
        var settled = service.Report(2, match.Id, forfeit: true);
        True(settled.Settled && settled.Match.ForfeitedBy == 2, "Self-forfeit did not settle against the reporting account.");
        Equal(1024, service.GetRating(1).Rating); Equal(976, service.GetRating(2).Rating);
        Equal(500L, service.GetRating(1).CoinsEarned); Equal(0L, service.GetRating(2).CoinsEarned);
        Equal(1, service.GetRating(1).Season.GamesPlayed); Equal(1, service.GetRating(2).Season.GamesPlayed);
        Equal(null, service.GetActive(1)); Equal(null, service.GetActive(2));
        Equal(settled.Match, service.Report(1, match.Id, 0, 50).Match);
        Equal(1, service.GetRating(1).GamesPlayed);
        var reverse = service.BindMatch(2, 1, 901);
        service.Report(2, reverse.Id, 1, 3);
        var agreed = service.Report(1, reverse.Id, 3, 1);
        True(agreed.Settled && agreed.Match.GoalsA == 1 && agreed.Match.GoalsB == 3, "Inverse peer score was not normalized to the bound orientation.");
        Equal(2, service.GetRating(1).Wins); Equal(2, service.GetRating(2).Losses);
        True(agreed.Match.RatingDeltaA + agreed.Match.RatingDeltaB == 0, "Elo transfer was not zero-sum.");
        var board = service.GetLeaderboard(); Equal(1L, board[0].AccountId);
        Throws<ArgumentOutOfRangeException>(() => service.GetLeaderboard(201));
        // Previously returned snapshots must never change when the ledger advances.
        Equal(0, pending.Reporter.GamesPlayed); Equal(0L, pending.Reporter.CoinsEarned);
    }

    internal static void EloBoundsAndSeasons(string root)
    {
        var options = new RankedOptions
        {
            InitialRating = 1000, MinimumRating = 990, MaximumRating = 1010,
            KFactor = 31, ProvisionalKFactor = 49, ProvisionalGames = 2,
            CheckpointInterval = 100, SeasonHistoryLimit = 1
        };
        using var service = new RankedService(DirectoryFor(root, "ranked-seasons"), options);
        for (int i = 0; i < 20; i++)
        {
            var match = service.BindMatch(10, 20);
            True(!service.Report(10, match.Id, 2, 0).Settled, "A single non-forfeit report changed ratings.");
            var result = service.Report(20, match.Id, 0, 2);
            True(result.Settled, "Corroborated match did not settle.");
            var a = service.GetRating(10); var b = service.GetRating(20);
            Equal(2000, a.Rating + b.Rating);
            True(a.Rating is >= 990 and <= 1010 && b.Rating is >= 990 and <= 1010, "Elo escaped configured bounds.");
            Equal(0, result.Match.RatingDeltaA + result.Match.RatingDeltaB);
            Equal((i + 1) % 10, a.Season.GamesPlayed); Equal((i + 1) % 10, b.Season.GamesPlayed);
        }
        var winner = service.GetRating(10); var loser = service.GetRating(20);
        Equal(1010, winner.Rating); Equal(990, loser.Rating);
        True(!winner.Provisional && !loser.Provisional, "Provisional status never completed.");
        Equal(8, winner.Division); Equal(10, loser.Division);
        Equal(3, winner.Season.SeasonId); Equal(3, loser.Season.SeasonId);
        Equal(2, winner.Season.TitlesWon); Equal(2, winner.Season.Promotions);
        Equal(1, winner.History.Count); Equal(1, loser.History.Count);
        Equal(2, winner.History[0].SeasonId);
        long expected = 20 * (options.WinCoins + 2L * options.GoalCoins) + Seasons.Evaluate(10, 30, true).Coins + Seasons.Evaluate(9, 30, true).Coins;
        Equal(expected, winner.CoinsEarned);
        var cancelled = service.BindMatch(10, 20);
        service.AbandonMatch(cancelled.Id);
        Equal(20, service.GetRating(10).GamesPlayed); Equal(expected, service.GetRating(10).CoinsEarned);
        Throws<InvalidOperationException>(() => service.Report(10, cancelled.Id, 1, 0));
        var expired = service.BindMatch(10, 20);
        Equal(1, service.ExpireMatches(DateTimeOffset.UtcNow.AddSeconds(1)));
        True(service.GetMatch(10, expired.Id).Cancelled, "Expired unresolved match still blocks its players.");
        Equal(20, service.GetRating(20).GamesPlayed);
        var roundingOptions = new RankedOptions { KFactor = 31, ProvisionalGames = 0 };
        using var rounding = new RankedService(DirectoryFor(root, "ranked-rounding"), roundingOptions);
        var half = rounding.BindMatch(1, 2); rounding.Report(1, half.Id, forfeit: true);
        Equal(984, rounding.GetRating(1).Rating); Equal(1016, rounding.GetRating(2).Rating);
    }

    internal static async Task ConcurrentRetries(string root)
    {
        using var service = new RankedService(DirectoryFor(root, "ranked-concurrent"));
        var bound = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.BindMatch(1, 2, 30))));
        Equal(1, bound.Select(m => m.Id).Distinct().Count());
        var match = bound[0];
        var waiter = service.WaitForSettlementAsync(1, match.Id, TimeSpan.FromSeconds(2));
        var responses = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => service.Report(i % 2 + 1, match.Id, i % 2 == 0 ? 3 : 1, i % 2 == 0 ? 1 : 3))));
        Equal(1, service.GetRating(1).GamesPlayed); Equal(1, service.GetRating(2).GamesPlayed);
        Equal(1, service.GetRating(1).Season.GamesPlayed); Equal(1, service.GetRating(2).Season.GamesPlayed);
        Equal(560L, service.GetRating(1).CoinsEarned); Equal(220L, service.GetRating(2).CoinsEarned);
        Equal(2000, service.GetRating(1).Rating + service.GetRating(2).Rating);
        var final = service.GetMatch(1, match.Id);
        Equal(final, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        True(responses.Where(r => r.Settled).All(r => r.Match == final), "Duplicate concurrent reports produced different receipts.");
        var independent = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            long a = 100 + i * 2, b = a + 1;
            var game = service.BindMatch(a, b); service.Report(a, game.Id, 0, 0); service.Report(b, game.Id, 0, 0);
            return service.GetRating(a).Rating + service.GetRating(b).Rating;
        })));
        True(independent.All(sum => sum == 2000), "Concurrent independent pairs crossed rating state.");
        var pending = service.BindMatch(1, 2);
        var timedOut = await service.WaitForSettlementAsync(1, pending.Id, TimeSpan.FromMilliseconds(1));
        True(!timedOut.Settled, "Settlement timeout invented a result.");
        var cancelledWait = service.WaitForSettlementAsync(2, pending.Id, TimeSpan.FromSeconds(2));
        service.AbandonMatch(pending.Id);
        True((await cancelledWait).Cancelled, "Cancellation did not release a pending settlement wait.");
    }

    internal static void JournalRestartAndTailRecovery(string root)
    {
        string directory = DirectoryFor(root, "ranked-restart");
        var options = new RankedOptions { CheckpointInterval = 1000 };
        long id;
        RankedMatchView receipt;
        using (var service = new RankedService(directory, options))
        {
            id = service.BindMatch(1, 2, 51).Id;
            service.Report(1, id, 2, 0);
        }
        using (var recovered = new RankedService(directory, options))
        {
            Equal(id, recovered.GetActive(1)!.Id);
            Equal(0, recovered.GetRating(1).GamesPlayed);
            receipt = recovered.Report(2, id, 0, 2).Match;
        }
        string journal = Path.Combine(directory, "ranked", "journal.bin");
        long intactLength = new FileInfo(journal).Length;
        using (var file = new FileStream(journal, FileMode.Append, FileAccess.Write))
        {
            Span<byte> torn = stackalloc byte[40]; torn.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(torn, 120); BinaryPrimitives.WriteInt32LittleEndian(torn[4..], ~120); file.Write(torn); file.Flush(true);
        }
        using (var recovered = new RankedService(directory, options))
        {
            Equal(intactLength, new FileInfo(journal).Length);
            Equal(receipt, recovered.Report(1, id, 2, 0).Match);
            Equal(1, recovered.GetRating(1).GamesPlayed); Equal(1, recovered.GetRating(2).GamesPlayed);
            Equal(540L, recovered.GetRating(1).CoinsEarned); Equal(200L, recovered.GetRating(2).CoinsEarned);
            var next = recovered.BindMatch(1, 2); True(next.Id > id, "Restart reused a match ID.");
            recovered.AbandonMatch(next.Id);
        }
        // A second restart cannot replay the previous settlement into either account.
        using var again = new RankedService(directory, options);
        Equal(1, again.GetRating(1).GamesPlayed); Equal(1, again.GetRating(2).GamesPlayed);
        Equal(540L, again.GetRating(1).CoinsEarned); Equal(200L, again.GetRating(2).CoinsEarned);

        string tornDirectory = DirectoryFor(root, "ranked-torn-settlement");
        long beforeSettlement, settledLength, tornId;
        using (var service = new RankedService(tornDirectory, options))
        {
            tornId = service.BindMatch(11, 12).Id;
            service.Report(11, tornId, 2, 0);
            beforeSettlement = new FileInfo(Path.Combine(tornDirectory, "ranked", "journal.bin")).Length;
            service.Report(12, tornId, 0, 2);
            settledLength = new FileInfo(Path.Combine(tornDirectory, "ranked", "journal.bin")).Length;
        }
        using (var truncated = new FileStream(Path.Combine(tornDirectory, "ranked", "journal.bin"), FileMode.Open, FileAccess.Write))
            truncated.SetLength(beforeSettlement + (settledLength - beforeSettlement) / 2);
        using (var replay = new RankedService(tornDirectory, options))
        {
            Equal(0, replay.GetRating(11).GamesPlayed); Equal(0, replay.GetRating(12).GamesPlayed);
            Equal(0L, replay.GetRating(11).CoinsEarned); Equal(0L, replay.GetRating(12).CoinsEarned);
            True(replay.Report(12, tornId, 0, 2).Settled, "A torn two-player settlement could not be safely retried.");
            Equal(1, replay.GetRating(11).GamesPlayed); Equal(1, replay.GetRating(12).GamesPlayed);
        }

        string headerDirectory = DirectoryFor(root, "ranked-torn-checkpoint-header");
        var checkpoints = new RankedOptions { CheckpointInterval = 1 };
        long headerId;
        using (var service = new RankedService(headerDirectory, checkpoints)) headerId = service.BindMatch(21, 22).Id;
        File.WriteAllBytes(Path.Combine(headerDirectory, "ranked", "journal.bin"), new byte[] { 73, 82, 65 });
        using var headerReplay = new RankedService(headerDirectory, checkpoints);
        Equal(headerId, headerReplay.GetActive(21)!.Id);
        True(headerReplay.Report(21, headerId, forfeit: true).Settled, "Torn post-checkpoint journal header lost the active binding.");
    }

    internal static void CheckpointReplayAndRetention(string root)
    {
        string directory = DirectoryFor(root, "ranked-checkpoints");
        var options = new RankedOptions { CheckpointInterval = 3, RetainedMatches = 2 };
        long first, last;
        byte[] oldJournal;
        using (var service = new RankedService(directory, options))
        {
            first = last = service.BindMatch(1, 2).Id;
            service.Report(1, first, 1, 1);
            // The service keeps its writer open. On Windows even a read handle must
            // explicitly share that existing write access; do not relax the writer lock.
            using (var snapshot = new FileStream(Path.Combine(directory, "ranked", "journal.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                oldJournal = new byte[checked((int)snapshot.Length)];
                snapshot.ReadExactly(oldJournal);
            }
            service.Report(2, first, 1, 1); // checkpoint includes all three events
        }
        // Crash immediately after replacing the checkpoint but before truncating
        // its WAL: old, already-checkpointed frames must be ignored, not reapplied.
        File.WriteAllBytes(Path.Combine(directory, "ranked", "journal.bin"), oldJournal);
        using (var recovered = new RankedService(directory, options))
        {
            Equal(1, recovered.GetRating(1).GamesPlayed); Equal(1, recovered.GetRating(2).GamesPlayed);
            for (int i = 0; i < 5; i++)
            {
                last = recovered.BindMatch(1, 2).Id;
                recovered.Report(1, last, forfeit: true);
            }
            Throws<KeyNotFoundException>(() => recovered.GetMatch(1, first));
            Throws<KeyNotFoundException>(() => recovered.Report(1, first, 1, 1));
            Equal(6, recovered.GetRating(1).GamesPlayed);
            True(new FileInfo(Path.Combine(directory, "ranked", "journal.bin")).Length < 100000, "Ranked journal did not compact.");
        }
        using var again = new RankedService(directory, options);
        Equal(6, again.GetRating(1).GamesPlayed); Equal(6, again.GetRating(2).GamesPlayed);
        True(again.GetMatch(1, last).Settled, "Newest bounded receipt did not survive compaction.");
        Throws<KeyNotFoundException>(() => again.Report(1, first, forfeit: true));
        True(again.BindMatch(1, 2).Id > last, "Compaction permitted a retired ID to be reused.");
    }

    internal static void CorruptionAndWriterExclusion(string root)
    {
        string directory = DirectoryFor(root, "ranked-corrupt");
        using (var service = new RankedService(directory))
        {
            Throws<IOException>(() => { using var duplicate = new RankedService(directory); });
            var game = service.BindMatch(1, 2); service.Report(1, game.Id, forfeit: true);
        }
        string journal = Path.Combine(directory, "ranked", "journal.bin");
        byte[] bytes = File.ReadAllBytes(journal); bytes[^1] ^= 1; File.WriteAllBytes(journal, bytes);
        Throws<InvalidDataException>(() => { using var corrupt = new RankedService(directory); });
        var invalid = new RankedOptions { InitialRating = 5000 };
        Throws<InvalidDataException>(() => invalid.Validate());
    }

    private static string DirectoryFor(string root, string name)
    {
        string path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Ranked invariant failed: expected {expected}, got {actual}.");
    }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Ranked invariant failed: expected " + typeof(T).Name + ".");
    }
}
