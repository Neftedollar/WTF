namespace SpiralLayout

// =============================================================================
// EXAMPLE WTF layout plugin — a Fibonacci / spiral tiling layout.
//
// This is the shipped TEMPLATE for the ".NET as a platform" extension point
// (#13): a compiled assembly that references WTF.Core and implements
// `IWtfLayoutPlugin`. Build it, copy SpiralLayout.dll into ~/.config/wtf/plugins/,
// and the "spiral" layout is available by name, exactly like a built-in.
//
// F# is the shipped template, but ANY .NET language works identically — a C#
// class library with the same Private=false WTF.Core reference implementing
// IWtfLayoutPlugin would behave the same. See README.md.
// =============================================================================

open WTF.Core

module Layouts =

    /// A Fibonacci spiral: the focused (first) window takes half the area, then
    /// each subsequent window takes half of the REMAINING area, rotating the cut
    /// direction right -> down -> left -> up so the windows wind inward. The last
    /// window absorbs whatever rectangle is left. A pure `Layout<WindowId>`:
    /// (Rect -> Stack<WindowId> -> (WindowId * Rect) list). Gaps are applied by
    /// World.arrange (Layout.withGaps), so this needs no gap handling.
    let spiral: Layout<WindowId> =
        fun area s ->
            // dir cycles 0..3: 0=window takes LEFT half, 1=TOP, 2=RIGHT, 3=BOTTOM.
            let rec go dir area ws =
                match ws with
                | [] -> []
                | [ w ] -> [ w, area ]   // last window: the whole remainder
                | w :: rest ->
                    let win, rem =
                        match dir % 4 with
                        | 0 -> let l, r = Rect.splitVertical 0.5 area in l, r
                        | 1 -> let t, b = Rect.splitHorizontal 0.5 area in t, b
                        | 2 -> let l, r = Rect.splitVertical 0.5 area in r, l
                        | _ -> let t, b = Rect.splitHorizontal 0.5 area in b, t
                    (w, win) :: go (dir + 1) rem rest
            go 0 area (Stack.toList s)

    /// A LayoutFactory ignores nmaster/ratio (the spiral is parameter-free).
    let spiralFactory: LayoutFactory = fun _nmaster _ratio -> spiral

/// The plugin entry point: a parameterless-ctor class the loader discovers by
/// reflection, instantiates, and whose `.Layouts` it registers.
type SpiralPlugin() =
    interface IWtfLayoutPlugin with
        member _.Name = "SpiralLayout"
        member _.Layouts = [ "spiral", Layouts.spiralFactory ]
