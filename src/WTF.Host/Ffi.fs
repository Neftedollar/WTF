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

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_fullscreen(int id, int on)

    // ---- input configuration (keyboard xkb/repeat + libinput pointer/touchpad) ----

    /// Mirrors `struct wtf_libinput_config` in compositor/wtf.h EXACTLY. Fully
    /// blittable (only int/double) so it passes by value with no custom marshaller.
    /// int sentinels: -1 = leave libinput default; 0/1 = off/on; 2 = third option.
    /// Field ORDER and TYPES must match the C struct byte-for-byte.
    [<StructLayout(LayoutKind.Sequential)>]
    type LibinputConfig =
        struct
            val mutable MouseAccel: float          // off 0  : -1.0..1.0 (0.0 neutral)
            val mutable MouseAccelProfile: int     // off 8  : -1 / 0 flat / 1 adaptive
            val mutable MouseNaturalScroll: int    // off 12 : -1 / 0 / 1
            val mutable Tap: int                   // off 16 : -1 / 0 / 1
            val mutable TapDrag: int               // off 20 : -1 / 0 / 1
            val mutable TpNaturalScroll: int       // off 24 : -1 / 0 / 1
            val mutable Dwt: int                   // off 28 : -1 / 0 / 1
            val mutable ScrollMethod: int          // off 32 : -1 / 0 none / 1 2fg / 2 edge
            val mutable ClickMethod: int           // off 36 : -1 / 0 none / 1 areas / 2 clickfinger
            val mutable TpAccel: float             // off 40 : -1.0..1.0
            val mutable TpAccelProfile: int        // off 48 : -1 / 0 flat / 1 adaptive
        end

    /// Empty string ("") => that xkb_rule_names field is NULL (xkb default) on the
    /// C side. repeat_rate keys/sec, repeat_delay ms. Ansi like wtf_spawn (ASCII).
    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)>]
    extern void wtf_set_keymap(string rules, string model, string layout,
                               string variant, string options,
                               int repeat_rate, int repeat_delay)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_libinput_config(LibinputConfig cfg)

    // ---- wallpaper (BACKGROUND layer) ----
    // The raw pixel buffer is `width*height*4` bytes, ABGR8888 in DRM terms — i.e.
    // byte order R,G,B,A (ImageSharp Rgba32 memory order), stride = width*4. The C
    // side COPIES the pixels synchronously, so the default `byte[]` marshalling
    // (pin for the call, pass as `const unsigned char *`) is correct: no GCHandle,
    // no ownership after the call returns.
    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_wallpaper(byte[] rgba, int width, int height)

    /// Solid-color wallpaper subset — a scene-rect sized to the output. RGB 0..1.
    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_set_wallpaper_color(double r, double g, double b)

    /// Remove any wallpaper (config has none / image failed to load).
    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void wtf_clear_wallpaper()
