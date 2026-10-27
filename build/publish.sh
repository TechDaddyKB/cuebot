#!/usr/bin/env bash
# Publishes a self-contained Cuebot build.  Usage: build/publish.sh [rid]   (default linux-x64)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-linux-x64}"
out="$root/artifacts/publish/$rid"
rm -rf "$out"
dotnet publish "$root/src/Cuebot.App/Cuebot.App.csproj" -c Release -r "$rid" --self-contained true -o "$out" -p:DebugType=none
chmod +x "$out/Cuebot"
echo "Published to $out"
