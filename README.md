# WTF — Wayland Tiling, F#

[![ci](https://github.com/Neftedollar/WTF/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/WTF/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/Neftedollar/WTF)](https://github.com/Neftedollar/WTF/releases)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Website & docs: [neftedollar.com/WTF](https://neftedollar.com/WTF/)**

**A tiling Wayland compositor whose configuration is real F# code — typo-proof,
hot-reloading, and drivable by scripts and LLM agents.**

WTF is for you if you liked xMonad's "your window manager is a program" idea
and want it on Wayland — with a compiler catching your config mistakes, modern
eye-candy (blur, rounded corners, macOS-style shadows), and a control socket
designed for automation from day one.

```fsharp
// ~/.config/wtf/config.fsx — this is the actual config format
let wtfConfig =
    config {
        modKey "Super"
        terminal "foot"
        defaultLayout Layouts.Tall        // Type Provider: a typo won't compile
        keys (keymap {
            bind "M-Return" (Spawn "foot")
            bind "M-j"      (Focus NextWindow)
            bind "M-space"  (SetLayout Layouts.Bsp)
        })
        manageHook (manage {
            rule (appIs "firefox") (ShiftToWorkspace "2")
            rule (titleContains "Picture-in-Picture") FloatWindow
        })
        gaps 8
        cornerRadius 10
        blur true
        shadow true                       // macOS-style drop shadows (scenefx)
        wallpaper (Dynamic ("~/pics/catalina.heic", Fill))  // time-of-day .heic
    }
```

Save the file and the running WM applies it instantly. A typo? The config is
rejected, the error goes to the log, and the **last good config stays active**
— your session never dies from a missing parenthesis.

> **Status: 0.1 beta.** Dogfooded daily as the author's main session. Single
> monitor is the well-trodden path today (multi-monitor tiling is the top
> roadmap item). Every commit runs a full build → install → headless boot →
> IPC smoke test on five distros in CI. Expect rough edges; the crash story
> below is honest about them.

## Screenshots

<!-- TODO: add real screenshots/GIFs before or shortly after launch:
     1. tiled session with gaps + shadows + blur (the money shot)
     2. config.fsx in an editor showing Apps./Layouts. autocomplete
     3. a short GIF: edit gaps in config.fsx, save, layout reflows live
     4. a short GIF: `wtfctl ask "put the browser on workspace 2"` -->
*Screenshots coming — the project is launching from the author's daily-driver
machine. Until then, the 60-second nested run below is the fastest way to see it.*

## Try it in 60 seconds

**One line, any supported distro** (Debian, Ubuntu, Fedora, Arch, openSUSE;
x86_64/aarch64). Detects your package manager and fetches the latest prebuilt
release — no .NET SDK, no meson, no compile:

```sh
curl -fsSL https://raw.githubusercontent.com/Neftedollar/WTF/master/scripts/get-wtf.sh | bash
```

Prefer to see what you run? [Read the script](scripts/get-wtf.sh) — it only
downloads a release asset and runs the same installers documented in
[Installation](docs/installation.md). Manual equivalent from the
[latest release](https://github.com/Neftedollar/WTF/releases/latest):

```sh
# Debian 13+ / Ubuntu 24.04+ (apt resolves the runtime deps):
sudo apt install ./wtf-wm_0.1.1_amd64.deb

# Any other distro of the supported set — the tarball:
tar xf wtf-0.1.1-linux-x64.tar.gz && cd wtf-0.1.1
sudo bash scripts/install-deps.sh    # system runtime libraries (once)
bash scripts/install-stage.sh stage  # atomic install into /usr/local
```

**Zero-risk first look — run it nested.** You don't have to log out or trust
it with your session: run `wtf` from a terminal *inside* your current desktop
and WTF opens as a regular window with a full compositor running in it.
Play with `Super+Return`, `Super+j/k`, `Super+space`; close the window when
you're done. Nothing outside that window is touched.

```sh
wtf            # nested session in a window
wtfctl state   # in another terminal: the whole WM state as JSON
```

**Make it your session.** Log out and pick **WTF** in your display manager
(GDM/SDDM). First steps: [Quickstart](docs/quickstart.md).

Building from source instead (any of the 5 CI distros, ~x86_64/aarch64):

```sh
sudo bash scripts/install-deps.sh   # deps incl. the .NET SDK if missing
bash scripts/install.sh             # build + atomic install + session entry
```

wlroots and scenefx are **vendored and bundled** — no distro wlroots package
needed, and the installed WM is **self-contained** (no .NET runtime required on
the target). Details: [Installation](docs/installation.md).

## Why WTF

**Config with a compiler behind it.** `~/.config/wtf/config.fsx` is a real F#
program. Machine-aware Type Providers turn *your machine* into types:
`Apps.` autocompletes to your installed applications (`Apps.Firefox.AppId`),
`Layouts.` to the valid layout names — a rule for an app you don't have, or
`SetLayout "tll"`, is a **compile error** in your editor, not a broken session.
`wtf-edit` sets up the F# language server for you. And because config is code,
appearance can be a function: per-app border colors, opacity rules, themes that
follow the wallpaper palette. See [Configuration](docs/configuration.md).

**Hot-reload with a safety net.** Every save recompiles and applies the config
live; a config that doesn't compile is rejected and the last good one stays
active. Off the main thread — the session never stutters or dies from an edit.

**Agent-first control.** The entire WM state is one JSON document; every action
is a semantic command (`focus the browser`, not `press Super+J`) on an NDJSON
unix socket. `wtfctl` is the human CLI; `wtfctl tools` emits a machine-readable
tool manifest so an LLM agent can discover the vocabulary; `wtfctl ask "put the
browser on workspace 2"` is the opt-in natural-language driver. Scripts,
`socat`, or an agent — same door. See [wtfctl & the control socket](docs/wtfctl.md).

**Looks, live-tunable.** Rounded corners, backdrop blur, macOS-style drop
shadows (via scenefx), slide/fade animations, per-window opacity, gaps, colored
focus borders — every knob works over the socket (`wtfctl corners 12`,
`wtfctl blur on`), so you iterate on your rice live and bake the result into
config. Wallpapers: solid, image, or **dynamic time-of-day `.heic`** (the
macOS dynamic wallpaper format, decoded with libheif) with a color palette
that follows the current frame. See [Appearance](docs/appearance.md).

**Crash-resilient by design.** The login manager runs a session wrapper, not
the raw compositor: on an abnormal exit it restarts WTF (bounded), then falls
back to **safe mode** (default config, no eye-candy), then returns you to the
greeter — and every session writes a complete log with backtraces to
`~/.local/state/wtf/`. See [Troubleshooting](docs/troubleshooting.md).

**Tested like a library, smoked like a product.** The window-management brain
is pure F# — 786 xUnit/FsCheck tests green. CI boots the real installed
compositor headless on Debian, Ubuntu, Fedora, Arch, and openSUSE and drives
it over IPC on every commit.

Also in the box: workspaces 1–9 with per-workspace layouts, `tall`/`wide`/
`bsp`/`grid`/`full` plus custom F#-defined layouts, floating & fullscreen,
window rules, XWayland, server-side decoration negotiation, a status bar and
launcher (omnibox), screenshot/screencast portals, undo/redo of window
arrangements, and an optional [NativeAOT build](docs/AOT.md).

## How it compares

Honest positioning — these are all good projects; WTF exists because no one of
them combines these particular properties.

| | WTF | xMonad | sway | Hyprland |
|---|---|---|---|---|
| Config model | **F# code**, compile-checked + Type-Provider autocomplete, hot-reload with last-good fallback | Haskell code, recompile to apply | text file (i3 syntax) | text file (+ plugins) |
| Display server | Wayland | X11 | Wayland | Wayland |
| Eye-candy | blur, rounded corners, shadows, animations (scenefx) | none built in | none by design | the reference point — deepest effects stack |
| Scriptable control | NDJSON socket, semantic commands, LLM tool manifest | X11 tools / custom | `swaymsg` IPC | `hyprctl` IPC |
| Multi-monitor | not yet (top roadmap item) | yes | yes, mature | yes, mature |
| Maturity | **0.1 beta** | decades | very mature, i3-compatible | mature, huge community |

If you need multi-monitor today, or maximum stability, sway and Hyprland are
the safer choices — genuinely. If you want your WM to be a typed program with
an agent-grade API, that's the niche WTF is built for.

## Documentation

| | |
|---|---|
| [Installation](docs/installation.md) | prebuilts, source build, first login |
| [Quickstart](docs/quickstart.md) | first session, the ten keys of day one |
| [Configuration](docs/configuration.md) | the `config.fsx` DSL end to end |
| [Keybindings](docs/keybindings.md) | chord syntax, full default map |
| [Appearance](docs/appearance.md) | borders, shadows, blur, wallpapers |
| [wtfctl](docs/wtfctl.md) | CLI, raw JSON protocol, the agent socket |
| [Troubleshooting](docs/troubleshooting.md) | logs, safe mode, crash recovery |
| [FAQ](docs/faq.md) | stability, F#, .NET-at-runtime, NVIDIA, agents |
| [Architecture](docs/architecture.md) | the "F# brain, C body" split, repo map |
| [Config editing](docs/CONFIG-EDITING.md) | `wtf-edit`, F# LSP autocomplete |
| [NativeAOT](docs/AOT.md) | the lean native-binary flavor |

## Architecture in one paragraph

**F# brain, C body.** All window-management logic — layouts, workspaces,
focus, rules, your config — is a pure, property-tested F# core. A thin C shim
over wlroots 0.18 + scenefx owns the GPU, input, and Wayland protocol; only
flat data (ids, rectangles, intents) crosses the boundary. A layout is
literally `Rect -> Stack<'a> -> ('a * Rect) list`: C calls the brain on
discrete events, gets rectangles back, and animates windows towards them.
The full story, the rejected alternatives, and the repo map:
[docs/architecture.md](docs/architecture.md).

## Contributing

Bug reports with a session log are gold — see
[Troubleshooting](docs/troubleshooting.md#reporting-a-bug) for what to attach.
Dev setup, the test/smoke matrix, and where help is most wanted (packaging,
multi-monitor, wlroots 0.19): [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE).
