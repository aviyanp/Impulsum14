#!/usr/bin/env python3
"""Check a player table exported from the installed client database. Does not modify game files."""
import argparse
import csv
import hashlib
import json
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("player_table", type=Path, help="CSV/TSV export with playerid, assetId or id column")
p.add_argument("--output", type=Path, required=True)
a = p.parse_args()
data = a.player_table.read_bytes()
text = data.decode("utf-8-sig")
dialect = csv.Sniffer().sniff(text[:8192], delimiters=",\t;")
rows = list(csv.DictReader(text.splitlines(), dialect=dialect))
if not rows:
    raise SystemExit("Player export is empty")
key = next((k for k in rows[0] if k.lower() in ("playerid", "assetid", "id")), None)
if not key:
    raise SystemExit("Expected playerid, assetId or id column")
installed = {int(row[key]) for row in rows if row.get(key, "").isdigit()}
catalogue = json.loads((Path(__file__).resolve().parents[1] / "Impulsum14/FUTDB/legends.json").read_text())
required = {p["assetId"] for p in catalogue["players"]}
missing = sorted(required - installed)
if missing:
    raise SystemExit(f"Client export is missing {len(missing)} legend identities: {missing}")
a.output.write_text(json.dumps({"sourceSha256": hashlib.sha256(data).hexdigest(), "assetIds": sorted(required)}, indent=2) + "\n")
print(f"Verified {len(required)} identity rows; wrote {a.output}. Models, portraits and gameplay still need in-game validation.")
