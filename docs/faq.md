# FAQ

## Is it stable enough to use?

It's a **0.1 beta**, dogfooded daily as the author's main session. The honest
picture:

- The window-management brain is pure F# with 786 tests (including FsCheck
  property tests) — layout/workspace/focus logic is the solid part.
- The compositor boots headless on five distros in CI on every commit
  (full build → install → boot → IPC smoke).
- Crashes are *contained* rather than pretended away: the session wrapper
  restarts WTF (bounded), falls back to safe mode, then to the greeter, and
  every session logs to `~/.local/state/wtf/` with backtraces. You lose the
  session, not your data — apps are told to close cleanly.
- Rough edges to expect: single-monitor tiling only (see below), no
  `Super+drag` for floating windows yet, and a small hardware test pool.

Try it [nested inside your current session](quickstart.md) first — that costs
you nothing.

## Why F#?

The xMonad thesis is that a window manager is a *program* and your config is
part of it. F# keeps that thesis and adds things Haskell-on-X11 couldn't give:

- **Type Providers** — config autocomplete generated from *your machine*:
  `Apps.` lists your installed applications, so a rule for an app you don't
  have is a compile error. No other config system does this.
- **Hot-reload with a compiler** — `config.fsx` is recompiled on save by the
  embedded F# compiler service; a broken config is rejected and the last good
  one stays live. You get type-checking *and* instant iteration.
- A mature runtime with a real async story, property-testing (FsCheck), and
  first-class JSON — which is what makes the [agent socket](wtfctl.md) cheap
  to build and test.

The performance-critical parts are not in F# — see the next question.

## Isn't .NET too slow / heavy for a compositor?

The .NET code is never in the render path. The C shim (wlroots + scenefx)
owns the event loop, rendering, animations, and damage tracking; the F# brain
is called on **discrete events** (window opened, key pressed) and returns
rectangles. Frames are produced entirely in C. See
[Architecture](architecture.md).

## Do I need .NET installed to run it?

**No.** Installs are self-contained: the .NET runtime is bundled under
`/usr/local/lib/wtf` (or `/usr/lib/wtf` for the `.deb`). Prebuilt release
artifacts need no .NET SDK, no meson, no compiler. Only building **from
source** needs the SDK — and `scripts/install-deps.sh` installs it for you.

There is also a [NativeAOT flavor](AOT.md) that compiles to a small native
binary (dropping hot-reload and the other reflection-dependent features).

## Does it work on NVIDIA?

Untested by the author (the daily-driver and CI pool are Mesa: AMD/Intel and
software GL). WTF needs GLES2 through GBM, which recent proprietary NVIDIA
drivers support and wlroots-based compositors generally run on — but we won't
claim it until someone's tried it. If you run WTF on NVIDIA, success *or*
failure reports (with the session log) are very welcome.

## What's the multi-monitor status?

Not there yet — it's the **top roadmap item**. Today WTF tiles on a single
primary output. Extra outputs are handled safely (hotplug/unplug won't crash
the session; if the primary fails to initialize, the next working output takes
over), but workspaces don't span or move across monitors. If this is your
daily requirement, sway or Hyprland will serve you better right now — and if
you want to help build it, see [CONTRIBUTING](../CONTRIBUTING.md).

## How is WTF different from xMonad / sway / Hyprland?

- **vs xMonad**: same "config is a program" philosophy, but on Wayland, with
  hot-reload instead of recompile-and-restart, machine-aware autocomplete via
  Type Providers, and built-in eye-candy. If you loved xmonad.hs, config.fsx
  will feel like home.
- **vs sway**: sway is mature, multi-monitor, i3-compatible, and deliberately
  minimal. WTF trades that maturity for a programmable config and effects.
  Different goals; sway is the safe choice, WTF is the programmable one.
- **vs Hyprland**: Hyprland has the deeper effects stack and a huge community.
  WTF has fewer effects (blur, rounded corners, shadows, animations via
  scenefx) but a typed, compiled config and an agent-grade control protocol.

## Can an LLM really drive it?

Yes — that's a design constraint, not a bolt-on. The entire WM state
serializes to one JSON document, and every action is a semantic command
(`{"cmd":"focus","app":"firefox"}`) on an NDJSON socket at
`$XDG_RUNTIME_DIR/wtf.sock`. Concretely:

- `wtfctl tools` returns a curated machine-readable tool manifest, so an agent
  can discover the command vocabulary instead of guessing.
- `wtfctl state` (or a raw `state` line on the socket) returns the full world
  snapshot an agent plans against.
- Commands are semantic — no pixel coordinates, no synthesized keypresses — so
  automations survive keybinding and layout changes.
- `wtfctl ask "put the browser on workspace 2"` routes natural language
  through an **opt-in** in-process driver. It is disabled unless
  `ANTHROPIC_API_KEY` is set; nothing talks to any network service without it.

Any MCP-style or scripted agent can skip `ask` entirely and speak the socket
protocol directly. Protocol limits (1 MB/line, 32 clients, malformed input
returns `{"error":…}` without disturbing the session) are documented in
[wtfctl](wtfctl.md).

## Is my config sandboxed? What if it's malicious?

`config.fsx` is a real program running as you, same as `xmonad.hs` or any
shell rc file — don't paste configs you haven't read. What WTF protects
against is *mistakes*: a config that doesn't compile or throws is rejected or
degraded (logged, falls back to defaults), never fatal to the session.

## Wayland-native? XWayland? Screen sharing?

Wayland-native, with **XWayland** for X11 apps. Screenshots and screencast go
through the standard wlr portals (`xdg-desktop-portal-wlr`), so `grim`, OBS,
and browser screen-sharing work as on sway. File pickers use the GTK portal.

## Where do I get help?

Check [Troubleshooting](troubleshooting.md) first — the session log at
`~/.local/state/wtf/` answers most "what just happened" questions. Then
[open an issue](https://github.com/Neftedollar/WTF/issues); the bug template
tells you exactly what to attach.
