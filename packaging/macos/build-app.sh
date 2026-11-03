#!/usr/bin/env bash
# Builds artifacts/Cuebot-macos-<arch>.dmg holding a self-contained Cuebot.app (named like Cuebot-win-x64.zip, without
# the version, so the releases page always has the same file names).
#   packaging/macos/build-app.sh <version> [osx-arm64|osx-x64]
# Signs ad hoc by default (runs on the Mac that built it; elsewhere Gatekeeper asks first). With
# CUEBOT_MACOS_SIGN_IDENTITY set to a "Developer ID Application: ..." identity it signs with the hardened runtime instead;
# notarizing is a separate step.
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

short="${version%%-*}"
sed -e "s/@VERSION@/$version/" -e "s/@SHORT_VERSION@/$short/" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
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
  # Inside out: every native library and the executable, then the bundle.
  find "$app/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 |
    xargs -0 codesign --force --timestamp --options runtime --sign "$CUEBOT_MACOS_SIGN_IDENTITY"
  codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/Cuebot.entitlements" \
    --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$app/Contents/MacOS/Cuebot"
  codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/Cuebot.entitlements" \
    --sign "$CUEBOT_MACOS_SIGN_IDENTITY" "$app"
else
  codesign --force --deep --sign - "$app"
fi
codesign --verify --deep --strict "$app"

ln -s /Applications "$stage/Applications"
hdiutil create -quiet -volname "Cuebot $version" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$root/artifacts/$name.dmg"
echo "$root/artifacts/$name.dmg"
