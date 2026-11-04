#!/usr/bin/env bash
# Runs ./gradlew with the given arguments, and runs it again (up to three times in all) when it failed only because
# dependencies could not be downloaded. Maven Central and Google's Maven sometimes answer "not found" for artifacts that
# exist, for a minute or so; Gradle retries timeouts and server errors itself, but not those. Any other failure, such
# as a compile error or a failing test, is reported at once.
#
#   ci/gradle.sh <gradle arguments…>
set -uo pipefail

cd "$(dirname "$0")/.." || exit
log="$(mktemp)"
trap 'rm -f "$log"' EXIT

refresh=()
for attempt in 1 2 3; do
  ./gradlew "${refresh[@]}" "$@" 2>&1 | tee "$log"
  status=${PIPESTATUS[0]}
  [ "$status" -eq 0 ] && exit 0
  grep -qE 'Could not (resolve|find|download|GET|HEAD|parse POM)' "$log" || exit "$status"
  [ "$attempt" -eq 3 ] && break
  echo "::warning::Gradle could not download its dependencies (attempt $attempt of 3); trying again in $((attempt * 30)) seconds."
  sleep $((attempt * 30))
  # Gradle remembers a missing artifact for the rest of the day: ask the repositories again.
  refresh=(--refresh-dependencies)
done
echo "::error::Gradle could not download its dependencies after 3 attempts."
exit "$status"
