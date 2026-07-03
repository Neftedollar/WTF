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
        bind "M-p"      (once (Spawn "wtf-omnibox"))   // singleton launch
        bind "M-j"      (Focus NextWindow)
        bind "M-1"      (SwitchWorkspace "1")
        bind "M-S-r"    ReloadConfig
    }
```

Chord syntax and the full command vocabulary: [Keybindings](keybindings.md).
Remember: `keys myKeys` **replaces** the built-in map.

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
live** (colors/segments/font apply on its next poll, ~1 s; position/thickness
apply when the bar starts). The omnibox reads its styling each time it opens.

```fsharp
bar (barConfig {
    position Bottom               // Top | Bottom | Left | Right
    height 32                     // thickness (bar width for Left/Right)
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

**Multiple bars** — give each a name and launch one `wtf-bar` process per
entry (the `--name` flag picks the entry; no flag = the first):

```fsharp
bars [
    barConfig { name "top" }
    barConfig { name "side"; position Left; height 34 }
]
// startup [ "wtf-bar"; "wtf-bar --name side"; ... ]
```

`Left`/`Right` bars render vertically: workspace pills stack top-to-bottom,
the `right` list stacks from the bottom, and the clock splits `HH:mm` into
two lines. Long segments (`Player`, `Network`) render compact glyphs there.

Bar segments: `Workspaces`, `Clock "<.NET time format>"`, `Battery`,
`Network`, `Player` (MPRIS now-playing), `Label "<text>"`. Bar knobs also
include `glass` (bool). Omnibox knobs: `width`, `height`, `rowHeight`,
`fontSize`, `background`, `inputBackground`, `foreground`, `dim`, `selection`,
`prompt`, `promptColor`, `placeholder`, `glass`. Every color knob takes a
`#rrggbb`/`#rrggbbaa` string **or** a `(fun p -> …)` palette function.

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

## Safe mode

If the session crash-loops, the wrapper relaunches WTF once with
`WTF_SAFE_MODE=1`: your `config.fsx` is **skipped** (built-in defaults),
eye-candy is off, startup apps don't run. Fix the config, then `M-S-r` or log
back in normally. See [Troubleshooting](troubleshooting.md).
