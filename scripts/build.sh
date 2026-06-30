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

echo ">> 2/3  building F# host + client apps (WTF.Bar / WTF.Omnibox if present)"
cd "$ROOT"
dotnet build src/WTF.Host/WTF.Host.fsproj -c Release
# The config Type Provider (not referenced by the host, but the config loader #r's it).
dotnet build src/WTF.TypeProviders/WTF.TypeProviders.fsproj -c Release
# The shared client lib (status bar + omnibox brain). Building it restores the
# pinned SixLabors ImageSharp/Drawing/Fonts the apps render with.
dotnet build src/WTF.Client/WTF.Client.fsproj -c Release
# The app executables are added by a later step; build them only once they exist.
for app in WTF.Bar WTF.Omnibox; do
  if [ -f "src/$app/$app.fsproj" ]; then
    dotnet build "src/$app/$app.fsproj" -c Release
  fi
done

echo ">> 3/3  placing libwtf_shim.so + libwtf_panel.so next to the binaries"
HOSTDIR="$ROOT/src/WTF.Host/bin/Release/net10.0"
SHIM=$(find "$ROOT/compositor/build" -name 'libwtf_shim.so' | head -1)
if [ -z "$SHIM" ]; then echo "!! libwtf_shim.so not found — check the C build"; exit 1; fi
cp -v "$SHIM" "$HOSTDIR/"

# The config Type Provider assembly: the FCS config loader injects a sibling
# `#r WTF.TypeProviders.dll` next to WTF.Core.dll so a config.fsx using Apps/Layouts/
# Xkb resolves. The host doesn't reference it, so the dev build must place it here
# (the install ships it the same way under /usr/local/lib/wtf).
TP=$(find "$ROOT/src/WTF.TypeProviders/bin/Release" -name 'WTF.TypeProviders.dll' | head -1)
[ -n "$TP" ] && cp -v "$TP" "$HOSTDIR/" || echo "(note: WTF.TypeProviders.dll not built — TP configs will fall back to default)"

# libwtf_panel.so is the CLIENT-side helper (built by the same ninja invocation).
PANEL=$(find "$ROOT/compositor/build" -name 'libwtf_panel.so' | head -1)
if [ -z "$PANEL" ]; then echo "!! libwtf_panel.so not found — check the C build"; exit 1; fi
# Place it next to each app binary that exists so its DllImport("wtf_panel") resolves.
for app in WTF.Bar WTF.Omnibox; do
  APPDIR="$ROOT/src/$app/bin/Release/net10.0"
  [ -d "$APPDIR" ] && cp -v "$PANEL" "$APPDIR/"
done

echo
echo ">> Build complete. Run nested with:  bash scripts/run.sh"
