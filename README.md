# WTF — Wayland Tiling, F#

A tiling window manager for Linux/Wayland, with the **configurability of xMonad**,
the **looks of Hyprland** (animations / transparency / blur / rounded corners),
and a first-class **agent-first control surface**.

**Status: 0.1 beta — ready to test.** Builds, installs as a login session, tiles
real windows on the GPU, driven by an F# brain. Rices live over a socket
(gaps, borders, opacity, animations, rounded corners, blur). 49 tests green.

## The big architectural decision: "F# brain, C body"

Writing a full Wayland compositor in pure F# is a multi-year research project
(you'd reimplement DRM/KMS, GBM/EGL, libinput). Instead we split the system
exactly like the Haskell [`tiny-wlhs`](https://github.com/l-Shane-l/tiny-wlhs)
project proved works:

```
        ┌──────────────────────────────┐   narrow, blittable C ABI    ┌────────────────────────┐
        │  C SHIM  (the "body")         │   ids + rectangles + intents │  F# BRAIN (this repo)  │
        │  derived from wlroots/tinywl  │ <──────────────────────────> │                        │
        │                               │                              │  • StackSet (zipper)   │
        │  • backend / renderer / EGL   │   C calls UP  → arrange()     │  • Layout functions    │
        │  • wl_display event loop      │   F# calls DOWN → move/focus  │  • workspaces, focus   │
        │  • wl_listener plumbing       │                              │  • keybinds, config    │
        │  • input capture, surfaces    │   only flat data crosses     │  • IPC / agent control │
        │  • animations, blur, damage   │   (never wlroots structs)    │                        │
        └──────────────────────────────┘                              └────────────────────────┘
```

- **C side** owns everything performance-critical and protocol-heavy, and it is
  the *only* thing that breaks when wlroots bumps its (unstable) ABI.
- **F# side** is 100% safe, pure, property-tested code. A layout is literally
  `Rect -> Stack<'a> -> ('a * Rect) list`. C calls it on discrete events,
  gets back rectangles, and animates windows *towards* them.

Rejected: full P/Invoke of wlroots from F# (hand-mirrored structs, pinned
listeners, breaks every release). See the research report in git history.

## The three pillars

| Pillar | Where it lives | How |
|---|---|---|
| **xMonad flexibility** | F# brain | Config *is* F# code, recompiled — layouts are plain functions, trivial to add. Pure + FsCheck-tested. |
| **Hyprland beauty** | C shim renderer | F# emits *target* rectangles; the C renderer interpolates (animations), and applies blur/opacity/rounded corners. Beauty = how we travel to the rects F# chose. |
| **Agent-first** | F# IPC boundary | A clean structured control protocol (query state, issue semantic commands) designed for an LLM driver, not just a CLI. The brain already holds all state as immutable data — easy to serialize and command. |

## Status

- [x] `.NET 10 LTS` toolchain (in `~/.dotnet`)
- [x] **Layout engine** — `Rect`, `Stack` zipper, `full`/`tall`/`bsp` layouts, gaps
- [x] **Multi-workspace `World`** — 9 tags, view / move-window-to-tag
- [x] **Named layout registry** — pluggable `LayoutFactory`, agent-discoverable
- [x] **Agent-first control** — `Command`/`Selector` intents, pure `Reducer`,
      `Effect`s for the compositor
- [x] **JSON protocol** — `Protocol.snapshot` (state for the LLM) +
      `Protocol.parse` (commands from the LLM)
- [x] xMonad-parity layouts: `tall`/`wide`(Mirror)/`bsp`/`grid`/`full` + `mirror`/`reflect` modifiers
- [x] **Config via computation expressions** — `config`/`keymap`/`manage`/`agent`
      CEs over our object model (`Config.fs`); `ManageHook` window rules
- [x] Property + unit tests (FsCheck/xUnit) — 39 passing
- [x] Visualisers: `demo.fsx`, `agent-demo.fsx`, `examples/config.fsx`
- [x] **FFI boundary**: `compositor/wtf.h` C ABI; `WTF.Host` F# host
      (delegates up, P/Invoke down, chord translation) — compiles
- [x] **C compositor shim** (`compositor/wtf-shim.c`, wlroots 0.18) — builds `libwtf_shim.so`
- [x] **First graphical beta WORKS** — runs nested, GLES2 renderer, auto-spawns
      terminals; the F# brain tiles real Wayland windows (verified: two kitty
      terminals → master/stack split). `scripts/build.sh` + `scripts/run.sh`.
- [x] **Agent-first door** — NDJSON unix socket (`$XDG_RUNTIME_DIR/wtf.sock`),
      `wtfctl` CLI + LLM channel; cross-thread→loop-thread bridge via eventfd
- [x] **Eye-candy** — slide+fade animations, active/inactive opacity, colored
      focus borders, **rounded corners + backdrop blur via scenefx** — all in the
      C renderer, all live-tunable over the socket
- [x] **Installable beta** — self-contained publish + login-session file
- [ ] Shadows (scenefx); floating windows; multi-output; interactive keybind pass

## Agent control protocol (`wtfctl` / LLM)

The compositor serves newline-delimited JSON on `$XDG_RUNTIME_DIR/wtf.sock`:
send one command per line, get the full world snapshot back. Friendly CLI:

```
wtfctl state                  # the world snapshot (what an LLM reads)
wtfctl layout bsp | next      # tall|wide|bsp|grid|full, or cycle
wtfctl focus next|prev|master | focus app firefox   # focus (no pixels)
wtfctl swap next|prev|master  # reorder / promote to master
wtfctl master inc|dec | master 2     # master-pane count
wtfctl ratio 0.6              # master-pane width
wtfctl workspace 2|next|prev | move 2
wtfctl spawn kitty | close
wtfctl gaps 16|inc|dec        # live gaps
wtfctl opacity 0.85           # live inactive-window transparency
wtfctl anim 0.4               # live animation speed
wtfctl border width 3         # live border thickness
wtfctl border active "#f38ba8"   # live focused-border color
wtfctl border inactive "#45475a"
wtfctl corners 12             # live rounded-corner radius (scenefx)
wtfctl blur on|off            # live backdrop blur (scenefx)
wtfctl '{"cmd":"focus","by":"next"}'   # raw JSON (same door an agent uses)
```

## Ricing

Appearance is configured in `~/.config/wtf/config.fsx` (gaps, border width +
colors, inactive opacity, animation speed) **and** adjustable **live** over the
socket — so you can iterate on your theme with `wtfctl` and bake the result into
config. A theme is just a few `wtfctl` lines you can drop in a startup script:

```sh
wtfctl gaps 12
wtfctl border width 3
wtfctl border active "#cba6f7"
wtfctl border inactive "#313244"
wtfctl opacity 0.90
```

All the rice essentials are in: gaps, colored focus borders, per-window opacity,
slide/fade animations, **rounded corners and backdrop blur** (via **scenefx**,
the SwayFX scene-graph fork — built automatically by `scripts/build-scenefx.sh`).
Every one of these is live-tunable over the socket. (Blur is opt-in/experimental;
shadows are the next scenefx addition.)

## Try it / install

```
sudo bash scripts/install-deps.sh   # wlroots 0.18 + wayland build deps (Debian 13)
bash scripts/build.sh               # libwtf_shim.so + F# host
bash scripts/run.sh                 # run WTF nested in your current session

# install as a real login session (appears in gdm/sddm as "WTF"):
bash scripts/install.sh             # self-contained — target needs NO .NET
```

The installer publishes a **self-contained** build, so machines you install on only
need the wlroots/wayland *runtime* libraries (present on any wlroots desktop), not
the .NET SDK. It drops a `wtf` launcher + `wtfctl` in `/usr/local/bin`, the shim in
`/usr/local/lib/wtf`, a session file in `/usr/share/wayland-sessions`, and seeds
`~/.config/wtf/config.fsx`.

### NativeAOT build (lean, fast-starting native binary)

An additive `-p:WtfAot=true` flavor compiles a small native binary (no 76 MB .NET
payload) by dropping the reflection/JIT-only subsystems (config.fsx hot-reload,
plugins, D-Bus desktop shell, LLM agent) and shipping the lean core WM with the
built-in config — recompile to reconfigure, xMonad-style.

```
dotnet build src/WTF.Host/WTF.Host.fsproj -c Release -p:WtfAot=true  # lean graph (no clang)
bash scripts/aot-publish.sh                                          # native binary (needs clang)
```

See [docs/AOT.md](docs/AOT.md) for the full feature matrix and the clang prerequisite.

## Layout / build

```
src/WTF.Core        # the F# brain (no system deps — runs on bare .NET)
tests/WTF.Core.Tests
demo.fsx            # ASCII visualiser

dotnet test         # run property tests
dotnet fsi demo.fsx # see layouts
```
