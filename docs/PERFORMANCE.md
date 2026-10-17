# Performance and verification

Measured on 2026-10-17 for the FUT polish/optimization pass. The current source is [77704fa](https://github.com/aviyanp/Impulsum14/commit/77704faebdb750efc8dfc5e8f7e1a4c651f283fb); the before build is [6fc5b4e](https://github.com/aviyanp/Impulsum14/commit/6fc5b4ece9e4374cbe16c48ded869c4876042a48). All eight completed runs returned HTTP 200 only, zero request errors and clean server exit: **1,227,600 measured requests**. Raw reports are in [performance/](performance/), with filenames beginning `v3-`.

## Same-workload comparison

Each row uses 100 independent accounts, 60 requests/account/s, a 60 Hz maintenance scheduler and a 30-second measured window: 180,000 requests at approximately 6,000 requests/s. Every twentieth request is a durable client-data update. Standard reads rotate credits, club inventory, market and squad screens. The second profile adds native gold/minRating market filters and one overpriced, two-hour private auction per account before warm-up.

| Profile | Build | Run | p50 ms | p95 ms | p99 ms | Max ms | Deadline misses | CPU s* | Peak RSS MiB |
|---|---|---|---|---|---|---|---|---|---|---|
| Standard | Before | 1 | 0.323 | 8.965 | 21.142 | 59.120 | 8,297 | 41.413 | 204.6 |
| Standard | After | 1 | 0.315 | 13.299 | 24.480 | 47.983 | 10,624 | 37.155 | 205.4 |
| Filtered market + idle auction | Before | 1 | 5.464 | 34.939 | 49.546 | 122.115 | 90,271 | 90.195 | 211.8 |
| Filtered market + idle auction | After | 1 | 0.294 | 10.112 | 19.586 | 42.323 | 4,938 | 36.878 | 213.4 |
| Filtered market + idle auction | Before | 2 | 5.080 | 33.808 | 45.518 | 121.906 | 83,928 | 90.163 | 209.5 |
| Filtered market + idle auction | After | 2 | 0.307 | 8.444 | 19.187 | 43.596 | 4,758 | 37.752 | 214.5 |

The two auction/filter comparisons used 58.1–59.1% less server CPU, with p99 changing from 45.5–49.5 ms to 19.2–19.6 ms (57.8–60.5% lower). The standard case used 10.3% less CPU, but p99 increased from 21.1 to 24.5 ms. That single standard comparison does **not** establish a latency improvement.

These are sequential runs on shared hardware, with newly provisioned random starter clubs and a moving virtual-auction clock. Execution order was standard before/after, filtered before/after, then filtered before/after again. Two repeats show a consistent workload-specific gain; they are not confidence intervals or a general capacity guarantee.

## Additional current-build checks

These use the standard profile, 5% durable writes and 30-second measured windows, with the scheduler set to the requested rate.

| Accounts | Requests/account/s | Requests | Requests/s | p99 ms | Max ms | Deadline misses | Peak RSS MiB |
|---|---|---|---|---|---|---|---|
| 100 | 30 | 90,000 | 2999.8 | 4.594 | 63.682 | 118 | 199.4 |
| 32 | 60 | 57,600 | 1919.9 | 2.836 | 18.061 | 5 | 175.7 |

At 32 accounts × 60 requests/s, five of 57,600 requests missed the scheduling-inclusive 16.7 ms period, even though p99 was 2.8 ms. The 100-account 60/s cases still exceeded that period at p99. The service does not promise hard real-time deadlines or simulate football at these rates.

*CPU seconds include startup, account/auction fixture creation, warm-up and shutdown; they are not steady-state CPU utilization. RSS is the child-process high-water mark. A deadline miss includes generator scheduling lag plus response time exceeding 33.3 ms at 30/s or 16.7 ms at 60/s. Consult raw reports for scheduling lag, sampled heap and exact throughput.*

## Reproduce and interpret

- Linux 6.18.55 x86-64, nine logical CPUs reported; container CPU quota `800000/100000` (eight CPUs). This is shared infrastructure, not a minimum-spec dedicated machine.
- .NET SDK 8.0.419; .NET/ASP.NET Core 8.0.25; framework-dependent Release publish, workstation GC and tiered PGO.
- Current `Impulsum14.dll` SHA-256: `8d5847c6781a2ca079ee1459638ada5b7aff3392d6d133b1d511df483579723e`. Before: `564c86483ea7edb11bb352b67397be60038c8a478fa34ea3f422784d479c83df`. The later documentation/load-tool commit does not change either server binary.
- The load generator is Python/aiohttp on the same host over HTTP loopback. No WAN latency, HTTP TLS or retail executable is involved. Backend resource assets use the committed roster baseline; the pre-existing local roster edit was preserved and excluded from both measured publishes.
- Four reads/account warm up the server. Accounts wait for each response before the next due request, so this is paced closed-loop load; under overload scheduling lag grows and achieved arrival rate can fall.
- The schedule is 50% credits, 20% club, 10% market, 10% squad list and 10% active squad before every twentieth operation is replaced by a write. This is a chosen synthetic distribution, not a captured retail traffic mix. It excludes large inventories, store/match mutation traffic, full Blaze matchmaking load and gameplay UDP.
- The auction profile lists the last starter player at 15,000,000 coins for 7,200 seconds. It exercises idle account-market maintenance; it does not benchmark auction completion. Functional checks cover actual award/expiry/retry behavior separately.
- The tool records the launched assembly hash, fails on request errors/nonzero server exit, and only allows automatic auction seeding with a disposable `--launch` instance.

Publish each source revision to a separate directory, then run the same current load tool against both DLLs:

```sh
dotnet publish Impulsum14/Impulsum14.csproj -c Release --no-self-contained -o artifacts/server
python3 -m pip install aiohttp
python3 tools/load_test.py --launch artifacts/server/Impulsum14.dll --clients 100 --hz 60 --tick 60 --duration 30 --mixed --filtered-market --active-listings --output artifacts/load.json
```

Omit the last two profile flags for the standard comparison. Repeat with 100 accounts at 30/s or 32 accounts at 60/s. Use separate load hardware, recorded representative traffic, intended storage, longer sessions and real client acceptance before production sizing. All `v2-` and 2026-10-16 reports remain historical evidence of earlier implementations/environments; their numbers are not substituted for this comparison.

## Resource design and FUT behavior

Shared catalogue selections have a 64-entry/4 MiB cache; deterministic historical starting prices have an 8,192-entry cache. Hits avoid the admission lock. Typed filters own their exact matching predicate; account bids, watches, purchases and auction timing are evaluated per request. Displayed current bids, filter bounds, watch counts and settlement share the same historical-price calculation. Shared reference valuations exclude account-private ownership. These caches do not turn the private bot market into a human exchange.

Empty requests avoid a body reader; cached reads avoid query parsing and per-request session-key concatenation. Wallet/club/squad snapshots are bounded by account revision, session, bytes and entries and expire within configurable `ResponseCacheSeconds` (30 by default). Mutations invalidate immediately. Market replies expire at the next Unix second. Idle auction maintenance no longer marks the account dirty or serializes its aggregate unless state actually changes.

Account writes flush the file and its replacement directory on POSIX, or use a write-through replacement on Windows. An uncertain post-rename failure stops further actions for that account until restart recovery. This preserves durable wallet/club/reward transactions while improving read paths. Native autosave fingerprints stream through a 4 KiB UTF-8 buffer while retaining the exact existing hashes.

Ranked pairing uses bounded indexed Elo matching and setup concurrency; the paired WAL persists both ratings/seasons/rewards together. Account projections remain idempotent. These structures have correctness coverage, not a measured huge-player ranked-capacity guarantee. Loaded accounts remain resident until restart; large clubs and loaded-account counts affect memory. File snapshots and the ranked single writer remain limits on mutation throughput.

Transport queues/frames are bounded and slow peers disconnect. Plain TCP writes are asynchronous; the legacy redirector TLS writer retains its worker because the pinned stream adapter shares inherited async read/write synchronization. Housekeeping and matchmaking scheduler rates do not define gameplay FPS.

## Functional verification

[Linux and Windows CI](https://github.com/aviyanp/Impulsum14/actions/runs/39054089857) passed **116/116 checks on each platform**, zero build warnings/errors, Release publish and artifact upload. [VERIFICATION.txt](VERIFICATION.txt) records the checks and source commit.

New coverage includes native consumable eligibility and exact-copy consumption, manager contracts and frozen participation, explicit played substitutes versus unused bench, post-match retries, chronological division history, stale cup/online transitions, unchanged save hashes, typed card-variant filters, cross-account market isolation, bid/settlement agreement, manager listing fidelity, safe delayed swaps, cache invalidation/compression and uncertain account recovery. Existing account, cup, season-boundary, Elo, WAL/corruption, framing, queue and bridge checks continue to pass.

Python scripts compile and shell/whitespace checks pass. Docker is unavailable here, so container execution remains unverified. Retail FIFA screens, full peer games and consoles still require the [compatibility acceptance work](COMPATIBILITY.md).
