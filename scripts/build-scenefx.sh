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

if [ -f "$PREFIX/lib/x86_64-linux-gnu/libscenefx-0.2.so" ]; then
  echo ">> scenefx already built at $PREFIX"; exit 0
fi

echo ">> fetching scenefx $TAG"
rm -rf "$SRC"
git clone --depth 1 --branch "$TAG" https://github.com/wlrfx/scenefx.git "$SRC"

echo ">> building scenefx -> $PREFIX"
meson setup "$SRC/build" "$SRC" --prefix="$PREFIX" -Dexamples=false >/dev/null
ninja -C "$SRC/build"
ninja -C "$SRC/build" install >/dev/null
echo ">> scenefx installed: $(PKG_CONFIG_PATH="$PREFIX/lib/x86_64-linux-gnu/pkgconfig" pkg-config --modversion scenefx-0.2)"
