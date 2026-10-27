#!/usr/bin/env bash
# Produces publish-ready AUR packages for a release: pins version + checksums and writes .SRCINFO.
# Run on Arch (needs makepkg). Output: artifacts/aur/<pkgname>/{PKGBUILD,.SRCINFO}
#   packaging/aur/prepare.sh <version> <source.tar.gz> <linux-x64.tar.gz> [linux-arm64.tar.gz]
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$1"; src="$2"; x64="$3"; arm64="${4:-}"
out="$root/artifacts/aur"
rm -rf "$out"; mkdir -p "$out"

sum() { sha256sum "$1" | cut -d' ' -f1; }

for pkg in cuebot cuebot-bin; do
  mkdir -p "$out/$pkg"
  sed -e "s/^pkgver=.*/pkgver=$version/" -e "s/^pkgrel=.*/pkgrel=1/" \
      "$root/packaging/aur/$pkg/PKGBUILD" > "$out/$pkg/PKGBUILD"
done

sed -i "s/^sha256sums=.*/sha256sums=('$(sum "$src")')/" "$out/cuebot/PKGBUILD"
sed -i "s/^sha256sums_x86_64=.*/sha256sums_x86_64=('$(sum "$x64")')/" "$out/cuebot-bin/PKGBUILD"
if [ -n "$arm64" ]; then
  sed -i "s/^sha256sums_aarch64=.*/sha256sums_aarch64=('$(sum "$arm64")')/" "$out/cuebot-bin/PKGBUILD"
fi

for pkg in cuebot cuebot-bin; do
  (cd "$out/$pkg" && makepkg --printsrcinfo > .SRCINFO)
done
echo "AUR packages ready in $out"
