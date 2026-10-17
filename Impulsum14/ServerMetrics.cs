using System.Globalization;
using System.Text;

namespace Impulsum14;

internal static class ServerMetrics
{
    private static readonly double[] Bounds = { 1, 2, 4, 8, 16.667, 33.333, 50, 100, 250, 500, 1000 };
    private static readonly long[] Buckets = new long[Bounds.Length + 1];
    private static long _errors, _micros;
    private static long _durableCommits, _commitFailures, _committedBytes, _commitMicros;
    internal static void Record(double ms, int status)
    {
        if (status >= 400) Interlocked.Increment(ref _errors);
        Interlocked.Add(ref _micros, (long)(ms * 1000));
        int bucket = 0;
        while (bucket < Bounds.Length && ms > Bounds[bucket]) bucket++;
        // Store disjoint buckets; accumulate once at scrape time rather than
        // incrementing every larger bucket on every fast request.
        Interlocked.Increment(ref Buckets[bucket]);
    }
    internal static void RecordDurableCommit(int bytes, double ms)
    {
        Interlocked.Add(ref _committedBytes, bytes);
        Interlocked.Add(ref _commitMicros, (long)(ms * 1000));
        Interlocked.Increment(ref _durableCommits);
    }
    internal static void RecordCommitFailure() => Interlocked.Increment(ref _commitFailures);
    internal static string Render()
    {
        var counts = new long[Buckets.Length];
        long requests = 0;
        for (int i = 0; i < counts.Length; i++) { requests += Interlocked.Read(ref Buckets[i]); counts[i] = requests; }
        var text = new StringBuilder("# TYPE impulsum_http_requests_total counter\nimpulsum_http_requests_total ").Append(requests);
        text.Append("\n# TYPE impulsum_http_errors_total counter\nimpulsum_http_errors_total ").Append(Interlocked.Read(ref _errors));
        text.Append("\n# TYPE impulsum_http_request_duration_ms histogram\n");
        for (int i = 0; i < Bounds.Length; i++) text.Append("impulsum_http_request_duration_ms_bucket{le=\"").Append(Bounds[i].ToString(CultureInfo.InvariantCulture)).Append("\"} ").Append(counts[i]).Append('\n');
        text.Append("impulsum_http_request_duration_ms_bucket{le=\"+Inf\"} ").Append(requests).Append('\n');
        text.Append("impulsum_http_request_duration_ms_count ").Append(requests).Append('\n');
        text.Append("impulsum_http_request_duration_ms_sum ").Append((Interlocked.Read(ref _micros) / 1000.0).ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("# TYPE impulsum_durable_commits_total counter\nimpulsum_durable_commits_total ").Append(Interlocked.Read(ref _durableCommits)).Append('\n');
        text.Append("# TYPE impulsum_commit_failures_total counter\nimpulsum_commit_failures_total ").Append(Interlocked.Read(ref _commitFailures)).Append('\n');
        text.Append("# TYPE impulsum_committed_bytes_total counter\nimpulsum_committed_bytes_total ").Append(Interlocked.Read(ref _committedBytes)).Append('\n');
        text.Append("# TYPE impulsum_commit_duration_ms_total counter\nimpulsum_commit_duration_ms_total ").Append((Interlocked.Read(ref _commitMicros) / 1000.0).ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("impulsum_loaded_players ").Append(PlayerRepository.Instance?.LoadedPlayerCount ?? 0).Append('\n');
        text.Append("impulsum_gc_heap_bytes ").Append(GC.GetTotalMemory(false)).Append('\n');
        return text.ToString();
    }
}
