#!/usr/bin/env bash
# Publish the LEAN NativeAOT host (#15): WTF.Core + WTF.Host(lean) compiled to a
# single native binary — fast startup, low memory, no 76 MB self-contained .NET
# payload. The reflection/JIT-only subsystems (FCS config hot-reload, reflective
# plugins, Tmds.DBus desktop shell, Extensions.AI/Anthropic agent) are dropped from
# the graph by -p:WtfAot=true; see docs/AOT.md for the feature tradeoff.
#
#   bash scripts/aot-publish.sh
#
# NativeAOT links the native image with CLANG on Linux (gcc alone is NOT enough).
# This machine may not have it; the script detects clang up front and, if absent,
# prints the install hint and stops cleanly WITHOUT producing or faking a binary.
# Everything else (the lean-graph build + analyzers) works without clang:
#   dotnet build src/WTF.Host/WTF.Host.fsproj -c Release -p:WtfAot=true
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

RID=linux-x64
OUT="$ROOT/build/aot"

# --- clang gate (the only hard external prerequisite) -----------------------
if ! command -v clang >/dev/null 2>&1; then
  echo "!! clang not found — NativeAOT needs it to LINK the native binary on Linux."
  echo "   (gcc is present but the ILC toolchain shells out to clang for the link.)"
  echo "   Install:  sudo apt install clang zlib1g-dev      (Debian/Ubuntu)"
  echo "   Then re-run:  bash scripts/aot-publish.sh"
  echo ""
  echo "   Without clang you can still verify the AOT-shaped code (no link needed):"
  echo "     dotnet build src/WTF.Host/WTF.Host.fsproj -c Release -p:WtfAot=true"
  exit 1
fi

echo ">> publishing NativeAOT lean host ($RID) — this invokes ILC + clang"
rm -rf "$OUT"
dotnet publish src/WTF.Host/WTF.Host.fsproj -c Release -r "$RID" \
  -p:WtfAot=true -p:PublishAot=true -o "$OUT"

BIN="$OUT/WTF.Host"
if [ ! -f "$BIN" ]; then
  echo "!! native binary not produced at $BIN"
  exit 1
fi
echo ">> native binary: $BIN  ($(du -h "$BIN" | cut -f1))"

# Place the C shim next to it so it runs (mirror build.sh step 3/3).
SO=$(find "$ROOT/compositor/build" -name 'libwtf_shim.so' 2>/dev/null | head -1)
if [ -n "$SO" ]; then
  cp -v "$SO" "$OUT/"
else
  echo "   (build the C shim first so the binary can run:  bash scripts/build.sh)"
fi
