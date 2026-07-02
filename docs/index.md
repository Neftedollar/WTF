# WTF — a Wayland Tiling window manager, configured in F#

> This documentation is published at
> **[neftedollar.github.io/WTF/docs](https://neftedollar.github.io/WTF/docs/)** —
> rebuilt from `docs/*.md` on every push, so the site never drifts from these files.

WTF is a tiling Wayland compositor in the xMonad tradition: your window manager
is a program, and its configuration is **real F# code** with autocomplete,
type-checking, and hot-reload. Typos in your config are compile errors caught in
your editor — not a black screen at login.

## Design

**F# brain, C body.** All window-management logic — layouts, workspaces, focus,
rules, your config — is a pure, fully-tested F# core (the *brain*). A thin C
shim over [wlroots](https://gitlab.freedesktop.org/wlroots/wlroots) +
[scenefx](https://github.com/wlrfx/scenefx) talks to the GPU, input devices and
Wayland clients (the *body*). The brain decides *where every window goes*; the
body just puts pixels there.

**Agent-first.** The entire WM state is one plain value that serializes to
JSON, and every action is a semantic command (`focus the browser`, not
`press Super+J`). Anything — a script, `wtfctl`, or an LLM agent — can read the
world and drive it over a local socket. See [wtfctl & the control socket](wtfctl.md).

**Config is code.** `~/.config/wtf/config.fsx` is an F# script with a small
declarative DSL. It hot-reloads on save. Machine-aware Type Providers give you
`Apps.Firefox.AppId` (your installed apps) and `Layouts.Bsp` (valid layouts) as
compile-checked literals. See [Configuration](configuration.md).

## Features

- Tiling layouts: `tall`, `wide`, `bsp`, `grid`, `full` — plus F#-defined
  custom layouts registered from your config
- Workspaces 1–9, per-workspace layouts, floating & fullscreen windows
- Manage rules (`firefox → workspace 2`, `Picture-in-Picture → float`)
- Eye-candy via scenefx: rounded corners, backdrop blur, macOS-style drop
  shadows, window animations, per-window opacity
- Wallpapers: solid color, image, or **dynamic time-of-day `.heic`**
  (macOS dynamic wallpaper format) — see [Appearance](appearance.md)
- Server-side decoration negotiation: uniform borders instead of every app
  drawing its own frame
- XWayland support for X11 apps
- Screenshots/screencast via the standard wlr portals (grim, OBS, …)
- A crash-resilient session wrapper: bounded restart, safe mode, rotating
  logs — see [Troubleshooting](troubleshooting.md)

## Where to go next

| Page | What it covers |
|---|---|
| [Installation](installation.md) | Requirements, build, install, first login |
| [Quickstart](quickstart.md) | Your first session, the default keys |
| [Configuration](configuration.md) | The `config.fsx` DSL, end to end |
| [Keybindings](keybindings.md) | Chord syntax + the full default map |
| [Appearance](appearance.md) | Borders, shadows, blur, wallpapers, ricing |
| [wtfctl](wtfctl.md) | CLI control, raw JSON, the agent socket |
| [Troubleshooting](troubleshooting.md) | Logs, safe mode, crash recovery |
| [FAQ](faq.md) | Stability, F#, .NET-at-runtime, NVIDIA, multi-monitor, agents |
| [Architecture](architecture.md) | The "F# brain, C body" split, repo layout |
| [Config editing setup](CONFIG-EDITING.md) | `wtf-edit`, F# LSP autocomplete |
