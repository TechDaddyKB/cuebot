#!/usr/bin/env bash
# Launches a packaged Cuebot on a headless display server, checks the window appears and the expected
# display backend was used, and saves a screenshot.
#   packaging/linux/smoke-test.sh <x11|wayland> <name> <command...>
# x11 needs: xvfb xdotool scrot.  wayland needs: sway grim jq (runs sway headless, no X server).
# Writes artifacts/smoke-<name>-<mode>.png and .log.
set -uo pipefail
mode="$1"; name="$2"; shift 2
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/artifacts/smoke-$name-$mode"
# SMOKE_APP_DATA: where the app keeps its data when CUEBOT_DATA cannot reach it (Flatpak sandbox).
if [ -n "${SMOKE_APP_DATA:-}" ]; then
  data="$SMOKE_APP_DATA"; rm -rf "$data/logs"
else
  data="$(mktemp -d)"; export CUEBOT_DATA="$data"
fi
pids=()

cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null; done; }
trap cleanup EXIT

case "$mode" in
  x11)
    Xvfb :99 -screen 0 1600x1000x24 >/dev/null 2>&1 & pids+=($!)
    export DISPLAY=:99
    unset WAYLAND_DISPLAY
    expected="X11"
    window_up() { xdotool search --name '^Cuebot$' >/dev/null 2>&1; }
    screenshot() { scrot --overwrite "$out.png"; }
    ;;
  wayland)
    export XDG_RUNTIME_DIR="$(mktemp -d)"; chmod 700 "$XDG_RUNTIME_DIR"
    unset DISPLAY   # no X server at all: a broken Wayland path cannot hide behind XWayland
    conf="$(mktemp)"
    printf 'output HEADLESS-1 resolution 1600x1000\nxwayland disable\n' > "$conf"
    WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 sway -c "$conf" >"$out.compositor.log" 2>&1 & pids+=($!)
    # Wait until sway answers on its IPC socket: the Wayland socket appears before its globals are ready.
    for _ in $(seq 1 40); do
      export SWAYSOCK="$(ls "$XDG_RUNTIME_DIR"/sway-ipc.*.sock 2>/dev/null | head -1)"
      [ -n "$SWAYSOCK" ] && swaymsg -t get_version >/dev/null 2>&1 && break
      sleep 0.5
    done
    sock="$(ls "$XDG_RUNTIME_DIR" | grep -E '^wayland-[0-9]+$' | head -1)"
    [ -n "$sock" ] || { echo "sway did not start:" >&2; cat "$out.compositor.log" >&2; exit 1; }
    export WAYLAND_DISPLAY="$sock"
    export LIBGL_ALWAYS_SOFTWARE=1   # Mesa software rendering; the CI runner has no GPU
    expected="Wayland"
    window_up() { swaymsg -t get_tree 2>/dev/null | jq -e '.. | objects | select(.name? == "Cuebot")' >/dev/null; }
    screenshot() { grim "$out.png"; }
    ;;
  *) echo "unknown mode $mode" >&2; exit 2 ;;
esac
sleep 2

"$@" >"$out.log" 2>&1 & app=$!; pids+=($app)

ok=0
for _ in $(seq 1 45); do
  sleep 1
  kill -0 "$app" 2>/dev/null || break
  if window_up; then ok=1; break; fi
done
sleep 4   # let the first frames render

if [ "$ok" = 1 ] && kill -0 "$app" 2>/dev/null; then
  screenshot
  echo "Cuebot window is up ($name, $mode)"
else
  echo "Cuebot did not show a window ($name, $mode); output:" >&2
  cat "$out.log" >&2
  ok=0
fi

cat "$data"/logs/*.log 2>/dev/null >>"$out.log"
if ! grep -q "display backend: $expected" "$out.log"; then
  echo "Expected the $expected display backend; app log:" >&2
  cat "$data"/logs/*.log >&2 2>/dev/null
  ok=0
fi
[ "$ok" = 1 ]
