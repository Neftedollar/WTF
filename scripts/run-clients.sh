#!/usr/bin/env bash
# Launch WTF's own client apps (the status bar and/or the omnibox) against a
# running WTF compositor — for dev verification without installing.
#
#   bash scripts/run-clients.sh bar        # just the status bar
#   bash scripts/run-clients.sh omnibox    # just the omnibox launcher
#   bash scripts/run-clients.sh            # bar, then omnibox
#
# Point this at the SAME Wayland session WTF is running (e.g. run it from a foot
# terminal spawned INSIDE the nested WTF window, where WAYLAND_DISPLAY is WTF's).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"

run_app() {
  local app="$1" exe="$2"
  local dir="$ROOT/src/$app/bin/Release/net10.0"
  if [ ! -x "$dir/$exe" ]; then
    echo "!! $app not built — run: bash scripts/build.sh"; return 1
  fi
  # libwtf_panel.so is copied next to the binary by scripts/build.sh; make sure
  # the loader (and DllImport("wtf_panel")) can find it either way.
  export LD_LIBRARY_PATH="$dir:$ROOT/compositor/build:${LD_LIBRARY_PATH:-}"
  echo ">> launching $exe (WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-?})"
  "$dir/$exe"
}

case "${1:-all}" in
  bar)     run_app WTF.Bar     wtf-bar ;;
  omnibox) run_app WTF.Omnibox wtf-omnibox ;;
  all)
    run_app WTF.Bar wtf-bar &
    run_app WTF.Omnibox wtf-omnibox
    ;;
  *) echo "usage: run-clients.sh [bar|omnibox]"; exit 2 ;;
esac
