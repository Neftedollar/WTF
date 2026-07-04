# Configuration

Your window manager lives in **`~/.config/wtf/config.fsx`** — an F# script
loaded at startup and hot-reloaded on every save. It must end with a binding
named `wtfConfig`:

```fsharp
open WTF.Core
open WTF.TypeProviders

let wtfConfig =
    config {
        modKey "Super"
        terminal "foot"
        gaps 8
        keys (keymap { bind "M-Return" (Spawn "foot") })
    }
```

Three ways to work on it:

- **`wtf-edit`** — opens the file with the F# language server configured, so
  you get autocomplete and inline compile errors (see
  [CONFIG-EDITING](CONFIG-EDITING.md)).
- **Save it** — the WM watches the file and applies changes live. A config
  that doesn't compile is *rejected*: the error goes to the session log and
  the last good config stays active. Your session never dies from a typo.
- **`M-S-r` or `wtfctl reload`** — re-read on demand. Compilation runs off the
  main thread; the session stays responsive.

You can also type-check without the WM: `dotnet fsi ~/.config/wtf/config.fsx`.

**Last-good fallback.** Every time your config compiles, WTF snapshots it to
`~/.config/wtf/config.last-good.fsx`. If a *later* start or `reload` hits a
config that won't compile, it falls back to that last-good snapshot — your last
working setup — instead of the built-in vanilla defaults, and only drops to
vanilla if the snapshot is unusable too. Run **`wtfctl save-default`** to bless
the current config as the fallback on demand (it refuses to save a config that
doesn't compile, so the fallback is always known-good).

## Machine-aware autocomplete (Type Providers)

With `open WTF.TypeProviders`, three providers turn *your machine* into types:

- `Apps.` — your installed `.desktop` applications. `Apps.Firefox.AppId` is
  the exact window app-id; an app you don't have is a **compile error**.
- `Layouts.` — the valid layout names. `Layouts.Bsp` is the literal `"bsp"`;
  `SetLayout "tll"` can't happen.
- `Xkb.` — keyboard layouts/options known to your xkb.

## The `config { … }` operations

| Operation | Type | Default | Meaning |
|---|---|---|---|
| `modKey` | string | `"Super"` | `"Super"` or `"Alt"` — what `M-` means |
| `terminal` | string | `"foot"` | your terminal (for scripts/agents) |
| `workspaces` | string list | `["1"..."9"]` | workspace tags |
| `defaultLayout` | string | `"tall"` | layout for new workspaces |
| `gaps` | int | `6` | inner gap around every tile, px |
| `borderWidth` | int | `2` | window border, px |
| `keys` | bindings | built-ins | your keymap — **replaces** the default |
| `manageHook` | rules | `[]` | window placement rules (below) |
| `startup` | string list | `[]` | commands to run once at startup |
| `inactiveOpacity` | float | `0.94` | unfocused window opacity 0–1 |
| `animSpeed` | float | `0.30` | window slide/fade easing (1.0 = instant) |
| `activeBorder` / `inactiveBorder` | hex | blue / gray | border colors |
| `borderColor` | function | — | *dynamic* per-window border color (below) |
| `windowOpacity` | function | — | *dynamic* per-window opacity |
| `effectStrategy` | string | `"none"` | pluggable per-window effect strategy (below) |
| `cornerRadius` | int | `0` | rounded corners, px (scenefx) |
| `blur` | bool | `false` | backdrop blur behind translucent windows |
| `shadow` + `shadowSigma/Color/Opacity/Offset` | — | off | drop shadows — see [Appearance](appearance.md) |
| `wallpaper` | Wallpaper | `Color "#1e1e2e"` | solid / image / dynamic — see [Appearance](appearance.md) |
| `scale` | float | `1.0` | HiDPI output scale |
| `historyLimit` | int | `64` | undo depth |
| `input` | InputConfig | defaults | keyboard/mouse/touchpad (below) |
| `bar` / `bars` | BarConfig | built-in look | status bar styling — [below](#bar--omnibox-styling) |
| `omnibox` | OmniboxConfig | built-in look | launcher styling — [below](#bar--omnibox-styling) |

## Keybindings

```fsharp
let myKeys =
    keymap {
        bind "M-Return" (Spawn "foot")
        bind "M-p"      ToggleOmnibox                  // in-process launcher (2b)
        bind "M-j"      (Focus NextWindow)
        bind "M-1"      (SwitchWorkspace "1")
        bind "M-S-r"    ReloadConfig
    }
```

Chord syntax and the full command vocabulary: [Keybindings](keybindings.md).
Remember: `keys myKeys` **replaces** the built-in map.

### Keybinding helpers

A chord binds to a `Command`. Beyond the raw verbs, a few pure helpers (auto-opened
with `WTF.Core`) build common commands for you:

```fsharp
keymap {
    bind "M-b"   (runOrKill "blueman-applet")        // toggle: run if off, kill if on
    bind "M-w"   (raiseOrRun "firefox" "firefox")    // focus its window if open, else launch
    bind "M-S-t" (inTerm "kitty" "htop")             // launch a program inside a terminal
    bind "M-S-w" (setWallpaper "~/pics/city.jpg")    // switch wallpaper live (palette follows)
    bind "M-p"   ToggleOmnibox                        // the in-process launcher
}
```

- `runOrKill name` — kill the process if it's running, else launch it (a scratchpad
  toggle). `runOrKillCmd name launch` when the launch command differs from the
  process name.
- `raiseOrRun app launch` — the classic run-or-raise: focus an existing window of
  `app` (matched by AppId), otherwise launch it. Resolved against the live layout.
- `inTerm term cmd` — `Spawn` a program inside a terminal emulator.
- `setWallpaper path` — swap the wallpaper at runtime (applied Fill); the wallpaper
  palette re-derives, so palette-driven bar/border/omnibox colors follow.

**Ricing on a key** — flip eye-candy live and apply presets:

```fsharp
keymap {
    bind "M-S-b" ToggleBlur                            // flip backdrop blur
    bind "M-S-g" ToggleWatercolor                      // watercolor frames
    bind "M-S-s" ToggleShadows                         // drop shadows
    bind "M-S-o" ToggleGlow                            // focus glow
    bind "M-S-w" (cycleWallpaper [ "~/a.jpg"; "~/b.jpg" ])   // step the wallpaper ring
    bind "M-f"   (batch [ SetGaps 0; SetLayout "full" ])     // "focus mode" preset
    bind "M-S-f" (batch [ SetGaps 8; SetLayout "tall" ])     // back to normal
}
```

- `ToggleBlur` / `ToggleWatercolor` / `ToggleShadows` / `ToggleGlow` — flip the current
  renderer state (re-applying your configured tint/sigma/… parameters), like
  `ToggleFloat` does for a window.
- `cycleWallpaper [paths]` — advance one step per press (the WM remembers the ring
  position); the palette follows each switch.
- `batch [c1; c2; …]` — run several commands from one chord: a preset / "mode".

**OS-tool wrappers** — thin `Spawn` sugar over the standard Wayland tools (swap in
your own with a raw `Spawn` if you use something else):

- `screenshot` — full grab to `~/Pictures/<timestamp>.png` (needs `grim`).
- `screenshotArea` — region → clipboard (needs `grim`, `slurp`, `wl-copy`).
- `lockScreen` — `loginctl lock-session` (needs a session lock handler).

These are ordinary functions returning a `Command`, so they compose anywhere a
command is expected (`bind`, `manage` actions, `wtfctl`). The socket verbs mirror
them: `{"cmd":"raise","app":"firefox","run":"firefox"}`, `{"cmd":"wallpaper","path":"…"}`.

## Manage rules — where new windows go

```fsharp
let myManage =
    manage {
        rule (appIs "firefox")                    (ShiftToWorkspace "2")
        rule (appIs Apps.Spotify.AppId)           (ShiftToWorkspace "9")
        rule (titleContains "Picture-in-Picture") FloatWindow
    }
```

Matchers: `appIs`, `titleContains` (any `WindowInfo -> bool` works).
Actions: `ShiftToWorkspace tag`, `FloatWindow`, `NoAction`. A rule that throws
is skipped (logged), never fatal.

## Input devices

```fsharp
input (inputDevices {
    keyboard {
        layout "us,ru"
        options "grp:alt_shift_toggle"   // Alt+Shift switches layout
        repeatRate 25
        repeatDelay 300
    }
    mouse {
        accelProfile "flat"
        naturalScroll false
    }
    touchpad {
        tap true
        naturalScroll true
        disableWhileTyping true
    }
})
```

Keyboard fields are xkb (`layout`, `variant`, `options`, `model`, `rules`) —
empty string means "xkb default". A layout string that doesn't compile falls
back to the default keymap (logged) instead of leaving you with a dead
keyboard. Mouse/touchpad knobs are libinput: `accelSpeed` (-1..1),
`accelProfile` (`"flat"`/`"adaptive"`), `scrollMethod`
(`"none"`/`"two-finger"`/`"edge"`), `clickMethod`
(`"none"`/`"button-areas"`/`"clickfinger"`), `tapDrag`.

## Bar & omnibox styling

The status bar and the launcher are styled from the same config — the WM
serves their config over the agent socket, so **a save restyles a running bar
live** (colors/segments/font apply on its next poll; position/thickness apply
when the bar starts). The omnibox reads its styling each time it opens.

**The launcher is in-process too.** Bind `ToggleOmnibox` to open/close the
built-in launcher as a centered overlay the WM draws itself — no separate
process, and it picks up its styling from the live config each time it opens.
`Spawn "wtf-omnibox"` still runs the standalone client if you prefer (or want it
on another compositor). Both share the exact same look + fuzzy launcher.

The bar polls every `refreshMs` (default **300 ms**) but only repaints when its
visible content actually changed, so a snappy cadence stays cheap — the clock
digit ticking over is the only idle redraw.

**Embedded vs standalone.** By default (`embedded true`) the WM renders each bar
**in-process** — no separate `wtf-bar` process, no socket round-trip; the WM
reserves the bar's strip and paints it directly, reading its state in-process.
Set `embedded false` to fall back to the standalone `wtf-bar` layer-shell client
(launch it yourself from `startup`) — useful to run the bar on another compositor
or to isolate it from the WM. Third-party bars (waybar, etc.) are always
external and unaffected. `embedded` is a host-only knob (it is not re-read on a
live reload the way styling is — flipping it takes effect when the bar next
starts / the WM restarts).

```fsharp
bar (barConfig {
    position Bottom               // Top | Bottom | Left | Right
    height 32                     // thickness (bar width for Left/Right)
    embedded true                 // render in-process (default); false = standalone wtf-bar
    refreshMs 300                 // poll/redraw cadence (ms); repaint only on change
    glass true                    // frost: backdrop-blur behind the bar
    accent "#f38ba8"
    background "#11111bcc"        // #rrggbbaa — translucent
    left  [ Workspaces; Label "λ" ]
    right [ Player; Battery; Clock "ddd HH:mm" ]
})

omnibox (omniboxConfig {
    width 720
    glass true                    // frost the launcher too
    selection "#f38ba8"
    prompt "λ"
    placeholder "run…"
})
```

**Colors take a palette function too.** Any color knob accepts either a fixed
hex or `(fun p -> …)` reading the wallpaper palette (resolved host-side each
snapshot, so it tracks a dynamic wallpaper live):

```fsharp
bar (barConfig {
    background (fun p -> Color.toHexA 0.45 p.Base)      // translucent, from wallpaper
    accent     (fun p -> Palette.accent 0.5 p |> Color.toHex)
    foreground (fun p -> Color.toHex p.Text)
})
```

`Color.toHexA a c` overrides alpha (0..1). Keep backgrounds calm and pull
accents from the palette — see [Appearance → Bar & omnibox](appearance.md#bar--omnibox).

**Multiple bars** — give each a name; embedded bars all render in-process (no
startup entries needed). For a `embedded false` bar, launch one `wtf-bar` process
per entry (the `--name` flag picks the entry; no flag = the first):

```fsharp
bars [
    barConfig { name "top" }                                  // embedded (in-process)
    barConfig { name "side"; position Left; height 34 }       // embedded
    barConfig { name "ext"; embedded false }                  // standalone wtf-bar
]
// only the embedded false bars need a launcher:
// startup [ "wtf-bar --name ext" ]
```

`Left`/`Right` bars render vertically: workspace pills stack top-to-bottom,
the `right` list stacks from the bottom, and the clock splits `HH:mm` into
two lines. Long segments (`Player`, `Network`) render compact glyphs there.

Bar segments: `Workspaces`, `Clock "<.NET time format>"`, `Battery`,
`Network`, `Player` (MPRIS now-playing), `Label "<text>"`, plus two custom
widgets (below). Bar knobs also include `refreshMs` (int, 50–5000), `glass`
(bool), and `embedded` (bool, default true — in-process vs standalone `wtf-bar`).
Omnibox knobs: `width`, `height`, `rowHeight`,
`fontSize`, `background`, `inputBackground`, `foreground`, `dim`, `selection`,
`prompt`, `promptColor`, `placeholder`, `glass`. Every color knob takes a
`#rrggbb`/`#rrggbbaa` string **or** a `(fun p -> …)` palette function.

### Custom widgets

Two segment kinds let you show your own content:

```fsharp
bar (barConfig {
    right [
        // F# widget: a function of the live WM+desktop state, evaluated by the
        // WM each snapshot. Type-safe, hot-reloads, zero external process.
        Custom (fun c -> sprintf "%d win" c.Windows.Length)
        Custom (fun c ->
            match c.Battery with
            | Some (pct, _) when pct < 20.0 -> sprintf "LOW %.0f%%" pct
            | _ -> "")
        // Shell widget (waybar/polybar-style): the WM polls the command every
        // `intervalMs` and shows its first stdout line. `script exec ms` is sugar
        // for `Script { Exec = exec; IntervalMs = ms }`.
        script "~/bin/cpu.sh" 2000
        Clock "HH:mm"
    ]
})
```

`Custom` receives a **`BarContext`** — a flat read-model of the live state:
`Windows` (list), `FocusedTitle`/`FocusedApp`, `Workspace`, `OccupiedTags`,
`Battery` (`(percent, state) option`), `Network` (`state option`), `Player`
(`(status, title, artist) option`), `Time`. It runs host-side (respecting the
bar's `refreshMs`); a widget that throws shows empty and is logged once — it can
never break the bar. `Script` runs `Exec` via `/bin/sh -c` on a background
poller with a timeout; a nonzero exit / missing binary / timeout shows empty.
Both render as plain text, so they work on the standalone bar over the socket.

## Dynamic appearance — knobs as functions

Because the config is code, appearance can be a **function of context**
instead of a constant. The context carries the window, its workspace, focus
state, and the wallpaper-derived palette:

```fsharp
// Per-app border colors; everything else falls through to activeBorder.
borderColor (fun ctx ->
    if ctx.Window.AppId = "firefox" then "#ff8800"
    elif ctx.Window.Floating then "#ff00ff"
    elif ctx.Focused then "#89b4fa"
    else "#45475a")

// Terminals slightly transparent, browsers opaque.
windowOpacity (fun ctx ->
    if ctx.Window.AppId = "kitty" && not ctx.Focused then 0.85 else 1.0)
```

`ctx.Palette` is the structured palette extracted from your wallpaper
(`Base`, `Surface`, `Overlay`, `Text`, `Subtext`, `Accents` ramp), so themes
can follow the wallpaper automatically. A function that throws degrades to the
static defaults — logged, never fatal.

## Custom layouts

A layout is a function; register one and it becomes a first-class name:

```fsharp
Registry.register "mytall" (fun nmaster ratio -> Layout.tall nmaster ratio)
// then: defaultLayout "mytall", bind "M-y" (SetLayout "mytall"), wtfctl layout mytall
```

## Custom surfaces — bar & overlay plugins

The same compiled-plugin mechanism that ships custom **layouts** also ships
custom **surfaces**: drop a .NET assembly implementing one of these interfaces
into `~/.config/wtf/plugins/` and the WM renders it in-process, exactly like the
built-in bar/omnibox. (Same loader, same `<Private>false</Private>` WTF.Core
reference rule as a layout plugin — see `examples/`.)

- **`IWtfBarPlugin`** — a non-interactive strip anchored to a screen edge. It
  hands back `Name`, `Anchor`, `Thickness`, `RefreshMs`, and
  `Render: BarContext -> width -> height -> byte[]` (BGRA pixels). The WM
  reserves its strip from the tiling area and repaints on its cadence. It fills
  any free bar slot (max 4 total, shared with `bars`).
- **`IWtfOverlayPlugin`** — an interactive centered panel (a spotlight / command
  palette). It hands back `Name`, `Width`, `Height`, `Open()`, `OnKey` (mods,
  keysym, codepoint → `OverlayRedraw` / `OverlayConsumed` / `OverlayClose`), and
  `Render: width -> height -> byte[]`. While shown it is modal — every key routes
  to it. Open it by name with `bind "M-o" (ToggleOverlay "myspotlight")`.

Colliding names replace last-wins (logged), so a plugin overlay named `"omnibox"`
overrides the built-in launcher. Third-party bars/launchers can instead stay
external layer-shell clients over the agent socket — both paths are supported.

## Custom effect strategies

`borderColor` / `windowOpacity` are the *built-in* per-window appearance hooks.
An **effect strategy** is the pluggable version: a named function
`RenderContext -> WindowEffect list` that decides which per-window effects apply,
selected by name so a plugin can ship it:

```fsharp
effectStrategy "dim-unfocused"    // default is "none" (no extra effects)
```

The name resolves against the effect registry, which always has the built-in
`"none"` (identity — this is byte-for-byte today's behavior) plus any strategies
contributed by plugins. An unknown name degrades to `"none"`. A strategy returns
zero or more `WindowEffect`s per window, layered **on top of** the static
appearance (a window that stops matching reverts cleanly):

- **`SetOpacity f`** — window opacity `0..1` (clamped).
- **`SetBorderColor "#hex"`** — border color (bad hex is ignored, keeping the
  static color).

Ship one from `~/.config/wtf/plugins/` by implementing **`IWtfEffectPlugin`**
(same loader / `<Private>false</Private>` rule as layouts and surfaces):

```fsharp
type MyEffects() =
    interface IWtfEffectPlugin with
        member _.Name = "MyEffects"
        member _.Strategies =
            [ "dim-unfocused",
                (fun ctx -> if ctx.Focused then [] else [ SetOpacity 0.6 ])
              "paint-firefox",
                (fun ctx ->
                    if ctx.Window.AppId = "firefox" then [ SetBorderColor "#ff8800" ] else []) ]
```

**Honest scope:** GPU effects (blur, shadow, corner radius) are fixed in the C /
scenefx layer and are *not* expressed here — a strategy composes the per-window
primitives the WM can already drive per window (opacity, border color). New
primitives are added additively as new `WindowEffect` cases. A throwing strategy
contributes no effects (logged, never fatal), like the appearance functions.

## Safe mode

If the session crash-loops, the wrapper relaunches WTF once with
`WTF_SAFE_MODE=1`: your `config.fsx` is **skipped** (built-in defaults),
eye-candy is off, startup apps don't run. Fix the config, then `M-S-r` or log
back in normally. See [Troubleshooting](troubleshooting.md).
