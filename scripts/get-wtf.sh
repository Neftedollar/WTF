#!/usr/bin/env bash
# get-wtf.sh — one-line installer for WTF prebuilt releases:
#
#   curl -fsSL https://raw.githubusercontent.com/Neftedollar/WTF/master/scripts/get-wtf.sh | bash
#
# Detects the architecture (x86_64/aarch64) and package manager, resolves the
# LATEST GitHub release, then:
#   apt distros            -> downloads the .deb and installs it via apt
#   everything else        -> downloads the tarball, installs runtime deps,
#                             and runs the atomic installer into /usr/local
# sudo is invoked only for the system-level steps; run this as a regular user.
set -euo pipefail

REPO="Neftedollar/WTF"
API="https://api.github.com/repos/$REPO/releases/latest"

case "$(uname -m)" in
  x86_64)  ARCH=x64;   DEB_ARCH=amd64 ;;
  aarch64) ARCH=arm64; DEB_ARCH=arm64 ;;
  *) echo "get-wtf: unsupported architecture $(uname -m) (x86_64/aarch64 only)" >&2; exit 1 ;;
esac

command -v curl >/dev/null 2>&1 || { echo "get-wtf: curl is required" >&2; exit 1; }

SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"

echo ">> resolving the latest WTF release"
JSON="$(curl -fsSL "$API")"
TAG="$(printf '%s' "$JSON" | grep -m1 '"tag_name"' | cut -d'"' -f4)"
[ -n "$TAG" ] || { echo "get-wtf: could not resolve the latest release" >&2; exit 1; }
echo ">> latest release: $TAG"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if command -v apt-get >/dev/null 2>&1; then
  URL="$(printf '%s' "$JSON" | grep -o "https://[^\"]*_${DEB_ARCH}\.deb" | head -1)"
  [ -n "$URL" ] || { echo "get-wtf: no .deb asset for $DEB_ARCH in $TAG" >&2; exit 1; }
  echo ">> downloading $(basename "$URL")"
  curl -fL --progress-bar -o "$TMP/wtf-wm.deb" "$URL"
  echo ">> installing ($SUDO apt install)"
  $SUDO apt install -y "$TMP/wtf-wm.deb"
  echo
  echo ">> Installed. Seed your config once before the first login:  wtf-edit"
else
  URL="$(printf '%s' "$JSON" | grep -o "https://[^\"]*linux-${ARCH}\.tar\.gz" | head -1)"
  [ -n "$URL" ] || { echo "get-wtf: no tarball asset for $ARCH in $TAG" >&2; exit 1; }
  echo ">> downloading $(basename "$URL")"
  curl -fL --progress-bar -o "$TMP/wtf.tar.gz" "$URL"
  tar -C "$TMP" -xzf "$TMP/wtf.tar.gz"
  DIR="$(find "$TMP" -maxdepth 1 -type d -name 'wtf-*' -print -quit)"
  [ -n "$DIR" ] || { echo "get-wtf: unexpected tarball layout" >&2; exit 1; }
  echo ">> installing system runtime libraries ($SUDO)"
  $SUDO bash "$DIR/scripts/install-deps.sh"
  echo ">> atomic install into /usr/local (ldd preflight first)"
  bash "$DIR/scripts/install-stage.sh" "$DIR/stage"
fi

echo
echo ">> Done. Log out and pick \"WTF\" in your display manager,"
echo "   or try it risk-free first: run 'wtf' in a terminal (nested window)."
