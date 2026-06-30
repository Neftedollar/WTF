#!/usr/bin/env bash
# Install WTF system-wide so it appears as a session in your display manager.
#
#   bash scripts/install.sh        # builds + stages (no root), then sudo-copies
#
# Builds a SELF-CONTAINED .NET publish, so target machines do NOT need the .NET
# SDK/runtime — only the wlroots/wayland runtime libraries (already present on
# any wlroots-based desktop; otherwise: scripts/install-deps.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

PREFIX=/usr/local
RID=linux-x64
STAGE="$ROOT/build/stage"
LIBWTF="$STAGE$PREFIX/lib/wtf"
BINWTF="$STAGE$PREFIX/bin"
SESS="$STAGE/usr/share/wayland-sessions"

echo ">> 1/5  building scenefx + the C shim"
bash scripts/build-scenefx.sh
export PKG_CONFIG_PATH="$ROOT/compositor/.scenefx/lib/x86_64-linux-gnu/pkgconfig:${PKG_CONFIG_PATH:-}"
( cd compositor && { [ -d build ] || meson setup build; } && ninja -C build >/dev/null )

echo ">> 2/5  publishing self-contained host + wtfctl ($RID)"
rm -rf "$STAGE"
mkdir -p "$LIBWTF" "$BINWTF" "$SESS"
pub() { dotnet publish "$1" -c Release -r "$RID" --self-contained \
          -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
          -o "$2" >/dev/null; }
pub src/WTF.Host/WTF.Host.fsproj "$LIBWTF"
TMPCTL="$STAGE/.ctl"; pub src/wtfctl/wtfctl.fsproj "$TMPCTL"

echo ">> 3/5  assembling the install tree under $STAGE"
install -Dm644 compositor/build/libwtf_shim.so "$LIBWTF/libwtf_shim.so"
# scenefx runtime lib next to the shim so the launcher's LD_LIBRARY_PATH finds it
install -Dm644 compositor/.scenefx/lib/x86_64-linux-gnu/libscenefx-0.2.so "$LIBWTF/libscenefx-0.2.so"
install -Dm755 "$TMPCTL/wtfctl" "$BINWTF/wtfctl"
rm -rf "$TMPCTL"
# launcher that points the runtime loader at the bundled shim
cat > "$BINWTF/wtf" <<EOF
#!/bin/sh
export LD_LIBRARY_PATH="$PREFIX/lib/wtf:\${LD_LIBRARY_PATH:-}"
exec "$PREFIX/lib/wtf/WTF.Host" "\$@"
EOF
chmod 755 "$BINWTF/wtf"
# session wrapper (what the .desktop launches): captures a log, restores the
# console on every exit, bounded restart loop, safe-mode escalation, fallback.
# Its default WTF_HOST is /usr/local/bin/wtf == the launcher written just above.
install -Dm755 scripts/wtf-session "$BINWTF/wtf-session"
# TTY smoke test the user can run from a free VT to verify DRM/KMS startup.
install -Dm755 scripts/smoke-drm.sh "$BINWTF/wtf-smoke-drm"
install -Dm644 packaging/wtf.desktop "$SESS/wtf.desktop"

echo ">> 4/5  copying into / (needs root)"
if [ "$(id -u)" -eq 0 ]; then
  cp -a "$STAGE"/. /
else
  sudo cp -a "$STAGE"/. /
fi

echo ">> 5/5  seeding a default user config (~/.config/wtf/config.fsx)"
mkdir -p "$HOME/.config/wtf"
[ -f "$HOME/.config/wtf/config.fsx" ] || cp examples/config.fsx "$HOME/.config/wtf/config.fsx"

echo
echo ">> Installed. Log out and pick \"WTF\" in your display manager,"
echo "   or run 'wtf' from a TTY. Control it live with 'wtfctl state'."
