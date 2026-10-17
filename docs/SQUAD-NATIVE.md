# Native squad document evidence

The repository's existing `impulsum14launcher/patches/CardsDLLzf.dll` was read without modification. This is static contract evidence; it does not establish that the C# backend completes a retail FIFA match.

The squad-save JSON serializer starts at virtual address `0x1014c7a0`. Its field-name resolver at `0x101665c0` reads the string pointer table at `0x101d45f0 + 4 * enum`. The following keys resolve directly:

| Enum | JSON key |
|---|---|
| `0xdf` | `id` |
| `0xe1` | `index` |
| `0xe9` | `itemData` |
| `0xf3` | `kicktakers` |
| `0xf5` | `kitNumber` |
| `0x54` | `chemistry` |
| `0xbf` | `formation` |
| `0x11a` | `manager` |
| `0x17a` | `players` |
| `0x19b` | `rating` |
| `0x1e0` | `squadName` |
| `0x1ed` | `starRating` |

The normal save branch writes metadata, a `manager` array containing an object with an `id`, and `players` rows with `index`, `itemData: {id}`, and `kitNumber`. The player-row loop is at `0x1014c9f0–0x1014cacb`; IDs use the 64-bit integer writer at `0x1016f980`. `kitNumber` is written at `0x1014ca84–0x1014caad`.

The branch selected when the request subtype at object offset `0x198` equals `2` writes role updates. At `0x1014cad7–0x1014cafb`, it writes literal `captain` followed by a 64-bit player item ID. At `0x1014cb00–0x1014cba5`, it writes `kicktakers` as five objects, each containing a numeric `index` from 0 through 4 and a 64-bit `id`. Multiple roles may name the same player. A numeric array of player IDs is not what this serializer emits.

```json
{
  "id": 0,
  "captain": 850910001,
  "kicktakers": [
    {"index": 0, "id": 850910001},
    {"index": 1, "id": 850910001},
    {"index": 2, "id": 850910002},
    {"index": 3, "id": 850910002},
    {"index": 4, "id": 850910001}
  ]
}
```

The checked-out `reference-f14` source supplies complementary captured-traffic evidence. `server/local_identity.py` documents that normal player updates carry an empty `squadName`, meaning unchanged. `tools/verify_fifa14_beta2.py` exercises building a new squad a few players at a time, preserving that name, and copying a squad through `POST /squad` with `id: 0` plus the full 23-row `players` array. Its simplified captain test uses a numeric list; the unchanged local DLL above is the stronger source for the actual role JSON shape.

`SquadEditor.Apply` shares these parsing rules across create/copy and updates. An explicit empty player array or zero player IDs clear saved membership; omitted players preserve membership. Shirt numbers from 0 through 99 round-trip per occupied slot. Only owned club players can be assigned. Captain and kick-taker IDs must belong to the resulting squad. Removing a player also clears roles that refer to that player. The per-account transaction remains responsible for durable commit and rollback.
