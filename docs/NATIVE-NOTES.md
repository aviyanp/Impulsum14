# Competition contract and evidence

Audited 2026-10-16. These checks establish backend behavior and conservative native save handling. No retail FIFA executable was run in this environment, so successful menu entry, gameplay and re-entry remain client acceptance tests.

## Primary game contract

EA's [FIFA 14 PC manual](https://akamai.cdn.ea.com/eadownloads/u/f/manuals/GAME-FIFA14/FIFA14pcwinMANOLen.pdf), printed page 16, describes FUT seasons as ten games in both Online and Single Player, with promotion, holding and relegation. It describes tournaments as single-round knockout. The implementation therefore settles season awards only after ten authoritative results, counts draws as one point, and treats DNF/QUIT as losses. Knockout draws are unresolved and cannot advance a bracket; a winner must be reported after extra time/penalties.

EA's [2013 FUT competition list](https://www.easports.com/fr/fifa/ultimate-team/news/2013/liste-competitions-fut14-fr) identifies the fourteen offline cup names and trophy unlock tiers. It lists separate first-clear packs and squad entry requirements. This backend's cup payouts are explicitly coin-only server presets; those packs and all historical eligibility variants are not claimed as implemented. The Silver Links trophy unlock is three. Ultimate Cup uses the highest supported AI difficulty preset; this is not a measured reconstruction of retail's Ultimate AI setting.

The complete historical online/offline threshold and economy tables were not recovered from an EA primary document. `Seasons.Rules` is the explicit server preset used identically for displayed prizes and settlement. It separates online from offline, gives Division 1 no promotion outcome, and makes Division 10 a bottom tier that cannot relegate. Offline Division 1 maintenance uses its holding award instead of a phantom promotion prize. No exact retail economy replication is asserted.

## Existing PC DLL inspection

The existing `impulsum14launcher/patches/CardsDLLzf.dll` was read without modification. SHA-256: `79d7c8cadd7efd8e438aae354da3314c1913b4c581d293773f8c676551d85c2d`. It is a PE32 i386 DLL. Its relevant literal strings occur at these file offsets:

| File offset | Literal / implication |
| --- | --- |
| `0x1a4578` | `ut/%s/season/%%s/reset` |
| `0x1a45a4` | `ut/%s/season/%%s/user` |
| `0x1a45c8` | `ut/%s/season/user` |
| `0x1a7be4` | `?divisionId=%d&seasonId=%d` |
| `0x1a8084` | Writer prefix with `round`, `dataVersion`, `data` |
| `0x1a7eb8` | Writer suffix with `progressDataVersion`, `progressData` |
| `0x1a99b4` | Cup writer prefix with `round`, `dataVersion`, `tournamentData` |
| `0x1a9b34` | `/teams?groupId=%d&count=%d` |
| `0x1ae924`, `0x1ae938` | Reader key table contains `progressDataVersion` and lowercase `progressdata` |
| `0x1aef78`, `0x1afac8` | Native key table includes `knockout` and `aigroup` |

Literal presence alone does not establish a full parser contract. The independently inspected literals agree with the reverse engineering notes in [`limevevo/f14-localfut/server/beta_identity.py`](https://github.com/limevevo/f14-localfut/blob/main/server/beta_identity.py), inspected in the supplied reference checkout. That source documents the following captured/parser-derived behavior:

- Season catalogue `id` and user `seasonId` are one-based; user `seasonId` selects a position in the exact served catalogue. Fixture `roundId` is zero-based while user/save `round` is one-based.
- Division 11 is the unplaced client's alias for Division 10. A fresh response uses `seasonId: -1` with the real division. Alias 11 is only returned alongside a valid bound save and a matching last-served catalogue entry.
- Native reads require each data buffer to precede its version field. Writers accept both progress casings; read responses emit only lowercase `progressdata`.
- Cup `rounds` is an array and `treeType` is a native string enum. `aigroup` is echoed as `groupId` when obtaining opponents. The exact teams response member is `teamId`, with fifteen opponents for a sixteen-team cup.
- Empty cup data and first-round all-zero progress are unsafe resume stubs. Season kickoff data has no player table; later season saves still need a populated table. The structural guard rejects malformed base64, invalid gzip, oversized compressed output and round values outside the fixture list.
- Native season prize entries use `prizeLevel`, `thresholdPoint` and `awardMappings[].awards[]`. The coin-only award form avoids inventing an item-zero asset lookup. The supplied reference describes this season award form as a hypothesis requiring its own executable test; cup acceptance does not prove season acceptance.

Every scheduled AI opponent now has at least sixteen players plus a kit and badge in the repository catalogues. Invalid historical pool entries are replaced deterministically with eligible teams near that cup's remaining mean strength. This establishes catalogued resources, not a guarantee that every installed client has identical assets.

## Authority, rollover and retention

Only settled match receipts can change played/W/D/L/points, divisions, trophies, history or coin awards. A native save can supply bounded opaque presentation buffers and versions. Its round must equal the server's next authoritative fixture. Path/query/body target IDs must agree with the current served catalogue and division; a round-11 write cannot finish or re-open a season. Client reset preserves lifetime counters and history, increments the offline season generation and keeps the current division. Online seasons cannot be reset by a client.

Each match binds its offline season generation or cup run/round at kickoff. Settlement rejects an old generation or round. Each result invalidates the preceding presentation blobs until the client saves the authoritative next round. Closed cup runs retain a tombstone; the global trophy total and per-cup win count are updated once by a final win. The wallet caller applies the returned prize in the same durable match transaction. Cup save echoing returns only accepted safe server state, never a rejected request's blobs.

Stored resume state is bound to its season/run generation. Old blobs with missing bindings fail closed. A bounded set of 2,048 retired fingerprints survives season rollover, cup completion, reset and repository restart; season history retains the newest 100 completed entries. Fingerprints normalize base64 whitespace. Native payloads contain no server generation nonce, so an unseen old opaque blob deliberately altered for an indistinguishable current round cannot be authenticated from these native fields alone. Such buffers still authorize no competitive progress or awards. The resume guard is structural, not a proof that arbitrary bytes are valid game objects.

## Regression coverage

`CompetitionInvariantTests.Register` checks all feasible ten-game records across both modes and all divisions, threshold boundaries, ladder endpoints, DNF/draw rules, counter/history retention, exact catalogue positions, fixture/team validity, native save member order/casing, unsafe stubs, hostile buffer sizes/gzip, forged target/result fields, stale same-division saves and generation-bound cup progression. Its snapshot rehydration test verifies persisted bindings and retired fingerprints. `PrepareRestartState` / `AssertRestartState` extend the integration suite's real repository close/reopen test to verify both resumable competitions and rejected retired saves after disk reload.
