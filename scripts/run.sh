#!/usr/bin/env bash
# Run the WTF compositor nested inside the current Wayland/X11 session.
# A new window opens — that's WTF. Inside it:
#   Super+Return  open a terminal (foot)      Super+j / Super+k  focus next/prev
#   Super+Space   BSP layout                  Super+t/w/g/f      tall/wide/grid/full
#   Super+1..9    switch workspace            Super+Shift+1..9   move window there
#   Super+Shift+c close window                Super+Shift+q      quit WTF
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"

HOSTDIR="$ROOT/src/WTF.Host/bin/Release/net10.0"
HOST="$HOSTDIR/WTF.Host"
if [ ! -x "$HOST" ]; then echo "!! host not built — run: bash scripts/build.sh"; exit 1; fi

# Let the host's DllImport("wtf_shim") resolve libwtf_shim.so + libscenefx.
export LD_LIBRARY_PATH="$HOSTDIR:$ROOT/compositor/build:$ROOT/compositor/.scenefx/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"

# A terminal to test with (Super+Return spawns 'kitty'); warn if absent.
command -v kitty >/dev/null || echo "(tip: install a terminal, e.g. 'sudo apt install kitty', for Super+Return)"

echo ">> launching WTF nested (close its window or Super+Shift+q to exit)"
exec "$HOST"
