#!/usr/bin/env bash
# Builds artifacts/Cuebot-<version>-<rid>.tar.gz: a self-contained build plus the desktop integration files
# (used by GitHub releases and the cuebot-bin AUR package).
#   packaging/linux/build-tarball.sh <version> [linux-x64|linux-arm64]
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$1"
rid="${2:-linux-x64}"
name="Cuebot-$version-$rid"
stage="$root/artifacts/$name"

rm -rf "$stage"
dotnet publish "$root/src/Cuebot.App/Cuebot.App.csproj" -c Release -r "$rid" --self-contained true \
  -p:DebugType=none -p:Version="$version" -o "$stage/lib"
mkdir -p "$stage/share"
cp "$root/packaging/linux/io.github.cuebot.Cuebot.desktop" "$root/packaging/linux/io.github.cuebot.Cuebot.metainfo.xml" "$root/LICENSE" "$stage/share/"
cp -r "$root/packaging/icons" "$stage/share/icons"
tar -C "$root/artifacts" -czf "$root/artifacts/$name.tar.gz" "$name"
echo "$root/artifacts/$name.tar.gz"
