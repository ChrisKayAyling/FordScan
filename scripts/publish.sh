#!/usr/bin/env bash
# Builds framework-dependent (needs the .NET 9 runtime) packages for all desktop platforms into artifacts/.
set -euo pipefail
cd "$(dirname "$0")/.."
for rid in "${@:-win-x64 linux-x64 osx-arm64 osx-x64}"; do
  for r in $rid; do
    out="artifacts/$r"
    rm -rf "$out"
    dotnet publish src/FordDiag.App -c Release -r "$r" --self-contained false -o "$out"
    (cd artifacts && rm -f "FordDiag-$r.zip" && zip -qr "FordDiag-$r.zip" "$r")
    echo "artifacts/FordDiag-$r.zip"
  done
done
