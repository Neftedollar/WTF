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
/* Rounded-corner radius in pixels (0 = sharp). */
void wtf_set_corner_radius(int radius);
/* Backdrop blur: enable/disable + radius and pass count (<=0 keeps current). */
void wtf_set_blur(int enabled, int radius, int passes);

#ifdef __cplusplus
}
#endif
#endif /* WTF_H */
