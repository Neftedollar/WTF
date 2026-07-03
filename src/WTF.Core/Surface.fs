namespace WTF.Core

// =============================================================================
// In-process SURFACE plugins — the ".NET as a platform" extension point (#13),
// generalized from layouts to surfaces. New OPTIONAL interfaces, siblings to
// `IWtfLayoutPlugin`: a plugin MAY implement them; the loader (WTF.Plugins)
// discovers them in the SAME reflective scan (IsAssignableFrom) and registers
// them into `SurfaceRegistry`, which the host reads to drive `wtf_set_bar` /
// `wtf_set_overlay`. The built-in bar and omnibox are the host's own surfaces;
// these interfaces are the USER extension point (a third-party spotlight, a
// custom bar) — exactly as `Registry.register` is for layouts.
//
// FROZEN-ABI DISCIPLINE (see Plugin.fs): keep each interface frozen — a new
// capability arrives as ANOTHER new interface, never as a member here, so an
// already-compiled plugin never stops satisfying the contract.
//
// CORE STAYS PURE: these are JUST TYPES. Pixels cross the boundary as a raw
// `byte[]` (BGRA8888, width*height*4, ARGB in DRM terms) — no ImageSharp, no IO
// in Core. A plugin owns its own rendering (it may use whatever it likes). The
// host copies the bytes straight into a scene buffer.
//
// Compiled AFTER Config.fs because `IWtfBarPlugin.Render` takes the `BarContext`
// read-model (the same flat state a `Custom` bar widget sees).
// =============================================================================

/// Which output edge a bar surface anchors to. Kept separate from the config
/// DSL's `BarPosition` so the plugin ABI does not depend on the config module.
type SurfaceAnchor =
    | AnchorTop
    | AnchorBottom
    | AnchorLeft
    | AnchorRight

/// A non-interactive BAR surface: a pixel strip anchored to a screen edge, its
/// thickness reserved from the tiling area. `Render ctx width height` returns a
/// width*height*4 BGRA byte buffer (short/empty buffers are ignored by the host).
type IWtfBarPlugin =
    /// Human-readable name; also the SurfaceRegistry key (collisions: last wins).
    abstract member Name: string
    abstract member Anchor: SurfaceAnchor
    /// Pixels reserved on the anchored edge (bar width for left/right).
    abstract member Thickness: int
    /// Desired repaint cadence in ms; the host clamps to a sane floor.
    abstract member RefreshMs: int
    /// Render to `width*height*4` BGRA bytes from the live read-model.
    abstract member Render: BarContext -> int -> int -> byte[]

/// What an overlay's key handler decided for one key press.
type OverlayKeyResult =
    | OverlayRedraw     // state changed — re-render + re-push pixels
    | OverlayConsumed   // handled, no visual change
    | OverlayClose      // dismiss the overlay (host clears it)

/// An interactive centered OVERLAY surface (launcher / spotlight / command
/// palette). Lifecycle driven entirely by the host: `Open()` on show (reset
/// state), `OnKey` for every key while shown (the host swallows them from the
/// focused window), `Render` for pixels. Any side effect (spawning the chosen
/// app) is the plugin's own to perform in-process, exactly as the standalone
/// launcher does — the host only owns the surface + key routing.
type IWtfOverlayPlugin =
    /// Human-readable name; also the SurfaceRegistry key + `ToggleOverlay` target.
    abstract member Name: string
    abstract member Width: int
    abstract member Height: int
    /// Reset transient state; called each time the overlay is shown.
    abstract member Open: unit -> unit
    /// mods (wlr mask), xkb keysym, utf32 codepoint -> what the host should do.
    abstract member OnKey: uint32 -> uint32 -> uint32 -> OverlayKeyResult
    /// Render to `width*height*4` BGRA bytes.
    abstract member Render: int -> int -> byte[]

/// The live registry of in-process surface plugins — mirrors `Registry` for
/// layouts. PURE + process-global; the loader registers at startup, the host
/// reads. Last-registered wins on a name collision (the loader warns). Not
/// thread-safe by design: registration is a one-shot startup phase, before the
/// compositor loop, like `Registry`.
module SurfaceRegistry =

    let private bars = System.Collections.Generic.Dictionary<string, IWtfBarPlugin>()
    let private overlays = System.Collections.Generic.Dictionary<string, IWtfOverlayPlugin>()

    /// True if a bar/overlay is already registered under `name` (loader warns).
    let hasBar name = bars.ContainsKey name
    let hasOverlay name = overlays.ContainsKey name

    let registerBar (p: IWtfBarPlugin) = bars[p.Name] <- p
    let registerOverlay (p: IWtfOverlayPlugin) = overlays[p.Name] <- p

    let tryBar name : IWtfBarPlugin option =
        match bars.TryGetValue name with true, p -> Some p | _ -> None
    let tryOverlay name : IWtfOverlayPlugin option =
        match overlays.TryGetValue name with true, p -> Some p | _ -> None

    let barNames () = bars.Keys |> List.ofSeq |> List.sort
    let overlayNames () = overlays.Keys |> List.ofSeq |> List.sort
    let allBars () : IWtfBarPlugin list = bars.Values |> List.ofSeq

    /// Drop every registration (used by tests to isolate cases).
    let clear () =
        bars.Clear()
        overlays.Clear()
