#!/usr/bin/env bash
# Build scenefx 0.4.1 (the effect-capable scene graph: blur / rounded corners /
# shadows) into compositor/.scenefx. Pinned to the tag that targets wlroots 0.19.
#   bash scripts/build-scenefx.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
PREFIX="$ROOT/compositor/.scenefx"
SRC="$ROOT/build/scenefx-src"
TAG=0.4.1   # wlroots >=0.19,<0.20

# Meson's default libdir is multiarch (lib/x86_64-linux-gnu) ONLY on Debian;
# Fedora/Arch use lib/, aarch64-Debian uses lib/aarch64-linux-gnu. Never
# hardcode it: force --libdir=lib for NEW builds and FIND the .so for existing
# ones (so a pre-existing multiarch build keeps working).
find_scenefx_lib() {
  find "$PREFIX/lib" "$PREFIX/lib64" -name 'libscenefx-0.4.so' -print -quit 2>/dev/null
}

if [ -n "$(find_scenefx_lib)" ]; then
  echo ">> scenefx already built at $PREFIX"; exit 0
fi

echo ">> fetching scenefx $TAG"
rm -rf "$SRC"
git clone --depth 1 --branch "$TAG" https://github.com/wlrfx/scenefx.git "$SRC"

# WTF patches on the pinned tag (see packaging/patches/*.patch for rationale):
#   - primary-node fallback: machines without a DRM RENDER node (VMs without
#     GPU passthrough, CI) get software GL via kms_swrast on the card node
#     instead of a refused startup.
for p in "$ROOT"/packaging/patches/scenefx-*.patch; do
  [ -f "$p" ] || continue
  echo ">> applying $(basename "$p")"
  # A patch that no longer applies to the pinned tag must NOT abort the build:
  # the fallback it adds only matters on render-node-less machines (CI/VMs), and
  # a real GPU builds fine without it. Warn loudly and continue so a stale patch
  # never blocks the whole compositor build; re-port it separately.
  if ! git -C "$SRC" apply "$p"; then
    echo "build-scenefx.sh: WARNING — $(basename "$p") did not apply to $TAG; " \
         "skipping (render-node fallback absent — fine on real GPUs)" >&2
  fi
done

echo ">> building scenefx -> $PREFIX"
# scenefx depends on wlroots; we vendor wlroots (no system 0.19 package), so its
# pkgconfig dir must FRONT the search path or meson can't resolve wlroots-0.19.
export PKG_CONFIG_PATH="$ROOT/compositor/.wlroots/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
# -Dwerror=false + visible setup log: same reasoning as build-wlroots.sh.
SETUP_LOG="$SRC/meson-setup.log"
# -Dc_std=c11: same meson-1.3 floor as build-wlroots.sh (Ubuntu 24.04).
if ! meson setup "$SRC/build" "$SRC" --prefix="$PREFIX" --libdir=lib \
    -Dc_std=c11 -Dwerror=false -Dexamples=false >"$SETUP_LOG" 2>&1; then
  echo "build-scenefx.sh: meson setup FAILED — full log:" >&2
  cat "$SETUP_LOG" >&2
  exit 1
fi
ninja -C "$SRC/build"
ninja -C "$SRC/build" install >/dev/null
PC_DIR="$(dirname "$(find "$PREFIX" -name 'scenefx-0.4.pc' -print -quit)")"
echo ">> scenefx installed: $(PKG_CONFIG_PATH="$PC_DIR" pkg-config --modversion scenefx-0.4)"
