#!/usr/bin/env bash
# Builds artifacts/Cuebot-<arch>.AppImage from a linux publish. Run on Linux (or in CI).
#   packaging/linux/build-appimage.sh [linux-x64|linux-arm64]
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
rid="${1:-linux-x64}"
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "unsupported rid $rid" >&2; exit 1 ;;
esac

publish="$root/artifacts/publish/$rid"
[ -x "$publish/Cuebot" ] || "$root/build/publish.sh" "$rid"

appdir="$root/artifacts/AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/lib/cuebot" "$appdir/usr/bin" "$appdir/usr/share/applications" \
         "$appdir/usr/share/metainfo" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$appdir/usr/lib/cuebot/"
ln -s ../lib/cuebot/Cuebot "$appdir/usr/bin/cuebot"

# Voice control's whisper.cpp libraries need the GCC OpenMP runtime (libgomp.so.1), which minimal desktops may lack.
# Bundle the build host's copy in a folder of its own; AppRun puts only that folder on the library path.
case "$arch" in x86_64) tag="x86-64" ;; *) tag="AArch64" ;; esac
gomp="$(ldconfig -p 2>/dev/null | grep -F "libgomp.so.1 " | grep -F "$tag" | head -n1 | sed "s/.*=> //")"
if [ -n "$gomp" ] && [ -f "$gomp" ]; then
  mkdir -p "$appdir/usr/lib/cuebot-deps"
  cp -L "$gomp" "$appdir/usr/lib/cuebot-deps/libgomp.so.1"
else
  echo "warning: libgomp.so.1 not found on the build host; Whisper voice control will need it on the user's system" >&2
fi

desktop="$root/packaging/linux/io.github.cuebot.Cuebot.desktop"
cp "$desktop" "$appdir/usr/share/applications/"
cp "$desktop" "$appdir/"
cp "$root/packaging/linux/io.github.cuebot.Cuebot.metainfo.xml" "$appdir/usr/share/metainfo/"
cp "$root/packaging/icons/cuebot-256.png" "$appdir/usr/share/icons/hicolor/256x256/apps/io.github.cuebot.Cuebot.png"
cp "$root/packaging/icons/cuebot-256.png" "$appdir/io.github.cuebot.Cuebot.png"

cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
# The folder holds nothing but libgomp (for voice control), so no other system library is affected.
export LD_LIBRARY_PATH="$here/usr/lib/cuebot-deps${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$here/usr/lib/cuebot/Cuebot" "$@"
EOF
chmod +x "$appdir/AppRun"

tool="$root/artifacts/appimagetool-$arch.AppImage"
if [ ! -x "$tool" ]; then
  curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$arch.AppImage"
  chmod +x "$tool"
fi
# --appimage-extract-and-run lets this work in containers without FUSE.
ARCH="$arch" "$tool" --appimage-extract-and-run "$appdir" "$root/artifacts/Cuebot-$arch.AppImage"
echo "Built $root/artifacts/Cuebot-$arch.AppImage"
