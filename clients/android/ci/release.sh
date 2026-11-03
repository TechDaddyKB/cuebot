#!/usr/bin/env bash
# Builds Cuebot Companion's release APK and AAB into clients/android/dist.
#
#   ci/release.sh <versionName> <versionCode>
#
# With ANDROID_KEYSTORE_BASE64 (a PKCS12 keystore) and ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS and
# ANDROID_KEY_PASSWORD set, as the GitHub Actions secrets provide them, both are signed with the release key, and
# the APK's certificate is checked against the release certificate below. Without them the build still succeeds:
# the files are unsigned and the job summary says so. The keystore is only ever written to the runner's temp folder
# and deleted at the end; nothing here prints a secret.
set -euo pipefail

version="${1:?versionName}"
code="${2:?versionCode}"
expected="BF:A2:61:65:91:4C:39:B1:02:03:D1:73:CD:B1:66:D3:BA:68:88:68:A8:23:08:D9:D1:4C:6C:6A:5C:56:37:87"
summary="${GITHUB_STEP_SUMMARY:-/dev/stderr}"

cd "$(dirname "$0")/.."
rm -rf dist && mkdir dist

signed=false
if [ -n "${ANDROID_KEYSTORE_BASE64:-}" ]; then
  keystore="${RUNNER_TEMP:-$(mktemp -d)}/cuebot-companion-release.p12"
  trap 'rm -f "$keystore"' EXIT
  printf '%s' "$ANDROID_KEYSTORE_BASE64" | base64 --decode > "$keystore"
  export ANDROID_KEYSTORE_FILE="$keystore"
  signed=true
else
  echo "::warning::ANDROID_KEYSTORE_BASE64 is not set: the Android APK and AAB are unsigned."
fi

./gradlew --no-configuration-cache -Pcuebot.versionName="$version" -Pcuebot.versionCode="$code" :app:assembleRelease :app:bundleRelease

if $signed; then
  apk=app/build/outputs/apk/release/app-release.apk
else
  apk=app/build/outputs/apk/release/app-release-unsigned.apk
fi
cp "$apk" "dist/Cuebot-Companion-$version.apk"
cp app/build/outputs/bundle/release/app-release.aab "dist/Cuebot-Companion-$version.aab"

if $signed; then
  apksigner="$(ls -d "$ANDROID_HOME"/build-tools/*/ | sort -V | tail -1)apksigner"
  certs="$("$apksigner" verify --print-certs "dist/Cuebot-Companion-$version.apk")"
  # "Signer #1 certificate SHA-256 digest: …" or "V2 Signer: certificate SHA-256 digest: …", per apksigner version
  # and signature scheme: every signer line must name the same, release, certificate.
  actual="$(printf '%s\n' "$certs" | sed -n 's/.*certificate SHA-256 digest: //p' | sort -u | tr '\n' ' ' | sed 's/ $//')"
  want="$(printf '%s' "$expected" | tr -d ':' | tr 'A-F' 'a-f')"
  if [ "$actual" != "$want" ]; then
    echo "::error::The APK is signed with certificate SHA-256 $actual, not the release certificate $expected."
    exit 1
  fi
  {
    echo "### Android"
    echo "Cuebot-Companion-$version.apk (versionCode $code) is signed and verified with \`apksigner verify --print-certs\`:"
    echo ""
    echo "Certificate SHA-256: \`$expected\`"
  } >> "$summary"
else
  {
    echo "### Android"
    echo "> [!WARNING]"
    echo "> Cuebot-Companion-$version.apk is **unsigned**: the ANDROID_KEYSTORE_BASE64 secret is not set for this run."
  } >> "$summary"
fi
ls -l dist
