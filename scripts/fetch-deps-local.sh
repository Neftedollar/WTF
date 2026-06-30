#!/usr/bin/env bash
# Fetch wlroots/wayland build deps WITHOUT root: download the .deb files and
# extract them into a local sysroot, then rewrite pkg-config prefixes so the
# headers, libs, wayland-scanner and protocol XML all resolve from the sysroot.
#
#   bash scripts/fetch-deps-local.sh        # populates .deps/sysroot
#   source .deps/env.sh                     # export the build environment
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
DEPS="$ROOT/.deps"
DEB="$DEPS/debs"
SYS="$DEPS/sysroot"
ARCH=x86_64-linux-gnu
mkdir -p "$DEB" "$SYS"

# dev + runtime packages we need (closure resolved below).
SEED=(
  libwlroots-0.18-dev libwlroots-0.18
  libwayland-dev libwayland-server0 libwayland-client0 libwayland-egl1 libwayland-cursor0
  libwayland-bin
  wayland-protocols
  libxkbcommon-dev libxkbcommon0
  libpixman-1-dev libpixman-1-0
  libdrm-dev libdrm2
  libgbm-dev libgbm1
  libinput-dev libinput10
  libseat-dev libseat1
  libudev-dev libudev1
  libgles-dev libegl-dev libglvnd-dev
)

echo ">> resolving dependency closure"
LIST=$(apt-cache depends --recurse --no-recommends --no-suggests \
        --no-conflicts --no-breaks --no-replaces --no-enhances "${SEED[@]}" 2>/dev/null \
        | grep -E '^\w' | sort -u)

echo ">> downloading $(echo "$LIST" | wc -l) packages into $DEB"
cd "$DEB"
# download what's available; ignore the handful of virtual/already-present ones
for p in $LIST; do apt-get download "$p" 2>/dev/null || true; done
echo "   got $(ls -1 *.deb 2>/dev/null | wc -l) .deb files"

echo ">> extracting into sysroot $SYS"
for d in "$DEB"/*.deb; do dpkg-deb -x "$d" "$SYS"; done

echo ">> rewriting pkg-config prefixes to the sysroot"
find "$SYS" -name '*.pc' -print0 | while IFS= read -r -d '' pc; do
  sed -i "s|^prefix=/usr|prefix=$SYS/usr|; s|=/usr/|=$SYS/usr/|g" "$pc"
done

echo ">> writing $DEPS/env.sh"
cat > "$DEPS/env.sh" <<EOF
# source me before building the compositor
export SYSROOT="$SYS"
export PKG_CONFIG_PATH="$SYS/usr/lib/$ARCH/pkgconfig:$SYS/usr/share/pkgconfig"
export PATH="$SYS/usr/bin:\$PATH"
export LD_LIBRARY_PATH="$SYS/usr/lib/$ARCH:\${LD_LIBRARY_PATH:-}"
export CPATH="$SYS/usr/include:$SYS/usr/include/$ARCH"
export LIBRARY_PATH="$SYS/usr/lib/$ARCH"
EOF

echo ">> verifying pkg-config can see the deps"
# shellcheck disable=SC1090
source "$DEPS/env.sh"
ok=1
for pc in wlroots-0.18 wayland-server wayland-scanner wayland-protocols xkbcommon pixman-1 libdrm gbm libinput; do
  if pkg-config --exists "$pc" 2>/dev/null; then
    printf "   OK   %-18s %s\n" "$pc" "$(pkg-config --modversion "$pc" 2>/dev/null)"
  else
    printf "   MISS %s\n" "$pc"; ok=0
  fi
done
[ "$ok" = 1 ] && echo ">> sysroot ready. Now: source .deps/env.sh && bash scripts/build.sh" \
             || echo ">> some deps missing — inspect above"
