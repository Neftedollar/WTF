#!/usr/bin/env bash
# Build a PINNED wlroots into compositor/.wlroots and VENDOR it with WTF.
#
# Why vendor: wlroots breaks ABI every minor release and distros package
# whichever version they like — depending on the system package makes "installs
# on any distro" a lottery (Arch ships only the newest; Debian N+1 may drop
# 0.19). Bundling the exact version the shim is written against (same treatment
# as scenefx) leaves only STABLE system ABIs as runtime deps: libinput, libdrm,
# libseat, libxkbcommon, Mesa, pixman, udev, xcb.
#
#   bash scripts/build-wlroots.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
PREFIX="$ROOT/compositor/.wlroots"
SRC="$ROOT/build/wlroots-src"
TAG=0.19.3   # what wtf-shim.c + scenefx 0.4.1 target

find_wlroots_lib() {
  find "$PREFIX/lib" -name 'libwlroots-0.19.so*' -print -quit 2>/dev/null
}

if [ -n "$(find_wlroots_lib)" ]; then
  echo ">> wlroots already built at $PREFIX"; exit 0
fi

# wlroots 0.19 needs libwayland >= 1.23; Ubuntu 24.04 LTS ships 1.22. When the
# system one is too old, vendor wayland into the same prefix first — it's a
# ~1-minute build, and the launcher's LD_LIBRARY_PATH already covers the
# staged copy. The vendored pkgconfig dir then FRONTS the search path so
# wlroots (and its wayland-scanner lookup) resolve the new one.
if ! pkg-config --atleast-version=1.23 wayland-server 2>/dev/null; then
  WAYLAND_TAG=1.23.1
  echo ">> system libwayland too old — vendoring wayland $WAYLAND_TAG"
  WSRC="$ROOT/build/wayland-src"
  rm -rf "$WSRC"
  git clone --depth 1 --branch "$WAYLAND_TAG" \
    https://gitlab.freedesktop.org/wayland/wayland.git "$WSRC"
  WSETUP_LOG="$WSRC/meson-setup.log"
  if ! meson setup "$WSRC/build" "$WSRC" \
      --prefix="$PREFIX" --libdir=lib \
      -Ddocumentation=false -Dtests=false \
      >"$WSETUP_LOG" 2>&1; then
    echo "build-wlroots.sh: wayland meson setup FAILED — full log:" >&2
    cat "$WSETUP_LOG" >&2
    exit 1
  fi
  ninja -C "$WSRC/build"
  ninja -C "$WSRC/build" install >/dev/null
fi
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig:${PKG_CONFIG_PATH:-}"

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
# -Dwerror=false: pinned wlroots sources + a NEWER toolchain/libinput trip -Werror
# (e.g. libinput 1.29 added LIBINPUT_SWITCH_KEYPAD_SLIDE => unhandled-enum
# error on Arch). Warnings in pinned third-party code are not our errors.
# Setup log goes to a FILE and is dumped on failure — never swallow the
# reason a distro can't configure the build.
# c_std=c11: wlroots asks for c23, which needs meson >= 1.4 — Ubuntu
# 24.04 ships 1.3. The code is c11-clean (0.17 built as c11); pinning c11
# keeps one meson floor across every distro.
SETUP_LOG="$SRC/meson-setup.log"
if ! meson setup "$SRC/build" "$SRC" \
    --prefix="$PREFIX" --libdir=lib \
    -Dc_std=c11 \
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
PC_DIR="$(dirname "$(find "$PREFIX" -name 'wlroots-0.19.pc' -print -quit)")"
echo ">> wlroots installed: $(PKG_CONFIG_PATH="$PC_DIR" pkg-config --modversion wlroots-0.19)"
