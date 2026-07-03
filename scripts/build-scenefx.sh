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

# The WTF patches are ABI-load-bearing (the glass-refraction patch adds a public
# symbol the shim links against), so a prebuilt prefix is only valid if it was
# built from the CURRENT patch set. Stamp the prefix with a hash of the patches
# and force a rebuild when they change — otherwise a stale cached .scenefx would
# silently miss new symbols and the shim link would fail far from the cause.
PATCH_STAMP="$PREFIX/.wtf-patchset.sha256"
patchset_hash() {
  cat "$ROOT"/packaging/patches/scenefx-*.patch 2>/dev/null | sha256sum | cut -d' ' -f1
}

if [ -n "$(find_scenefx_lib)" ]; then
  if [ "$(cat "$PATCH_STAMP" 2>/dev/null)" = "$(patchset_hash)" ]; then
    echo ">> scenefx already built at $PREFIX (patch set current)"; exit 0
  fi
  echo ">> scenefx prefix present but patch set changed — rebuilding"
  rm -rf "$PREFIX"
fi

echo ">> fetching scenefx $TAG"
rm -rf "$SRC"
git clone --depth 1 --branch "$TAG" https://github.com/wlrfx/scenefx.git "$SRC"

# WTF patches on the pinned tag (see packaging/patches/*.patch for rationale):
#   - glass-refraction: adds the frosted-glass edge-refraction shader + a public
#     wlr_scene_rect_set_refraction() symbol. REQUIRED — the shim links it, so a
#     failure to apply must abort rather than yield an unlinkable scenefx.
#   - primary-node fallback: software GL on render-node-less machines (CI/VMs).
#     Optional — a real GPU builds fine without it, so it only warns.
REQUIRED_PATCHES="scenefx-glass-refraction.patch"
for p in "$ROOT"/packaging/patches/scenefx-*.patch; do
  [ -f "$p" ] || continue
  base="$(basename "$p")"
  echo ">> applying $base"
  if git -C "$SRC" apply "$p"; then
    continue
  fi
  if printf '%s\n' $REQUIRED_PATCHES | grep -qx "$base"; then
    echo "build-scenefx.sh: FATAL — required patch $base did not apply to $TAG;" \
         "scenefx would build without the glass-refraction ABI and the shim" \
         "would fail to link. Re-port the patch to the pinned tag." >&2
    exit 1
  fi
  echo "build-scenefx.sh: WARNING — $base did not apply to $TAG; skipping" \
       "(optional; render-node fallback absent — fine on real GPUs)" >&2
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
# Record which patch set this prefix was built from so a later run with changed
# patches invalidates the early-exit above and rebuilds.
patchset_hash > "$PATCH_STAMP"
PC_DIR="$(dirname "$(find "$PREFIX" -name 'scenefx-0.4.pc' -print -quit)")"
echo ">> scenefx installed: $(PKG_CONFIG_PATH="$PC_DIR" pkg-config --modversion scenefx-0.4)"
