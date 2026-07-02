#!/usr/bin/env bash
# Build a PINNED wlroots into compositor/.wlroots and VENDOR it with WTF.
#
# Why vendor: wlroots breaks ABI every minor release and distros package
# whichever version they like — depending on the system package makes "installs
# on any distro" a lottery (Arch ships only the newest; Debian N+1 may drop
# 0.18). Bundling the exact version the shim is written against (same treatment
# as scenefx) leaves only STABLE system ABIs as runtime deps: libinput, libdrm,
# libseat, libxkbcommon, Mesa, pixman, udev, xcb.
#
#   bash scripts/build-wlroots.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
PREFIX="$ROOT/compositor/.wlroots"
SRC="$ROOT/build/wlroots-src"
TAG=0.18.2   # what wtf-shim.c + scenefx 0.2.1 target

find_wlroots_lib() {
  find "$PREFIX/lib" -name 'libwlroots-0.18.so*' -print -quit 2>/dev/null
}

if [ -n "$(find_wlroots_lib)" ]; then
  echo ">> wlroots already built at $PREFIX"; exit 0
fi

echo ">> fetching wlroots $TAG"
rm -rf "$SRC"
git clone --depth 1 --branch "$TAG" \
  https://gitlab.freedesktop.org/wlroots/wlroots.git "$SRC"

echo ">> building wlroots -> $PREFIX"
# --libdir=lib: never let meson pick a distro-specific multiarch libdir.
# gles2 only (no vulkan -> no glslang build dep); examples off; xwayland
# support enabled (the WM builds against its own wlroots, so this must be on).
# Backend/feature deps that are missing (e.g. no xcb headers => no x11
# nested backend) degrade via meson auto features instead of failing.
# -Dwerror=false: 0.18 sources + a NEWER toolchain/libinput trip -Werror
# (e.g. libinput 1.29 added LIBINPUT_SWITCH_KEYPAD_SLIDE => unhandled-enum
# error on Arch). Warnings in pinned third-party code are not our errors.
# Setup log goes to a FILE and is dumped on failure — never swallow the
# reason a distro can't configure the build.
SETUP_LOG="$SRC/meson-setup.log"
if ! meson setup "$SRC/build" "$SRC" \
    --prefix="$PREFIX" --libdir=lib \
    -Dwerror=false \
    -Dexamples=false \
    -Drenderers=gles2 \
    -Dxwayland=enabled \
    >"$SETUP_LOG" 2>&1; then
  echo "build-wlroots.sh: meson setup FAILED — full log:" >&2
  cat "$SETUP_LOG" >&2
  exit 1
fi
ninja -C "$SRC/build"
ninja -C "$SRC/build" install >/dev/null
PC_DIR="$(dirname "$(find "$PREFIX" -name 'wlroots-0.18.pc' -print -quit)")"
echo ">> wlroots installed: $(PKG_CONFIG_PATH="$PC_DIR" pkg-config --modversion wlroots-0.18)"
