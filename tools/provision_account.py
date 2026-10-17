#!/usr/bin/env python3
"""Create a private-server identity. The access token is printed once; only its hash is stored."""
import argparse
import hashlib
import json
import os
import secrets
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("file", type=Path)
p.add_argument("--id", type=int, required=True)
p.add_argument("--name", required=True)
a = p.parse_args()
if a.id < 1 or not 1 <= len(a.name) <= 32 or any(ord(c) < 32 for c in a.name):
    raise SystemExit("Invalid id or name")
accounts = json.loads(a.file.read_text()) if a.file.exists() else []
if any(x["Id"] == a.id for x in accounts):
    raise SystemExit("Account id already exists")
token = secrets.token_urlsafe(32)
accounts.append({"Id": a.id, "Name": a.name, "TokenSha256": hashlib.sha256(token.encode()).hexdigest()})
a.file.parent.mkdir(parents=True, exist_ok=True)
temporary = a.file.with_name(a.file.name + ".tmp")
with os.fdopen(os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as f:
    json.dump(accounts, f, indent=2)
    f.flush()
    os.fsync(f.fileno())
os.replace(temporary, a.file)
print(f"Account {a.id} created. Store this private-server token securely:\n{token}")
