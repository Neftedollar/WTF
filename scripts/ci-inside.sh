#!/usr/bin/env bash
# Everything the CI matrix runs INSIDE a distro container:
# deps -> mesa software GL -> full build + atomic install -> headless smoke.
# Kept as a script (not workflow YAML) so a failure is reproducible locally:
#   docker run --rm --device /dev/dri -v "$PWD:/w" -w /w <image> bash scripts/ci-inside.sh
set -euo pipefail
cd "$(dirname "$0")/.."

git config --global --add safe.directory "$PWD" 2>/dev/null || true

echo "=== deps ==="
bash scripts/install-deps.sh

echo "=== mesa software GL (kms_swrast on the vgem node) ==="
if command -v apt-get >/dev/null 2>&1; then
  apt-get install -y libgl1-mesa-dri libegl1 libgbm1
elif command -v dnf >/dev/null 2>&1; then
  dnf install -y mesa-dri-drivers mesa-libEGL mesa-libgbm
elif command -v pacman >/dev/null 2>&1; then
  : # mesa already pulled by install-deps
elif command -v zypper >/dev/null 2>&1; then
  zypper --non-interactive install Mesa-dri Mesa-libEGL1 libgbm1 || \
  zypper --non-interactive install Mesa-dri "pkgconfig(egl)" "pkgconfig(gbm)"
fi

echo "=== build + install ==="
WTF_ALLOW_ROOT=1 bash scripts/install.sh

echo "=== headless smoke ==="
WTF_SMOKE_DIR="$PWD/smoke-run" bash scripts/smoke-headless.sh
