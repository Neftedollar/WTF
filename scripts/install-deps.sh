#!/usr/bin/env bash
# WTF build + runtime dependencies, multi-distro. Run with sudo:
#   sudo bash scripts/install-deps.sh
#
# Supported: Debian/Ubuntu (apt), Fedora (dnf), Arch (pacman), openSUSE (zypper).
# wlroots itself is NOT taken from the distro — it is vendored (pinned build via
# scripts/build-wlroots.sh), so this installs wlroots' BUILD deps instead.
# Also installs the .NET 10 SDK into the invoking user's ~/.dotnet when no
# dotnet is available (build machines only; installed WTF is self-contained).
set -euo pipefail

# ---------------- package lists per family ----------------
APT_PKGS=(
  build-essential meson ninja-build pkg-config scdoc git curl
  wayland-protocols libwayland-dev libwayland-bin libxkbcommon-dev
  # wlroots 0.18 build deps (vendored build):
  libdrm-dev libgbm-dev libpixman-1-dev
  libinput-dev libseat-dev libudev-dev
  libgles2-mesa-dev libegl1-mesa-dev
  hwdata libdisplay-info-dev libliftoff-dev
  # x11 backend (nested-in-X11) + xwayland support:
  libxcb1-dev libxcb-composite0-dev libxcb-dri3-dev libxcb-present-dev
  libxcb-render0-dev libxcb-render-util0-dev libxcb-shm0-dev
  libxcb-xfixes0-dev libxcb-icccm4-dev libxcb-res0-dev libxcb-errors-dev
  libxcb-ewmh-dev xwayland
  # runtime desktop-shell tooling: portals + screenshot CLIs
  xdg-desktop-portal xdg-desktop-portal-wlr xdg-desktop-portal-gtk
  grim slurp
  # dynamic .heic wallpapers (optional at runtime; cheap to have)
  libheif1
)

DNF_PKGS=(
  gcc gcc-c++ meson ninja-build pkgconf-pkg-config scdoc git curl
  wayland-devel wayland-protocols-devel libxkbcommon-devel
  libdrm-devel mesa-libgbm-devel pixman-devel
  libinput-devel libseat-devel systemd-devel
  mesa-libEGL-devel mesa-libGLES-devel
  hwdata libdisplay-info-devel libliftoff-devel
  libxcb-devel xcb-util-renderutil-devel xcb-util-wm-devel xcb-util-errors-devel
  xorg-x11-server-Xwayland
  xdg-desktop-portal xdg-desktop-portal-wlr xdg-desktop-portal-gtk
  grim slurp
  libheif
)

PACMAN_PKGS=(
  base-devel meson ninja pkgconf scdoc git curl
  wayland wayland-protocols libxkbcommon
  libdrm pixman libinput seatd mesa
  hwdata libdisplay-info libliftoff
  libxcb xcb-util-renderutil xcb-util-wm xcb-util-errors
  xorg-xwayland
  xdg-desktop-portal xdg-desktop-portal-wlr xdg-desktop-portal-gtk
  grim slurp
  libheif
)

ZYPPER_PKGS=(
  gcc gcc-c++ meson ninja pkg-config scdoc git curl
  wayland-devel wayland-protocols-devel libxkbcommon-devel
  libdrm-devel libgbm-devel libpixman-1-0-devel
  libinput-devel libseat-devel systemd-devel
  Mesa-libEGL-devel Mesa-libGLESv3-devel
  hwdata libdisplay-info-devel libliftoff-devel
  libxcb-devel xcb-util-renderutil-devel xcb-util-wm-devel xcb-util-errors-devel
  xwayland
  xdg-desktop-portal xdg-desktop-portal-wlr xdg-desktop-portal-gtk
  grim slurp
  libheif1
)

# ---------------- install via the detected package manager ----------------
if command -v apt-get >/dev/null 2>&1; then
  echo ">> apt (Debian/Ubuntu): installing ${#APT_PKGS[@]} packages"
  apt-get update
  apt-get install -y "${APT_PKGS[@]}"
elif command -v dnf >/dev/null 2>&1; then
  echo ">> dnf (Fedora): installing ${#DNF_PKGS[@]} packages"
  dnf install -y "${DNF_PKGS[@]}"
elif command -v pacman >/dev/null 2>&1; then
  echo ">> pacman (Arch): installing ${#PACMAN_PKGS[@]} packages"
  pacman -Sy --noconfirm --needed "${PACMAN_PKGS[@]}"
elif command -v zypper >/dev/null 2>&1; then
  echo ">> zypper (openSUSE): installing ${#ZYPPER_PKGS[@]} packages"
  zypper --non-interactive install "${ZYPPER_PKGS[@]}"
else
  echo "install-deps.sh: no supported package manager (apt/dnf/pacman/zypper)" >&2
  exit 1
fi

# ---------------- .NET 10 SDK (build machines only) ----------------
# The installed WM is self-contained; the SDK is needed only to BUILD. Install
# per-user into ~/.dotnet of the INVOKING user (we run under sudo), matching
# what scripts/install.sh looks for.
TARGET_USER="${SUDO_USER:-$(id -un)}"
TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
if command -v dotnet >/dev/null 2>&1 || [ -x "$TARGET_HOME/.dotnet/dotnet" ]; then
  echo ">> dotnet present — skipping SDK install"
else
  echo ">> installing .NET 10 SDK into $TARGET_HOME/.dotnet (user $TARGET_USER)"
  TMP_DI="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP_DI"
  chmod +x "$TMP_DI"
  if [ "$TARGET_USER" = "$(id -un)" ]; then
    bash "$TMP_DI" --channel 10.0 --install-dir "$TARGET_HOME/.dotnet"
  else
    su - "$TARGET_USER" -c "bash '$TMP_DI' --channel 10.0 --install-dir '$TARGET_HOME/.dotnet'"
  fi
  rm -f "$TMP_DI"
fi

# ---------------- verify ----------------
echo
echo ">> verifying pkg-config can see the build deps:"
ok=1
for pc in wayland-server wayland-scanner xkbcommon pixman-1 libdrm gbm libinput; do
  if pkg-config --exists "$pc"; then
    printf "   OK   %-16s %s\n" "$pc" "$(pkg-config --modversion "$pc")"
  else
    printf "   MISS %s\n" "$pc"; ok=0
  fi
done

echo
if [ "$ok" = 1 ]; then
  echo ">> All build dependencies present. Next: bash scripts/install.sh"
else
  echo ">> Some dependencies are missing — check the package names for your distro." >&2
  exit 1
fi
