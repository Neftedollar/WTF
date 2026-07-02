# Appearance & ricing

Everything here lives in `config { … }` and hot-reloads on save. Most knobs
are also live-tunable without touching the config: `wtfctl gaps 16`,
`wtfctl corners 12`, `wtfctl border active "#ff8800"`, `wtfctl blur on`.

## Borders

```fsharp
borderWidth 2
activeBorder "#89b4fa"
inactiveBorder "#45475a"
```

WTF draws one uniform border per window and asks clients **not** to draw their
own decorations (xdg-decoration + KDE server-decoration protocols). Terminals
and Qt apps comply fully; GTK4/libadwaita apps keep their headerbar (they have
no server-side mode) but drop their shadows when tiled, so windows still pack
edge-to-edge.

Per-window/conditional border colors: the `borderColor` function — see
[Configuration → Dynamic appearance](configuration.md#dynamic-appearance--knobs-as-functions).

## Rounded corners & blur

```fsharp
cornerRadius 10
blur true        // backdrop blur behind translucent windows
```

Both are rendered by scenefx. Blur only shows through translucent surfaces
(e.g. a terminal with opacity < 1, or `inactiveOpacity` below 1.0).

## Drop shadows (macOS-style)

```fsharp
shadow true
shadowSigma 24.0        // blur spread, px
shadowColor "#000000"
shadowOpacity 0.45      // 0..1
shadowOffset 0 8        // (dx, dy) px — light from above => dy > 0
```

A soft shadow is drawn under every window (below its border), moving with the
window's animations. The defaults are tuned to the macOS look: a wide, dark,
slightly downward shadow. All eye-candy (shadows, blur, corners, animations)
is forced off in safe mode.

## Opacity & animations

```fsharp
inactiveOpacity 0.92    // unfocused windows slightly transparent
animSpeed 0.30          // window slide/fade easing; 1.0 = instant
```

Windows ease into their tile positions and fade on open. Per-window opacity
rules use the `windowOpacity` function.

## Wallpapers

```fsharp
wallpaper (Color "#1e1e2e")                       // solid color
wallpaper (Image ("~/pics/bg.png", Fill))         // image
wallpaper (Dynamic ("~/pics/catalina.heic", Fill)) // time-of-day dynamic
```

Scaling modes: `Fill` (cover, crop overflow), `Fit` (contain, letterbox),
`Stretch`, `Center`, `Tile`. Images are decoded in the host and re-scaled
automatically when the output size changes. A missing or broken file logs and
falls back — it never breaks startup.

### Dynamic wallpapers (`.heic`)

WTF natively supports the **macOS dynamic wallpaper format**: a single `.heic`
containing many frames spanning the day (any wallpaper from macOS or sites
like dynamicwallpaper.club). Decoding uses the system `libheif`.

- The frame matching the current time of day is shown; frames are spread
  evenly across 24 h in stored order.
- WTF switches frames automatically at each boundary — no cron, no extra
  daemon.
- The **color palette follows the current frame**, so `ctx.Palette`-driven
  themes shift through the day with the wallpaper.
- No `libheif` on the machine → logged, wallpaper cleared, session unaffected.

### The wallpaper palette

Whatever wallpaper you set, WTF extracts a structured palette from it
(deterministic median-cut → semantic roles):

```fsharp
borderColor (fun ctx -> Color.toHex (ctx.Palette.Accents 0.3))
```

`Palette` fields: `Base`, `Surface`, `Overlay`, `Text`, `Subtext`, and
`Accents` — a ramp you sample with a number in 0–1 instead of a fixed list.

## Gaps

```fsharp
gaps 8
```

Inner gap around every tile, in px. Live: `M-equal` / `M-minus`, or
`wtfctl gaps <n>` / `wtfctl gaps inc` / `wtfctl gaps dec`.

## HiDPI

```fsharp
scale 2.0
```

Physical pixels per logical pixel; layouts and gaps are computed in logical
pixels.
