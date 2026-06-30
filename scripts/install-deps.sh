#!/usr/bin/env bash
# WTF build dependencies for Debian 13 (trixie). Run with sudo:
#   sudo bash ~/Dev/WTF/scripts/install-deps.sh
set -euo pipefail

PKGS=(
  build-essential meson ninja-build pkg-config scdoc git
  wayland-protocols libwayland-dev libxkbcommon-dev
  libwlroots-0.18-dev
  libdrm-dev libgbm-dev libpixman-1-dev
  libinput-dev libseat-dev libudev-dev
  libgles2-mesa-dev libegl1-mesa-dev
  # runtime desktop-shell tooling: portals (screenshots/screencast/file-picker)
  # + screenshot CLIs. The compositor exports the screencopy/dmabuf protocols
  # these rely on (Phase 2 #9).
  xdg-desktop-portal xdg-desktop-portal-wlr xdg-desktop-portal-gtk
  grim slurp
)

echo ">> apt-get update"
apt-get update

echo ">> installing ${#PKGS[@]} packages"
apt-get install -y "${PKGS[@]}"

echo
echo ">> verifying pkg-config can see everything:"
ok=1
for pc in wayland-server wayland-scanner xkbcommon pixman-1 libdrm gbm libinput wlroots-0.18; do
  if pkg-config --exists "$pc"; then
    printf "   OK   %-16s %s\n" "$pc" "$(pkg-config --modversion "$pc")"
  else
    printf "   MISS %s\n" "$pc"; ok=0
  fi
done

echo
if [ "$ok" = 1 ]; then
  echo ">> All build dependencies present. Ready to build the compositor."
else
  echo ">> Some packages missing — check the apt output above for errors."
  exit 1
fi
