#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build tests/Impulsum14.Tests/Impulsum14.Tests.csproj -c Release --nologo
dotnet tests/Impulsum14.Tests/bin/Release/net8.0/Impulsum14.Tests.dll
python3 -m py_compile tools/provision_account.py tools/legend_manifest.py tools/load_test.py
