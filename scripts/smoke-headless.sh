#!/usr/bin/env bash
# Headless smoke test: boot the INSTALLED compositor with no GPU/DRM/seat
# (wlroots headless backend + Mesa software GL), verify the brain + IPC answer,
# drive a few commands, and check a clean SIGTERM teardown.
#
# This is what CI runs in a container on every distro; it exercises the whole
# chain except real DRM hardware and the display manager:
#   launcher -> vendored wlroots/scenefx -> fx_renderer (llvmpipe) -> F# host
#   -> config load -> agent socket -> command dispatch -> graceful shutdown.
#
#   bash scripts/smoke-headless.sh [/path/to/wtf]
set -euo pipefail

HOST="${1:-/usr/local/bin/wtf}"
# ALWAYS a fresh private runtime dir — never inherit the session's. Inheriting
# would (a) race a live WTF's wtf.sock on dev machines and (b) let a STALE
# socket file from a previous run pass the socket-exists check while nothing
# is listening ("Connection refused").
# WTF_SMOKE_DIR override: CI runs this inside a --rm container and mounts the
# workspace — pointing the dir there keeps the log after the container dies.
export XDG_RUNTIME_DIR="${WTF_SMOKE_DIR:-/tmp/wtf-smoke-$$}"
mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
# wlroots reads WLR_BACKENDS (PLURAL). Also drop any inherited display so
# autocreate can't silently pick a nested x11/wayland backend on a dev machine
# — this test must exercise the same headless path everywhere.
export WLR_BACKENDS=headless
unset DISPLAY WAYLAND_DISPLAY
export WLR_LIBINPUT_NO_DEVICES=1
LOG="$XDG_RUNTIME_DIR/smoke.log"
SOCK="$XDG_RUNTIME_DIR/wtf.sock"

fail() { echo "SMOKE FAIL: $*" >&2; echo "--- log tail ---" >&2; tail -40 "$LOG" >&2 || true; exit 1; }

echo ">> launching $HOST (headless) ..."
"$HOST" >"$LOG" 2>&1 &
PID=$!
trap 'kill -KILL $PID 2>/dev/null || true' EXIT

# Wait for the agent socket (compositor + F# brain fully up).
for _ in $(seq 1 60); do
  [ -S "$SOCK" ] && break
  kill -0 "$PID" 2>/dev/null || fail "compositor exited during startup"
  sleep 0.5
done
[ -S "$SOCK" ] || fail "agent socket never appeared"

echo ">> socket up; querying state"
STATE="$(wtfctl state)" || fail "wtfctl state errored"
echo "$STATE" | grep -q '"workspaces"' || fail "state has no workspaces: $STATE"

echo ">> driving commands (layout/gaps/workspace)"
wtfctl layout grid >/dev/null || fail "layout grid errored"
wtfctl gaps 12 >/dev/null     || fail "gaps errored"
wtfctl workspace 3 >/dev/null || fail "workspace switch errored"
STATE="$(wtfctl state)"
echo "$STATE" | grep -q '"current": *"3"' || fail "workspace didn't switch: $STATE"

echo ">> config hot-reload"
wtfctl reload >/dev/null || fail "reload errored"
sleep 2
kill -0 "$PID" 2>/dev/null || fail "compositor died after reload"

echo ">> graceful shutdown (SIGTERM)"
kill -TERM "$PID"
for _ in $(seq 1 20); do
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then fail "compositor ignored SIGTERM"; fi
wait "$PID" && RC=0 || RC=$?
[ "$RC" -eq 0 ] || fail "non-zero exit on SIGTERM: rc=$RC"

trap - EXIT
echo ">> SMOKE OK (log: $LOG)"
