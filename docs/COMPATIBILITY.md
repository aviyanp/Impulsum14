# Compatibility and completion audit

The implementation is reviewable and deployable as a backend. It is not a claim that all original FIFA online services have been recreated or that a retail executable has been play-tested here.

| Upstream TODO / bug | Implemented behavior | Validation / remaining boundary |
|---|---|---|
| Legends | 42-card metadata, pack hook, opt-in and mandatory identity manifest | Catalogue/guard tests; installed PC assets and gameplay still required |
| Divisions | 10 → 1 ladder, fixtures, points, title/promotion/hold/relegation rewards, history; ranked online ten-game progress is ledger-owned and separate from offline state | State-machine and JSON checks; retail save decoding/resume and online gameplay require game testing |
| Manager challenge rewards | Server-observed 18-task community catalogue and one-time individual/completion coin claims; native `/managerquest` state round-trip | Idempotency, native schema and isolation tests; native quest-to-event bit mapping and retail pack awards remain unverified |
| Tournament underway crash | Exclude kickoff stubs, buffer-before-version order, lowercase `progressdata`, invalidate previous-round blobs, preserve authoritative round | Protocol regressions; actual tournament re-entry still requires retail play-test |
| Compare-price / squad-builder purchases | Exact `definitionId` card variant, masked base ID, bounded search, serialized wallet/item commit, duplicate-purchase tests, stable owned manager/staff IDs | Real HTTP tests; retail screen transition/crash acceptance pending |
| Missing club info | Account-bound IDs, W-D-L, wallet, trophy counts, club visuals and season progression are data-driven | Backend payload checks; no claim every retail club-info member has been reverse-engineered |

## Other capabilities

| Area | Status |
|---|---|
| HTTP keepalive, chunked bodies, method overrides | Exercised using live Kestrel sockets |
| Blaze login/session notifications/settings | Exercised using real Fire/Heat2 sockets |
| Offline GameReporting completion | Terminal ResultNotification tested; wallet settlement remains exclusively `/match/end` |
| Replay after restart | Persisted receipt tested; no second payout for a repeated settled match |
| Legacy redirector | TLS handshake and configured-address response exercised over real sockets; actual executable certificate behavior needs client validation |
| Two-player peer lobbies | Opt-in `EnablePeerLobbies`; create/join/leave/destroy, state/ownership checks, separate protocol/mode/ranked pools, bounded Elo-window matchmaking and setup notifications |
| Ranked Elo and rewards | Server-bound two-account receipts; inverse score corroboration or self-forfeit; shared zero-sum K including provisional policy; atomic two-player WAL settlement and cumulative account projection; see [RANKED.md](RANKED.md) |
| Ranked REST extensions | Authenticated own rating/active receipt, bounded leaderboard, participant receipt lookup and reports under `/fut/rs4/ut/game/fifa14/ranked`; not stock retail endpoint compatibility claims |
| Native ranked match end | Bounded asynchronous peer-settlement wait, pending-result flag and refreshed settled receipt; exact retail post-match UI acceptance remains unverified |
| Peer gameplay | Experimental: no FIFA-vs-FIFA match run, UDP relay, QoS probe server, NAT service, host migration or gameplay physics |
| Synthetic market | Per-account bot market; bounded windows, bid timing/escrow/purchases persisted |
| Shared human transfer market | Not implemented; account-private bot auctions must not be presented as a global player economy |
| Manager task API | GET `/managertasks` or `/manager/tasks`; POST/PUT `/<id>` or `/reward` claims only server-observed progress |
| Native manager quest state | GET/PUT/POST `/managerquest`: `entries` with integer `key` 0, 1, 2 and signed integer `value`; persisted per account, never used as reward authority |
| Remote PC bridge | Actual socket authentication/URL-rewrite tests; retail game acceptance pending |
| Console login/gameplay | Unsupported; SDK message classes are not implementations |
| Ranked anti-cheat / trusted match adjudication | Not implemented; server authenticates fixed opponents and corroborated reports, not gameplay truth. Colluding clients can fabricate matching scores. Abrupt gameplay disconnect does not declare a winner |
| Full EASFC/social/store services | Existing placeholders remain outside the completed FUT backend paths |
| Multi-node deployment | Not supported by the file store; one process per data directory |

## Native save precautions

A native save is opaque client-produced data. A kickoff stub must never be advertised as a playable save. Tournament data and progress buffers precede their version fields; response progress uses lowercase `progressdata`. Season round IDs are one-based on user state and zero-based in fixture arrays. A catalogue's season ID is the position of that entry in the exact served list. Division 11 is an unplaced alias rather than another playable division. Set `ResumeCompetitionBlobs=false` to exercise minimal state responses without discarding stored data.

Both tournament and season results are server-accounted. Completing a match advances the server tally once. For tournaments, an old blob is invalidated on round advancement until the client saves the matching bracket. A client-provided round or claimed manager task never directly pays coins.

Online ranked seasons advance only when the paired ledger settles, independently of offline seasons and of Elo. Native online saves cannot reset that competition or set authoritative points/rewards. Pending/conflicting reports award nothing. Explicit own quits concede; unexpected in-game transport loss leaves the receipt unresolved until corroboration, self-forfeit or creation-age timeout cancellation. Receipt/history retention does not erase cumulative rating/W/D/L/coin receipts. The WAL and player snapshots are one backup/restore unit; see [RANKED.md](RANKED.md).

## Acceptance work requiring a client

Record the exact `fifa14.exe` and Cards/POW DLL hashes. Test a new club and an established club, buy from compare-price and squad builder, open/claim a pack, save/load squads, play all tournament rounds, leave/re-enter underway, complete a season, restart, then repeat on the remote bridge. Enable Legends only on a compatible client asset set. Run two distinct PC accounts through ranked lobby setup and a full peer game, both report orders, delayed native settlement, quit/disconnect/conflict cases, restart and ten-game online seasons before treating online play as supported. Any console support needs a separate platform capture and acceptance matrix.

These are concrete validation gaps; they are not completed by increasing `TickRate`.
