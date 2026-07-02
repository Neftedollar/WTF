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

# Run UNPRIVILEGED: as root, DOTNET_ROOT points at /root/.dotnet, build/ gets
# root-owned, and step 5 would seed /root/.config instead of the user's. sudo
# is invoked internally for exactly one step (the copy into /).
# WTF_ALLOW_ROOT=1 overrides for containers/CI, where root IS the only user.
if [ "$(id -u)" -eq 0 ] && [ -z "${WTF_ALLOW_ROOT:-}" ]; then
  echo "install.sh: run as a regular user (sudo is used internally for step 4 only)" >&2
  echo "            containers/CI: set WTF_ALLOW_ROOT=1" >&2
  exit 1
fi

# Only steer dotnet at the user-local SDK when one exists; on a machine with a
# distro-packaged dotnet, a bogus DOTNET_ROOT breaks apphost resolution.
if [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

PREFIX=/usr/local
# RID must match the HOST arch: the C shim/scenefx are compiled natively, and
# `dotnet publish -r linux-x64` on aarch64 "succeeds" — then exec-format-fails
# at first login (the worst failure mode: builds clean, breaks at boot).
case "$(uname -m)" in
  x86_64)  RID=linux-x64 ;;
  aarch64) RID=linux-arm64 ;;
  *) echo "install.sh: unsupported arch $(uname -m)" >&2; exit 1 ;;
esac
STAGE="$ROOT/build/stage"
LIBWTF="$STAGE$PREFIX/lib/wtf"
BINWTF="$STAGE$PREFIX/bin"
SESS="$STAGE/usr/share/wayland-sessions"

echo ">> 1/5  building wlroots (vendored) + scenefx + the C shim"
# wlroots is VENDORED (pinned 0.18.x, bundled into /usr/local/lib/wtf) so the
# install never depends on which wlroots the distro packages. scenefx and the
# shim both build against the vendored copy.
bash scripts/build-wlroots.sh
WLROOTS_PC="$(find "$ROOT/compositor/.wlroots" -name 'wlroots-0.18.pc' -print -quit)"
if [ -z "$WLROOTS_PC" ]; then
  echo "install.sh: wlroots build missing (no wlroots-0.18.pc under compositor/.wlroots)" >&2
  exit 1
fi
export PKG_CONFIG_PATH="$(dirname "$WLROOTS_PC"):${PKG_CONFIG_PATH:-}"
bash scripts/build-scenefx.sh
# scenefx's libdir varies by distro (lib/ vs lib/<multiarch>): locate the .pc.
SCENEFX_PC="$(find "$ROOT/compositor/.scenefx" -name 'scenefx-0.2.pc' -print -quit)"
if [ -z "$SCENEFX_PC" ]; then
  echo "install.sh: scenefx build missing (no scenefx-0.2.pc under compositor/.scenefx)" >&2
  exit 1
fi
export PKG_CONFIG_PATH="$(dirname "$SCENEFX_PC"):$PKG_CONFIG_PATH"
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
# scenefx + vendored wlroots runtime libs next to the shim so the launcher's
# LD_LIBRARY_PATH finds them (libdir varies by distro — resolve, don't hardcode).
SCENEFX_SO="$(find "$ROOT/compositor/.scenefx" -name 'libscenefx-0.2.so' -print -quit)"
install -Dm644 "$SCENEFX_SO" "$LIBWTF/libscenefx-0.2.so"
WLROOTS_SO="$(find "$ROOT/compositor/.wlroots" -name 'libwlroots-0.18.so' -print -quit)"
install -Dm644 "$WLROOTS_SO" "$LIBWTF/libwlroots-0.18.so"
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

echo ">> 4/5  copying into / (needs root; atomic per-file — safe over a LIVE session)"
# NEVER `cp -a stage/. /` here. cp overwrites destination files IN PLACE, which
# corrupts the text pages of the running compositor's mmap'd libwtf_shim.so and
# WTF.Host — the live session dies with SIGSEGV the moment the copy touches
# them, and the wtf-session restart loop then execs HALF-WRITTEN binaries and
# dies with SIGBUS (including safe-mode). Seen live 2026-07-01 00:23: running
# install.sh from inside a WTF session killed it 3+1 times in one second.
# Atomic install instead: write each file next to its destination, then
# rename(2) it into place. A running process keeps the OLD inode alive, and no
# reader/exec can ever observe a partially-written file.
# Preflight: every staged native lib must resolve on THIS machine's system
# libs (wlroots-0.18, libinput, ...). Catch a missing/mismatched dependency
# NOW with package hints — not at first login as a DllNotFoundException
# crash-loop with no display.
if command -v ldd >/dev/null 2>&1; then
  MISSING="$(LD_LIBRARY_PATH="$LIBWTF" ldd "$LIBWTF"/libwtf_shim.so "$LIBWTF"/libscenefx-0.2.so 2>/dev/null \
               | grep 'not found' | sort -u || true)"
  if [ -n "$MISSING" ]; then
    echo "install.sh: staged libraries have UNRESOLVED dependencies on this machine:" >&2
    echo "$MISSING" >&2
    echo "  Debian/Ubuntu: bash scripts/install-deps.sh   (wlroots-0.18, libinput, ...)" >&2
    echo "  Fedora: dnf install wlroots libinput | Arch: pacman -S wlroots0.18 libinput" >&2
    exit 1
  fi
fi

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
