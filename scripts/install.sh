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
# Self-contained but NOT single-file: the runtime is bundled (no .NET needed on the
# target), yet the assemblies sit as FILES on disk. This is deliberate — the FCS config
# loader does `#r typeof<WtfConfig>.Assembly.Location`, and Location is EMPTY under a
# single-file publish, which would break ~/.config/wtf/config.fsx loading + hot-reload.
# Multi-file keeps Location valid so the user's config works. (Also fewer edge cases.)
pub() { dotnet publish "$1" -c Release -r "$RID" --self-contained \
          -p:PublishSingleFile=false \
          -o "$2" >/dev/null; }
pub src/WTF.Host/WTF.Host.fsproj "$LIBWTF"
# The config Type Provider assembly (config.fsx #r's it via the loader); the host
# doesn't reference it, so place it next to WTF.Core.dll in the published host dir.
dotnet build src/WTF.TypeProviders/WTF.TypeProviders.fsproj -c Release >/dev/null
TPDLL=$(find src/WTF.TypeProviders/bin/Release -name 'WTF.TypeProviders.dll' | head -1)
[ -n "$TPDLL" ] && cp "$TPDLL" "$LIBWTF/"
# wtfctl is published self-contained MULTI-file into its own dir (like bar/omnibox);
# a launcher in bin/ execs it from there so its sibling runtime dlls resolve. Copying
# just the apphost binary breaks with "wtfctl.dll does not exist".
pub src/wtfctl/wtfctl.fsproj "$LIBWTF/ctl"
# The two client apps (the status bar + the omnibox launcher), each into its own
# dir under lib/wtf so libwtf_panel.so can sit next to the binary it DllImports.
pub src/WTF.Bar/WTF.Bar.fsproj "$LIBWTF/bar"
pub src/WTF.Omnibox/WTF.Omnibox.fsproj "$LIBWTF/omnibox"

# Reference dlls for the STRONGLY-TYPED config (#15). The seeded config.fsx #r's
# WTF.Core.dll + WTF.TypeProviders.dll so the F# language server (FsAutoComplete)
# and `dotnet fsi` can resolve the config DSL + the Apps/Layouts/Xkb Type
# Providers. These are framework-dependent (NOT the single-file host bundle), on
# disk next to each other so the config's #r — and the WM loader's sibling lookup
# of WTF.TypeProviders.dll beside WTF.Core.dll — both resolve.
echo "   staging config reference dlls (WTF.Core + WTF.TypeProviders) for the editor"
REFTMP="$STAGE/.ref"
# A plain (framework-dependent) publish brings WTF.Core.dll + FSharp.Core.dll on
# disk; the TP build adds WTF.TypeProviders.dll. These sit next to each other so
# the config's #r and the loader's sibling lookup both resolve.
dotnet publish src/WTF.Config/WTF.Config.fsproj -c Release -o "$REFTMP" >/dev/null
dotnet build   src/WTF.TypeProviders/WTF.TypeProviders.fsproj -c Release -o "$REFTMP/tp" >/dev/null
install -Dm644 "$REFTMP/WTF.Core.dll"        "$LIBWTF/WTF.Core.dll"
install -Dm644 "$REFTMP/tp/WTF.TypeProviders.dll" "$LIBWTF/WTF.TypeProviders.dll"
if [ -f "$REFTMP/FSharp.Core.dll" ]; then
  install -Dm644 "$REFTMP/FSharp.Core.dll" "$LIBWTF/FSharp.Core.dll"
fi
rm -rf "$REFTMP"

echo ">> 3/5  assembling the install tree under $STAGE"
install -Dm644 compositor/build/libwtf_shim.so "$LIBWTF/libwtf_shim.so"
# scenefx runtime lib next to the shim so the launcher's LD_LIBRARY_PATH finds it
install -Dm644 compositor/.scenefx/lib/x86_64-linux-gnu/libscenefx-0.2.so "$LIBWTF/libscenefx-0.2.so"
# libwtf_panel.so next to BOTH client binaries so their DllImport("wtf_panel") resolves.
install -Dm644 compositor/build/libwtf_panel.so "$LIBWTF/bar/libwtf_panel.so"
install -Dm644 compositor/build/libwtf_panel.so "$LIBWTF/omnibox/libwtf_panel.so"
cat > "$BINWTF/wtfctl" <<EOF
#!/bin/sh
exec "$PREFIX/lib/wtf/ctl/wtfctl" "\$@"
EOF
chmod 755 "$BINWTF/wtfctl"
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
# wtf-edit: opens ~/.config/wtf/config.fsx with the F# LSP (FsAutoComplete) set up
# so the config Type Provider gives autocomplete (`Apps.`, `Layouts.`). See
# docs/CONFIG-EDITING.md.
install -Dm755 scripts/wtf-edit "$BINWTF/wtf-edit"
# A pristine copy of the seed config, templated with the INSTALLED #r paths, so
# wtf-edit can re-seed a deleted config and the install can seed a fresh one.
TEMPLATE="$STAGE/usr/share/wtf/config.fsx"
mkdir -p "$(dirname "$TEMPLATE")"
sed -E \
  -e "s|^#r \".*WTF\\.Core\\.dll\"|#r \"$PREFIX/lib/wtf/WTF.Core.dll\"|" \
  -e "s|^#r \".*WTF\\.TypeProviders\\.dll\"|#r \"$PREFIX/lib/wtf/WTF.TypeProviders.dll\"|" \
  examples/config.fsx > "$TEMPLATE"
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
# Seed from the installed template (templated with the /usr/local/lib/wtf #r paths
# so the editor's F# LSP resolves WTF.Core + WTF.TypeProviders). Falls back to the
# repo seed if the template copy isn't on disk yet.
if [ ! -f "$HOME/.config/wtf/config.fsx" ]; then
  if [ -f "$PREFIX/share/wtf/config.fsx" ]; then
    cp "$PREFIX/share/wtf/config.fsx" "$HOME/.config/wtf/config.fsx"
  else
    cp examples/config.fsx "$HOME/.config/wtf/config.fsx"
  fi
fi

echo
echo ">> Edit your config with autocomplete:  wtf-edit   (see docs/CONFIG-EDITING.md)"

echo
echo ">> Installed. Log out and pick \"WTF\" in your display manager,"
echo "   or run 'wtf' from a TTY. Control it live with 'wtfctl state'."
