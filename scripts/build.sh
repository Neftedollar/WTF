#!/usr/bin/env bash
# Build the whole WTF compositor: the C shim (libwtf_shim.so) + the F# host.
#   bash scripts/build.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo ">> 0/3  ensuring scenefx (blur/rounded/shadows) is built"
bash "$ROOT/scripts/build-scenefx.sh"
export PKG_CONFIG_PATH="$ROOT/compositor/.scenefx/lib/x86_64-linux-gnu/pkgconfig:$ROOT/compositor/.scenefx/lib/pkgconfig:${PKG_CONFIG_PATH:-}"

echo ">> 1/3  building C shim (libwtf_shim.so) via meson/ninja"
cd "$ROOT/compositor"
if [ ! -d build ]; then
  meson setup build
fi
ninja -C build

echo ">> 2/3  building F# host"
cd "$ROOT"
dotnet build src/WTF.Host/WTF.Host.fsproj -c Release

echo ">> 3/3  placing libwtf_shim.so next to the host binary"
HOSTDIR="$ROOT/src/WTF.Host/bin/Release/net10.0"
SO=$(find "$ROOT/compositor/build" -name 'libwtf_shim.so' | head -1)
if [ -z "$SO" ]; then echo "!! libwtf_shim.so not found — check the C build"; exit 1; fi
cp -v "$SO" "$HOSTDIR/"

echo
echo ">> Build complete. Run nested with:  bash scripts/run.sh"
