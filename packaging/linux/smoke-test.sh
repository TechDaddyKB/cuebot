#!/usr/bin/env bash
# Launches a packaged Cuebot on a headless display server, checks the window appears and the expected
# display backend was used, and saves a screenshot. In the x11 and wayland modes it then sends SIGTERM and checks
# that Cuebot quits on its own, flushing its stores.
#   packaging/linux/smoke-test.sh <x11|wayland|wayland-weston|fallback|hang> <name> <command...>
# x11: xvfb xdotool scrot. wayland: sway grim jq. wayland-weston: weston. fallback: sway + x11 tools. hang: python3 + x11 tools.
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

# SMOKE_STOP: extra command that stops the app when killing its launcher is not enough (e.g. "flatpak kill <app-id>":
# the sandboxed process outlives `flatpak run` and would keep holding the single-instance lock).
cleanup() {
  [ -n "${SMOKE_STOP:-}" ] && eval "$SMOKE_STOP" >/dev/null 2>&1
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null; done
}
trap cleanup EXIT

start_xvfb() {
  Xvfb :99 -screen 0 1600x1000x24 >/dev/null 2>&1 & pids+=($!)
  export DISPLAY=:99
}

start_sway() {
  conf="$(mktemp)"
  printf 'output HEADLESS-1 resolution 1600x1000\nxwayland disable\n' > "$conf"
  WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 sway -c "$conf" >"$out.compositor.log" 2>&1 & pids+=($!)
  # Wait until sway answers on its IPC socket: the Wayland socket appears before its globals are ready.
  for _ in $(seq 1 40); do
    export SWAYSOCK="$(ls "$XDG_RUNTIME_DIR"/sway-ipc.*.sock 2>/dev/null | head -1)"
    [ -n "$SWAYSOCK" ] && swaymsg -t get_version >/dev/null 2>&1 && break
    sleep 0.5
  done
  use_wayland_socket
}

start_weston() {
  weston --backend=headless --renderer=pixman --width=1600 --height=1000 --debug >"$out.compositor.log" 2>&1 & pids+=($!)
  for _ in $(seq 1 40); do ls "$XDG_RUNTIME_DIR" | grep -qE '^wayland-[0-9]+$' && break; sleep 0.5; done
  sleep 2
  use_wayland_socket
}

use_wayland_socket() {
  sock="$(ls "$XDG_RUNTIME_DIR" | grep -E '^wayland-[0-9]+$' | head -1)"
  [ -n "$sock" ] || { echo "Wayland compositor did not start:" >&2; cat "$out.compositor.log" >&2; exit 1; }
  export WAYLAND_DISPLAY="$sock"
  export LIBGL_ALWAYS_SOFTWARE=1   # Mesa software rendering; the CI runner has no GPU
}

app_logged_backend() { grep -qs "display backend: $expected" "$data"/logs/*.log; }

case "$mode" in
  x11)
    start_xvfb
    unset WAYLAND_DISPLAY
    expected="X11"
    window_up() { xdotool search --name '^Cuebot$' >/dev/null 2>&1; }
    screenshot() { scrot --overwrite "$out.png"; }
    ;;
  wayland)          # native Wayland on sway; no X server, so XWayland cannot mask a failure
    export XDG_RUNTIME_DIR="$(mktemp -d)"; chmod 700 "$XDG_RUNTIME_DIR"
    unset DISPLAY
    start_sway
    expected="Wayland"
    window_up() { swaymsg -t get_tree 2>/dev/null | jq -e '.. | objects | select(.name? == "Cuebot")' >/dev/null; }
    screenshot() { grim "$out.png"; }
    ;;
  wayland-weston)   # native Wayland on weston (has no window IPC: rely on the app log, then screenshot)
    export XDG_RUNTIME_DIR="$(mktemp -d)"; chmod 700 "$XDG_RUNTIME_DIR"
    unset DISPLAY
    start_weston
    expected="Wayland"
    window_up() { app_logged_backend; }
    screenshot() { (cd "$(dirname "$out")" && weston-screenshooter >/dev/null 2>&1 \
                     && mv -f wayland-screenshot*.png "$out.png" 2>/dev/null) || true; }
    ;;
  fallback)         # a Wayland compositor Avalonia cannot use, plus an X server: expect a relaunch on X11
    export XDG_RUNTIME_DIR="$(mktemp -d)"; chmod 700 "$XDG_RUNTIME_DIR"
    start_sway
    start_xvfb
    expected="X11"
    window_up() { xdotool search --name '^Cuebot$' >/dev/null 2>&1; }
    screenshot() { scrot --overwrite "$out.png"; }
    ;;
  hang)             # a "compositor" that accepts the connection and never answers, plus an X server:
                    # Avalonia blocks on it instead of failing, so the start-up watchdog must relaunch on X11
    export XDG_RUNTIME_DIR="$(mktemp -d)"; chmod 700 "$XDG_RUNTIME_DIR"
    python3 -c 'import socket,sys,time
s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1]); s.listen(8); held=[]
while True: held.append(s.accept()[0])' "$XDG_RUNTIME_DIR/wayland-hang" & pids+=($!)
    export WAYLAND_DISPLAY=wayland-hang
    export CUEBOT_WAYLAND_TIMEOUT=8
    start_xvfb
    expected="X11"
    window_up() { xdotool search --name '^Cuebot$' >/dev/null 2>&1; }
    screenshot() { scrot --overwrite "$out.png"; }
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

# The Cuebot process itself (named cuebot in the Flatpak): launchers (AppImage runtime, bwrap) do not pass SIGTERM on.
cuebot_pid() {
  local queue=("$app") p child
  while [ ${#queue[@]} -gt 0 ]; do
    p="${queue[0]}"; queue=("${queue[@]:1}")
    [[ "$(cat /proc/"$p"/comm 2>/dev/null)" == [Cc]uebot ]] && { echo "$p"; return; }
    for child in $(pgrep -P "$p"); do queue+=("$child"); done
  done
  echo "$app"
}

quit_checked=0
if [ "$ok" = 1 ] && [[ "$mode" == x11 || "$mode" == wayland* ]]; then
  quit_checked=1
  kill -TERM "$(cuebot_pid)"
  for _ in $(seq 1 15); do kill -0 "$app" 2>/dev/null || break; sleep 1; done
  if kill -0 "$app" 2>/dev/null; then
    echo "Cuebot did not quit within 15 s of SIGTERM ($name, $mode)" >&2
    ok=0
  else
    echo "Cuebot quit on SIGTERM ($name, $mode)"
  fi
fi

cat "$data"/logs/*.log 2>/dev/null >>"$out.log"
if [ "$quit_checked" = 1 ] && ! grep -q "Shutting down" "$out.log"; then
  echo "Expected Cuebot to flush its stores after SIGTERM; output:" >&2
  cat "$out.log" >&2
  ok=0
fi
if ! grep -q "display backend: $expected" "$out.log"; then
  echo "Expected the $expected display backend; app log:" >&2
  cat "$data"/logs/*.log >&2 2>/dev/null
  ok=0
fi
if [ "$mode" = fallback ] && ! grep -q "relaunching with X11" "$out.log"; then
  echo "Expected a Wayland failure followed by an X11 relaunch; output:" >&2
  cat "$out.log" >&2
  ok=0
fi
if [ "$mode" = hang ] && ! grep -q "did not open a window within .*relaunching with X11" "$out.log"; then
  echo "Expected the start-up watchdog to relaunch on X11; output:" >&2
  cat "$out.log" >&2
  ok=0
fi
[ "$ok" = 1 ]
