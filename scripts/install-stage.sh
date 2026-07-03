#!/usr/bin/env bash
# Install a PRE-ASSEMBLED WTF stage tree into / — atomically, per file.
# Shared by scripts/install.sh (after it builds the stage) and by the prebuilt
# release tarballs (whose stage was built in CI):
#
#   bash scripts/install-stage.sh [stage-dir]     # default: build/stage
#
# Steps: ldd preflight of the bundled native libs (fail loudly NOW, not as a
# crash-loop at first login) -> sudo atomic copy (rename(2) per file — safe
# over a LIVE session) -> seed ~/.config/wtf/config.fsx.
set -euo pipefail
cd "$(dirname "$0")/.."

STAGE="${1:-$PWD/build/stage}"
STAGE="$(cd "$STAGE" && pwd)"
PREFIX=/usr/local
LIBWTF="$STAGE$PREFIX/lib/wtf"

if [ ! -d "$LIBWTF" ]; then
  echo "install-stage.sh: $STAGE does not look like a WTF stage (no .$PREFIX/lib/wtf)" >&2
  exit 1
fi

# Preflight: every staged native lib must resolve on THIS machine's system
# libs. Catch a missing dependency NOW with package hints — not at first login
# as a crash-loop with no display.
if command -v ldd >/dev/null 2>&1; then
  MISSING="$(LD_LIBRARY_PATH="$LIBWTF" ldd "$LIBWTF"/libwtf_shim.so "$LIBWTF"/libscenefx-0.4.so "$LIBWTF"/libwlroots-0.19.so 2>/dev/null \
               | grep 'not found' | sort -u || true)"
  if [ -n "$MISSING" ]; then
    echo "install-stage.sh: bundled libraries have UNRESOLVED dependencies on this machine:" >&2
    echo "$MISSING" >&2
    echo "  Debian/Ubuntu, Fedora, Arch, openSUSE: sudo bash scripts/install-deps.sh" >&2
    exit 1
  fi
fi

echo ">> copying into / (needs root; atomic per-file — safe over a LIVE session)"
# NEVER `cp -a stage/. /` here: cp overwrites destination files IN PLACE,
# corrupting the mmap'd code pages of a RUNNING session (SIGSEGV mid-copy,
# then SIGBUS restarts off half-written binaries — observed live 2026-07-01).
# Write each file next to its destination and rename(2) it into place: a
# running process keeps the old inode; nothing ever sees a partial file.
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  SUDO="sudo"
  # Honor an askpass helper when configured (e.g. driven by an agent with no tty).
  [ -n "${SUDO_ASKPASS:-}" ] && SUDO="sudo -A"
fi
$SUDO bash -euo pipefail -s "$STAGE" <<'ATOMIC_INSTALL'
STAGE="$1"
cd "$STAGE"
find . -mindepth 1 -type d -print0 | while IFS= read -r -d '' d; do
  mkdir -p "/${d#./}"
done
# Abort mid-run (disk full, read-only /usr on ostree) must not litter /usr with
# half-written temp files: clean the in-flight temp explicitly on either
# failure (a trap in the parent can't see `tmp` — the loop is a pipe subshell).
find . \( -type f -o -type l \) -print0 | while IFS= read -r -d '' f; do
  dst="/${f#./}"
  tmp="$dst.wtf-new.$$"
  cp -a "$f" "$tmp" || { rm -f "$tmp"; exit 1; }  # -a keeps mode/symlink
  mv -f "$tmp" "$dst" || { rm -f "$tmp"; exit 1; } # rename(2): atomic swap
done
# Sweep stale temps from PREVIOUS aborted installs (bounded to our trees).
find "/usr/local/lib/wtf" "/usr/local/bin" -maxdepth 2 -name '*.wtf-new.*' -delete 2>/dev/null || true
ATOMIC_INSTALL
if pgrep -f 'lib/wtf/WTF\.Host' >/dev/null 2>&1; then
  echo "   NOTE: a live WTF session is running. It safely keeps the OLD binaries"
  echo "   (old inodes) until restart: M-S-r reloads only config.fsx; log out and"
  echo "   back in (Super+Shift+q) to pick up the new build."
fi

echo ">> seeding a default user config (~/.config/wtf/config.fsx)"
mkdir -p "$HOME/.config/wtf"
if [ ! -f "$HOME/.config/wtf/config.fsx" ]; then
  if [ -f "$PREFIX/share/wtf/config.fsx" ]; then
    cp "$PREFIX/share/wtf/config.fsx" "$HOME/.config/wtf/config.fsx"
  elif [ -f examples/config.fsx ]; then
    cp examples/config.fsx "$HOME/.config/wtf/config.fsx"
  fi
fi

echo
echo ">> Installed. Log out and pick \"WTF\" in your display manager,"
echo "   or run 'wtf' from a TTY. Control it live with 'wtfctl state'."
