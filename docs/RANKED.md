# Ranked matches, Elo and online seasons

This is an opt-in, two-account ranked backend. The server binds the opponents, authenticates reports, updates both ratings and rewards in one durable ledger transition, and projects cumulative receipts into each account. It does not run or observe the football simulation. Retail PC peer gameplay and the native post-match screens still require acceptance testing; protocol fixtures are not evidence that two retail executables completed a match.

## Enable and pair two players

Set `EnablePeerLobbies` to `true` in `server.json` (or the JSON selected by `FIFA14_CONFIG`). It defaults to `false`. There is no separate Elo enable flag and no `FIFA14_ENABLE_PEER_LOBBIES` environment override. The ranked ledger is opened at normal server startup even when peer lobbies are disabled.

```json
{
  "EnablePeerLobbies": true,
  "MatchmakingInitialEloRange": 100,
  "MatchmakingEloRangeStep": 50,
  "MatchmakingEloRangeIntervalSeconds": 10,
  "MatchmakingMaxEloRange": 1000,
  "MatchmakingMaxWaitSeconds": 120,
  "Ranked": {
    "InitialRating": 1000,
    "MinimumRating": 100,
    "MaximumRating": 3000,
    "KFactor": 32,
    "ProvisionalKFactor": 48,
    "ProvisionalGames": 10,
    "SettlementWaitMilliseconds": 2500
  }
}
```

This is a configuration fragment, not a complete hosted deployment. Two distinct accounts are required. The default trusted loopback account cannot play itself; for two PCs, use `LocalMode=false`, provision two tokens in `AccountsFile`, and configure reachable addresses as described in [DEPLOYMENT.md](DEPLOYMENT.md). Use the loopback bridge where needed, but arrange peer reachability separately: the bridge relays HTTP/Blaze, not gameplay packets.

The authenticated Blaze `StartMatchmaking` request must set `CriteriaData.RankedGameRulePrefs.DesiredRankedGameValue` to `RANKED`. Otherwise the search is unranked. Direct `CreateGame`/`JoinGame` lobbies do not create ranked receipts. The exact protocol-version string, the `OSDK_gameMode` generic rule's single desired value (or `default` when omitted), and ranked/unranked flag define separate pools. Other submitted skill values do not replace the server's Elo rating.

Within a compatible pool, arrival order breaks ties. The waiting search's rating window starts at ±100 and grows by 50 every 10 seconds, up to ±1000 by default; the older search controls the window. Maintenance retries matching as the window widens, even without a new arrival. The requested session duration is clamped to 1 second through `MatchmakingMaxWaitSeconds` (120 seconds by default), and never extends beyond the authenticated session's expiry. Queue capacity is the smaller of `MatchmakingQueueLimit` (1000) and server `MaxPlayers` (1000); up to `MatchmakingConcurrentSetups` pair setups can be in progress (16 by default, validated 1–256).

A successful ranked pair receives a fixed, server-allocated ranked match ID bound to both accounts and the lobby's game ID. The lobby's `GameReportingId` becomes that ranked ID. An account may have only one active ranked receipt. Binding is durably flushed outside the lobby lock; a failed/cancelled setup queues cancellation of any binding without rewards, retrying failed cleanup. Starting actual gameplay requires two members and each member's own connected mesh observation. There is no trust in a peer's claim that the opponent disconnected.

## Authenticated REST API

These are this backend's extensions, not a claim that stock FIFA 14 already calls them. Use the configured HTTP(S) origin and this prefix:

`/fut/rs4/ut/game/fifa14/ranked`

Every endpoint requires the normal account authentication: `Authorization: Bearer <private-token>`, or a valid `X-UT-SID`/`EASW-Session`. The normal trusted loopback identity still applies in local mode. A Nucleus-ID header cannot select another account. The read/report endpoints remain available when peer lobbies are disabled; disabling lobbies prevents new peer lobby calls, not ledger recovery or existing receipt access.

| Method | Path after prefix | Result |
|---|---|---|
| `GET` | `/self` | `{ "rating": <rating view>, "activeMatch": <receipt or null> }` for the caller |
| `GET` | `/leaderboard?count=50&start=0` | `{ "players": [...] }`; known ledger accounts, sorted by rating descending, wins descending, then account ID ascending |
| `GET` | `/match` | `{ "match": <active receipt or null> }` for the caller |
| `GET` | `/match/<id>` | A retained receipt; only either bound participant may read it |
| `POST` or `PUT` | `/match/<id>/report` | Submit a score or self-forfeit; returns `{ "result": <report view>, "credits": <current wallet> }` |

`count` defaults to 50 and is clamped to 1–200 (also bounded by general `MaxPageSize`); `start` defaults to zero and is bounded by the general query parser and `Ranked.MaxPlayers`. Unseen accounts have the initial rating in `/self`, but merely reading one does not persist it or add it to the leaderboard. A typed rating/receipt/report view uses its C# property names, such as `AccountId`, `Rating`, `Season`, `Id`, `PlayerA`, `Status`, `Match`, `Reporter`, and `Duplicate`; the wrapper names in the table are lowercase as shown. Timestamps in receipts/history are Unix seconds. Receipt scores and rating deltas use fixed `PlayerA`/`PlayerB` orientation.

Score reports use the reporter's perspective:

```json
{ "goalsFor": 2, "goalsAgainst": 1 }
```

If A sends this report, B must send `{"goalsFor":1,"goalsAgainst":2}`. Both values must be integers from 0 to 50. The first report remains `pending` without changing ratings, match rewards or season progress. Matching inverse reports settle once. A disagreement becomes `conflict`; neither side is awarded a result. A submitted score cannot be edited. An identical retry is a duplicate; either player can still concede their own unresolved game:

```json
{ "forfeit": true }
```

This immediately settles a 0–3 loss for the caller without requiring peer corroboration. A caller cannot name an opponent to forfeit. A retained settled report retry returns the existing receipt and cannot pay again. Cancelled matches reject reports. Unknown or retired IDs return 404; a different account is rejected; changed scores and reports against cancelled matches return 409. Invalid payloads return 400. There is deliberately no REST endpoint to create/rebind a ranked pair, overwrite Elo, award a winner, or reset the online season.

## Elo and rewards

The default initial rating is 1000, bounded to 100–3000. For player A, the expected score is `1 / (1 + 10^((ratingB - ratingA) / 400))`. Actual score is 1 for a win, 0.5 for a draw, and 0 for a loss. The transfer is `round(K * (actual - expected))`, with midpoint rounding away from zero; A receives the transfer and B loses exactly the same amount.

`KFactor` defaults to 32. A player is provisional while their settled `GamesPlayed` is below `ProvisionalGames` (10). If either opponent is provisional, both use the shared `ProvisionalKFactor` (48). The transfer is clamped as a pair so both stay inside the rating bounds while preserving their exact combined rating. Score margin does not affect Elo. A self-forfeit counts as an ordinary 0–3 result for ratings, W/D/L and season points.

Ranked match rewards are configured under `Ranked`, separately from the top-level offline match reward options:

| Option | Default | Meaning |
|---|---|---|
| `WinCoins` | 500 | Win reward |
| `DrawCoins` | 300 | Draw reward |
| `LossCoins` | 200 | Loss reward |
| `GoalCoins` | 20 | Per scored goal in a corroborated result |

The conceding player receives zero **match** coins; the opponent receives the win reward without synthetic goal bonuses. Either can still receive the applicable season-completion reward. Receipt `CoinsA`/`CoinsB` include season rewards; `SeasonCoinsA`/`SeasonCoinsB` identify that component. `CoinsEarned` in a rating view is cumulative earned ranked coins, not the spendable wallet balance.

Rating bounds must contain `InitialRating`, with `MinimumRating >= 0`, `MaximumRating <= 100000`, and minimum below maximum. Both K values accept 1–1000; provisional games accepts 0–1000; each coin option accepts 0–1000000. Configuration validation is not a migration: tightening bounds below existing stored ratings causes startup validation to fail. Back up and plan policy changes rather than editing ledger files.

## Separate ten-game online seasons

Elo is a continuous rating; online division is a separate points competition, not an Elo band. New ranked accounts begin in Division 10, Season 1. Each settled ranked game contributes three points for a win, one for a draw and zero for a loss. After exactly ten settled games, `Seasons.Evaluate(..., online: true)` awards one title/promotion/maintenance/relegation outcome and coins, adjusts the division by at most one, records history, and starts the next season with zero games/points. A title also promotes when below Division 1. Division 1 cannot promote; Division 10 cannot relegate. Lifetime titles/promotions/relegations remain cumulative.

These are server competition presets, not a complete recreation of every historical retail award:

| Division | Title points / coins | Promotion points / coins | Hold points / coins | Relegation coins |
|---|---|---|---|---|
| 10 | 12 / 2000 | 9 / 1600 | 0 / 1000 | 0 |
| 9 | 13 / 3000 | 10 / 2400 | 6 / 1900 | 1300 |
| 8 | 15 / 4000 | 12 / 3200 | 8 / 2500 | 1600 |
| 7 | 17 / 5000 | 14 / 4000 | 8 / 2800 | 1800 |
| 6 | 19 / 6000 | 16 / 4800 | 10 / 3400 | 1900 |
| 5 | 19 / 7000 | 16 / 5600 | 10 / 3900 | 2200 |
| 4 | 19 / 9000 | 16 / 7200 | 10 / 5000 | 2900 |
| 3 | 21 / 11000 | 18 / 8800 | 12 / 6200 | 3100 |
| 2 | 21 / 13000 | 18 / 10400 | 12 / 7400 | 3400 |
| 1 | 23 / 15000 | — | 14 / 10500 | 4500 |

The ranked ledger owns online progress. Its cumulative receipts project into the account's `OnlineDivision`, `OnlineSeason`, online history, wallet and overall W/D/L. Offline `Season`/`OfflineDivision` are separate and are not advanced by ranked games. Opaque online save blobs cannot set points, ratings, rewards or divisions; clients cannot reset a ranked online season. Advancing to a new ranked season retires the previous season's resumable blob.

## Native match-end integration and unresolved games

An active server-bound ranked receipt makes native `POST .../match` online; requesting `type: "online"` without a binding is rejected. The local native `matchId` and shared `rankedMatchId` are separate identity domains, even if their numbers happen to coincide. Native `/match/ready` binds the starters. `POST`/`PUT .../match/end` (also the existing `destroymatch` alias) reports the score from `myMatchStats.goals` and `opponentMatchStats.goals`, verifies that `endReason` agrees, and never directly pays a second online reward.

The end handler waits asynchronously for up to `Ranked.SettlementWaitMilliseconds` (default 2500, configurable 0–10000). If the opponent corroborates in that window, the native response is refreshed with settled coins, season coins, `elo` and `eloChange`. Otherwise it returns `settlementPending: true` with no ranked award yet. A later retained-receipt end retry can finalize a settled or cancelled result; finalized responses remain unchanged after later spending. If a terminal ledger receipt has already been pruned, the native receipt finalizes with zero attributed match award, current projected wallet and `rankedReceiptStatus: "retired"`, never a new credit or an invented winner. a later authenticated account transaction also projects cumulative earnings even if that player was offline when settlement occurred. Do not promise that stock retail UI understands the pending fields or retries: verify both peers' native post-match behavior in the exact client build.

| Event | Ranked policy |
|---|---|
| Explicit score reports agree | Settle the reported result once |
| Score reports disagree | Leave `conflict`; allow own concession or eventual timeout cancellation |
| Native `QUIT`/`DNF` (including `FORFEIT` alias) | Self-forfeit, including before native ready |
| Native `NO_CONTEST` or match reset after creating a bound native match | Explicit self-forfeit; the client's optional Ready flag cannot erase a peer's result |
| Ranked lobby teardown in `INITIALIZING`/`PRE_GAME` | Before either score report: cancel with no result. Once a score was submitted, explicit own departure concedes; transport loss leaves the result unresolved |
| Explicit own leave, or host destroy, during `IN_GAME` | The departing caller self-forfeits; a host cannot kick the opponent to declare them the loser |
| Abrupt transport loss, session revocation or expiry during `IN_GAME` | Remove the lobby but leave the receipt unresolved; do not infer a loser from connection loss |
| Unresolved receipt reaches `MatchTimeoutMinutes` | Cancel without rating/reward/season changes; timeout uses creation time, not last report time |

`MatchTimeoutMinutes` defaults to 60. Maintenance checks expiry; it is not an exact wall-clock scheduling guarantee. A disconnected participant may authenticate again and corroborate or explicitly concede before expiry. Unresolved active receipts survive a restart and block that account's next ranked binding until settlement or cancellation. A POST_GAME lobby teardown does not itself supply a missing result.

## Durability, retention and operation

The authoritative ledger lives under `DataDirectory/ranked/`:

| File | Purpose |
|---|---|
| `writer.lock` | Exclusive open handle rejects another ledger writer |
| `journal.bin` | Versioned, length/complement/SHA-256-framed write-ahead log |
| `snapshot.json` | Latest complete checkpoint |
| `snapshot.json.tmp` | Temporary checkpoint replacement, if present |

Each accepted transition is synchronously flushed before publication. A settlement frame contains the match and both complete account transitions, so one player's win cannot be committed without the other's loss. Checkpoints flush a temporary snapshot, replace the checkpoint in the same directory, persist the directory on POSIX (write-through replacement on Windows), and only then reset the journal. Recovery replays newer sequence numbers, truncates an incomplete tail, and fails closed on a complete corrupt frame or invalid state. SHA-256 detects corruption, not malicious modification by an administrator. An uncertain write stops further ledger writes until restart recovery; it is not safe to continue awarding results in that process.

| `Ranked` option | Default | Accepted range / purpose |
|---|---|---|
| `MaxPlayers` | 10000 | 2–100000 durable ranked accounts; separate from server `MaxPlayers` |
| `MaxActiveMatches` | 1000 | 1 through `Ranked.MaxPlayers / 2` |
| `RetainedMatches` | 2000 | 0–100000 terminal receipts; active receipts excluded |
| `SeasonHistoryLimit` | 20 | 0–100 completed online seasons per player |
| `CheckpointInterval` | 128 | 1–10000 ledger transitions, not matches |
| `MaxJournalBytes` | 16777216 | 1048576–268435456; checkpoint trigger after an accepted transition |
| `MatchTimeoutMinutes` | 60 | 1–10080 |
| `SettlementWaitMilliseconds` | 2500 | 0–10000 native response wait |

Active receipts are not evicted by terminal retention. Lowering receipt/history retention is supported on restart, but lowering active/player bounds below stored state is not a data migration. Retired match IDs are never reused and cannot create a new settlement. Receipt lookup/retry returns 404 after retirement; retain enough receipts for the expected retry window. Retention does not erase lifetime ratings, W/D/L, titles or cumulative earned coins, and does not bound the number of stored players beyond `MaxPlayers`.

The account projection records the cumulative ranked coins/W/D/L already applied and the ranked revision. On the next account transaction it credits only the difference, then atomically persists that account snapshot. If the ledger settled before a process crash or the opponent was away, replaying the projection does not double-credit it. The ledger and player files must be backed up/restored together: a ledger older than an account's applied cumulative receipts is rejected rather than silently subtracting or replaying earnings.

Run exactly one process per data directory on a local filesystem, not replicas or a shared network filesystem. Stop the server before copying the entire data directory for a consistent backup, including `players/` and `ranked/`. Keep write access restricted and retain the authentication/network precautions in [DEPLOYMENT.md](DEPLOYMENT.md). Bounds describe supported input/storage policy, not measured concurrent capacity or latency.

No console platform authentication, gameplay simulation, P2P relay, NAT traversal service, host migration, gameplay anti-cheat, or trusted match adjudicator is supplied. Two colluding authenticated clients can corroborate a fabricated score. Validate two exact PC client builds through lobby setup, full peer gameplay, both report orders, waiting/late settlement, quit/disconnect/conflict, ten-game online progression and restart recovery before claiming retail online support.

## Division policy overrides

`OnlineDivisionRules` and `OfflineDivisionRules` are optional top-level arrays. Empty arrays select the documented presets. An override must contain exactly ten rows ordered Division 1 through Division 10, each with `Title`, `Promotion`, `Hold`, `TitleCoins`, `PromotionCoins`, `HoldCoins`, and optional `RelegationCoins`. Points must fit a ten-game season (0–30), Division 1 must have `Promotion: 0`, Division 10 must have `Hold: 0`, and reward amounts are bounded 0–1000000. The same table drives catalogue awards and actual settlement, including the ranked ledger. Change competition policy between seasons after backing up; a threshold change immediately affects any unfinished season.
