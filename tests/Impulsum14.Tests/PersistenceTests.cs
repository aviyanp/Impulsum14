using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Impulsum14;

// Register after the shared live fixture has stopped and its repository has
// closed. These fixtures deliberately exercise exclusive store ownership.
internal static class PersistenceTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Initialization and failed durable commits roll the whole account back", () => WithFixture(async fixture =>
        {
            var initial = new PlayerState();
            initial.Club.AllPlayersSeeded = true;
            initial.Club.Seeded = true;
            initial.Club.ActiveSquadId = 77;
            File.WriteAllBytes(fixture.StatePath(2001), JsonSerializer.SerializeToUtf8Bytes(initial, PlayerRepository.JsonOptions));
            var session = fixture.Repository.Authenticate(fixture.Token1);
            await ThrowsAsync<InvalidOperationException>(() => Run(fixture.Repository, session, () => throw new InvalidOperationException("failed first request")));
            Check(!session.Account.Initialized, "Failed first request retained initialization flag.");
            Check(session.Account.Data.Club.AllPlayersSeeded && session.Account.Data.Club.ActiveSquadId == 77, "Initialization escaped transaction rollback.");

            await Run(fixture.Repository, session, () => { });
            Check(session.Account.Initialized && !session.Account.Data.Club.AllPlayersSeeded && session.Account.Data.Club.ActiveSquadId == 0, "Initialization retry did not finish.");
            await Run(fixture.Repository, session, () =>
            {
                PlayerContext.MarkDirty();
                PlayerContext.Current.Data.Profile.Coins = 100;
                PlayerContext.Current.Data.Market.HeldCoins = 25;
                PlayerContext.Current.Data.ClientData["committed"] = "yes";
            });
            byte[] stored = File.ReadAllBytes(fixture.StatePath(2001));
            byte[] committed = session.Account.Committed;
            long revision = session.Account.Revision;
            for (int i = 0; i < 20; i++) await Run(fixture.Repository, session, () => Check(PlayerContext.Current.Data.Profile.Coins == 100, "Read observed another balance."));
            Check(ReferenceEquals(committed, session.Account.Committed) && revision == session.Account.Revision, "Read requests rewrote their committed snapshot.");

            string temporary = fixture.StatePath(2001) + ".tmp";
            Directory.CreateDirectory(temporary); // Deterministic failure before atomic replacement.
            bool failed = false;
            try
            {
                await Run(fixture.Repository, session, () =>
                {
                    PlayerContext.MarkDirty();
                    PlayerContext.Current.Data.Profile.Coins += 900;
                    PlayerContext.Current.Data.Market.HeldCoins += 400;
                    PlayerContext.Current.Data.ClientData["committed"] = "no";
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            finally { Directory.Delete(temporary); }
            Check(failed, "Blocked temporary file did not fail the transaction.");
            Check(session.Account.Data.Profile.Coins == 100 && session.Account.Data.Market.HeldCoins == 25 && session.Account.Data.ClientData["committed"] == "yes", "Commit failure partially retained wallet or progress changes.");
            Check(session.Account.Initialized && !session.Account.Dirty && session.Account.Revision == revision, "Commit failure damaged transaction bookkeeping.");
            Check(stored.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.StatePath(2001))), "Failed commit replaced the durable snapshot.");

            await ThrowsAsync<InvalidOperationException>(() => Run(fixture.Repository, session, () => FutProfileStore.Mutate(profile =>
            {
                profile.Coins += 500;
                throw new InvalidOperationException("failed profile callback");
            })));
            await ThrowsAsync<InvalidOperationException>(() => Run(fixture.Repository, session, () => ClubStore.Mutate(club =>
            {
                club.ActiveSquadId = 999;
                throw new InvalidOperationException("failed club callback");
            })));
            Check(session.Account.Data.Profile.Coins == 100 && session.Account.Data.Club.ActiveSquadId == 0, "A throwing store callback escaped transaction rollback.");

            await ThrowsAsync<InvalidDataException>(() => Run(fixture.Repository, session, () =>
            {
                PlayerContext.MarkDirty();
                PlayerContext.Current.Data.Profile.Coins = -1;
            }));
            Check(session.Account.Data.Profile.Coins == 100, "Wallet invariant failure did not roll back.");

            session.Account.HttpCache["credits"] = new(session.Account.Revision, 0, new HttpReply(200, "application/json", new byte[16], new NameValueCollection()));
            session.Account.HttpCacheBytes = 16;
            await Run(fixture.Repository, session, () =>
            {
                PlayerContext.MarkDirty();
                Check(session.Account.HttpCache.Count == 0 && session.Account.HttpCacheBytes == 0, "Dirty transaction retained a pre-settlement cached response.");
            });
            Check(session.Account.Revision == revision, "A byte-identical mutation rewrote its snapshot.");
        }));

        test("Revoked queued sessions cannot act or revoke a successor", () => WithFixture(async fixture =>
        {
            var old = fixture.Repository.Authenticate(fixture.Token1);
            var entered = Signal();
            var release = Signal();
            Task first = fixture.Repository.RunAsync(old, async () => { entered.SetResult(); await release.Task; return 0; });
            await entered.Task;
            bool invoked = false;
            Task queued = Run(fixture.Repository, old, () => invoked = true);
            fixture.Repository.Revoke(old);
            release.SetResult();
            await first;
            await ThrowsAsync<UnauthorizedAccessException>(() => queued);
            Check(!invoked && !fixture.Repository.IsSessionCurrent(old), "Revoked work executed after waiting for its gate.");
            Throws<UnauthorizedAccessException>(() => fixture.Repository.ResolveWeb(new NameValueCollection { ["X-UT-SID"] = old.Key }));

            var replacement = fixture.Repository.Authenticate(fixture.Token1);
            var successor = fixture.Repository.NewSession(replacement.Account);
            fixture.Repository.Revoke(replacement);
            Check(fixture.Repository.IsSessionCurrent(successor), "Revoking an old session removed its successor.");
            Check(!fixture.Repository.IsSessionCurrent(replacement), "Rotated session remained valid.");
            var sessions = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => fixture.Repository.Authenticate(fixture.Token1))));
            Check(sessions.All(s => ReferenceEquals(s, successor)), "Concurrent authentication created different active sessions.");
            Check(fixture.Repository.LoadedPlayerCount == 1, "Concurrent authentication loaded duplicate account objects.");
            await Run(fixture.Repository, successor, () => { });
        }));

        test("Maintenance and cancellation retain exclusive account gates", () => WithFixture(async fixture =>
        {
            ServerOptions.Current.MaintenanceIntervalMs = 60000;
            var a = fixture.Repository.Authenticate(fixture.Token1);
            var b = fixture.Repository.Authenticate(fixture.Token2);
            await Task.WhenAll(Run(fixture.Repository, a, () => { }), Run(fixture.Repository, b, () => { }));
            a.Account.LastMaintenance = b.Account.LastMaintenance = Environment.TickCount64 - 60000;
            var entered = Signal();
            var release = Signal();
            Task first = fixture.Repository.RunAsync(a, async () => { entered.SetResult(); await release.Task; return 0; });
            await entered.Task;
            try
            {
                bool busy = await fixture.Repository.TryRunMaintenanceAsync(a.Account, () => throw new Exception("Busy account maintenance ran.")).WaitAsync(TimeSpan.FromSeconds(2));
                Check(!busy, "Maintenance queued behind a busy player.");
                bool ran = await fixture.Repository.TryRunMaintenanceAsync(b.Account, () =>
                {
                    PlayerContext.MarkDirty();
                    PlayerContext.Current.Data.Profile.Coins += 7;
                    return Task.CompletedTask;
                });
                Check(ran && b.Account.Data.Profile.Coins == 7, "Independent player maintenance did not commit.");

                using var cancel = new CancellationTokenSource();
                bool invoked = false;
                Task queued = fixture.Repository.RunAsync(a, () => { invoked = true; return Task.FromResult(0); }, cancel.Token);
                cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => queued);
                Check(!invoked, "Canceled queued action executed.");
            }
            finally { release.TrySetResult(); await first; }
            Check(await fixture.Repository.TryRunMaintenanceAsync(a.Account, () => Task.CompletedTask), "Idle account maintenance was not scheduled.");
            Check(!await fixture.Repository.TryRunMaintenanceAsync(a.Account, () => throw new Exception("Maintenance interval ignored.")), "Maintenance ran twice inside its interval.");
            await Run(fixture.Repository, a, () => { });
            using var durable = JsonDocument.Parse(File.ReadAllBytes(fixture.StatePath(2002)));
            Check(durable.RootElement.GetProperty("Profile").GetProperty("Coins").GetInt64() == 7, "Maintenance returned before durable state was written.");
        }));

        test("Exclusive store ownership lasts through disposal and durable restart", () => WithFixture(async fixture =>
        {
            var session = fixture.Repository.Authenticate(fixture.Token1);
            Throws<IOException>(() => { using var competing = new PlayerRepository(); });
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Run(fixture.Repository, session, () =>
            {
                PlayerContext.MarkDirty();
                PlayerContext.Current.Data.Profile.Coins++;
            })));
            Check(session.Account.Data.Profile.Coins == 16, "Concurrent commits lost wallet updates.");
            var entered = Signal();
            var release = Signal();
            Task active = fixture.Repository.RunAsync(session, async () =>
            {
                PlayerContext.MarkDirty();
                PlayerContext.Current.Data.Profile.Coins += 17;
                PlayerContext.Current.Data.ClientData["restart"] = "durable";
                entered.SetResult();
                await release.Task;
                return 0;
            });
            await entered.Task;
            Task closing = Task.Run(fixture.Repository.Dispose);
            try
            {
                Check(!closing.IsCompleted, "Dispose finished while a durable transaction was still in flight.");
                Throws<IOException>(() => { using var competing = new PlayerRepository(); });
            }
            finally { release.TrySetResult(); await active; await closing; }
            await ThrowsAsync<ObjectDisposedException>(() => Run(fixture.Repository, session, () => { }));
            fixture.Reopen();
            var reopened = fixture.Repository.Authenticate(fixture.Token1);
            await Run(fixture.Repository, reopened, () =>
            {
                Check(PlayerContext.Current.Data.Profile.Coins == 33, "Restart lost a committed wallet update.");
                Check(PlayerContext.Current.Data.ClientData["restart"] == "durable", "Restart lost account progress from the same commit.");
            });
        }));

        test("Malformed unsupported and incomplete snapshots fail closed", async () =>
        {
            foreach (string invalid in new[] { "{invalid", "null", "{\"SchemaVersion\":2}", "{\"Profile\":null}", "{\"Market\":null}", "{\"Profile\":{\"Coins\":-1}}", "{\"Market\":{\"HeldCoins\":-1}}" })
            {
                await WithFixture(fixture =>
                {
                    string path = fixture.StatePath(2001);
                    File.WriteAllText(path, invalid);
                    File.WriteAllText(path + ".tmp", "{}");
                    bool failed = false;
                    try { fixture.Repository.Authenticate(fixture.Token1); }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException) { failed = true; }
                    Check(failed && fixture.Repository.LoadedPlayerCount == 0, "An invalid snapshot was replaced by a fresh account.");
                    Check(File.ReadAllText(path) == invalid, "An invalid snapshot was silently overwritten or recovered from a temporary file.");
                    return Task.CompletedTask;
                });
            }
            await WithFixture(fixture =>
            {
                Directory.CreateDirectory(fixture.StatePath(2001));
                bool failed = false;
                try { fixture.Repository.Authenticate(fixture.Token1); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed && fixture.Repository.LoadedPlayerCount == 0, "An unreadable canonical state path silently became a fresh account.");
                return Task.CompletedTask;
            });
        });

        test("Credential digest preserves UTF8 semantics for short and long tokens", () =>
        {
            foreach (string token in new[] { "ordinary-private-token", "éки猫😀", "unpaired-\ud800-surrogate", new string('x', 4096) })
            {
                string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                Check(AccountTokenHash.FromToken(token) == AccountTokenHash.FromHex(expected), "Credential digest changed UTF8 token semantics.");
            }
            return Task.CompletedTask;
        });

        test("Latency histogram buckets remain cumulative and include slow errors", () =>
        {
            var before = Metrics();
            ServerMetrics.Record(0.5, 200);
            ServerMetrics.Record(2, 200);
            ServerMetrics.Record(16.667, 503);
            ServerMetrics.Record(2000, 401);
            var after = Metrics();
            Check(Delta("impulsum_http_requests_total") == 4 && Delta("impulsum_http_errors_total") == 2, "Histogram request/error totals changed.");
            Check(Delta("impulsum_http_request_duration_ms_bucket{le=\"1\"}") == 1, "Fast latency bucket counted the wrong samples.");
            Check(Delta("impulsum_http_request_duration_ms_bucket{le=\"2\"}") == 2, "Latency buckets are not cumulative.");
            Check(Delta("impulsum_http_request_duration_ms_bucket{le=\"16.667\"}") == 3, "60 Hz bucket lost its boundary sample.");
            Check(Delta("impulsum_http_request_duration_ms_bucket{le=\"+Inf\"}") == 4 && Delta("impulsum_http_request_duration_ms_count") == 4, "Histogram dropped an over-one-second error.");
            return Task.CompletedTask;

            double Delta(string metric) => after[metric] - before[metric];
        });

        test("Concurrent account rate limiting has one bounded fixed-second budget", () =>
        {
            long state = 0;
            int accepted = 0;
            Parallel.For(0, 512, _ =>
            {
                if (AccountRequestLimit.TryAccept(ref state, 37, 100)) Interlocked.Increment(ref accepted);
            });
            Check(accepted == 37, "Concurrent HTTP/RPC-style requests exceeded or lost their shared budget.");
            Check(!AccountRequestLimit.TryAccept(ref state, 37, 100), "Denied requests reopened the exhausted window.");
            Check(AccountRequestLimit.TryAccept(ref state, 37, 101), "Next second did not replenish the budget.");
            for (int i = 1; i < 37; i++) Check(AccountRequestLimit.TryAccept(ref state, 37, 101), "Next-second budget was unexpectedly depleted.");
            Check(!AccountRequestLimit.TryAccept(ref state, 37, 100) && !AccountRequestLimit.TryAccept(ref state, 37, 101), "A delayed previous-second request reopened the current budget.");
            state = unchecked((long)(((ulong)uint.MaxValue << 32) | 37));
            Check(AccountRequestLimit.TryAccept(ref state, 37, 0), "Monotonic second rollover broke rate limiting.");
            return Task.CompletedTask;
        });
    }

    private static async Task WithFixture(Func<Fixture, Task> action)
    {
        var previous = ServerOptions.Current;
        string directory = Path.Combine(Path.GetTempPath(), "impulsum-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var fixture = new Fixture(directory);
            await action(fixture);
        }
        finally
        {
            ServerOptions.Current = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Name1 = "PersistenceAlpha";
        internal string Token1 => "persistence-private-one";
        internal string Token2 => "persistence-private-two-😀";
        internal PlayerRepository Repository { get; private set; }
        private readonly string _directory;

        internal Fixture(string directory)
        {
            _directory = directory;
            string accounts = Path.Combine(directory, "accounts.json");
            File.WriteAllText(accounts, JsonSerializer.Serialize(new[]
            {
                new { Id = 2001, Name = Name1, TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token1))) },
                new { Id = 2002, Name = "PersistenceBeta", TokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token2))) }
            }));
            var options = new ServerOptions { DataDirectory = directory, AccountsFile = accounts, LocalMode = false, RequestsPerSecond = 10000 };
            options.Validate();
            ServerOptions.Current = options;
            Repository = new PlayerRepository();
        }

        internal string StatePath(long id) => Path.Combine(_directory, "players", id + ".json");
        internal void Reopen() { Repository.Dispose(); Repository = new PlayerRepository(); }
        public void Dispose() => Repository.Dispose();
    }

    private static Task Run(PlayerRepository repository, PlayerSession session, Action action) => repository.RunAsync(session, () => { action(); return Task.FromResult(0); });
    private static Dictionary<string, double> Metrics() => ServerMetrics.Render().Split('\n')
        .Where(line => line.Length > 0 && line[0] != '#')
        .Select(line => line.Split(' ', 2))
        .ToDictionary(parts => parts[0], parts => double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
