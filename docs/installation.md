# Installation

## Requirements

- Linux with a GPU that does GLES2 (any Mesa driver; llvmpipe works for VMs)
- **wlroots 0.18** and its runtime libraries (libinput, libxkbcommon, …)
- The .NET 10 SDK (build machine only — the installed WM is self-contained
  and needs **no** .NET on the target)
- `meson` + `ninja` + a C compiler (for the small compositor shim)
- Optional: `libheif` for dynamic `.heic` wallpapers (degrades gracefully
  without it), `Xwayland` for X11 apps, `xdg-desktop-portal-wlr` +
  `xdg-desktop-portal-gtk` for screenshots/screencast/file pickers

On Debian/Ubuntu, one script installs the build dependencies:

```sh
sudo bash scripts/install-deps.sh
```

On Fedora/Arch install the equivalents manually (`wlroots`, `libinput`,
`meson`, `ninja`, .NET SDK). The installer verifies at install time that every
bundled library resolves on your machine and fails loudly with hints if
something is missing — you will not discover a missing wlroots at first login.

## Build & install

```sh
git clone <repo> && cd WTF
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
