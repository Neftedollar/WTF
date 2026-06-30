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

echo ">> 2/5  publishing self-contained host + wtfctl + bar + omnibox ($RID)"
rm -rf "$STAGE"
mkdir -p "$LIBWTF" "$BINWTF" "$SESS"
pub() { dotnet publish "$1" -c Release -r "$RID" --self-contained \
          -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
          -o "$2" >/dev/null; }
pub src/WTF.Host/WTF.Host.fsproj "$LIBWTF"
TMPCTL="$STAGE/.ctl"; pub src/wtfctl/wtfctl.fsproj "$TMPCTL"
# The two client apps (the status bar + the omnibox launcher), each into its own
# dir under lib/wtf so libwtf_panel.so can sit next to the binary it DllImports.
pub src/WTF.Bar/WTF.Bar.fsproj "$LIBWTF/bar"
pub src/WTF.Omnibox/WTF.Omnibox.fsproj "$LIBWTF/omnibox"

echo ">> 3/5  assembling the install tree under $STAGE"
install -Dm644 compositor/build/libwtf_shim.so "$LIBWTF/libwtf_shim.so"
# scenefx runtime lib next to the shim so the launcher's LD_LIBRARY_PATH finds it
install -Dm644 compositor/.scenefx/lib/x86_64-linux-gnu/libscenefx-0.2.so "$LIBWTF/libscenefx-0.2.so"
# libwtf_panel.so next to BOTH client binaries so their DllImport("wtf_panel") resolves.
install -Dm644 compositor/build/libwtf_panel.so "$LIBWTF/bar/libwtf_panel.so"
install -Dm644 compositor/build/libwtf_panel.so "$LIBWTF/omnibox/libwtf_panel.so"
install -Dm755 "$TMPCTL/wtfctl" "$BINWTF/wtfctl"
rm -rf "$TMPCTL"
# launcher that points the runtime loader at the bundled shim
cat > "$BINWTF/wtf" <<EOF
#!/bin/sh
export LD_LIBRARY_PATH="$PREFIX/lib/wtf:\${LD_LIBRARY_PATH:-}"
exec "$PREFIX/lib/wtf/WTF.Host" "\$@"
EOF
chmod 755 "$BINWTF/wtf"
# launcher wrappers for the bar + omnibox: point the loader at their app dir (which
# holds libwtf_panel.so) and exec the self-contained binary. These names are what
# the example config's startup ("wtf-bar") and M-p bind ("wtf-omnibox") spawn.
cat > "$BINWTF/wtf-bar" <<EOF
#!/bin/sh
export LD_LIBRARY_PATH="$PREFIX/lib/wtf/bar:\${LD_LIBRARY_PATH:-}"
exec "$PREFIX/lib/wtf/bar/wtf-bar" "\$@"
EOF
chmod 755 "$BINWTF/wtf-bar"
cat > "$BINWTF/wtf-omnibox" <<EOF
#!/bin/sh
export LD_LIBRARY_PATH="$PREFIX/lib/wtf/omnibox:\${LD_LIBRARY_PATH:-}"
exec "$PREFIX/lib/wtf/omnibox/wtf-omnibox" "\$@"
EOF
chmod 755 "$BINWTF/wtf-omnibox"
# session wrapper (what the .desktop launches): captures a log, restores the
# console on every exit, bounded restart loop, safe-mode escalation, fallback.
# Its default WTF_HOST is /usr/local/bin/wtf == the launcher written just above.
install -Dm755 scripts/wtf-session "$BINWTF/wtf-session"
# TTY smoke test the user can run from a free VT to verify DRM/KMS startup.
install -Dm755 scripts/smoke-drm.sh "$BINWTF/wtf-smoke-drm"
install -Dm644 packaging/wtf.desktop "$SESS/wtf.desktop"
# xdg-desktop-portal routing (screenshots/screencast -> wlr, file-picker -> gtk),
# selected when XDG_CURRENT_DESKTOP=wtf. Needs the portal packages installed.
install -Dm644 packaging/wtf-portals.conf "$STAGE/usr/share/xdg-desktop-portal/wtf-portals.conf"

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
