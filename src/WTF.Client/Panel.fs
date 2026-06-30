namespace WTF.Client

open System
open System.Runtime.InteropServices

/// The P/Invoke surface to libwtf_panel.so. Mirrors compositor/wtf-panel.h
/// exactly, and the Ffi.fs / Program.fs pattern from the host: a Sequential
/// callbacks struct of four fn-ptrs, a blittable config struct, concrete delegate
/// types turned into function pointers via Marshal.GetFunctionPointerForDelegate
/// and rooted with GC.KeepAlive for the lifetime of the run.
module Panel =

    /// Matches `struct wtf_panel_callbacks` — four C function pointers by value.
    [<StructLayout(LayoutKind.Sequential)>]
    type Callbacks =
        struct
            val mutable Render: nativeint
            val mutable Key: nativeint
            val mutable Configure: nativeint
            val mutable Closed: nativeint
        end

    /// Matches `struct wtf_panel_config`. `ns` is an LPStr (ASCII, like wtf_spawn);
    /// every other field is an int so the struct passes by value with no custom
    /// marshaller beyond the string pointer.
    [<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)>]
    type Config =
        struct
            [<MarshalAs(UnmanagedType.LPStr)>]
            val mutable Ns: string
            val mutable Layer: int
            val mutable Anchor: int
            val mutable Width: int
            val mutable Height: int
            val mutable ExclusiveZone: int
            val mutable Keyboard: int
            val mutable MarginTop: int
            val mutable MarginRight: int
            val mutable MarginBottom: int
            val mutable MarginLeft: int
        end

    // Delegate types for the C -> F# callbacks (concrete/non-generic so the
    // reverse-pinvoke thunk is statically known — AOT-friendly like the host).
    type RenderDelegate = delegate of nativeint * int * int * int -> unit
    type KeyDelegate = delegate of uint32 * uint32 -> unit
    type ConfigureDelegate = delegate of int * int -> unit
    type ClosedDelegate = delegate of unit -> unit

    [<Literal>]
    let private Lib = "wtf_panel"

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int wtf_panel_init(Config cfg, Callbacks cbs)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_panel_request_redraw()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_panel_set_exclusive(int zone)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int wtf_panel_run()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_panel_quit()

    /// Layer-shell layer enum (matches the protocol / the C side numeric values).
    [<Literal>]
    let LayerBackground = 0
    [<Literal>]
    let LayerBottom = 1
    [<Literal>]
    let LayerTop = 2
    [<Literal>]
    let LayerOverlay = 3

    /// Anchor bitfield (matches the layer_surface anchor enum).
    [<Literal>]
    let AnchorTop = 1
    [<Literal>]
    let AnchorBottom = 2
    [<Literal>]
    let AnchorLeft = 4
    [<Literal>]
    let AnchorRight = 8

    /// keyboard_interactivity values.
    [<Literal>]
    let KeyboardNone = 0
    [<Literal>]
    let KeyboardExclusive = 1
    [<Literal>]
    let KeyboardOnDemand = 2

    /// A live panel: keeps the delegates rooted (GC.KeepAlive equivalent) for the
    /// whole run so the C side's function pointers never dangle. Build it with the
    /// F# callbacks, call `init`, then `run` (blocks). `requestRedraw`/`quit`/
    /// `setExclusive` forward to the C side.
    type Handle(render, key, configure, closed) =
        let dRender = RenderDelegate(render)
        let dKey = KeyDelegate(key)
        let dConfigure = ConfigureDelegate(configure)
        let dClosed = ClosedDelegate(closed)

        let mutable cbs = Callbacks()
        do
            cbs.Render <- Marshal.GetFunctionPointerForDelegate dRender
            cbs.Key <- Marshal.GetFunctionPointerForDelegate dKey
            cbs.Configure <- Marshal.GetFunctionPointerForDelegate dConfigure
            cbs.Closed <- Marshal.GetFunctionPointerForDelegate dClosed

        /// Connect + bind + create the layer surface. Returns 0 on success, <0 on
        /// no display / missing wl_shm / missing zwlr_layer_shell_v1.
        member _.Init(cfg: Config) : int = wtf_panel_init (cfg, cbs)

        /// Run the dispatch loop (blocks until quit / display gone).
        member _.Run() : int =
            let rc = wtf_panel_run ()
            GC.KeepAlive dRender
            GC.KeepAlive dKey
            GC.KeepAlive dConfigure
            GC.KeepAlive dClosed
            rc

        member _.RequestRedraw() = wtf_panel_request_redraw ()
        member _.SetExclusive(zone: int) = wtf_panel_set_exclusive zone
        member _.Quit() = wtf_panel_quit ()
