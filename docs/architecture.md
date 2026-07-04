# Architecture: "F# brain, C body"

Writing a full Wayland compositor in pure F# is a multi-year research project
(you'd reimplement DRM/KMS, GBM/EGL, libinput). Instead WTF splits the system
exactly like the Haskell [`tiny-wlhs`](https://github.com/l-Shane-l/tiny-wlhs)
project proved works:

```
        ┌──────────────────────────────┐   narrow, blittable C ABI    ┌────────────────────────┐
        │  C SHIM  (the "body")         │   ids + rectangles + intents │  F# BRAIN              │
        │  derived from wlroots/tinywl  │ <──────────────────────────> │                        │
        │                               │                              │  • StackSet (zipper)   │
        │  • backend / renderer / EGL   │   C calls UP  → arrange()     │  • Layout functions    │
        │  • wl_display event loop      │   F# calls DOWN → move/focus  │  • workspaces, focus   │
        │  • wl_listener plumbing       │                              │  • keybinds, config    │
        │  • input capture, surfaces    │   only flat data crosses     │  • IPC / agent control │
        │  • animations, blur, damage   │   (never wlroots structs)    │                        │
        └──────────────────────────────┘                              └────────────────────────┘
```

- The **C side** (`compositor/wtf-shim.c`, wlroots 0.19 + scenefx) owns
  everything performance-critical and protocol-heavy, and it is the *only*
  thing that breaks when wlroots bumps its (unstable) ABI.
- The **F# side** is 100% safe, pure, property-tested code. A layout is
  literally `Rect -> Stack<'a> -> ('a * Rect) list`. C calls it on discrete
  events, gets back rectangles, and animates windows *towards* them.

**Rejected alternative:** full P/Invoke of wlroots from F# (hand-mirrored
structs, pinned listeners, breaks every wlroots release). See the research
notes in git history.

## The three pillars

| Pillar | Where it lives | How |
|---|---|---|
| **xMonad flexibility** | F# brain | Config *is* F# code, compiled by the WM — layouts are plain functions, trivial to add. Pure + FsCheck-tested. |
| **Hyprland-style looks** | C shim renderer | F# emits *target* rectangles; the C renderer interpolates (animations) and applies blur/opacity/rounded corners/shadows via scenefx. Beauty = how we travel to the rects F# chose. |
| **Agent-first** | F# IPC boundary | A structured control protocol (query state, issue semantic commands) designed for an LLM driver, not just a CLI. The brain holds all state as immutable data — easy to serialize and command. |

## Why crossing the boundary is cheap

Only flat, blittable data crosses the C↔F# boundary: window ids, rectangles,
key chords, command intents. The C side never sees an F# object; the F# side
never sees a wlroots struct. Events are discrete (window mapped, key pressed,
output changed) — the brain is invoked per event, not per frame, so the .NET
runtime is never in the render hot path. Animations, damage tracking, and
blur run entirely in C at frame rate.

## Repository layout

```
compositor/              the C body
  wtf-shim.c             wlroots 0.19 + scenefx compositor shim → libwtf_shim.so
  wtf-panel.c            layer-shell client library for the bar/omnibox
  wtf.h                  the narrow C ABI both sides compile against
src/
  WTF.Core/              the brain: Rect/Stack/Layout/World, Command/Reducer,
                         config DSL, palette, protocol (pure — no system deps)
  WTF.Host/              the process: P/Invoke bridge, chord translation,
                         config loading (FCS), IPC socket, wallpapers, session
  WTF.TypeProviders/     machine-aware config Type Providers (Apps/Layouts/Xkb)
  WTF.Config/            config compilation engine (FSharp.Compiler.Service)
  WTF.Desktop/           D-Bus desktop services: notifications, battery,
                         network, media players
  WTF.Agent/             the opt-in LLM driver behind `wtfctl ask`
  WTF.Client/            shared client plumbing (socket, fuzzy match, panel +
                         bar render — reused in-process by the embedded bar)
  WTF.Bar/               the status bar (standalone layer-shell client)
  WTF.Omnibox/           the launcher
  WTF.Plugins/           reflective layout-plugin loader
  wtfctl/                the CLI over the control socket
tests/                   xUnit + FsCheck suites per project
scripts/                 build / install / smoke / session tooling
packaging/               .desktop, portals config, PKGBUILD, rpm spec, patches
docs/                    user documentation
```

## Surfaces: bar & omnibox

WTF's own bar and launcher are **surfaces** — pixels the WM draws over the tiled
windows. Two paths render them, sharing one pure composition (`WTF.Client`'s
`BarRender` / `Render.Surface`, `Bgra32` = `ARGB8888`):

- **In-process (default).** The host renders each `embedded` bar directly:
  `BarRender.draw` → `byte[]` → `wtf_set_bar(id, pixels, …)`, which the shim puts
  in a scene buffer on the layer-shell TOP layer and reserves its strip from the
  usable area (so tiling never overlaps the bar) — the same mechanism the
  wallpaper uses one layer down. State is read in-process (no socket), on a timer
  gated by `refreshMs` with change-detection, so an idle bar costs nothing.
- **External (opt-in / third-party).** `bar { embedded false }`, `wtf-bar`,
  `wtf-omnibox`, and any third-party client (waybar, wofi…) run as **standalone
  layer-shell clients** and poll the **agent socket** for the state snapshot. The
  socket + layer-shell contract is stable, so external surfaces stay first-class
  and portable to other compositors.

The **omnibox** works the same way: `ToggleOmnibox` shows the built-in launcher
as a centered `wtf_set_overlay` scene buffer in the OVERLAY layer (no strip
reserved, no wl client). There is no keyboard grab — the compositor already
delivers every key to the brain via the `key` callback (now carrying the utf32
codepoint for text entry), so while an overlay is shown the host routes keys to
it instead of matching keybinds. Esc/Enter dismiss it.

Both surfaces are also the USER extension point (the ".NET as a platform" story,
generalized from layouts): a plugin implementing `IWtfBarPlugin` or
`IWtfOverlayPlugin` is discovered by the SAME `PluginLoader` scan and registered
into `SurfaceRegistry`, which the host reads to drive `wtf_set_bar` /
`wtf_set_overlay`. The built-in omnibox is itself an `IWtfOverlayPlugin` (name
`"omnibox"`), so it is just the default surface a user plugin can replace.

The standalone exes are thin layer-shell wrappers around the *same* shared
render, so both paths look identical. Under NativeAOT the host has no embedded
surface (it uses the external `wtf-bar` / `wtf-omnibox`).

The SAME loader scan carries a third extension interface, `IWtfEffectPlugin`
(alongside `IWtfLayoutPlugin` and the surfaces): its named strategies flow into
`EffectRegistry`, and the host resolves the config's `effectStrategy` name to a
`RenderContext -> WindowEffect list` applied per window in the restyle path,
layered on top of the static appearance. The built-in `"none"` strategy keeps
this byte-for-byte today's behavior; only the *composition/targeting* of the
per-window primitives (opacity, border color) is pluggable — the atomic GPU
effects stay fixed in C/scenefx.

## Builds

Two flavors from one tree:

- **Default (JIT)**: self-contained .NET publish — every feature, including
  `config.fsx` hot-reload and `wtfctl eval`. This is what `scripts/install.sh`
  and the release artifacts ship.
- **NativeAOT** (`-p:WtfAot=true`): a small native binary that drops the
  reflection-dependent subsystems (hot-reload, plugins, D-Bus shell, LLM
  agent) and bakes the config in, xMonad-style. See [AOT.md](AOT.md) for the
  feature matrix.

wlroots and scenefx are **vendored** (pinned versions built by
`scripts/build-wlroots.sh` / `build-scenefx.sh` and bundled into
`/usr/local/lib/wtf`), so an install never depends on which wlroots the
distro packages.
