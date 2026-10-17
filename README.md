# Impulsum14

A community backend for FIFA 14 PC FUT, based on [Mk0M/Impulsum14](https://github.com/Mk0M/Impulsum14). This branch adds hosted accounts, transactional persistence, backend competition progression, a remote PC bridge, bounded network handling, deployment files, and executable regression checks.

**This is a FUT service backend. It does not simulate football at 30/60 Hz.** Match simulation and peer gameplay run in the game. `TickRate` configures the housekeeping/matchmaking scheduler; HTTP and Blaze commands execute immediately. Retail PC gameplay, the exact Manager Tasks UI, and consoles require client validation; see [the compatibility matrix](docs/COMPATIBILITY.md).

## Run locally

Install the .NET 8 SDK, then:

```sh
dotnet build Impulsum14/Impulsum14.csproj -c Release
dotnet Impulsum14/bin/Release/net8.0/Impulsum14.dll
```

Defaults bind only to `127.0.0.1`: redirector TCP 42127, Blaze TCP 10000, HTTP 9988. Use the existing PC launcher/client setup with the installed copy of FIFA 14. See [client connection instructions](docs/CLIENTS.md) before using a remote host. No FIFA executable or additional game binaries are added by this branch.

## What changed

- Independent account wallets, clubs, settings, pending packs, auction escrow, and competition state. Server credentials are hashed; HTTP sessions and Blaze identities are bound to the same account.
- One transaction per account across both protocols, independent concurrency between accounts, atomic snapshot replacement, rollback on failed requests, and persisted match receipts that prevent duplicate payouts after retries or restart.
- Division 10 → 1 progression, promotion/relegation, separate online/offline season records, history, native fixture fields and guarded save resumption.
- Tournament kickoff stubs are excluded from resumable runs. Native buffers precede their version fields; read responses use `progressdata`. Completed rounds invalidate old blobs until the client saves the next bracket.
- A 42-card Legends catalogue, opt-in pack selection, and mandatory client identity manifest. Legends remain off for stock PC clients.
- Eighteen server-observed community manager tasks, configurable coin rewards and one-time claims, plus the native `/managerquest` three-entry state format recovered from the existing client DLL. Retail task bit mapping and reward-screen behavior still need validation.
- Kestrel HTTP, keepalive/chunked request support, bounded bodies/pages/connections, timeouts, bounded Blaze queues and frames, defensive TDF decoding, release logging improvements, cached static files, bounded account read caches invalidated on commit, and latency metrics.
- Bounded synthetic-market searches, exact card-variant compare filters, serialized purchases, corrected escrow display, persisted bid cycle timing, stable owned manager/staff IDs, and guards against selling an already-listed player twice.
- Opt-in two-player peer lobbies, indexed Elo-aware matchmaking with fair widening and mode/protocol separation, bounded setup concurrency, strict mesh readiness, session ownership and disconnect cleanup.
- A durable two-player ranked ledger: corroborated scores or explicit self-forfeit, zero-sum bounded Elo, provisional policy, separate ten-game online seasons, atomic paired results, replay-safe wallet projection and a bounded leaderboard. See [ranked operation](docs/RANKED.md).
- Native-shaped post-match statistics, fixed final receipts, exact MatchReady starter tracking, squad copy/clear/roles/shirt numbers, complete quick-sell ownership updates and current unassigned pack recovery. Retail gameplay remains an explicit acceptance step.
- Consumables apply once to the correct owned copy and eligible target; ineffective applications preserve the card. Manager contracts persist through purchases, application and match settlement. Match squads/managers stay bound across menu changes; unused bench rows do not spend contracts. Delayed trade offers recheck funds and ownership before transferring anything.
- Shared bounded caches reuse immutable market filters and historical auction prices. Bids, expiry and private purchases remain live. Stable account reads survive idle auction checks, while mutation invalidation keeps wallet and squad menus current.
- A loopback bridge that injects a private-server credential and rewrites advertised web URLs for the existing PC client flow.

The synthetic market is private to each account. It is **not a shared player-to-player exchange**. Original placeholder services such as full EASFC catalogues, trusted gameplay/anti-cheat adjudication, social features and console login have not become complete services merely because their SDK types exist.

## Configure and deploy

Read [deployment/configuration](docs/DEPLOYMENT.md). General options are in `ServerOptions.cs` and ranked policy in `RankedModels.cs`; an annotated-by-documentation example is in [deploy/server.example.json](deploy/server.example.json). Unknown JSON option names and invalid limits fail at startup.

```sh
cp deploy/server.example.json deploy/server.json
python3 tools/provision_account.py deploy/accounts.json --id 1001 --name YourName
# Edit AdvertisedAddress and PublicWebUrl in deploy/server.json to your reachable host.
docker compose up --build -d
```

The command prints a newly generated private-server token once. Store it for your PC bridge. The compose stack expects the account/config paths shown in the example. A systemd unit and HTTP reverse-proxy example are also provided.

## Verify

```sh
dotnet build tests/Impulsum14.Tests/Impulsum14.Tests.csproj -c Release
dotnet tests/Impulsum14.Tests/bin/Release/net8.0/Impulsum14.Tests.dll
dotnet publish Impulsum14/Impulsum14.csproj -c Release --no-self-contained -o artifacts/server
```

The executable checks cover account isolation, concurrency, rollback, journal/checkpoint restart and corruption recovery, all feasible ten-game season score combinations, native save safety, purchases and pack recovery, squad editing, bounded TDF/framing/queues, real HTTP/Blaze sockets, matchmaking widening and ranked lifecycle, paired HTTP settlement/retries, and the PC bridge. CI is configured for Linux and Windows; consult the workflow results for the platforms actually run.

For a reproducible **backend-only synthetic HTTP** load test:

```sh
python3 -m pip install aiohttp
python3 tools/load_test.py --launch artifacts/server/Impulsum14.dll --clients 32 --hz 60 --tick 60 --duration 10 --mixed --output artifacts/load.json
```

See [performance methodology and measured limits](docs/PERFORMANCE.md). Do not infer game FPS, concurrent FIFA match capacity, or a player-count guarantee from HTTP request rates.

## Credits and license

Apache-2.0 for this project. Original project contributors include MYorderlyHuman, Toniboi, Draz and Ron. Existing SDK work: [Aim4kill/BlazeSDK](https://github.com/Aim4kill/BlazeSDK). Compatibility research: [limevevo/f14-localfut](https://github.com/limevevo/f14-localfut), [KyroGeorge2/FIFA-14-Local-FUT](https://github.com/KyroGeorge2/FIFA-14-Local-FUT), and [ZamboniDevelopment/Zamboni3](https://github.com/ZamboniDevelopment/Zamboni3). The imported Legends metadata carries its MIT notice in [third_party](third_party/f14-localfut-LICENSE.txt); details are in [research notes](docs/RESEARCH.md).

This unofficial preservation project is not affiliated with Electronic Arts.
