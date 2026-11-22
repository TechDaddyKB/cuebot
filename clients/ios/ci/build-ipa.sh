#!/usr/bin/env bash
# Archives Cuebot Companion and exports an App Store .ipa to $RUNNER_TEMP/export.
# Usage: build-ipa.sh <version> <build>. Needs import-signing.sh to have run.
set -euo pipefail

version="$1"
build="$2"
bundle_id=digital.techdaddy.cuebot.companion
team_id=2X7RD3TDJ7
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$here/../CuebotCompanion"
archive="$RUNNER_TEMP/CuebotCompanion.xcarchive"
export_dir="$RUNNER_TEMP/export"
options="$RUNNER_TEMP/ExportOptions.plist"

python3 -I "$here/prepare_signing.py" "$project_dir/CuebotCompanion.xcodeproj/project.pbxproj" \
  "$bundle_id" "$version" "$build" "$CUEBOT_IOS_PROFILE_NAME"

xcodebuild archive \
  -project "$project_dir/CuebotCompanion.xcodeproj" \
  -scheme CuebotCompanion \
  -configuration Release \
  -destination generic/platform=iOS \
  -archivePath "$archive"

/usr/libexec/PlistBuddy -c "Clear dict" "$options" > /dev/null 2>&1 || true
plist() { /usr/libexec/PlistBuddy -c "Add $1" "$options" > /dev/null; }
plist ":method string app-store-connect"
plist ":destination string export"
plist ":signingStyle string manual"
plist ":signingCertificate string Apple Distribution"
plist ":teamID string $team_id"
plist ":uploadSymbols bool true"
plist ":manageAppVersionAndBuildNumber bool false"
plist ":provisioningProfiles dict"
/usr/libexec/PlistBuddy -c "Add :provisioningProfiles:$bundle_id string $CUEBOT_IOS_PROFILE_NAME" "$options" > /dev/null

xcodebuild -exportArchive \
  -archivePath "$archive" \
  -exportPath "$export_dir" \
  -exportOptionsPlist "$options"

ipa="$export_dir/Cuebot Companion.ipa"
[ -f "$ipa" ] || { echo "::error::The export produced no .ipa."; ls -la "$export_dir"; exit 1; }

# What goes to Apple must carry the version and build this run decided on.
info="$RUNNER_TEMP/ipa-Info.plist"
unzip -p "$ipa" 'Payload/*.app/Info.plist' > "$info"
got_version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$info")
got_build=$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" "$info")
if [ "$got_version" != "$version" ] || [ "$got_build" != "$build" ]; then
  echo "::error::The .ipa is $got_version ($got_build), expected $version ($build)."
  exit 1
fi
echo "Built Cuebot Companion $got_version ($got_build)"
