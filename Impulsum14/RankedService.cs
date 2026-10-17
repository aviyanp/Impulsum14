using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impulsum14;

/// <summary>
/// Authoritative ranked ledger, independent of per-account aggregate snapshots.
/// Each flushed journal frame contains the complete transition for both players.
/// Recovery replays frames after an atomic checkpoint; torn tails are discarded.
/// Full corrupt frames fail closed. Retired IDs can never create new settlements.
/// This authenticates paired reports, not the simulation of a retail executable.
/// </summary>
internal sealed class RankedService : IDisposable
{
    internal static RankedService? Current { get; set; }
    private static readonly byte[] JournalMagic = Encoding.ASCII.GetBytes("IRANK001");
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int FrameHeaderBytes = 40;
    private const int MaxFrameBytes = 1024 * 1024;
    private const long MaxSnapshotBytes = 512L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly RankedOptions _options;
    private readonly string _directory;
    private readonly string _snapshotPath;
    private readonly string _journalPath;
    private readonly FileStream _writerLock;
    private FileStream _journal = null!;
    private Snapshot _state = new();
    private readonly Dictionary<long, long> _active = new();
    private readonly Queue<long> _terminal = new();
    private readonly ConcurrentDictionary<long, RankedRatingView> _ratings = new();
    private readonly ConcurrentDictionary<long, RankedMatchView> _matches = new();
    private readonly ConcurrentDictionary<long, RankedMatchView> _activeViews = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RankedMatchView>> _completion = new();
    private LeaderboardCache? _leaderboard;
    private long _publishedRevision;
    private int _eventsSinceCheckpoint;
    private bool _disposed;
    private Exception? _writeFailure;

    internal RankedService(string dataDirectory, RankedOptions? options = null)
    {
        _options = (options ?? new RankedOptions()).Copy();
        _options.Validate();
        string directory = _directory = Path.Combine(Path.GetFullPath(dataDirectory), "ranked");
        Directory.CreateDirectory(directory);
        _snapshotPath = Path.Combine(directory, "snapshot.json");
        _journalPath = Path.Combine(directory, "journal.bin");
        // A second server must not replay or append the same ledger concurrently.
        _writerLock = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            LoadSnapshot();
            BuildIndexes();
            OpenAndReplayJournal();
            FlushDirectory(directory);
            FlushDirectory(Path.GetDirectoryName(directory)!);
            BuildIndexes();
        }
        catch
        {
            _journal?.Dispose();
            _writerLock.Dispose();
            throw;
        }
    }

    /// <summary>Lock-free immutable snapshot; an unseen account has the configured
    /// initial rating and incurs no allocation in durable storage until paired.</summary>
    internal RankedRatingView GetRating(long accountId)
    {
        RequireAccount(accountId);
        return _ratings.TryGetValue(accountId, out var view) ? view : RatingView(NewPlayer(accountId));
    }

    internal bool TryGetRating(long accountId, [NotNullWhen(true)] out RankedRatingView? view)
    {
        RequireAccount(accountId);
        return _ratings.TryGetValue(accountId, out view);
    }

    internal RankedMatchView? GetActive(long accountId)
    {
        RequireAccount(accountId);
        return _activeViews.TryGetValue(accountId, out var match) ? match : null;
    }

    internal RankedMatchView GetMatch(long accountId, long matchId)
    {
        RequireAccount(accountId);
        if (!_matches.TryGetValue(matchId, out var match)) throw new KeyNotFoundException("Unknown or retired ranked match.");
        if (accountId != match.PlayerA && accountId != match.PlayerB) throw new UnauthorizedAccessException("Ranked match does not belong to this account.");
        return match;
    }

    internal async Task<RankedMatchView> WaitForSettlementAsync(long accountId, long matchId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var current = GetMatch(accountId, matchId);
        if (current.Settled || current.Cancelled || timeout == TimeSpan.Zero) return current;
        if (!_completion.TryGetValue(matchId, out var completion)) return GetMatch(accountId, matchId);
        try { return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return GetMatch(accountId, matchId); }
    }

    internal IReadOnlyList<RankedRatingView> GetLeaderboard(int limit = 50, int offset = 0)
    {
        if (limit is < 1 or > 200 || offset < 0 || offset > _options.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(limit), "Ranked leaderboard page is outside its bounds.");
        long revision = Volatile.Read(ref _publishedRevision);
        var cache = Volatile.Read(ref _leaderboard);
        if (cache is null || cache.Revision != revision)
        {
            var ordered = _ratings.Values.OrderByDescending(p => p.Rating).ThenByDescending(p => p.Wins).ThenBy(p => p.AccountId).ToArray();
            cache = new(revision, ordered);
            // If a settlement overlaps sorting, this cache is not reused by later
            // revisions; a reader still receives immutable, bounded account views.
            if (Volatile.Read(ref _publishedRevision) == revision) Volatile.Write(ref _leaderboard, cache);
        }
        return Array.AsReadOnly(cache.Players.Skip(offset).Take(limit).ToArray());
    }

    /// <summary>Server-only binding after a verified two-player pairing. This
    /// flushes disk and must be called outside the GameManager lobby lock.</summary>
    internal RankedMatchView BindMatch(long playerA, long playerB, ulong gameId = 0)
    {
        RequireAccount(playerA); RequireAccount(playerB);
        if (playerA == playerB) throw new InvalidOperationException("A ranked opponent must be a different account.");
        lock (_sync)
        {
            Writable();
            if (_active.TryGetValue(playerA, out long existingA) || _active.TryGetValue(playerB, out _))
            {
                if (_active.TryGetValue(playerA, out existingA) && _active.TryGetValue(playerB, out long existingB) && existingA == existingB)
                {
                    var prior = _state.Matches[existingA];
                    if (prior.GameId == gameId) return MatchView(prior);
                }
                throw new InvalidOperationException("Finish the active ranked match first.");
            }
            if (_active.Count / 2 >= _options.MaxActiveMatches) throw new InvalidOperationException("Ranked active match limit reached.");
            int additions = (_state.Players.ContainsKey(playerA) ? 0 : 1) + (_state.Players.ContainsKey(playerB) ? 0 : 1);
            if (_state.Players.Count + additions > _options.MaxPlayers) throw new InvalidOperationException("Ranked player limit reached.");
            var a = CopyPlayer(Player(playerA)); var b = CopyPlayer(Player(playerB));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var match = new Match
            {
                Id = _state.NextMatchId, GameId = gameId, PlayerA = playerA, PlayerB = playerB,
                CreatedAt = now, UpdatedAt = now, RatingBeforeA = a.Rating, RatingBeforeB = b.Rating,
                RatingAfterA = a.Rating, RatingAfterB = b.Rating
            };
            Commit(match, a, b);
            return MatchView(match);
        }
    }

    /// <summary>Only a fixed member can report. A score requires the peer's exact
    /// inverse corroboration. A forfeit can only concede the reporter's own game.</summary>
    internal RankedReportView Report(long accountId, long matchId, int? goalsFor = null, int? goalsAgainst = null, bool forfeit = false)
    {
        RequireAccount(accountId);
        if (forfeit ? goalsFor.HasValue || goalsAgainst.HasValue : !goalsFor.HasValue || !goalsAgainst.HasValue || goalsFor < 0 || goalsAgainst < 0 || goalsFor > 50 || goalsAgainst > 50)
            throw new InvalidDataException("Report a score from 0 to 50 for both sides, or an explicit self-forfeit.");
        lock (_sync)
        {
            Writable();
            if (!_state.Matches.TryGetValue(matchId, out var prior)) throw new KeyNotFoundException("Unknown or retired ranked match.");
            bool isA = accountId == prior.PlayerA;
            if (!isA && accountId != prior.PlayerB) throw new UnauthorizedAccessException("Ranked match does not belong to this account.");
            if (prior.Status == "cancelled") throw new InvalidOperationException("Ranked match was cancelled without a result.");
            if (prior.Status == "settled") return new(MatchView(prior), GetRating(accountId), true);
            int normalizedA = isA ? goalsFor ?? 0 : goalsAgainst ?? 0;
            int normalizedB = isA ? goalsAgainst ?? 0 : goalsFor ?? 0;
            var previous = isA ? prior.ReportA : prior.ReportB;
            if (!forfeit && previous is not null)
            {
                if (previous.GoalsA != normalizedA || previous.GoalsB != normalizedB) throw new InvalidOperationException("A submitted ranked score cannot be changed; a player may still concede.");
                return new(MatchView(prior), GetRating(accountId), true);
            }
            var match = CopyMatch(prior);
            var report = new ScoreReport { GoalsA = normalizedA, GoalsB = normalizedB };
            if (isA) match.ReportA = report; else match.ReportB = report;
            match.UpdatedAt = Math.Max(prior.UpdatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            RankedPlayer? a = null, b = null;
            if (forfeit)
            {
                match.ForfeitedBy = accountId;
                match.GoalsA = isA ? 0 : 3; match.GoalsB = isA ? 3 : 0;
                a = CopyPlayer(_state.Players[match.PlayerA]); b = CopyPlayer(_state.Players[match.PlayerB]);
                Settle(match, a, b);
            }
            else if (match.ReportA is not null && match.ReportB is not null)
            {
                if (match.ReportA.GoalsA == match.ReportB.GoalsA && match.ReportA.GoalsB == match.ReportB.GoalsB)
                {
                    match.GoalsA = match.ReportA.GoalsA; match.GoalsB = match.ReportA.GoalsB;
                    a = CopyPlayer(_state.Players[match.PlayerA]); b = CopyPlayer(_state.Players[match.PlayerB]);
                    Settle(match, a, b);
                }
                else match.Status = "conflict";
            }
            Commit(match, a, b);
            return new(MatchView(match), GetRating(accountId), false);
        }
    }

    /// <summary>Server cancellation awards neither side. It is appropriate before
    /// gameplay or when a result cannot be established, never to declare a winner.</summary>
    internal bool TryGetMatchForServer(long matchId, out RankedMatchView? match) => _matches.TryGetValue(matchId, out match);

    internal RankedMatchView AbandonMatch(long matchId, bool allowReported = false)
    {
        lock (_sync)
        {
            Writable();
            if (!_state.Matches.TryGetValue(matchId, out var prior)) throw new KeyNotFoundException("Unknown or retired ranked match.");
            if (Terminal(prior)) return MatchView(prior);
            if (!allowReported && (prior.ReportA is not null || prior.ReportB is not null))
                throw new InvalidOperationException("A reported game requires corroboration, a self-forfeit or server timeout.");
            var match = CopyMatch(prior);
            match.Status = "cancelled"; match.UpdatedAt = Math.Max(prior.UpdatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Commit(match, null, null);
            return MatchView(match);
        }
    }

    internal int ExpireMatches() => ExpireMatches(DateTimeOffset.UtcNow.AddMinutes(-_options.MatchTimeoutMinutes));

    internal int ExpireMatches(DateTimeOffset cutoff)
    {
        lock (_sync)
        {
            Writable();
            var expired = _active.Values.Distinct().Where(id => _state.Matches[id].CreatedAt <= cutoff.ToUnixTimeSeconds()).ToArray();
            foreach (long id in expired) AbandonMatch(id, allowReported: true);
            return expired.Length;
        }
    }

    private void Settle(Match match, RankedPlayer a, RankedPlayer b)
    {
        if (!match.GoalsA.HasValue || !match.GoalsB.HasValue) throw new InvalidOperationException("An unsettled score cannot change ratings.");
        int comparison = match.GoalsA.Value.CompareTo(match.GoalsB.Value);
        double outcome = comparison > 0 ? 1 : comparison < 0 ? 0 : 0.5;
        bool provisional = a.GamesPlayed < _options.ProvisionalGames || b.GamesPlayed < _options.ProvisionalGames;
        int k = provisional ? _options.ProvisionalKFactor : _options.KFactor;
        double expected = 1 / (1 + Math.Pow(10, (b.Rating - a.Rating) / 400d));
        int delta = (int)Math.Round(k * (outcome - expected), MidpointRounding.AwayFromZero);
        // Clamp the shared transfer, rather than clamping players independently.
        // Rating bounds therefore preserve the exact original two-player sum.
        delta = Math.Clamp(delta, Math.Max(_options.MinimumRating - a.Rating, b.Rating - _options.MaximumRating), Math.Min(_options.MaximumRating - a.Rating, b.Rating - _options.MinimumRating));
        match.RatingBeforeA = a.Rating; match.RatingBeforeB = b.Rating;
        a.Rating += delta; b.Rating -= delta;
        match.RatingAfterA = a.Rating; match.RatingAfterB = b.Rating;
        int seasonA = AdvanceSeason(a, comparison, match.UpdatedAt);
        int seasonB = AdvanceSeason(b, -comparison, match.UpdatedAt);
        int coinsA = MatchCoins(a.AccountId, comparison, match.GoalsA.Value, match.ForfeitedBy);
        int coinsB = MatchCoins(b.AccountId, -comparison, match.GoalsB.Value, match.ForfeitedBy);
        checked
        {
            a.CoinsEarned += coinsA + seasonA; b.CoinsEarned += coinsB + seasonB;
            a.GamesPlayed++; b.GamesPlayed++;
            if (comparison > 0) { a.Wins++; b.Losses++; }
            else if (comparison < 0) { a.Losses++; b.Wins++; }
            else { a.Draws++; b.Draws++; }
        }
        match.CoinsA = coinsA + seasonA; match.CoinsB = coinsB + seasonB;
        match.SeasonCoinsA = seasonA; match.SeasonCoinsB = seasonB;
        match.Status = "settled";
    }

    private int MatchCoins(long accountId, int comparison, int goals, long? forfeitedBy)
    {
        if (forfeitedBy == accountId) return 0;
        return checked((comparison > 0 ? _options.WinCoins : comparison < 0 ? _options.LossCoins : _options.DrawCoins) + (forfeitedBy.HasValue ? 0 : goals * _options.GoalCoins));
    }

    private int AdvanceSeason(RankedPlayer player, int comparison, long finishedAt)
    {
        var s = player.Season;
        checked
        {
            s.GamesPlayed++;
            if (comparison > 0) { s.GamesWon++; s.Points += 3; }
            else if (comparison < 0) s.GamesLost++;
            else { s.GamesDraw++; s.Points++; }
        }
        if (s.GamesPlayed < Seasons.Rounds) return 0;
        var resolution = Seasons.Evaluate(player.Division, s.Points, online: true);
        player.History.Add(new(s.SeasonId, player.Division, s.Points, s.GamesWon, s.GamesDraw, s.GamesLost, resolution.Outcome, resolution.Coins, finishedAt));
        while (player.History.Count > _options.SeasonHistoryLimit) player.History.RemoveAt(0);
        player.Season = new Season
        {
            SeasonId = checked(s.SeasonId + 1), TitlesWon = checked(s.TitlesWon + (resolution.Title ? 1 : 0)),
            Promotions = checked(s.Promotions + (resolution.NextDivision < player.Division ? 1 : 0)),
            Relegations = checked(s.Relegations + (resolution.NextDivision > player.Division ? 1 : 0))
        };
        player.Division = resolution.NextDivision;
        return resolution.Coins;
    }

    private void Commit(Match match, RankedPlayer? a, RankedPlayer? b)
    {
        long nextSequence = checked(_state.Sequence + 1);
        if (a is not null) a.Revision = nextSequence;
        if (b is not null) b.Revision = nextSequence;
        var record = new JournalRecord { Sequence = nextSequence, NextMatchId = Math.Max(_state.NextMatchId, checked(match.Id + 1)), Match = match, PlayerA = a, PlayerB = b };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Ranked journal transition exceeds the supported size.");
        try
        {
            Span<byte> header = stackalloc byte[FrameHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], ~payload.Length);
            SHA256.HashData(payload, header[8..]);
            _journal.Write(header); _journal.Write(payload); _journal.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write may have reached disk; continuing in this process could
            // acknowledge a different outcome. Restart recovery decides once.
            _writeFailure = ex;
            throw;
        }
        Apply(record, publish: true);
        _eventsSinceCheckpoint++;
        if (_eventsSinceCheckpoint >= _options.CheckpointInterval || _journal.Length >= _options.MaxJournalBytes)
        {
            try { Checkpoint(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The event is already durable and acknowledged. Stop further
                // writes if compaction was interrupted at an uncertain boundary;
                // restart chooses the completed snapshot and valid journal tail.
                _writeFailure = ex;
            }
        }
    }

    private void Apply(JournalRecord record, bool publish)
    {
        bool wasTerminal = _state.Matches.TryGetValue(record.Match.Id, out var previous) && Terminal(previous);
        _state.Sequence = record.Sequence; _state.NextMatchId = record.NextMatchId;
        if (record.PlayerA is not null) _state.Players[record.PlayerA.AccountId] = record.PlayerA;
        if (record.PlayerB is not null) _state.Players[record.PlayerB.AccountId] = record.PlayerB;
        _state.Matches[record.Match.Id] = record.Match;
        if (Terminal(record.Match))
        {
            _active.Remove(record.Match.PlayerA); _active.Remove(record.Match.PlayerB);
            if (!wasTerminal) _terminal.Enqueue(record.Match.Id);
        }
        else
        {
            _active[record.Match.PlayerA] = record.Match.Id; _active[record.Match.PlayerB] = record.Match.Id;
        }
        if (publish)
        {
            if (record.PlayerA is not null) _ratings[record.PlayerA.AccountId] = RatingView(record.PlayerA);
            if (record.PlayerB is not null) _ratings[record.PlayerB.AccountId] = RatingView(record.PlayerB);
            var view = MatchView(record.Match);
            _matches[view.Id] = view;
            if (Terminal(record.Match))
            {
                _activeViews.TryRemove(view.PlayerA, out _); _activeViews.TryRemove(view.PlayerB, out _);
                if (_completion.TryRemove(view.Id, out var completion)) completion.TrySetResult(view);
            }
            else
            {
                _completion.GetOrAdd(view.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                _activeViews[view.PlayerA] = view; _activeViews[view.PlayerB] = view;
            }
        }
        Prune(publish);
        if (publish) Volatile.Write(ref _publishedRevision, record.Sequence);
    }

    private void Prune(bool publish)
    {
        while (_terminal.Count > _options.RetainedMatches)
        {
            long id = _terminal.Dequeue(); _state.Matches.Remove(id);
            if (publish) _matches.TryRemove(id, out _);
        }
    }

    private void LoadSnapshot()
    {
        if (!File.Exists(_snapshotPath)) return;
        if (new FileInfo(_snapshotPath).Length > MaxSnapshotBytes) throw new InvalidDataException("Ranked checkpoint exceeds its size limit.");
        try { _state = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(_snapshotPath), Json) ?? throw new InvalidDataException("Empty ranked checkpoint."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid ranked checkpoint.", ex); }
        ValidateSnapshot();
    }

    private void OpenAndReplayJournal()
    {
        _journal = new FileStream(_journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 16384, FileOptions.SequentialScan);
        if (_journal.Length == 0) { _journal.Write(JournalMagic); _journal.Flush(flushToDisk: true); return; }
        if (_journal.Length < JournalMagic.Length)
        {
            byte[] prefix = new byte[(int)_journal.Length]; _journal.ReadExactly(prefix);
            if (!JournalMagic.AsSpan(0, prefix.Length).SequenceEqual(prefix)) throw new InvalidDataException("Invalid ranked journal header.");
            // A header can be torn only before any frame in a new ledger or
            // after a complete checkpoint has replaced the previous journal.
            _journal.SetLength(0); _journal.Position = 0; _journal.Write(JournalMagic); _journal.Flush(flushToDisk: true); return;
        }
        if (_journal.Length > (long)_options.MaxJournalBytes + MaxFrameBytes * 2L) throw new InvalidDataException("Invalid ranked journal length.");
        Span<byte> magic = stackalloc byte[JournalMagic.Length]; _journal.ReadExactly(magic);
        if (!magic.SequenceEqual(JournalMagic)) throw new InvalidDataException("Unsupported ranked journal format.");
        long validLength = _journal.Position;
        Span<byte> header = stackalloc byte[FrameHeaderBytes];
        while (_journal.Position < _journal.Length)
        {
            if (_journal.Length - _journal.Position < FrameHeaderBytes) break;
            _journal.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            // The complement protects the length before interpreting a short
            // tail; header corruption must not masquerade as an incomplete write.
            if (length is <= 0 or > MaxFrameBytes || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != ~length) throw new InvalidDataException("Invalid ranked journal frame length.");
            if (_journal.Length - _journal.Position < length) break;
            byte[] payload = new byte[length]; _journal.ReadExactly(payload);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header[8..])) throw new InvalidDataException("Ranked journal checksum mismatch.");
            JournalRecord record;
            try { record = JsonSerializer.Deserialize<JournalRecord>(payload, Json) ?? throw new InvalidDataException("Empty ranked journal frame."); }
            catch (JsonException ex) { throw new InvalidDataException("Invalid ranked journal frame.", ex); }
            if (record.Sequence > _state.Sequence)
            {
                ValidateRecord(record);
                Apply(record, publish: false);
            }
            validLength = _journal.Position;
        }
        if (validLength != _journal.Length) { _journal.SetLength(validLength); _journal.Flush(flushToDisk: true); }
        _journal.Position = _journal.Length;
    }

    private void BuildIndexes()
    {
        // Replay may already have populated indexes; rebuild them from retained
        // state so checkpoint-only starts and replay starts have identical bounds.
        _active.Clear(); _terminal.Clear(); _ratings.Clear(); _matches.Clear(); _activeViews.Clear(); _completion.Clear();
        ValidateSnapshot();
        foreach (var player in _state.Players.Values) _ratings[player.AccountId] = RatingView(player);
        foreach (var match in _state.Matches.Values.OrderBy(m => m.Id))
        {
            if (Terminal(match)) _terminal.Enqueue(match.Id);
            else
            {
                if (!_active.TryAdd(match.PlayerA, match.Id) || !_active.TryAdd(match.PlayerB, match.Id)) throw new InvalidDataException("Ranked player has multiple active matches.");
                _completion[match.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var active = MatchView(match); _activeViews[match.PlayerA] = active; _activeViews[match.PlayerB] = active;
            }
            _matches[match.Id] = MatchView(match);
        }
        if (_active.Count / 2 > _options.MaxActiveMatches) throw new InvalidDataException("Ranked active match limit exceeded.");
        Prune(publish: true);
        Volatile.Write(ref _publishedRevision, _state.Sequence);
    }

    private void Checkpoint()
    {
        string temporary = _snapshotPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, _state, Json);
            if (file.Length > MaxSnapshotBytes) throw new InvalidDataException("Ranked checkpoint exceeds its size limit.");
            file.Flush(flushToDisk: true);
        }
        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH. Both paths are
            // in one directory; never use a copy/delete fallback across volumes.
            if (!MoveFileEx(temporary, _snapshotPath, 0x1 | 0x8)) throw new IOException("Unable to replace ranked checkpoint.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        else
        {
            File.Move(temporary, _snapshotPath, overwrite: true);
            // fsync(file) alone does not make the renamed directory entry
            // durable. Persist it before destroying the previous journal.
            FlushDirectory(_directory);
        }
        // Snapshot must be replaced before truncation. If interrupted between
        // these steps, replay skips journal sequences already in the snapshot.
        _journal.SetLength(0); _journal.Position = 0;
        _journal.Write(JournalMagic); _journal.Flush(flushToDisk: true);
        _eventsSinceCheckpoint = 0;
    }

    private void ValidateSnapshot()
    {
        if (_state.Version != 1 || _state.Sequence < 0 || _state.NextMatchId < 1 || _state.Players is null || _state.Matches is null || _state.Players.Count > _options.MaxPlayers || _state.Matches.Count > _options.MaxActiveMatches + 100000)
            throw new InvalidDataException("Invalid ranked checkpoint bounds.");
        foreach (var pair in _state.Players)
        {
            if (pair.Value is null || pair.Key != pair.Value.AccountId) throw new InvalidDataException("Invalid ranked player identity.");
            ValidatePlayer(pair.Value);
            if (pair.Value.Revision > _state.Sequence) throw new InvalidDataException("Ranked player revision exceeds the checkpoint.");
        }
        foreach (var pair in _state.Matches)
        {
            if (pair.Value is null || pair.Key != pair.Value.Id || pair.Key >= _state.NextMatchId) throw new InvalidDataException("Invalid ranked match identity.");
            ValidateMatch(pair.Value);
        }
    }

    private void ValidateRecord(JournalRecord r)
    {
        if (r.Version != 1 || r.Sequence != _state.Sequence + 1 || r.NextMatchId < _state.NextMatchId || r.Match is null || r.Match.Id >= r.NextMatchId) throw new InvalidDataException("Ranked journal sequence is inconsistent.");
        ValidateMatch(r.Match, r.PlayerA, r.PlayerB);
        if ((r.PlayerA is null) != (r.PlayerB is null)) throw new InvalidDataException("A ranked settlement must contain both players.");
        if (r.PlayerA is not null)
        {
            if (r.PlayerA.AccountId != r.Match.PlayerA || r.PlayerB!.AccountId != r.Match.PlayerB || r.PlayerA.Revision != r.Sequence || r.PlayerB.Revision != r.Sequence) throw new InvalidDataException("Ranked transition identities are inconsistent.");
            ValidatePlayer(r.PlayerA); ValidatePlayer(r.PlayerB);
        }
        bool exists = _state.Matches.TryGetValue(r.Match.Id, out var prior);
        if (!exists && r.Match.Id < _state.NextMatchId) throw new InvalidDataException("A retired ranked match cannot be replayed.");
        if (exists && (prior!.PlayerA != r.Match.PlayerA || prior.PlayerB != r.Match.PlayerB || Terminal(prior))) throw new InvalidDataException("A ranked match was rebound or settled twice.");
        if (exists && r.NextMatchId != _state.NextMatchId || !exists && (r.Match.Id != _state.NextMatchId || r.NextMatchId != _state.NextMatchId + 1 || r.Match.Status != "pending" || r.PlayerA is null))
            throw new InvalidDataException("Ranked match ID allocation is inconsistent.");
        if (r.Match.Status == "settled")
        {
            if (r.PlayerA is null || r.PlayerB is null) throw new InvalidDataException("A ranked settlement must atomically update both accounts.");
            ValidateSettlementPlayer(_state.Players[r.Match.PlayerA], r.PlayerA, r.Match.RatingBeforeA, r.Match.RatingAfterA, r.Match.CoinsA);
            ValidateSettlementPlayer(_state.Players[r.Match.PlayerB], r.PlayerB, r.Match.RatingBeforeB, r.Match.RatingAfterB, r.Match.CoinsB);
        }
        else if (exists && r.PlayerA is not null) throw new InvalidDataException("An unconfirmed report cannot update account ratings or rewards.");
    }

    private static void ValidateSettlementPlayer(RankedPlayer before, RankedPlayer after, int receiptBefore, int receiptAfter, int coins)
    {
        if (before.Rating != receiptBefore || after.Rating != receiptAfter || (long)after.GamesPlayed != (long)before.GamesPlayed + 1 || (long)after.Wins + after.Draws + after.Losses != (long)before.GamesPlayed + 1 || after.Wins < before.Wins || after.Draws < before.Draws || after.Losses < before.Losses || after.CoinsEarned < before.CoinsEarned || after.CoinsEarned - before.CoinsEarned != coins)
            throw new InvalidDataException("Ranked settlement is not one complete account transition.");
    }

    private void ValidatePlayer(RankedPlayer p)
    {
        if (p.AccountId <= 0 || p.Rating < _options.MinimumRating || p.Rating > _options.MaximumRating || p.Division is < 1 or > 10 || p.GamesPlayed < 0 || p.Wins < 0 || p.Draws < 0 || p.Losses < 0 || (long)p.Wins + p.Draws + p.Losses != p.GamesPlayed || p.CoinsEarned < 0 || p.Revision < 0 || p.Season is null || p.History is null || p.History.Count > 100)
            throw new InvalidDataException("Invalid ranked player state or changed rating bounds.");
        var s = p.Season;
        if (s.SeasonId < 1 || s.GamesPlayed is < 0 or >= Seasons.Rounds || s.GamesWon < 0 || s.GamesDraw < 0 || s.GamesLost < 0 || (long)s.GamesWon + s.GamesDraw + s.GamesLost != s.GamesPlayed || (long)s.GamesWon * 3 + s.GamesDraw != s.Points || s.TitlesWon < 0 || s.Promotions < 0 || s.Relegations < 0 || (long)(s.SeasonId - 1) * Seasons.Rounds + s.GamesPlayed != p.GamesPlayed || s.GamesWon > p.Wins || s.GamesDraw > p.Draws || s.GamesLost > p.Losses)
            throw new InvalidDataException("Invalid ranked season state.");
        int previousSeason = 0;
        foreach (var h in p.History)
        {
            if (h is null || h.SeasonId <= previousSeason || h.SeasonId >= s.SeasonId || h.Division is < 1 or > 10 || h.Won < 0 || h.Draw < 0 || h.Lost < 0 || (long)h.Won + h.Draw + h.Lost != Seasons.Rounds || (long)h.Won * 3 + h.Draw != h.Points || h.Coins < 0 || h.FinishedAt <= 0 || h.Outcome is not ("CHAMPIONSHIP" or "PROMOTION" or "RELEGATION" or "MAINTENANCE"))
                throw new InvalidDataException("Invalid ranked season history.");
            previousSeason = h.SeasonId;
        }
        // Retention is an operational bound and may be lowered across restarts;
        // this changes neither authoritative totals nor settled game IDs.
        while (p.History.Count > _options.SeasonHistoryLimit) p.History.RemoveAt(0);
    }

    private void ValidateMatch(Match m, RankedPlayer? a = null, RankedPlayer? b = null)
    {
        if (m.Id < 1 || m.PlayerA <= 0 || m.PlayerB <= 0 || m.PlayerA == m.PlayerB || m.CreatedAt <= 0 || m.UpdatedAt < m.CreatedAt || m.Status is not ("pending" or "conflict" or "settled" or "cancelled") || (!_state.Players.ContainsKey(m.PlayerA) && a?.AccountId != m.PlayerA) || (!_state.Players.ContainsKey(m.PlayerB) && b?.AccountId != m.PlayerB))
            throw new InvalidDataException("Invalid ranked match state.");
        if (m.Status == "settled" && (!m.GoalsA.HasValue || !m.GoalsB.HasValue || m.GoalsA < 0 || m.GoalsB < 0 || m.GoalsA > 50 || m.GoalsB > 50 || m.RatingBeforeA + m.RatingBeforeB != m.RatingAfterA + m.RatingAfterB || m.ForfeitedBy is long forfeited && forfeited != m.PlayerA && forfeited != m.PlayerB))
            throw new InvalidDataException("Invalid ranked settlement receipt.");
        if (m.ReportA is not null && !ValidReport(m.ReportA) || m.ReportB is not null && !ValidReport(m.ReportB) || m.CoinsA < 0 || m.CoinsB < 0 || m.SeasonCoinsA < 0 || m.SeasonCoinsB < 0 || m.SeasonCoinsA > m.CoinsA || m.SeasonCoinsB > m.CoinsB)
            throw new InvalidDataException("Invalid ranked report or reward receipt.");
        if (m.Status == "settled")
        {
            bool corroborated = m.ForfeitedBy is null && m.ReportA is not null && m.ReportB is not null && m.ReportA.GoalsA == m.GoalsA && m.ReportA.GoalsB == m.GoalsB && m.ReportB.GoalsA == m.GoalsA && m.ReportB.GoalsB == m.GoalsB;
            bool conceded = m.ForfeitedBy == m.PlayerA && m.GoalsA == 0 && m.GoalsB == 3 && m.CoinsA == m.SeasonCoinsA || m.ForfeitedBy == m.PlayerB && m.GoalsA == 3 && m.GoalsB == 0 && m.CoinsB == m.SeasonCoinsB;
            if (!corroborated && !conceded) throw new InvalidDataException("Ranked receipt has no corroborated score or self-forfeit.");
        }
    }

    private static bool ValidReport(ScoreReport report) => report.GoalsA is >= 0 and <= 50 && report.GoalsB is >= 0 and <= 50;

    private RankedPlayer Player(long id) => _state.Players.TryGetValue(id, out var player) ? player : NewPlayer(id);
    private RankedPlayer NewPlayer(long id) => new() { AccountId = id, Rating = _options.InitialRating };
    private static void RequireAccount(long id) { if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "Ranked accounts must have a positive ID."); }
    private static bool Terminal(Match m) => m.Status is "settled" or "cancelled";
    private void Writable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeFailure is not null) throw new IOException("Ranked ledger requires restart after an uncertain write.", _writeFailure);
    }

    private static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        int descriptor = OpenDirectory(path, 0); // POSIX O_RDONLY, including directories.
        if (descriptor < 0) throw new IOException("Unable to open ranked checkpoint directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (SyncDescriptor(descriptor) != 0) throw new IOException("Unable to persist ranked checkpoint directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { CloseDescriptor(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDescriptor(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDescriptor(int descriptor);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFile, string replacementFile, uint flags);

    private RankedRatingView RatingView(RankedPlayer p) => new(p.AccountId, p.Rating, p.GamesPlayed, p.Wins, p.Draws, p.Losses, p.GamesPlayed < _options.ProvisionalGames, p.Division,
        new(p.Season.SeasonId, p.Season.GamesPlayed, p.Season.GamesWon, p.Season.GamesDraw, p.Season.GamesLost, p.Season.Points, p.Season.TitlesWon, p.Season.Promotions, p.Season.Relegations), Array.AsReadOnly(p.History.ToArray()), p.CoinsEarned, p.Revision);
    private static RankedMatchView MatchView(Match m) => new(m.Id, m.GameId, m.PlayerA, m.PlayerB, m.CreatedAt, m.UpdatedAt, m.Status, m.GoalsA, m.GoalsB, m.ReportA is not null, m.ReportB is not null, m.ForfeitedBy, m.RatingBeforeA, m.RatingBeforeB, m.RatingAfterA, m.RatingAfterB, m.CoinsA, m.CoinsB, m.SeasonCoinsA, m.SeasonCoinsB);
    private static RankedPlayer CopyPlayer(RankedPlayer p) => new()
    {
        AccountId = p.AccountId, Rating = p.Rating, GamesPlayed = p.GamesPlayed, Wins = p.Wins, Draws = p.Draws, Losses = p.Losses,
        Division = p.Division, CoinsEarned = p.CoinsEarned, Revision = p.Revision, History = new(p.History),
        Season = new() { SeasonId = p.Season.SeasonId, GamesPlayed = p.Season.GamesPlayed, GamesWon = p.Season.GamesWon, GamesDraw = p.Season.GamesDraw, GamesLost = p.Season.GamesLost, Points = p.Season.Points, TitlesWon = p.Season.TitlesWon, Promotions = p.Season.Promotions, Relegations = p.Season.Relegations }
    };
    private static Match CopyMatch(Match m) => new()
    {
        Id = m.Id, GameId = m.GameId, PlayerA = m.PlayerA, PlayerB = m.PlayerB, CreatedAt = m.CreatedAt, UpdatedAt = m.UpdatedAt, Status = m.Status,
        GoalsA = m.GoalsA, GoalsB = m.GoalsB, ReportA = m.ReportA, ReportB = m.ReportB, ForfeitedBy = m.ForfeitedBy,
        RatingBeforeA = m.RatingBeforeA, RatingBeforeB = m.RatingBeforeB, RatingAfterA = m.RatingAfterA, RatingAfterB = m.RatingAfterB,
        CoinsA = m.CoinsA, CoinsB = m.CoinsB, SeasonCoinsA = m.SeasonCoinsA, SeasonCoinsB = m.SeasonCoinsB
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            // Every accepted transition was already flushed. Do not require a
            // checkpoint on shutdown, so normal tests also exercise WAL recovery.
            _journal.Dispose(); _writerLock.Dispose();
            foreach (var completion in _completion.Values) completion.TrySetCanceled();
            _completion.Clear();
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }

    private sealed record LeaderboardCache(long Revision, RankedRatingView[] Players);
    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public long Sequence { get; set; }
        public long NextMatchId { get; set; } = 1;
        public Dictionary<long, RankedPlayer> Players { get; set; } = new();
        public Dictionary<long, Match> Matches { get; set; } = new();
    }
    private sealed class JournalRecord
    {
        public int Version { get; set; } = 1;
        public long Sequence { get; set; }
        public long NextMatchId { get; set; }
        public Match Match { get; set; } = null!;
        public RankedPlayer? PlayerA { get; set; }
        public RankedPlayer? PlayerB { get; set; }
    }
    private sealed class RankedPlayer
    {
        public long AccountId { get; set; }
        public int Rating { get; set; }
        public int GamesPlayed { get; set; }
        public int Wins { get; set; }
        public int Draws { get; set; }
        public int Losses { get; set; }
        public int Division { get; set; } = 10;
        public Season Season { get; set; } = new();
        public List<RankedSeasonHistoryView> History { get; set; } = new();
        public long CoinsEarned { get; set; }
        public long Revision { get; set; }
    }
    private sealed class Season
    {
        public int SeasonId { get; set; } = 1;
        public int GamesPlayed { get; set; }
        public int GamesWon { get; set; }
        public int GamesDraw { get; set; }
        public int GamesLost { get; set; }
        public int Points { get; set; }
        public int TitlesWon { get; set; }
        public int Promotions { get; set; }
        public int Relegations { get; set; }
    }
    private sealed class ScoreReport
    {
        public int GoalsA { get; set; }
        public int GoalsB { get; set; }
    }
    private sealed class Match
    {
        public long Id { get; set; }
        public ulong GameId { get; set; }
        public long PlayerA { get; set; }
        public long PlayerB { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public string Status { get; set; } = "pending";
        public int? GoalsA { get; set; }
        public int? GoalsB { get; set; }
        public ScoreReport? ReportA { get; set; }
        public ScoreReport? ReportB { get; set; }
        public long? ForfeitedBy { get; set; }
        public int RatingBeforeA { get; set; }
        public int RatingBeforeB { get; set; }
        public int RatingAfterA { get; set; }
        public int RatingAfterB { get; set; }
        public int CoinsA { get; set; }
        public int CoinsB { get; set; }
        public int SeasonCoinsA { get; set; }
        public int SeasonCoinsB { get; set; }
    }
}
