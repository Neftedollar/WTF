#ifndef WTF_H
#define WTF_H
#include <stdint.h>

/*
 * The C ABI contract between the wlroots compositor shim (the "body") and the
 * F# brain. Deliberately tiny and flat: integers, rectangles, C strings. No
 * wlroots type ever crosses this line, so the F# side stays 100% safe code and
 * is immune to wlroots ABI churn — only this shim recompiles on a wlroots bump.
 *
 * Direction of calls:
 *   C  -> F#   via the callbacks in `struct wtf_callbacks` (events)
 *   F# -> C    via the wtf_* functions below (imperative effects)
 */

#ifdef __cplusplus
extern "C" {
#endif

/* Events the compositor reports up to the F# brain. */
struct wtf_callbacks {
    /* A toplevel surface was mapped. `id` is a stable handle the shim assigns. */
    void (*view_map)(int id, const char *app_id, const char *title);
    /* A toplevel was unmapped/destroyed. */
    void (*view_unmap)(int id);
    /* A key was pressed. mods = wlr modifier mask, sym = xkb keysym.
     * Return 1 if the brain handled it (compositor swallows it), 0 to forward. */
    int  (*key)(uint32_t mods, uint32_t sym);
    /* The active output's usable area changed (also fired once at startup).
     * This is the output minus layer-shell exclusive zones; x,y may be non-zero
     * for a top/left bar. */
    void (*output_resize)(int x, int y, int width, int height);
    /* The compositor is up and its WAYLAND_DISPLAY is live. The brain can now
     * spawn startup clients into it. Fired once, just before the event loop. */
    void (*ready)(void);
    /* Fired on the event-loop thread after wtf_command_notify() was called from
     * another thread. The brain drains its command queue here, where mutating
     * state and calling the wtf_* ops below is safe. */
    void (*drain)(void);
};

/* Register callbacks and run the compositor event loop. Blocks until quit.
 * Returns the process exit code. */
int wtf_run(struct wtf_callbacks cbs);

/* ---- imperative effects the F# brain issues ---- */

/* Position and size a mapped view (the result of a layout's arrange). */
void wtf_configure(int id, int x, int y, int width, int height);
/* Give keyboard focus to a view and raise it. */
void wtf_focus(int id);
/* Ask a view to close. */
void wtf_close(int id);
/* Flip a view's fullscreen protocol state (xdg or xwayland). on != 0 enters
 * fullscreen and hides its border; 0 leaves it. Full-screen *positioning* is
 * still driven by the brain via wtf_configure (it sends the Screen rect). */
void wtf_set_fullscreen(int id, int on);
/* Spawn a program via `/bin/sh -c cmd`. */
void wtf_spawn(const char *cmd);
/* Stop the compositor event loop. */
void wtf_quit(void);

/* Wake the event loop from ANOTHER thread so it fires the `drain` callback.
 * The only wtf_* function that is safe to call off the loop thread. */
void wtf_command_notify(void);

/* ---- live appearance knobs (driven by the brain / agent via the API) ---- */
/* Animation easing factor per frame, 0.01..1.0 (higher = snappier). */
void wtf_set_anim_speed(double speed);
/* Opacity of unfocused windows, 0.0..1.0. */
void wtf_set_inactive_opacity(double opacity);
/* Window border thickness in pixels. */
void wtf_set_border_width(int width);
/* Border color; active != 0 sets the focused color, 0 the unfocused. RGB 0..1. */
void wtf_set_border_color(int active, double r, double g, double b);
/* ---- E1 per-window style overrides (driven by the brain per window) ---- */
/* Per-window border color override (RGBA 0..1); authoritative until cleared. */
void wtf_set_window_border_color(int id, double r, double g, double b, double a);
/* Per-window opacity target override (0..1); animates via the existing lerp. */
void wtf_set_window_opacity(int id, double opacity);
/* Clear both per-window overrides; window reverts to the global focus path. */
void wtf_clear_window_style(int id);
/* Rounded-corner radius in pixels (0 = sharp). */
void wtf_set_corner_radius(int radius);
/* Backdrop blur: enable/disable + radius and pass count (<=0 keeps current). */
void wtf_set_blur(int enabled, int radius, int passes);

/* ---- wallpaper (BACKGROUND layer, drawn below layer-shell bg clients) ---- */
/* Set an image wallpaper from raw RGBA pixels. `rgba` is exactly width*height*4
 * bytes in ImageSharp Rgba32 memory order (R,G,B,A per pixel), stride width*4
 * (= DRM_FORMAT_ABGR8888). The pixels are copied synchronously; the caller does
 * NOT own the buffer after the call returns. The image is shown in the
 * BACKGROUND scene tree, scaled to fill the whole output. The F# brain has
 * already scaled to the output size; the node is dest-sized to the output box. */
void wtf_set_wallpaper(const unsigned char *rgba, int width, int height);
/* Set a solid-color wallpaper (a scene-rect sized to the output). RGB in 0..1. */
void wtf_set_wallpaper_color(double r, double g, double b);
/* Remove any wallpaper (image buffer + color rect). */
void wtf_clear_wallpaper(void);

/* ---- input configuration (keyboard xkb/repeat + libinput pointer/touchpad) ---- */

/* Set the xkb rule-names and key-repeat applied to every keyboard (existing and
 * future). An empty string ("") for any of rules/model/layout/variant/options
 * means "use the xkb compile default for that field" (passed as NULL). The
 * layout-switch behaviour (e.g. options "grp:alt_shift_toggle" with layout
 * "us,ru") is handled entirely inside xkb once the options reach it.
 * repeat_rate is keys/sec, repeat_delay is the ms before repeat starts. */
void wtf_set_keymap(const char *rules, const char *model, const char *layout,
                    const char *variant, const char *options,
                    int repeat_rate, int repeat_delay);

/* Per-device libinput knobs, applied to each matching pointer/touchpad on
 * attach and re-applied to already-attached devices when this is called.
 * int fields use a sentinel: -1 = leave libinput's default; 0/1 = off/on; for
 * scroll_method 0=none/1=two-finger/2=edge; for click_method 0=none/
 * 1=button-areas/2=clickfinger; for *_accel_profile 0=flat/1=adaptive. The
 * accel speeds are doubles in -1.0..1.0 and are always applied (0.0 neutral).
 * Field order/types MUST match the F# [<StructLayout(Sequential)>] mirror. */
struct wtf_libinput_config {
    double mouse_accel;          /* -1.0..1.0 (always applied; 0.0 neutral) */
    int    mouse_accel_profile;  /* -1 leave / 0 flat / 1 adaptive */
    int    mouse_natural_scroll; /* -1 leave / 0 off / 1 on */
    int    tap;                  /* -1 / 0 / 1 (tap-to-click; touchpad) */
    int    tap_drag;             /* -1 / 0 / 1 (tap-and-drag; touchpad) */
    int    tp_natural_scroll;    /* -1 / 0 / 1 (touchpad) */
    int    dwt;                  /* -1 / 0 / 1 (disable-while-typing) */
    int    scroll_method;        /* -1 leave / 0 none / 1 two-finger / 2 edge */
    int    click_method;         /* -1 leave / 0 none / 1 button-areas / 2 clickfinger */
    double tp_accel;             /* -1.0..1.0 (touchpad) */
    int    tp_accel_profile;     /* -1 leave / 0 flat / 1 adaptive */
};
void wtf_set_libinput_config(struct wtf_libinput_config cfg);

#ifdef __cplusplus
}
#endif
#endif /* WTF_H */
