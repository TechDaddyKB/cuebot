#!/usr/bin/env bash
# Runs every opt-in suite that talks to real services (Twitch, streaming platforms, OBS, Python) using the
# settings in ~/.cuebot/live.env (or $CUEBOT_LIVE_ENV_FILE), and writes reports to artifacts/live-tests.
#   build/run-live-tests.sh            # everything configured
#   build/run-live-tests.sh Twitch     # only tests whose name contains "Twitch"
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
results="$root/artifacts/live-tests"
mkdir -p "$results"
stamp="$(date +%Y%m%d-%H%M%S)"
env_file="${CUEBOT_LIVE_ENV_FILE:-$HOME/.cuebot/live.env}"

# Load KEY=VALUE pairs into this process only; values are never printed.
if [ -f "$env_file" ]; then
  export CUEBOT_LIVE_ENV_FILE="$env_file"
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    [[ -z "$line" || "$line" == \#* ]] && continue
    line="${line#export }"
    key="${line%%=*}"; value="${line#*=}"
    [[ "$key" == "$line" || -z "$value" ]] && continue
    value="${value%\"}"; value="${value#\"}"; value="${value%\'}"; value="${value#\'}"
    [ -n "${!key:-}" ] && continue   # variables already set in the environment win over the file
    export "$key=$value"
  done < "$env_file"
  echo "Loaded live-test settings from $env_file"
else
  echo "No $env_file; only settings already in the environment are used (see docs/live.env.example)."
fi

failed=0
run() {
  local project="$1" filter="$2" name
  name="$(basename "$project")"
  [ -n "${3:-}" ] && filter="${filter:+$filter&}FullyQualifiedName~$3"
  local args=(test "$root/$project" --results-directory "$results"
              --logger "trx;LogFileName=$name-$stamp.trx" --logger "console;verbosity=normal")
  [ -n "$filter" ] && args+=(--filter "$filter")
  dotnet "${args[@]}" 2>&1 | tee "$results/$name-$stamp.log"
  [ "${PIPESTATUS[0]}" -eq 0 ] || failed=$((failed + 1))
}
run tests/Cuebot.LiveTests "" "${1:-}"
run tests/Cuebot.Obs.Tests "FullyQualifiedName~LiveObsTests" "${1:-}"
run tests/Cuebot.Scripting.Tests "FullyQualifiedName~Python" "${1:-}"
echo "Reports: $results"
exit "$failed"
