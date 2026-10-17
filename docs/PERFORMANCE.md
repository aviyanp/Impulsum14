# Performance and verification

The results below were measured on 2026-10-17 against the published Release build with ranked matchmaking, the two-player ledger and native-response fixes. They measure a selected synthetic HTTP workload on one shared host. They do not measure game simulation, actual FIFA peer-game traffic, trusted adjudication or console behavior.

## Measured HTTP runs

Each account has its own session and real persisted starter club. Every twentieth request is a durable client-data update; the rest rotate through credits, club inventory, synthetic market pages, squad list and active squad. Every committed run below returned only HTTP 200, zero request errors and clean server exit code 0. Raw results, including generator scheduling lag and sampled GC heap, are in [performance/](performance/).

| Accounts | Requests/account/s | Seconds | Requests | Requests/s | p50 ms | p95 ms | p99 ms | Max ms | Deadline misses | Peak RSS MiB | CPU s* |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 32 | 30 | 30 | 28,800 | 960.0 | 0.405 | 2.154 | 8.834 | 133.383 | 298 | 290.5 | 15.476 |
| 32 | 60 | 60 | 115,200 | 1,919.9 | 0.389 | 1.690 | 4.785 | 47.769 | 560 | 287.5 | 37.622 |
| 100 | 30 | 30 | 90,000 | 2,999.8 | 0.355 | 2.690 | 8.197 | 83.424 | 336 | 301.6 | 29.933 |
| 100 | 60 | 60 | 360,000 | 5,998.5 | 0.651 | 13.507 | 26.977 | 183.915 | 48,209 | 304.7 | 79.846 |

*CPU seconds include server startup, fixture creation, warm-up and shutdown. They are not steady-state CPU utilization. RSS is the child-process high-water mark. A deadline miss means client scheduling lag plus response latency exceeded 33.3 ms at 30 requests/account/s or 16.7 ms at 60. Even a p99 below a period does not mean every request met it; the maxima and deadline counts must be read together. These results establish no hard-real-time or huge-player capacity guarantee.

At 100 accounts × 60 requests/s, p99 response time exceeded 16.7 ms and 13.4% of requests missed the scheduling-inclusive period. At 32 accounts × 60, p99 was 4.8 ms, but 0.49% still missed it. Same-host generator scheduling lag is included in these miss counts; neither case proves a hard 60 Hz deadline. These are one recorded run per case, not repeated-trial confidence intervals or minimum-spec hardware results.

## Scope and reproduction

- Linux 6.18.44, x86-64, glibc 2.39; nine logical CPUs reported and container CPU quota `800000/100000` (eight CPUs of quota). This is shared infrastructure, not a minimum-spec dedicated machine.
- .NET SDK 8.0.419, .NET/ASP.NET Core 8.0.25; Release framework-dependent publish, workstation GC, tiered PGO.
- Measured server source: `2b653b29fdfa84fd576c4c70249b909fad746a4a` on this fork (the subsequent Windows fixture fix changes tests only). Published `Impulsum14.dll` SHA-256: `564c86483ea7edb11bb352b67397be60038c8a478fa34ea3f422784d479c83df`; `ProtoFire.dll`: `b823350a5648128b0bcdfa647086d16859b66dd0a1a0a21927ed070d069941e8`.
- Python/aiohttp runs on the same host over loopback, sharing CPU with the server. No WAN latency and no TLS on HTTP. Four warm-up reads per account precede the paced measured window; JIT/GC may still occur during it.
- This is a paced closed-loop generator: each account waits for its response before sending its next due request. Under overload arrival rate can fall and schedule lag grows. It is not a fixed-arrival-rate/open-loop benchmark.
- The synthetic schedule is 50% credits, 20% club, 10% market, 10% squad list and 10% active squad before every twentieth operation is replaced by a write. It is not a measured retail traffic distribution. It does not include store/match mutation load, full inventories, live Blaze matchmaking load, gameplay UDP, or FIFA itself.
- Backend resource assets use the repository's committed baseline. The pre-existing local roster-binary edit was preserved outside the backend change and excluded from the measured publish resources.

```sh
dotnet publish Impulsum14/Impulsum14.csproj -c Release --no-self-contained -o artifacts/server
python3 -m pip install aiohttp
python3 tools/load_test.py --launch artifacts/server/Impulsum14.dll --clients 32 --hz 60 --tick 60 --duration 60 --mixed --output artifacts/load.json
```

Repeat with 100 accounts at 30 or 60 requests/account/s. The script provisions disposable tokens/state and verifies process startup and exit. For production sizing use separate load hardware, captured representative traffic, larger inventories, long-running sessions, intended storage and a real pair of clients; examine p99, errors, disk latency, GC and connection counts together.

Earlier ten-second runs from 2026-10-16 are retained in the same raw-results directory, including a pre-cache baseline. They belong to an earlier implementation and must not be substituted for current measurements or treated as a controlled comparison.

## Resource design and limits

Kestrel handles HTTP and persistent sockets. Catalogues, starter-visual pools and static resources are shared. Account response caches are bounded by bytes and entries, keyed to session and committed revision, and invalidated before any mutation. Eligible empty-body reads avoid redundant serialization. Maintenance sweeps accounts at its configured interval rather than scanning every account 60 times per second. Token digest, rate-budget and histogram updates avoid their earlier hot-path allocations/locks.

Matchmaking indexes compatible pools and rating ranges, applies oldest-compatible fairness with timed widening, and limits concurrent setup work. Its independent test compares indexed selections against a direct fairness oracle, including removals, extreme rating gaps and pool isolation. These are correctness tests, not a measured production matchmaking-capacity guarantee.

The ranked ledger publishes immutable lookup snapshots and performs disk writes outside the lobby lock. One flushed journal event contains both participants' complete result transitions; bounded checkpoints and terminal receipt retention limit unbounded match history. Durable reports/checkpoints still serialize under one writer, and a checkpoint can pause new ranked mutations while it writes. The account store keeps a whole committed snapshot for rollback and rewrites the changed account on a durable mutation. Loaded account state stays resident until restart, so memory grows with loaded clubs/inventories. `MaxPlayers` and ranked storage caps are limits, not proven active-player counts.

Fire/Fire2 input, outgoing and deferred-notification queues are bounded by count and bytes. The derived default queue byte budget is 4 MiB per direction/deferred category; input can additionally hold the active/read frame. Slow peers time out/disconnect. Plain TCP sends are asynchronous. Legacy redirector TLS retains a synchronous writer worker because the pinned TLS Stream adapter shares its inherited async read/write semaphore; replacing it blindly with async writes can deadlock replies behind a pending read.

## Functional verification

Release build and publish passed with no warnings. All **83/83** executable checks passed locally; [VERIFICATION.txt](VERIFICATION.txt) records their names and timings. They cover real HTTP/Blaze/TLS/bridge sockets, portable PFX identity/key-store validation, paired online native reports, Elo/season/cumulative-wallet consistency, duplicate receipts after spending, cancellation/conflict/timeout/retired receipts, cleanup races, matchmaking fairness/heartbeat/ownership, single-writer WAL/checkpoint/torn-tail/corruption recovery, account rollback/session revocation, season outcome boundaries, native cup/save safety, squad copying/clearing/roles/shirt numbers, pack recovery and quick selling, malformed frames/TDF, pipelining, bounded slow-peer queues and awaited shutdown.

The [Linux and Windows CI run](https://github.com/aviyanp/Impulsum14/actions/runs/39031094676) for source/test commit `d6d1d169f2111aac6293bb6642d79e0fa79de795` passed all 83 checks on both platforms, Release builds and framework-dependent publishes, and uploaded both server artifacts. The Windows run exercises direct PFX loading and checkpoint crash/replay with Windows file-sharing semantics; it is not a Windows FIFA gameplay test.

Python scripts compile, shell syntax and whitespace checks pass. Docker is unavailable here, so container build/run is unverified. Native retail post-match screens, gamepad/menu flows and full peer gameplay still require the acceptance matrix in [COMPATIBILITY.md](COMPATIBILITY.md).
