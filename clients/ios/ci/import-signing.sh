#!/usr/bin/env bash
# Puts the Apple Distribution certificate into a temporary keychain and installs the App Store profile, after checking
# that the profile is the right kind for this app. The workflow removes both at the end of the job.
set -euo pipefail

bundle_id=digital.techdaddy.cuebot.companion
team_id=2X7RD3TDJ7

keychain="$RUNNER_TEMP/cuebot-ios.keychain-db"
keychain_password="$(uuidgen)"
echo "::add-mask::$keychain_password"
security create-keychain -p "$keychain_password" "$keychain"
echo "CUEBOT_IOS_KEYCHAIN=$keychain" >> "$GITHUB_ENV"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"

p12="$RUNNER_TEMP/ios-dist.p12"
(umask 077; printf '%s' "$IOS_DIST_CERT_P12_BASE64" | base64 --decode > "$p12")
security import "$p12" -k "$keychain" -P "$IOS_DIST_CERT_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security
rm -f "$p12"
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keychain_password" "$keychain" > /dev/null
# shellcheck disable=SC2046  # the existing keychains are separate words
security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')
security find-identity -v -p codesigning "$keychain" | grep -q "Apple Distribution" || {
  echo "::error::The certificate secret holds no Apple Distribution identity."
  exit 1
}

profile="$RUNNER_TEMP/app-store.mobileprovision"
printf '%s' "$IOS_APPSTORE_PROFILE_BASE64" | base64 --decode > "$profile"
plist="$RUNNER_TEMP/app-store-profile.plist"
security cms -D -i "$profile" > "$plist"

read_key() { /usr/libexec/PlistBuddy -c "Print :$1" "$plist"; }
uuid=$(read_key UUID)
name=$(read_key Name)
app_id=$(read_key Entitlements:application-identifier)
if [ "$app_id" != "$team_id.$bundle_id" ]; then
  echo "::error::The profile is for $app_id, not $team_id.$bundle_id."
  exit 1
fi
if [ "$(read_key Entitlements:get-task-allow)" != "false" ]; then
  echo "::error::The profile is a development profile. An App Store profile is needed."
  exit 1
fi
if /usr/libexec/PlistBuddy -c "Print :ProvisionedDevices" "$plist" > /dev/null 2>&1; then
  echo "::error::The profile is an ad hoc profile. An App Store profile is needed."
  exit 1
fi
expires=$(date -j -f "%a %b %d %T %Z %Y" "$(read_key ExpirationDate)" +%s 2>/dev/null || echo 0)
now=$(date +%s)
if [ "$expires" -gt 0 ] && [ "$expires" -le "$now" ]; then
  echo "::error::The App Store profile has expired. Renew it (docs/CODE_SIGNING.md)."
  exit 1
fi
if [ "$expires" -gt 0 ] && [ "$((expires - now))" -lt 2592000 ]; then
  echo "::warning::The App Store profile expires within 30 days. Renew it (docs/CODE_SIGNING.md)."
fi

# Xcode 16 and later read the second folder, older versions the first.
for dir in "$HOME/Library/MobileDevice/Provisioning Profiles" "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"; do
  mkdir -p "$dir"
  cp "$profile" "$dir/$uuid.mobileprovision"
done
rm -f "$profile" "$plist"

{
  echo "CUEBOT_IOS_PROFILE_UUID=$uuid"
  echo "CUEBOT_IOS_PROFILE_NAME=$name"
} >> "$GITHUB_ENV"
