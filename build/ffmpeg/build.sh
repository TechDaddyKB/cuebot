#!/usr/bin/env bash
# Builds Cuebot's own minimal LGPL ffmpeg and ffprobe, used only to clean mod portal uploads
# (docs/MOD_PORTAL_PLAN.md, section 8). Only the formats the portal accepts are compiled in: no network, no devices,
# no hardware, no GPL or non-free parts. The results are static apart from the system libraries.
#
#   build/ffmpeg/build.sh <rid> <output folder>
#
# rid: linux-x64, linux-arm64, win-x64, osx-x64 or osx-arm64. win-x64 is cross-built on Linux (mingw-w64); linux-arm64
# builds natively on arm64 or with aarch64-linux-gnu-gcc on x64; osx-x64 builds on Apple silicon with -arch x86_64.
# Needs C and C++ compilers (libvpx has a C++ file), make, curl and nasm (x64 targets). Sources are downloaded once and checked against the SHA-256
# sums below (CUEBOT_FFMPEG_SOURCES keeps them; CUEBOT_FFMPEG_WORK holds the build).
set -euo pipefail

rid=${1:?rid}
out=${2:?output folder}
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
sources=${CUEBOT_FFMPEG_SOURCES:-$root/artifacts/ffmpeg-sources}
work=${CUEBOT_FFMPEG_WORK:-$root/artifacts/ffmpeg-build/$rid}
jobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)

# name|url|sha256. FFmpeg comes from its GitHub mirror (the n-tags are the release tags).
packages=(
  "ffmpeg|https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n9.0.2.tar.gz|6e374ed621e48faa40639307dff48ba6fe574a509977956d2cce9669b7cc27e9"
  "libvpx|https://github.com/webmproject/libvpx/archive/refs/tags/v1.17.0.tar.gz|1020f184046187baa2985dbde38e0691f49c44088bca7a1842b0236c6081dc0a"
  "opus|https://downloads.xiph.org/releases/opus/opus-1.6.1.tar.gz|6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1"
  "libwebp|https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz|e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564"
  "zlib|https://github.com/madler/zlib/releases/download/v1.4/zlib-1.4.tar.gz|02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584"
  "lame|https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz|ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e"
)

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# Toolchain per target. host: the autotools --host; vpx: libvpx's --target; arch/os: ffmpeg's.
cross="" host="" extra_cflags="" extra_ldflags="" exe=""
case "$rid" in
  linux-x64)   vpx=x86_64-linux-gcc; arch=x86_64; os=linux; extra_ldflags="-static" ;;
  linux-arm64) vpx=arm64-linux-gcc; arch=aarch64; os=linux; extra_ldflags="-static"
               if [ "$(uname -m)" != aarch64 ]; then cross=aarch64-linux-gnu-; host=aarch64-linux-gnu; fi ;;
  win-x64)     vpx=x86_64-win64-gcc; arch=x86_64; os=mingw32; cross=x86_64-w64-mingw32-; host=x86_64-w64-mingw32
               extra_ldflags="-static"; exe=.exe ;;
  osx-arm64)   vpx=arm64-darwin20-gcc; arch=arm64; os=darwin; extra_cflags="-arch arm64 -mmacosx-version-min=11.0"
               extra_ldflags="-arch arm64 -mmacosx-version-min=11.0"; [ "$(uname -m)" = arm64 ] || host=aarch64-apple-darwin ;;
  osx-x64)     vpx=x86_64-darwin20-gcc; arch=x86_64; os=darwin; extra_cflags="-arch x86_64 -mmacosx-version-min=11.0"
               extra_ldflags="-arch x86_64 -mmacosx-version-min=11.0"; [ "$(uname -m)" = x86_64 ] || host=x86_64-apple-darwin ;;
  *) echo "unknown rid $rid" >&2; exit 2 ;;
esac
cc="${cross}gcc"; [ "$os" = darwin ] && cc=clang
prefix="$work/prefix"
export CC="$cc" CFLAGS="-O2 $extra_cflags" LDFLAGS="$extra_ldflags"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig" PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
# Empty arrays are expanded as ${a[@]+"${a[@]}"}: macOS ships Bash 3.2, where "${a[@]}" of an empty array fails set -u.
hostflag=(); [ -n "$host" ] && hostflag=(--host="$host")

mkdir -p "$sources" "$work" "$out"
for p in "${packages[@]}"; do
  IFS='|' read -r name url sum <<<"$p"
  file="$sources/$name-$(basename "$url")"
  if [ ! -f "$file" ]; then curl -fsSL --retry 3 -o "$file.part" "$url"; mv "$file.part" "$file"; fi
  [ "$(sha256 "$file")" = "$sum" ] || { echo "checksum mismatch: $file" >&2; rm -f "$file"; exit 1; }
  rm -rf "$work/$name"; mkdir -p "$work/$name"
  tar -xf "$file" -C "$work/$name" --strip-components=1
done

( cd "$work/zlib"
  CHOST="${cross%-}" ./configure --static --prefix="$prefix" >/dev/null   # zlib takes its tool prefix from CHOST
  make -j"$jobs" libz.a >/dev/null && make install >/dev/null )

( cd "$work/lame"
  ./configure ${hostflag[@]+"${hostflag[@]}"} --prefix="$prefix" --enable-static --disable-shared --disable-frontend --disable-decoder \
    --disable-gtktest --disable-analyzer-hooks >/dev/null
  make -j"$jobs" >/dev/null && make install >/dev/null )

( cd "$work/opus"
  ./configure ${hostflag[@]+"${hostflag[@]}"} --prefix="$prefix" --enable-static --disable-shared --disable-doc --disable-extra-programs >/dev/null
  make -j"$jobs" >/dev/null && make install >/dev/null )

( cd "$work/libwebp"
  ./configure ${hostflag[@]+"${hostflag[@]}"} --prefix="$prefix" --enable-static --disable-shared --enable-libwebpmux --disable-libwebpdemux \
    --disable-libwebpdecoder --disable-libwebpextras --disable-png --disable-jpeg --disable-tiff --disable-gif --disable-wic \
    --disable-sdl --disable-gl >/dev/null
  make -j"$jobs" >/dev/null && make install >/dev/null )

( cd "$work/libvpx"
  CROSS="$cross" ./configure --target="$vpx" --prefix="$prefix" --enable-static --disable-shared --disable-examples \
    --disable-tools --disable-docs --disable-unit-tests --disable-vp8-encoder --enable-vp9-highbitdepth \
    --enable-pic >/dev/null
  make -j"$jobs" >/dev/null && make install >/dev/null )

list() { local IFS=,; echo "$*"; }
demuxers=(mov matroska mp3 ogg wav flac aac gif image_png_pipe image_jpeg_pipe image_webp_pipe)
# libvpx decodes VP8 and VP9 with transparency (ffmpeg's own decoders drop the alpha of WebM files).
decoders=(h264 hevc vp8 vp9 libvpx_vp8 libvpx_vp9 mjpeg png webp gif mp3 mp3float aac vorbis opus flac alac
  pcm_u8 pcm_s16le pcm_s16be pcm_s24le pcm_s24be pcm_s32le pcm_s32be pcm_f32le pcm_f32be pcm_f64le)
encoders=(png libwebp libwebp_anim libmp3lame libvpx_vp9 libopus)
muxers=(image2 webp mp3 webm)
parsers=(h264 hevc vp8 vp9 mpegaudio aac flac opus vorbis png mjpeg gif webp)
filters=(scale format aformat aresample fps null anull setsar crop transpose hflip vflip)
threads=(); [ "$os" = mingw32 ] && threads=(--enable-w32threads)
crossflags=(); [ -n "$cross$host" ] && crossflags=(--enable-cross-compile --cross-prefix="$cross")

config=(--prefix="$prefix" --arch="$arch" --target-os="$os" --cc="$cc" ${crossflags[@]+"${crossflags[@]}"} ${threads[@]+"${threads[@]}"}
  --pkg-config=pkg-config --pkg-config-flags=--static
  --extra-cflags="-I$prefix/include $extra_cflags" --extra-ldflags="-L$prefix/lib $extra_ldflags"
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug --disable-avdevice
  --disable-ffplay --enable-ffmpeg --enable-ffprobe --enable-static --disable-shared
  --enable-libvpx --enable-libopus --enable-libmp3lame --enable-libwebp --enable-zlib
  --enable-protocol=file --enable-demuxer="$(list "${demuxers[@]}")" --enable-decoder="$(list "${decoders[@]}")"
  --enable-encoder="$(list "${encoders[@]}")" --enable-muxer="$(list "${muxers[@]}")"
  --enable-parser="$(list "${parsers[@]}")" --enable-filter="$(list "${filters[@]}")" --enable-bsf=vp9_superframe)
( cd "$work/ffmpeg"
  ./configure "${config[@]}" >/dev/null || { tail -40 ffbuild/config.log >&2; exit 1; }
  grep -q '#define CONFIG_GPL 0' config.h && grep -q '#define CONFIG_NONFREE 0' config.h   # LGPL only, never GPL or non-free
  make -j"$jobs" >/dev/null )

cp "$work/ffmpeg/ffmpeg$exe" "$work/ffmpeg/ffprobe$exe" "$out/"
[ "$os" = darwin ] || "${cross}strip" "$out/ffmpeg$exe" "$out/ffprobe$exe"
cp "$work/ffmpeg/COPYING.LGPLv2.1" "$out/LICENSE.txt"
{
  echo "Cuebot's ffmpeg for cleaning mod portal uploads ($rid), LGPL 2.1 or later."
  echo "Built by build/ffmpeg/build.sh in https://github.com/TechDaddyKB/cuebot from these sources:"
  for p in "${packages[@]}"; do IFS='|' read -r name url sum <<<"$p"; echo "  $url (sha256 $sum)"; done
  echo "Configure: ${config[*]}"
} > "$out/SOURCES.txt"
echo "ffmpeg for $rid in $out"
