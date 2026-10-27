#!/usr/bin/env bash
# Launches a packaged Cuebot under a virtual X server, checks it starts and stays up, and screenshots it.
#   packaging/linux/smoke-test.sh <name> <command...>
# Requires xvfb, imagemagick (import) and xdotool. Writes artifacts/smoke-<name>.png and .log.
set -uo pipefail
name="$1"; shift
root="$(cd "$(dirname "$0")/../.." && pwd)"
data="$(mktemp -d)"
log="$root/artifacts/smoke-$name.log"
export CUEBOT_DATA="$data"

Xvfb :99 -screen 0 1600x1000x24 >/dev/null 2>&1 &
xvfb=$!
export DISPLAY=:99
sleep 2

"$@" >"$log" 2>&1 &
app=$!

ok=0
for _ in $(seq 1 40); do
  sleep 1
  if ! kill -0 "$app" 2>/dev/null; then break; fi
  if xdotool search --name '^Cuebot$' >/dev/null 2>&1; then ok=1; break; fi
done
sleep 4   # let the first frames render

if [ "$ok" = 1 ] && kill -0 "$app" 2>/dev/null; then
  import -window root "$root/artifacts/smoke-$name.png"
  echo "Cuebot window is up ($name)"
else
  echo "Cuebot did not show a window ($name); output:" >&2
  cat "$log" >&2
fi

# The app's own log (CUEBOT_DATA works inside Flatpak too because the env var is passed through).
cat "$data"/logs/*.log 2>/dev/null | tee -a "$log" || true

kill "$app" 2>/dev/null; wait "$app" 2>/dev/null
kill "$xvfb" 2>/dev/null
[ "$ok" = 1 ]
