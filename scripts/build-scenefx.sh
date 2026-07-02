#!/usr/bin/env bash
# Build scenefx 0.2.1 (the effect-capable scene graph: blur / rounded corners /
# shadows) into compositor/.scenefx. Pinned to the tag that targets wlroots 0.18.
#   bash scripts/build-scenefx.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
PREFIX="$ROOT/compositor/.scenefx"
SRC="$ROOT/build/scenefx-src"
TAG=0.2.1   # wlroots >=0.18.1,<0.19

# Meson's default libdir is multiarch (lib/x86_64-linux-gnu) ONLY on Debian;
# Fedora/Arch use lib/, aarch64-Debian uses lib/aarch64-linux-gnu. Never
# hardcode it: force --libdir=lib for NEW builds and FIND the .so for existing
# ones (so a pre-existing multiarch build keeps working).
find_scenefx_lib() {
  find "$PREFIX/lib" "$PREFIX/lib64" -name 'libscenefx-0.2.so' -print -quit 2>/dev/null
}

if [ -n "$(find_scenefx_lib)" ]; then
  echo ">> scenefx already built at $PREFIX"; exit 0
fi

echo ">> fetching scenefx $TAG"
rm -rf "$SRC"
git clone --depth 1 --branch "$TAG" https://github.com/wlrfx/scenefx.git "$SRC"

echo ">> building scenefx -> $PREFIX"
# -Dwerror=false + visible setup log: same reasoning as build-wlroots.sh.
SETUP_LOG="$SRC/meson-setup.log"
if ! meson setup "$SRC/build" "$SRC" --prefix="$PREFIX" --libdir=lib \
    -Dwerror=false -Dexamples=false >"$SETUP_LOG" 2>&1; then
  echo "build-scenefx.sh: meson setup FAILED — full log:" >&2
  cat "$SETUP_LOG" >&2
  exit 1
fi
ninja -C "$SRC/build"
ninja -C "$SRC/build" install >/dev/null
PC_DIR="$(dirname "$(find "$PREFIX" -name 'scenefx-0.2.pc' -print -quit)")"
echo ">> scenefx installed: $(PKG_CONFIG_PATH="$PC_DIR" pkg-config --modversion scenefx-0.2)"
