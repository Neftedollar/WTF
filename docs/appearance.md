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

## Watercolor frames

```fsharp
watercolor true          // turn each window border into a tinted wash
watercolorTint 0.35      // how strongly the frame colour reads over the wash (0..1)
watercolorFrost true     // true = frosted (blurred) backdrop; false = sharp backdrop
watercolorRefraction 0.0 // px of edge lensing (subtle; see the honesty note below)
cornerRadius 10          // pair with rounding for the full look
borderWidth 5            // thin frames read best — this is a wash, not a slab
```

> The name `glass` is **reserved** for a future proper liquid-glass effect
> (see the port issue). Today's effect — tinted frosted frames — is `watercolor`.

`watercolor` turns each window's border into a translucent strip: the backdrop
(wallpaper, neighbours) shows through the frame, washed with the border's own
colour. The focused window keeps its `activeBorder` hue, unfocused frames go
`inactiveBorder` — so focus stays readable *through* the wash. Colours come
straight from your config; nothing is hardcoded.

`watercolorTint` is the wash strength (0..1): `0` = pure see-through backdrop (the
frame all but disappears), `~0.3–0.5` = watercolor (recommended), high values
approach a solid painted frame. `watercolorFrost true` blurs what's behind the
frame (milky, soft — the classic frost); `false` keeps it sharp.

**Honesty note on `watercolorRefraction`:** the rim can *lens* the backdrop (a WTF
displacement shader inside scenefx bends the edge like a convex bead, and a
thinner frame bends harder). It works, but at ordinary desktop DPI with thin
frames the bend is subtle — do not expect the dramatic macOS "liquid glass"
retina look. `0` (off) is the default; `~8–14` adds a faint wobble at the rim
where there is high-contrast content behind the frame. It costs an extra
backdrop copy per frame, so leave it off unless you like what it does on your
screen.

Cost note: the watercolor backdrop is recomputed around every window each frame via
scenefx's per-rect path. WTF logs `Optimized blur buffer not populated; using
the per-rect blur path` once (INFO) — that is expected and harmless: the frame
uses the per-rect path rather than the whole-screen optimized-blur buffer
(which WTF does not set up), and it appears on every GPU, not just Intel. Keep
`borderWidth` modest on weaker GPUs to bound the per-frame cost. Off in safe
mode with the rest of the eye-candy.

## Focus glow

```fsharp
glow true           // the FOCUSED frame emits a halo in its own colour
glowSigma 20.0      // halo spread in px (bigger = softer, wider)
glowIntensity 0.6   // halo strength 0..1
```

The focused window's frame radiates a soft colored halo — "the frame emits
light". The hue is the frame's own colour, so `activeBorder` drives it (change
the theme and the glow follows; a per-window border override glows in that
override's colour). Only the focused window glows; the halo is centered (no
offset — a shadow *falls*, a glow *radiates*) and hugs the frame's rounded
corners. Hidden and fullscreen windows never glow. Rendered as a scenefx
shadow node, so it moves with the window's animations. Forced off in safe mode
with the rest of the eye-candy.

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

#### Contrast frames from the wallpaper

`Palette.contrastAccent` picks the palette color that stands out most against
the wallpaper's ground (`Base`) — perceptual distance in OKLab, gated so the
pick reads as *color*, not gray. On a **monochrome** wallpaper nothing clears
the bar, it returns `None`, and *you* choose the out-of-palette color — that's
`contrastAccentOr`:

```fsharp
borderColor (fun ctx ->
    // From the wallpaper's own palette when it has color to offer;
    // YOUR color when the wallpaper is monochrome (grays, solid dark).
    let accent = ctx.Palette |> Palette.contrastAccentOr (Color.ofHexOr Color.white "#f38ba8")
    // Focused = full contrast; unfocused = the same hue sunk toward Overlay.
    let c = if ctx.Focused then accent else Color.mix 0.7 accent ctx.Palette.Overlay
    Color.toHex c)
```

Change the wallpaper and the frames re-derive themselves; the watercolor tint and
the focus glow follow the same color automatically. Deterministic: the same
wallpaper always produces the same frame color.

## Bar & omnibox

The status bar and launcher are themed from the same `config.fsx` — colors,
segments, fonts, position (all four screen edges), multiple bars. A save
restyles the running bar live. Full reference:
[Configuration → Bar & omnibox styling](configuration.md#bar--omnibox-styling).

### Palette colors (from the wallpaper)

Every bar/omnibox color takes **either** a fixed hex **or** a function of the
wallpaper palette — the same `Palette` the borders read:

```fsharp
bar (barConfig {
    background (fun p -> Color.toHexA 0.45 p.Base)   // translucent, wallpaper-derived
    foreground (fun p -> Color.toHex p.Text)
    accent     (fun p -> Palette.accent 0.5 p |> Color.toHex)  // workspace pills
})
omnibox (omniboxConfig {
    selection   (fun p -> Palette.accent 0.4 p |> Color.toHex)
    promptColor (fun p -> Palette.accent 0.7 p |> Color.toHex)
})
```

`Color.toHexA a c` overrides the alpha (0..1) for a translucent panel. Palette
colors are resolved **host-side each snapshot**, so with a dynamic (`.heic`)
wallpaper the bar/omnibox re-tint through the day — no restart. The wire stays
plain hex, so the clients need no change.

Taste tip: keep the **background** calm (a fixed dark, or `p.Base` which is the
wallpaper's darkest role) and pull **accents** from the palette — a saturated
`p.Base` can make a whole panel vivid. `Palette.accent t` (t in 0..1) samples
the accent ramp; `p.Text`/`p.Subtext` are legible on `p.Base`.

### Glass panels

```fsharp
bar (barConfig { glass true; background (fun p -> Color.toHexA 0.5 p.Base) })
omnibox (omniboxConfig { glass true; background (fun p -> Color.toHexA 0.85 p.Base) })
```

`glass true` makes the compositor **backdrop-blur** behind that panel (scenefx),
so a translucent bar/omnibox frosts what is behind it. Transparency itself is
just the alpha in `background` — `glass` adds the blur. Implemented per
layer-shell namespace: all `wtf-bar` surfaces frost when any bar sets `glass`
(one namespace for the fleet); the omnibox is separate. Off in safe mode with
the rest of the eye-candy.

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
