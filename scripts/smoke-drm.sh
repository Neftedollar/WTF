#!/usr/bin/env bash
# smoke-drm.sh — verify WTF actually starts on real DRM/KMS WITHOUT locking the
# machine.
#
# RUN FROM A FREE VT, NOT inside a graphical session:
#   1. Switch to a free virtual terminal: Ctrl-Alt-F3
#   2. Log in on that TTY.
#   3. Run:  bash scripts/smoke-drm.sh [TIMEOUT_SECONDS]   (default 15)
#
# Core safety net: the host runs under coreutils `timeout`, so it ALWAYS exits on
# its own even if input is dead, and the console is restored on EVERY exit path
# (host returns, times out, is SIGKILLed by --kill-after, or the script is
# interrupted). It is therefore impossible for this script to wedge the TTY.
#
# Strict: set -u. Every external command is guarded with `command -v`.

set -u

TIMEOUT="${1:-15}"

if ! command -v timeout >/dev/null 2>&1; then
	printf 'FAIL: coreutils timeout is required for a self-limiting smoke test\n' >&2
	exit 1
fi

# Resolve the host: WTF_HOST, then the installed launcher, then a dev build.
HOST=""
if [ -n "${WTF_HOST:-}" ] && [ -x "${WTF_HOST:-}" ]; then
	HOST="$WTF_HOST"
else
	for cand in \
		/usr/local/bin/wtf \
		"$PWD/src/WTF.Host/bin/Release/net10.0/WTF.Host"; do
		if [ -x "$cand" ]; then
			HOST="$cand"
			break
		fi
	done
fi
if [ -z "$HOST" ]; then
	printf 'FAIL: no WTF host found (set WTF_HOST, or install, or build Release)\n' >&2
	exit 1
fi

STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/wtf"
mkdir -p "$STATE_DIR" 2>/dev/null || true
if command -v date >/dev/null 2>&1; then
	STAMP="$(date +%Y%m%d-%H%M%S 2>/dev/null || echo unknown)"
else
	STAMP="unknown"
fi
LOG="$STATE_DIR/smoke-$STAMP.log"
: >"$LOG" 2>/dev/null || true

# restore_console — identical guarded helper as the wrapper. Trapped on every
# terminating path so the TTY can never be left in raw mode.
restore_console() {
	if command -v kbd_mode >/dev/null 2>&1; then
		kbd_mode -s 2>/dev/null || true
	fi
	if command -v stty >/dev/null 2>&1; then
		stty sane 2>/dev/null || true
	fi
	if command -v clear >/dev/null 2>&1; then
		clear 2>/dev/null || true
	fi
}
trap restore_console EXIT INT TERM

printf 'smoke-drm: host=%s timeout=%ss log=%s\n' "$HOST" "$TIMEOUT" "$LOG"

# Background watcher: wait for the host to advertise its socket, then launch a
# test terminal bound to it so there is something to eyeball.
watch_and_spawn() {
	i=0
	sock=""
	while [ "$i" -lt "$TIMEOUT" ]; do
		if command -v grep >/dev/null 2>&1 && grep -q 'WAYLAND_DISPLAY=' "$LOG" 2>/dev/null; then
			sock="$(grep -o 'WAYLAND_DISPLAY=[^ ]*' "$LOG" 2>/dev/null | head -n1 | cut -d= -f2)"
			break
		fi
		if command -v sleep >/dev/null 2>&1; then
			sleep 1
		fi
		i="$((i + 1))"
	done
	if [ -z "$sock" ]; then
		return 0
	fi
	for term in foot kitty alacritty xterm; do
		if command -v "$term" >/dev/null 2>&1; then
			WAYLAND_DISPLAY="$sock" "$term" >>"$LOG" 2>&1 &
			return 0
		fi
	done
}

watch_and_spawn &
watcher=$!

# Self-timeout: the host ALWAYS dies within TIMEOUT (+5s SIGKILL grace).
timeout --signal=TERM --kill-after=5 "$TIMEOUT" "$HOST" >>"$LOG" 2>&1
rc=$?
log_rc="$rc"

kill "$watcher" 2>/dev/null || true
wait "$watcher" 2>/dev/null || true

restore_console

# PASS/FAIL is decided by the log marker, NOT rc: timeout returns 124 on the
# expected self-timeout, which is normal and not a failure here.
if command -v grep >/dev/null 2>&1 && grep -q 'WTF compositor running on WAYLAND_DISPLAY=' "$LOG"; then
	printf 'PASS: compositor came up on DRM/KMS (host rc=%s, see %s)\n' "$log_rc" "$LOG"
	exit 0
fi
printf 'FAIL: compositor did not come up (host rc=%s, see %s)\n' "$log_rc" "$LOG" >&2
exit 1
