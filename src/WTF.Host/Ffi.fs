namespace WTF.Host

open System.Runtime.InteropServices

/// The P/Invoke surface to libwtf_shim.so. Mirrors compositor/wtf.h exactly.
module Ffi =

    /// Matches `struct wtf_callbacks` — four C function pointers, by value.
    [<StructLayout(LayoutKind.Sequential)>]
    type Callbacks =
        struct
            val mutable ViewMap: nativeint
            val mutable ViewUnmap: nativeint
            val mutable Key: nativeint
            val mutable OutputResize: nativeint
            val mutable Ready: nativeint
            val mutable Drain: nativeint
        end

    // Delegate types for the callbacks the C side invokes (C -> F#).
    type ViewMapDelegate = delegate of int * nativeint * nativeint -> unit
    type ViewUnmapDelegate = delegate of int -> unit
    type KeyDelegate = delegate of uint32 * uint32 -> int
    type OutputResizeDelegate = delegate of int * int * int * int -> unit
    type ReadyDelegate = delegate of unit -> unit
    type DrainDelegate = delegate of unit -> unit

    [<Literal>]
    let private Lib = "wtf_shim"

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int wtf_run(Callbacks cbs)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_configure(int id, int x, int y, int width, int height)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_focus(int id)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_close(int id)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern void wtf_spawn(string cmd)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_quit()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_command_notify()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_anim_speed(double speed)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_inactive_opacity(double opacity)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_border_width(int width)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_border_color(int active, double r, double g, double b)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_corner_radius(int radius)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_blur(int enabled, int radius, int passes)
