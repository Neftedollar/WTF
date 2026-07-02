#!/usr/bin/env bash
# Build a binary .deb from a WTF stage built with WTF_PREFIX=/usr:
#
#   WTF_ALLOW_ROOT=1 WTF_STAGE_ONLY=1 WTF_PREFIX=/usr bash scripts/install.sh
#   bash scripts/build-deb.sh <version> [stage-dir]
#
# Produces build/wtf-wm_<version>_<arch>.deb. Package name is wtf-wm ("wtf"
# is already a classic Debian package). Depends are the STABLE system ABIs
# only — wlroots + scenefx are bundled inside /usr/lib/wtf.
set -euo pipefail
cd "$(dirname "$0")/.."

VER="${1:?usage: build-deb.sh <version> [stage-dir]}"
STAGE="${2:-build/stage}"
if [ ! -d "$STAGE/usr/lib/wtf" ]; then
  echo "build-deb.sh: $STAGE has no usr/lib/wtf — build it with WTF_PREFIX=/usr first" >&2
  exit 1
fi

case "$(uname -m)" in
  x86_64)  ARCH=amd64 ;;
  aarch64) ARCH=arm64 ;;
  *) echo "build-deb.sh: unsupported arch $(uname -m)" >&2; exit 1 ;;
esac

PKGDIR="build/deb/wtf-wm_${VER}_${ARCH}"
rm -rf "$PKGDIR"
mkdir -p "$PKGDIR/DEBIAN"
cp -a "$STAGE"/. "$PKGDIR"/

cat > "$PKGDIR/DEBIAN/control" <<EOF
Package: wtf-wm
Version: $VER
Architecture: $ARCH
Maintainer: WTF <https://github.com/Neftedollar/WTF/issues>
Section: x11
Priority: optional
Homepage: https://github.com/Neftedollar/WTF
Depends: libinput10, libseat1, libxkbcommon0, libdrm2, libgbm1, libegl1, libgles2, libpixman-1-0, libudev1, libwayland-server0, libxcb1
Recommends: xwayland, xdg-desktop-portal-wlr, xdg-desktop-portal-gtk, grim, libheif1
Description: Wayland tiling window manager configured in F#
 WTF (Wayland Tiling, F#) is a tiling Wayland compositor in the xMonad
 tradition: the configuration is real F# code with autocomplete and
 hot-reload. scenefx effects (blur, rounded corners, shadows), dynamic
 .heic wallpapers, and an agent-first JSON control socket. The exact
 wlroots + scenefx versions it targets are bundled in /usr/lib/wtf.
EOF

dpkg-deb --build --root-owner-group "$PKGDIR" "build/wtf-wm_${VER}_${ARCH}.deb"
echo ">> built build/wtf-wm_${VER}_${ARCH}.deb"
