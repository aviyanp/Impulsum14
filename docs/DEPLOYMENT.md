# Deployment and configuration

## Configuration precedence

Defaults → JSON (`FIFA14_CONFIG`, otherwise `server.json` beside the executable) → supported environment overrides. Relative paths resolve against the process working directory. Use absolute paths in a service. Unknown JSON properties, malformed booleans/numbers, unsafe local-mode binds, duplicate listener ports and invalid limits fail validation.

| Environment | JSON option | Default |
|---|---|---|
| `FIFA14_BIND_ADDRESS` | `BindAddress` | `127.0.0.1` |
| `FIFA14_ADVERTISED_ADDRESS` | `AdvertisedAddress` | `127.0.0.1` |
| `FIFA14_PUBLIC_WEB_URL` | `PublicWebUrl` | `http://127.0.0.1:9988` |
| `FIFA14_REDIR_PORT` | `RedirectorPort` | `42127` |
| `FIFA14_BLAZE_PORT` | `BlazePort` | `10000` |
| `FIFA14_WEB_PORT` | `WebPort` | `9988` |
| `FIFA14_DATA_DIRECTORY` | `DataDirectory` | `Profile` beside the executable |
| `FIFA14_LOCAL_MODE` | `LocalMode` | `true` |
| `FIFA14_ACCOUNTS_FILE` | `AccountsFile` | none |
| `FIFA14_TICK_RATE` | `TickRate` | `30` |
| `FIFA14_LOG_LEVEL` | `LogLevel` | `Warning` |

All other options are configured in JSON. Repeated eligible GET responses are cached per account only within the current Unix second, keyed to the committed revision and session. `ResponseCacheBytes` (default 131072, body bytes) and `ResponseCacheEntries` (default 8) bound this cache; set either to zero to disable it. Wallet/inventory commits invalidate cached reads immediately. `FIFA14_CERTIFICATE_PASSWORD` supplies an optional PFX password. Legacy `FIFA14_FIRE2=1` and `FIFA14_HEAT1=1` remain available for diagnostics; FIFA 14 PC and the bridge use the default Fire/Heat2 combination.

The redirector and PC bridge parse their PFX directly with Bouncy Castle, without importing private keys into the Windows key store or relying on CNG export support. Supply a MAC-protected PKCS#12 file containing exactly one RSA private key and its matching leaf-first certificate chain; the bundled empty-password identity is supported. Wrong passwords, missing/ambiguous keys and mismatched certificates fail startup. Preserve the identity required by your client setup: a different certificate is not automatically trusted by the retail executable. This legacy TLS identity is separate from production HTTPS termination.

`BindAddress` selects the network interface. `AdvertisedAddress` is the reachable IPv4 address returned by the legacy redirector; never use `0.0.0.0` there. `PublicWebUrl` is the externally reachable HTTP(S) origin, including its port. The game cannot connect to a container-only address. Port mappings must agree with advertised values.

## Peer lobbies and ranked policy

`EnablePeerLobbies` defaults to `false` and is JSON-only. Set it to `true` for experimental two-player lobbies and use two distinct provisioned accounts. There is no separate Elo flag: only Blaze searches explicitly requesting `RANKED` create a ranked binding; direct create/join and unranked searches do not. The ranked ledger opens on normal server startup even when lobbies are disabled. Its authenticated REST reads/reports use `/fut/rs4/ut/game/fifa14/ranked`.

The default matchmaking window is ±100 Elo, widening by 50 every 10 seconds up to ±1000. `MatchmakingMaxWaitSeconds` defaults to 120; requested durations and session expiry can end a search sooner. Configure the JSON properties `MatchmakingInitialEloRange`, `MatchmakingEloRangeStep`, `MatchmakingEloRangeIntervalSeconds`, `MatchmakingMaxEloRange`, `MatchmakingMaxWaitSeconds`, and `MatchmakingQueueLimit` (1000 by default). Protocol version, mode and ranked flag isolate pools. These are queue policy, not proof of gameplay or throughput capacity.

The nested `Ranked` object controls rating/rewards and ledger bounds. Defaults are 1000 initial rating, 100–3000 bounds, K=32, shared provisional K=48 for either player's first ten games, and a 2500 ms native match-end settlement wait. Ranked coin rewards are separate from top-level offline match rewards. Its `MaxPlayers` (10000) limits durable ranked accounts and differs from top-level `MaxPlayers` (1000). See [RANKED.md](RANKED.md) for exact endpoint payloads, all defaults/ranges, ten-game online seasons and report/quit/timeout policy. None of these options changes simulation tick rate or supplies a peer relay.

## Identities

`LocalMode=true` is a single trusted account and requires a loopback bind. It preserves the existing launcher experience. Hosted/LAN mode requires `LocalMode=false` and an accounts file:

```sh
python3 tools/provision_account.py deploy/accounts.json --id 1001 --name PlayerOne
python3 tools/provision_account.py deploy/accounts.json --id 1002 --name PlayerTwo
```

Each entry stores `Id`, `Name`, `TokenSha256`. Private-server tokens are unrelated to EA credentials. Restart to load changed account definitions. Origin/Silent/Login/Express Blaze login accepts the private-server credential; the PC bridge substitutes it automatically. HTTP accepts `Authorization: Bearer <private-token>` or the returned `X-UT-SID`/`EASW-Session` session key. Nucleus-ID headers select no identity and must agree with the authenticated account. Session lifetime is configurable; revoked/expired sessions fail further authenticated Blaze calls. The single-process account store is not an EA authentication service.

## Persistence and backups

A request holds only its player's gate. Wallet, inventory, pending items, escrow and competition progress commit together to `DataDirectory/players/<id>.json` via a flushed temporary file and atomic replacement. Failed requests restore the last committed snapshot. A success is emitted after the replacement; matching match receipts are stored in that same snapshot. No write-behind loss window is used. Atomic rename assumes one local filesystem; power-loss durability of directory metadata remains filesystem-dependent.

Ranked results have an additional authoritative store in `DataDirectory/ranked/`: an exclusive `writer.lock`, flushed framed `journal.bin` WAL, and atomically replaced `snapshot.json`. A ranked settlement journals both accounts' rating, season and cumulative reward changes in one transition before publishing them. It checkpoints every 128 transitions or at 16 MiB of journal by default. Recovery truncates an incomplete tail, but a complete corrupt frame or invalid checkpoint fails startup. An uncertain ledger write prevents further ledger mutations until restart recovery.

The per-player projection applies only the difference from cumulative ranked coin/W/D/L receipts already recorded in that player's snapshot. A settlement survives even if the second account is offline or its projection has not run yet; a subsequent account transaction applies it without a second payout. A failed HTTP/account transaction rolls back its account snapshot, not an already durable ranked ledger settlement. Keep the ledger and all player snapshots together when backing up or restoring: restoring an older ledger against newer applied player receipts is rejected. Do not delete the ledger as a way to clear a pending game.

`Ranked.RetainedMatches` retains 2000 terminal receipts by default; active receipts stay until resolved. `SeasonHistoryLimit` keeps 20 completed online seasons by default. Retiring old receipts/history does not erase cumulative ratings, rewards or W/D/L, and retired match IDs cannot settle again. A retired receipt is no longer available for lookup/retry, so choose retention for the expected client retry window. Unresolved games cancel without awards after `MatchTimeoutMinutes` (60 by default), measured from creation. Unexpected in-game disconnects do not automatically award a win; an explicit own concession can settle them sooner.

One process exclusively owns a data directory. Do not put the data volume on a shared network filesystem or point multiple replicas at it. Stop the process before copying the data directory for a consistent backup across players. A corrupt player file fails loading; it is never silently replaced with an empty wallet. Legacy local `fut_profile.json`, `club_data.json`, `account.json` and `clientdata.json` are imported for account 1000 when no aggregate snapshot exists. Original files remain untouched.

This is a bounded single-node design. MaxPlayers limits provisioned identities, not proof of simultaneous capacity. Loaded player objects remain cached until restart. For growth beyond measured single-node capacity, add explicit account routing and a transactional shared store; do not add replicas behind a random load balancer.

## Containers

Copy the example to `deploy/server.json`, set reachable addresses, provision `deploy/accounts.json`, then run `docker compose up --build -d`. Player data uses a named volume; config mounts are read-only. The runtime runs as the .NET image's non-root application UID, with a read-only root filesystem and a small temporary filesystem. Logs rotate. Use `docker compose logs -f server` for startup errors. The Docker build is provided for a Docker-capable host; this implementation environment did not have Docker installed.

`/healthz` and `/readyz` report process HTTP availability. `/metrics` exposes request counts, error counts, cumulative latency buckets including 16.667/33.333 ms, loaded accounts and GC heap usage. These are operational endpoints; put them behind your network access controls as appropriate. They do not certify retail client compatibility.

For systemd, publish to `/opt/impulsum14`, create the `impulsum14` service user, copy config to `/etc/impulsum14/server.json`, set `DataDirectory` to `/var/lib/impulsum14`, and install `deploy/impulsum14.service`. The unit grants write access only to that state directory. Verify the actual `dotnet` executable path on the host.

## Networking

Open/map TCP 42127, 10000 and 9988 as configured. Legacy Blaze is plaintext and redirector TLS exists for old client compatibility; neither supplies modern transport security for the whole stack. Use a private LAN/VPN for Blaze. HTTPS can terminate in front of HTTP using the supplied Caddy example. A web reverse proxy does not proxy Blaze automatically.

Peer gameplay exchanges addresses carried by the experimental lobby service. It needs reachable peer ports/NAT behavior supplied by the game; no STUN/TURN/UDP relay, QoS probe server or host migration is implemented here.

Authentication and paired score corroboration are not gameplay anti-cheat or trusted adjudication. No platform-console authentication or verified retail PC peer-game acceptance is supplied. Test two actual supported PC installations before opening ranked play to a community; see [CLIENTS.md](CLIENTS.md) and [COMPATIBILITY.md](COMPATIBILITY.md).
