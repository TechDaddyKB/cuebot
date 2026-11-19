#!/usr/bin/env bash
# Packs the Cuebot plugin for StreamController: dist/Cuebot-StreamController-plugin.zip holds the plugin folder
# (io_github_cuebot_Cuebot), ready to unzip into StreamController's plugins folder.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
out="$here/dist"
rm -rf "$out" && mkdir -p "$out"
cd "$here"
find io_github_cuebot_Cuebot -name '__pycache__' -prune -o -type f -print | LC_ALL=C sort \
  | zip -q -X "$out/Cuebot-StreamController-plugin.zip" -@
unzip -l "$out/Cuebot-StreamController-plugin.zip"
