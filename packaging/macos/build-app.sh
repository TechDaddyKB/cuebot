#!/usr/bin/env bash
# Builds artifacts/Cuebot-macos-<arch>.dmg holding a self-contained Cuebot.app (named like Cuebot-win-x64.zip, without
# the version, so the releases page always has the same file names).
#   packaging/macos/build-app.sh <version> [osx-arm64|osx-x64]
# Signs ad hoc by default (runs on the Mac that built it; elsewhere Gatekeeper asks first). With
# CUEBOT_MACOS_SIGN_IDENTITY set to a "Developer ID Application: ..." identity it signs with the hardened runtime instead.
# With CUEBOT_NOTARY_KEY_PATH (an App Store Connect API key .p8), CUEBOT_NOTARY_KEY_ID and CUEBOT_NOTARY_ISSUER_ID set as
# well, it also notarizes and staples the app and the disk image (docs/CODE_SIGNING.md).
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$1"
rid="${2:-osx-arm64}"
case "$rid" in
  osx-arm64) arch=arm64 ;;
  osx-x64) arch=x64 ;;
  *) echo "Unknown runtime '$rid' (osx-arm64 or osx-x64)" >&2; exit 2 ;;
esac
name="Cuebot-macos-$arch"
stage="$root/artifacts/$name"
app="$stage/Cuebot.app"

rm -rf "$stage" "$root/artifacts/$name.dmg"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish "$root/src/Cuebot.App/Cuebot.App.csproj" -c Release -r "$rid" --self-contained true \
  -p:DebugType=none -p:Version="$version" -o "$app/Contents/MacOS"

# CFBundleVersion must be numbers and dots ("0.1.0-rc.7" is not valid): both keys get the numeric part.
short="${version%%-*}"
sed -e "s/@VERSION@/$short/" -e "s/@SHORT_VERSION@/$short/" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"

# Icon: every size iconutil wants, from the PNGs in packaging/icons (scaled with sips where a size is missing).
iconset="$stage/Cuebot.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  for scale in 1 2; do
    px=$((size * scale))
    suffix=""; [ "$scale" = 2 ] && suffix="@2x"
    file="Cuebot.iconset/icon_${size}x${size}$suffix.png"
    src="$root/packaging/icons/cuebot-$px.png"
    [ -f "$src" ] || src="$root/packaging/icons/cuebot-512.png"
    sips -z "$px" "$px" "$src" --out "$stage/$file" > /dev/null
  done
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Cuebot.icns"
rm -rf "$iconset"
cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/"

if [ -n "${CUEBOT_MACOS_SIGN_IDENTITY:-}" ]; then
  # Inside out: every native library and helper program (such as .NET's createdump), then the executable, then the
  # bundle. Notarization rejects any unsigned Mach-O file.
  find "$app/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' -o -perm -u+x \) ! -path "$app/Contents/MacOS/Cuebot" -print0 |
    while IFS= read -r -d '' file; do
      if file -b "$file" | grep -q Mach-O; then
        codesign --force --timestamp --options runtime --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$file"
      fi
    done
  codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/Cuebot.entitlements" \
    --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$app/Contents/MacOS/Cuebot"
  # --deep also seals the managed .dll files next to the executable: codesign treats everything in Contents/MacOS as
  # code and "verify --deep --strict" fails on an unsigned one.
  codesign --force --deep --timestamp --options runtime --entitlements "$root/packaging/macos/Cuebot.entitlements" \
    --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$app"
else
  codesign --force --deep --sign - "$app"
fi
codesign --verify --deep --strict "$app"

notarize=0
if [ -n "${CUEBOT_MACOS_SIGN_IDENTITY:-}" ] && [ -n "${CUEBOT_NOTARY_KEY_PATH:-}" ]; then
  : "${CUEBOT_NOTARY_KEY_ID:?CUEBOT_NOTARY_KEY_ID is required to notarize}" "${CUEBOT_NOTARY_ISSUER_ID:?CUEBOT_NOTARY_ISSUER_ID is required to notarize}"
  notarize=1
fi

# Submits a file to Apple's notary service and waits; prints Apple's log when it is rejected. The key is only passed as a
# file path, never printed.
notarize_file() {
  local result
  result="$(xcrun notarytool submit "$1" --key "$CUEBOT_NOTARY_KEY_PATH" --key-id "$CUEBOT_NOTARY_KEY_ID" \
    --issuer "$CUEBOT_NOTARY_ISSUER_ID" --wait --timeout 30m --output-format json)" || { echo "$result" >&2; return 1; }
  echo "$result"
  if ! echo "$result" | grep -q '"status" *: *"Accepted"'; then
    id="$(echo "$result" | sed -n 's/.*"id" *: *"\([^"]*\)".*/\1/p' | head -n1)"
    [ -n "$id" ] && xcrun notarytool log "$id" --key "$CUEBOT_NOTARY_KEY_PATH" --key-id "$CUEBOT_NOTARY_KEY_ID" \
      --issuer "$CUEBOT_NOTARY_ISSUER_ID" >&2 || true
    return 1
  fi
}

if [ "$notarize" = 1 ]; then
  ditto -c -k --keepParent "$app" "$stage/Cuebot-notarize.zip"
  notarize_file "$stage/Cuebot-notarize.zip"
  rm -f "$stage/Cuebot-notarize.zip"
  xcrun stapler staple "$app"
  xcrun stapler validate "$app"
  spctl -a -vv -t exec "$app"
fi

ln -s /Applications "$stage/Applications"
dmg="$root/artifacts/$name.dmg"
hdiutil create -quiet -volname "Cuebot $version" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg"
if [ "$notarize" = 1 ]; then
  codesign --force --timestamp --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$dmg"
  notarize_file "$dmg"
  xcrun stapler staple "$dmg"
  xcrun stapler validate "$dmg"
  spctl -a -vv -t open --context context:primary-signature "$dmg"
fi
echo "$root/artifacts/$name.dmg"
