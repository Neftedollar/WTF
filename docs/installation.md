# Installation

## Prebuilt packages (fastest — no toolchain)

Every [release](https://github.com/Neftedollar/WTF/releases) ships prebuilt
artifacts for x86_64 and aarch64. They are built on Ubuntu 24.04 (glibc 2.39),
so they run on Debian 13+, Ubuntu 24.04+, and current Fedora/Arch/openSUSE —
**no .NET SDK, no meson, no compiler** on your machine.

**`.deb`** (Debian 13+ / Ubuntu 24.04+; package name `wtf-wm`):

```sh
sudo apt install ./wtf-wm_0.1.1_amd64.deb    # apt resolves the runtime deps
```

The `.deb` installs under `/usr` and registers the login session. It does
*not* seed `~/.config/wtf/config.fsx` — run `wtf-edit` once before your first
login to seed it from the packaged template (otherwise the built-in defaults
apply, which assume `kitty` and `wofi`).

**Tarball** (Fedora, Arch, openSUSE, or anything else):

```sh
tar xf wtf-0.1.1-linux-x64.tar.gz && cd wtf-0.1.1
sudo bash scripts/install-deps.sh     # system runtime libraries (once)
bash scripts/install-stage.sh stage   # preflight check + atomic install into /usr/local
```

`install-stage.sh` runs an `ldd` preflight of the bundled libraries (so a
missing system library fails loudly *now*, with package hints — not at first
login), then installs atomically and seeds your `config.fsx`.
(`install-deps.sh` is shared with source builds, so it also pulls build tools
— more than the prebuilt strictly needs, but it is the supported path.)

Then skip ahead to [First login](#first-login) — or try it risk-free first:
run `wtf` from a terminal *inside* your current desktop session and WTF opens
nested as a regular window.

## Requirements (source build)

- Linux with a GPU that does GLES2 (any Mesa driver; llvmpipe works for VMs)
- Stable system libraries only: libinput, libdrm, libseat, libxkbcommon,
  Mesa, pixman — **wlroots and scenefx are bundled** (pinned versions built
  by the installer), so no specific distro wlroots package is required
- The .NET 10 SDK (build machine only — the installed WM is self-contained
  and needs **no** .NET on the target; `install-deps.sh` installs the SDK
  automatically if missing)
- `meson` + `ninja` + a C compiler (for the compositor shim + bundled libs)
- Optional: `libheif` for dynamic `.heic` wallpapers (degrades gracefully
  without it), `Xwayland` for X11 apps, `xdg-desktop-portal-wlr` +
  `xdg-desktop-portal-gtk` for screenshots/screencast/file pickers

One script installs everything on **Debian/Ubuntu (apt), Fedora (dnf),
Arch (pacman), and openSUSE (zypper)** — including the .NET SDK when absent:

```sh
sudo bash scripts/install-deps.sh
```

The installer additionally verifies at install time that every bundled
library resolves on your machine and fails loudly with hints if something is
missing — you will not discover a missing library at first login. Every
supported distro is exercised in CI: full build → install → headless boot of
the real compositor → IPC smoke test.

## Build & install from source

```sh
git clone https://github.com/Neftedollar/WTF.git && cd WTF
bash scripts/install.sh
```

Run it as a **regular user** (it refuses root); `sudo` is invoked internally
for exactly one step — copying the staged tree into `/usr/local`. The script:

1. builds scenefx (pinned 0.2.1) and the C shim,
2. publishes the self-contained host + `wtfctl` + bar + omnibox for your
   architecture (x86_64 and aarch64 supported),
3. assembles everything under `build/stage/`,
4. copies it into `/` **atomically, per file** — safe to run over a live WTF
   session (the running session keeps the old binaries until you log out),
5. seeds `~/.config/wtf/config.fsx` if you don't have one.

Installed layout:

| Path | What |
|---|---|
| `/usr/local/bin/wtf` | compositor launcher |
| `/usr/local/bin/wtf-session` | session wrapper (what the login manager runs) |
| `/usr/local/bin/wtfctl`, `wtf-bar`, `wtf-omnibox`, `wtf-edit` | tools |
| `/usr/local/lib/wtf/` | the self-contained runtime + native libs |
| `/usr/share/wayland-sessions/wtf.desktop` | login-manager session entry |
| `~/.config/wtf/config.fsx` | **your** configuration |

## First login

Log out and pick **WTF** in your display manager's session list (GDM: the gear
icon on the password screen; SDDM: the session dropdown).

Alternatives:

- **Nested, zero risk**: run `wtf` from a terminal *inside* your current
  desktop session — WTF opens as a regular window with a full compositor in
  it. Close the window (or `Super+Shift+q`) when done; nothing else is touched.
- From a free TTY: run `wtf-session` (recommended — you get the restart loop
  and logging) or plain `wtf`.
- DRM smoke test without logging in: `wtf-smoke-drm` from a TTY.

If anything goes wrong at startup, the session wrapper writes a full log to
`~/.local/state/wtf/session-<timestamp>.log` and escalates through restarts →
safe mode → back to the greeter. See [Troubleshooting](troubleshooting.md).

## Updating

Pull and re-run `bash scripts/install.sh`. Installing over a running WTF
session is safe (atomic per-file rename); the live session keeps running the
old build until you log out and back in. `Super+Shift+r` reloads only your
config, not binaries.

## Uninstall

There is no uninstall script yet. Remove `/usr/local/lib/wtf`,
`/usr/local/bin/{wtf,wtf-session,wtfctl,wtf-bar,wtf-omnibox,wtf-edit,wtf-smoke-drm}`,
`/usr/share/wayland-sessions/wtf.desktop`, and (optionally) `~/.config/wtf`.
