#ifndef WTF_PANEL_H
#define WTF_PANEL_H
#include <stdint.h>

/*
 * The C ABI contract between libwtf_panel (the Wayland/layer-shell/shm/keyboard
 * "body" for WTF's own client apps — the status bar and the omnibox launcher)
 * and the F# brain (src/WTF.Client). It deliberately mirrors compositor/wtf.h:
 * tiny and flat — only ints/uint32/ptrs/const char* cross the line, so the F#
 * side stays 100% safe code and is immune to Wayland-client churn.
 *
 * Direction of calls:
 *   C  -> F#   via the callbacks in `struct wtf_panel_callbacks` (events)
 *   F# -> C    via the wtf_panel_* functions below (imperative effects)
 *
 * Numeric enums match the zwlr_layer_shell_v1 protocol so F# passes raw ints
 * with no extra mapping table.
 */

#ifdef __cplusplus
extern "C" {
#endif

/* layer: 0 bg 1 bottom 2 top 3 overlay (== zwlr_layer_shell_v1 enum).
   anchor bitfield: top=1 bottom=2 left=4 right=8 (== layer_surface anchor enum).
   keyboard: 0 none 1 exclusive 2 on_demand. Numeric values match the protocol so
   F# passes raw ints — no extra mapping table. */
struct wtf_panel_callbacks {
    /* C->F#: draw w*h ARGB8888 into buf (stride bytes/row). Loop-thread only. */
    void (*render)(void *buf, int width, int height, int stride);
    /* C->F#: key press. sym = xkb keysym; codepoint = Unicode scalar (0 if none). */
    void (*key)(uint32_t keysym, uint32_t codepoint);
    /* C->F#: layer_surface configured to width x height (buffer (re)created). */
    void (*configure)(int width, int height);
    /* C->F#: surface closed / display gone; F# should quit. */
    void (*closed)(void);
};

struct wtf_panel_config {
    const char *ns;            /* layer-shell namespace: "wtf-bar" | "wtf-omnibox" */
    int layer;                 /* TOP=2 (bar) | OVERLAY=3 (omnibox) */
    int anchor;                /* bar: top|left|right=1|4|8; omnibox: 0 (centered) */
    int width, height;         /* 0 => compositor decides that axis */
    int exclusive_zone;        /* bar: =height; omnibox: 0; -1 ignore exclusivity */
    int keyboard;              /* bar: 0 none; omnibox: 1 exclusive */
    int margin_top, margin_right, margin_bottom, margin_left;
};

/* Connect wl_display, bind globals, create wl_surface + zwlr_layer_surface_v1,
   initial commit w/o buffer. Returns 0 ok; <0 if no display / missing wl_shm /
   missing zwlr_layer_shell_v1 (F# logs + clean error exit). */
int  wtf_panel_init(struct wtf_panel_config cfg, struct wtf_panel_callbacks cbs);
void wtf_panel_request_redraw(void);   /* schedule render via frame callback */
void wtf_panel_set_exclusive(int zone);/* live bar-height change */
int  wtf_panel_run(void);              /* wl_display_dispatch loop; blocks */
void wtf_panel_quit(void);             /* stop loop (from key/closed) */

#ifdef __cplusplus
}
#endif
#endif /* WTF_PANEL_H */
