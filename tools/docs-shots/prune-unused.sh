#!/usr/bin/env bash
# The screenshot tests (tests/Cuebot.UiTests/DocsScreenshots.cs and DocsOverlayScreenshots.cs) save every tab, card and
# editor of the app, many more pictures than the guides use. Run this after them to delete the pictures no page links
# to, so only used ones are committed (some unused ones show local file paths, which should not be published).
set -euo pipefail
docs="$(cd "$(dirname "$0")/../../website/docs" && pwd)"
used="$(grep -rhoE 'screens/[A-Za-z0-9._-]+\.png' "$docs" --include='*.md' | sed 's|screens/||' | sort -u)"
removed=0
for file in "$docs"/assets/screens/*.png; do
  name="$(basename "$file")"
  if ! grep -qxF "$name" <<<"$used"; then
    rm -- "$file"
    removed=$((removed + 1))
  fi
done
echo "Removed $removed unused screenshots; $(ls "$docs"/assets/screens/*.png | wc -l) left."
