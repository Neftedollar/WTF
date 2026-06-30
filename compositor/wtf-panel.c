/*
 * libwtf_panel — the thin C "body" for WTF's own Wayland-CLIENT apps (the status
 * bar and the omnibox launcher). It does ONLY Wayland/layer-shell/shm/keyboard
 * plumbing; ALL content + rendering decisions are F# (src/WTF.Client). This is
 * the SAME "F# brain, C body" split as the compositor shim (compositor/wtf-shim.c),
 * applied to the clients — mirroring its `static struct ... g_cb` callback style.
 *
 * Pipeline: connect the running compositor -> bind wl_compositor/wl_shm/
 * zwlr_layer_shell_v1 (CLIENT)/wl_seat->wl_keyboard -> a wl_surface +
 * zwlr_layer_surface_v1 (configurable layer/anchor/size/margins/exclusive/
 * keyboard) -> on configure, (re)create a double-buffered ARGB8888 shm pool and
 * call UP to F# render() to draw the pixels -> attach+damage+commit. Keyboard
 * keys are decoded via xkbcommon and passed UP as (keysym, utf32) so the omnibox
 * F# state machine can edit its query.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>

#include "wlr-layer-shell-unstable-v1-client-protocol.h"
#include "wtf-panel.h"

/* ---- callbacks UP to F# (the C->F# direction), mirroring wtf-shim.c:245 ---- */
static struct wtf_panel_callbacks g_cb;

/* ---- a single global panel instance (each client process drives one) ---- */
struct buffer {
    struct wl_buffer *wl_buffer;
    void *data;       /* mmap'd shm */
    size_t size;      /* bytes */
    int width, height, stride;
    bool busy;        /* held by the compositor (between attach and release) */
};

static struct {
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_compositor *compositor;
    struct wl_shm *shm;
    struct zwlr_layer_shell_v1 *layer_shell;
    struct wl_seat *seat;
    struct wl_keyboard *keyboard;

    struct wl_surface *surface;
    struct zwlr_layer_surface_v1 *layer_surface;

    struct xkb_context *xkb_ctx;
    struct xkb_keymap *xkb_keymap;
    struct xkb_state *xkb_state;

    struct wtf_panel_config cfg;

    int width, height;       /* current configured buffer size */
    struct buffer buffers[2];
    bool configured;
    bool redraw_pending;     /* a render was requested but not yet committed */
    bool frame_pending;      /* a frame callback is in flight */
    bool running;
} P;

/* ---------------------------------------------------------------- shm pool -- */

/* Create an anonymous, sealable file for the shm pool (memfd preferred). */
static int create_anon_file(size_t size) {
    int fd = -1;
#ifdef __linux__
    fd = memfd_create("wtf-panel", MFD_CLOEXEC | MFD_ALLOW_SEALING);
#endif
    if (fd < 0) {
        char tmpl[] = "/tmp/wtf-panel-XXXXXX";
        fd = mkstemp(tmpl);
        if (fd < 0)
            return -1;
        unlink(tmpl);
        long flags = fcntl(fd, F_GETFD);
        if (flags != -1)
            fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
    }
    if (ftruncate(fd, (off_t)size) < 0) {
        close(fd);
        return -1;
    }
    return fd;
}

static void buffer_release(void *data, struct wl_buffer *wl_buffer) {
    (void)wl_buffer;
    struct buffer *b = data;
    b->busy = false;
}
static const struct wl_buffer_listener buffer_listener = { .release = buffer_release };

static void buffer_destroy(struct buffer *b) {
    if (b->wl_buffer)
        wl_buffer_destroy(b->wl_buffer);
    if (b->data && b->data != MAP_FAILED)
        munmap(b->data, b->size);
    memset(b, 0, sizeof(*b));
}

/* (Re)create a buffer at w x h, ARGB8888, stride = w*4. Returns 0 on success. */
static int buffer_create(struct buffer *b, int w, int h) {
    buffer_destroy(b);
    int stride = w * 4;
    size_t size = (size_t)stride * (size_t)h;
    if (size == 0)
        return -1;
    int fd = create_anon_file(size);
    if (fd < 0)
        return -1;
    void *data = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (data == MAP_FAILED) {
        close(fd);
        return -1;
    }
    struct wl_shm_pool *pool = wl_shm_create_pool(P.shm, fd, (int32_t)size);
    struct wl_buffer *wlb = wl_shm_pool_create_buffer(
        pool, 0, w, h, stride, WL_SHM_FORMAT_ARGB8888);
    wl_shm_pool_destroy(pool);
    close(fd);
    if (!wlb) {
        munmap(data, size);
        return -1;
    }
    b->wl_buffer = wlb;
    b->data = data;
    b->size = size;
    b->width = w;
    b->height = h;
    b->stride = stride;
    b->busy = false;
    wl_buffer_add_listener(wlb, &buffer_listener, b);
    return 0;
}

/* Pick a buffer the compositor is not holding, (re)sizing it if stale. */
static struct buffer *next_buffer(void) {
    struct buffer *b = NULL;
    for (int i = 0; i < 2; i++) {
        if (!P.buffers[i].busy) {
            b = &P.buffers[i];
            break;
        }
    }
    if (!b)
        return NULL;
    if (!b->wl_buffer || b->width != P.width || b->height != P.height) {
        if (buffer_create(b, P.width, P.height) != 0)
            return NULL;
    }
    return b;
}

/* ------------------------------------------------------------- frame draw -- */

static void surface_frame_done(void *data, struct wl_callback *cb, uint32_t t);
static const struct wl_callback_listener frame_listener = { .done = surface_frame_done };

/* Draw one frame: ask F# to render into a free buffer, then attach+damage+commit. */
static void draw(void) {
    if (!P.configured || P.width <= 0 || P.height <= 0)
        return;
    struct buffer *b = next_buffer();
    if (!b) {
        /* Both buffers in flight — keep the request pending; a release or the
         * next frame callback will retry. */
        P.redraw_pending = true;
        return;
    }
    P.redraw_pending = false;

    if (g_cb.render)
        g_cb.render(b->data, b->width, b->height, b->stride);

    /* Schedule a frame callback so we don't busy-loop; redraws between frames
     * just set redraw_pending and are coalesced here. */
    if (!P.frame_pending) {
        struct wl_callback *cb = wl_surface_frame(P.surface);
        wl_callback_add_listener(cb, &frame_listener, NULL);
        P.frame_pending = true;
    }

    b->busy = true;
    wl_surface_attach(P.surface, b->wl_buffer, 0, 0);
    wl_surface_damage_buffer(P.surface, 0, 0, b->width, b->height);
    wl_surface_commit(P.surface);
}

static void surface_frame_done(void *data, struct wl_callback *cb, uint32_t t) {
    (void)data;
    (void)t;
    wl_callback_destroy(cb);
    P.frame_pending = false;
    if (P.redraw_pending)
        draw();
}

/* ---------------------------------------------------- layer_surface events -- */

static void layer_surface_configure(void *data,
                                    struct zwlr_layer_surface_v1 *ls,
                                    uint32_t serial, uint32_t w, uint32_t h) {
    (void)data;
    zwlr_layer_surface_v1_ack_configure(ls, serial);

    int nw = (int)w, nh = (int)h;
    /* w/h of 0 means "you chose" — fall back to the requested config size. */
    if (nw == 0)
        nw = P.cfg.width;
    if (nh == 0)
        nh = P.cfg.height;
    if (nw <= 0)
        nw = 1;
    if (nh <= 0)
        nh = 1;

    bool size_changed = (nw != P.width || nh != P.height);
    P.width = nw;
    P.height = nh;
    P.configured = true;

    if (size_changed) {
        /* Drop stale buffers; they are recreated lazily in next_buffer(). */
        for (int i = 0; i < 2; i++)
            if (!P.buffers[i].busy)
                buffer_destroy(&P.buffers[i]);
        if (g_cb.configure)
            g_cb.configure(P.width, P.height);
    }
    draw();
}

static void layer_surface_closed(void *data, struct zwlr_layer_surface_v1 *ls) {
    (void)data;
    (void)ls;
    if (g_cb.closed)
        g_cb.closed();
    P.running = false;
}

static const struct zwlr_layer_surface_v1_listener layer_surface_listener = {
    .configure = layer_surface_configure,
    .closed = layer_surface_closed,
};

/* --------------------------------------------------------------- keyboard -- */

static void kb_keymap(void *data, struct wl_keyboard *kb, uint32_t format,
                      int32_t fd, uint32_t size) {
    (void)data;
    (void)kb;
    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1) {
        close(fd);
        return;
    }
    char *map = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (map == MAP_FAILED) {
        close(fd);
        return;
    }
    struct xkb_keymap *keymap = xkb_keymap_new_from_string(
        P.xkb_ctx, map, XKB_KEYMAP_FORMAT_TEXT_V1, XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(map, size);
    close(fd);
    if (!keymap)
        return;
    struct xkb_state *state = xkb_state_new(keymap);
    if (!state) {
        xkb_keymap_unref(keymap);
        return;
    }
    if (P.xkb_state)
        xkb_state_unref(P.xkb_state);
    if (P.xkb_keymap)
        xkb_keymap_unref(P.xkb_keymap);
    P.xkb_keymap = keymap;
    P.xkb_state = state;
}

static void kb_enter(void *data, struct wl_keyboard *kb, uint32_t serial,
                     struct wl_surface *surface, struct wl_array *keys) {
    (void)data; (void)kb; (void)serial; (void)surface; (void)keys;
}
static void kb_leave(void *data, struct wl_keyboard *kb, uint32_t serial,
                     struct wl_surface *surface) {
    (void)data; (void)kb; (void)serial; (void)surface;
}

static void kb_key(void *data, struct wl_keyboard *kb, uint32_t serial,
                   uint32_t time, uint32_t key, uint32_t state) {
    (void)data; (void)kb; (void)serial; (void)time;
    if (state != WL_KEYBOARD_KEY_STATE_PRESSED || !P.xkb_state)
        return;
    /* libinput/evdev keycodes are offset by 8 from xkb keycodes. */
    xkb_keycode_t keycode = key + 8;
    xkb_keysym_t sym = xkb_state_key_get_one_sym(P.xkb_state, keycode);
    uint32_t cp = xkb_state_key_get_utf32(P.xkb_state, keycode);
    if (g_cb.key)
        g_cb.key((uint32_t)sym, cp);
}

static void kb_modifiers(void *data, struct wl_keyboard *kb, uint32_t serial,
                         uint32_t depressed, uint32_t latched, uint32_t locked,
                         uint32_t group) {
    (void)data; (void)kb; (void)serial;
    if (P.xkb_state)
        xkb_state_update_mask(P.xkb_state, depressed, latched, locked, 0, 0, group);
}

static void kb_repeat_info(void *data, struct wl_keyboard *kb, int32_t rate,
                           int32_t delay) {
    (void)data; (void)kb; (void)rate; (void)delay;
}

static const struct wl_keyboard_listener keyboard_listener = {
    .keymap = kb_keymap,
    .enter = kb_enter,
    .leave = kb_leave,
    .key = kb_key,
    .modifiers = kb_modifiers,
    .repeat_info = kb_repeat_info,
};

static void seat_capabilities(void *data, struct wl_seat *seat, uint32_t caps) {
    (void)data;
    bool has_kb = (caps & WL_SEAT_CAPABILITY_KEYBOARD) != 0;
    if (has_kb && !P.keyboard) {
        P.keyboard = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(P.keyboard, &keyboard_listener, NULL);
    } else if (!has_kb && P.keyboard) {
        wl_keyboard_destroy(P.keyboard);
        P.keyboard = NULL;
    }
}
static void seat_name(void *data, struct wl_seat *seat, const char *name) {
    (void)data; (void)seat; (void)name;
}
static const struct wl_seat_listener seat_listener = {
    .capabilities = seat_capabilities,
    .name = seat_name,
};

/* --------------------------------------------------------------- registry -- */

static void registry_global(void *data, struct wl_registry *reg, uint32_t name,
                            const char *interface, uint32_t version) {
    (void)data;
    if (strcmp(interface, wl_compositor_interface.name) == 0) {
        P.compositor = wl_registry_bind(reg, name, &wl_compositor_interface,
                                        version < 4 ? version : 4);
    } else if (strcmp(interface, wl_shm_interface.name) == 0) {
        P.shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    } else if (strcmp(interface, zwlr_layer_shell_v1_interface.name) == 0) {
        P.layer_shell = wl_registry_bind(reg, name, &zwlr_layer_shell_v1_interface,
                                         version < 4 ? version : 4);
    } else if (strcmp(interface, wl_seat_interface.name) == 0) {
        /* Only bind a seat if this panel wants keyboard input. */
        if (P.cfg.keyboard != 0 && !P.seat) {
            P.seat = wl_registry_bind(reg, name, &wl_seat_interface,
                                      version < 5 ? version : 5);
            wl_seat_add_listener(P.seat, &seat_listener, NULL);
        }
    }
}
static void registry_global_remove(void *data, struct wl_registry *reg, uint32_t name) {
    (void)data; (void)reg; (void)name;
}
static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

/* ------------------------------------------------------------- public ABI -- */

int wtf_panel_init(struct wtf_panel_config cfg, struct wtf_panel_callbacks cbs) {
    memset(&P, 0, sizeof(P));
    g_cb = cbs;
    P.cfg = cfg;
    P.width = cfg.width;
    P.height = cfg.height;

    P.display = wl_display_connect(NULL);
    if (!P.display) {
        fprintf(stderr, "wtf-panel: cannot connect to a Wayland display\n");
        return -1;
    }
    P.registry = wl_display_get_registry(P.display);
    wl_registry_add_listener(P.registry, &registry_listener, NULL);
    /* Two roundtrips: the first gets the globals, the second lets the seat's
     * capabilities arrive so the keyboard binds before we need it. */
    wl_display_roundtrip(P.display);
    wl_display_roundtrip(P.display);

    if (!P.compositor || !P.shm) {
        fprintf(stderr, "wtf-panel: compositor missing wl_compositor/wl_shm\n");
        return -1;
    }
    if (!P.layer_shell) {
        fprintf(stderr, "wtf-panel: compositor lacks zwlr_layer_shell_v1\n");
        return -1;
    }

    P.xkb_ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);

    P.surface = wl_compositor_create_surface(P.compositor);
    P.layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        P.layer_shell, P.surface, NULL /* default output */,
        (uint32_t)cfg.layer, cfg.ns ? cfg.ns : "wtf-panel");
    if (!P.layer_surface) {
        fprintf(stderr, "wtf-panel: failed to create layer surface\n");
        return -1;
    }
    zwlr_layer_surface_v1_add_listener(P.layer_surface, &layer_surface_listener, NULL);

    zwlr_layer_surface_v1_set_size(P.layer_surface,
                                   (uint32_t)(cfg.width > 0 ? cfg.width : 0),
                                   (uint32_t)(cfg.height > 0 ? cfg.height : 0));
    zwlr_layer_surface_v1_set_anchor(P.layer_surface, (uint32_t)cfg.anchor);
    if (cfg.exclusive_zone >= -1)
        zwlr_layer_surface_v1_set_exclusive_zone(P.layer_surface, cfg.exclusive_zone);
    zwlr_layer_surface_v1_set_margin(P.layer_surface, cfg.margin_top,
                                     cfg.margin_right, cfg.margin_bottom,
                                     cfg.margin_left);
    zwlr_layer_surface_v1_set_keyboard_interactivity(P.layer_surface,
                                                     (uint32_t)cfg.keyboard);

    /* Initial commit WITHOUT a buffer: the compositor replies with configure,
     * which is where we (re)create the shm buffer and draw. */
    wl_surface_commit(P.surface);
    wl_display_roundtrip(P.display);
    return 0;
}

void wtf_panel_request_redraw(void) {
    P.redraw_pending = true;
    if (P.configured && !P.frame_pending)
        draw();
}

void wtf_panel_set_exclusive(int zone) {
    if (P.layer_surface) {
        zwlr_layer_surface_v1_set_exclusive_zone(P.layer_surface, zone);
        wl_surface_commit(P.surface);
    }
}

int wtf_panel_run(void) {
    if (!P.display)
        return -1;
    P.running = true;
    while (P.running && wl_display_dispatch(P.display) != -1) {
        /* loop: dispatch blocks until events; redraws flow via frame callbacks */
    }
    /* Display gone or quit requested. */
    return 0;
}

void wtf_panel_quit(void) {
    P.running = false;
    if (P.display)
        wl_display_roundtrip(P.display); /* flush a final commit if any */
}
