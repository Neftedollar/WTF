/*
 * wtf-shim.c — a wlroots 0.18 compositor "body" exposing the flat C ABI in
 * wtf.h to an F# "brain". Structurally derived from the canonical tinywl.c on
 * the wlroots 0.18 branch (0.18.2), but rewired so that:
 *
 *   - it does NOT auto-tile or auto-focus; instead it reports map/unmap/key/
 *     output-resize events up to the brain via struct wtf_callbacks, and
 *   - it exposes the imperative wtf_configure/focus/close/spawn/quit ops the
 *     brain drives.
 *
 * Single-threaded wl_display event loop, so module-global state is safe.
 *
 * wlroots 0.18 requires WLR_USE_UNSTABLE before any wlr header.
 */
#define _GNU_SOURCE  /* setenv, setsid, eventfd */
#define WLR_USE_UNSTABLE

#include <assert.h>
#include <math.h>
#include <stdbool.h>
#include <sys/eventfd.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <signal.h>
#include <execinfo.h>   /* backtrace() in the fatal signal handler */
#include <drm_fourcc.h>
#include <wayland-server-core.h>
#include <wlr/backend.h>
#include <wlr/interfaces/wlr_buffer.h>
#include <wlr/render/allocator.h>
#include <wlr/render/wlr_renderer.h>
#include <wlr/types/wlr_cursor.h>
#include <wlr/types/wlr_compositor.h>
#include <wlr/types/wlr_data_device.h>
#include <wlr/types/wlr_data_control_v1.h>
#include <wlr/types/wlr_export_dmabuf_v1.h>
#include <wlr/types/wlr_gamma_control_v1.h>
#include <wlr/types/wlr_primary_selection_v1.h>
#include <wlr/types/wlr_screencopy_v1.h>
#include <wlr/types/wlr_input_device.h>
#include <wlr/types/wlr_keyboard.h>
#include <wlr/types/wlr_output.h>
#include <wlr/types/wlr_output_layout.h>
#include <wlr/types/wlr_xdg_output_v1.h>
#include <wlr/types/wlr_pointer.h>
#include <scenefx/types/wlr_scene.h>
#include <scenefx/types/fx/blur_data.h>
#include <scenefx/types/fx/corner_location.h>
#include <scenefx/render/fx_renderer/fx_renderer.h>
#include <wlr/types/wlr_seat.h>
#include <wlr/types/wlr_subcompositor.h>
#include <wlr/types/wlr_xcursor_manager.h>
#include <wlr/types/wlr_xdg_shell.h>
#include <wlr/types/wlr_layer_shell_v1.h>
#include <wlr/xwayland.h>
#include <wlr/util/box.h>
#include <wlr/util/log.h>
#include <wlr/backend/libinput.h>
#include <libinput.h>
#include <xkbcommon/xkbcommon.h>

#include "wtf.h"
#include <stddef.h>  /* offsetof, for the ABI static asserts below */

/* ABI GUARD: struct wtf_libinput_config is passed BY VALUE across the FFI and is
 * mirrored field-for-field by the F# [<StructLayout(Sequential)>] LibinputConfig in
 * src/WTF.Host/Ffi.fs (the single riskiest interop element: mixed double/int). These
 * compile-time asserts lock the C layout (size 56, doubles at 0 and 40) so any
 * accidental reorder/resize here BREAKS THE BUILD, forcing the F# mirror to be
 * updated in lockstep instead of silently corrupting every input setting. */
_Static_assert(sizeof(struct wtf_libinput_config) == 56, "wtf_libinput_config size drifted from the F# Ffi.fs mirror");
_Static_assert(offsetof(struct wtf_libinput_config, mouse_accel) == 0, "mouse_accel offset drifted");
_Static_assert(offsetof(struct wtf_libinput_config, tp_accel) == 40, "tp_accel offset drifted");
_Static_assert(offsetof(struct wtf_libinput_config, tp_accel_profile) == 48, "tp_accel_profile offset drifted");

/* ------------------------------------------------------------------ */
/* state                                                              */
/* ------------------------------------------------------------------ */

enum wtf_cursor_mode {
	WTF_CURSOR_PASSTHROUGH,
	WTF_CURSOR_MOVE,
	WTF_CURSOR_RESIZE,
};

struct wtf_server {
	struct wl_display *wl_display;
	struct wlr_backend *backend;
	struct wlr_renderer *renderer;
	struct wlr_allocator *allocator;
	struct wlr_scene *scene;
	struct wlr_scene_output_layout *scene_layout;

	struct wlr_xdg_shell *xdg_shell;
	struct wl_listener new_xdg_toplevel;
	struct wl_listener new_xdg_popup;
	struct wl_list toplevels;

	/* Layer-shell (bars, wallpaper, launchers, notifications). */
	struct wlr_layer_shell_v1 *layer_shell;
	struct wl_listener new_layer_surface;
	struct wl_list layer_surfaces;
	/* The layer surface currently holding an EXCLUSIVE keyboard grab, if any. */
	struct wlr_layer_surface_v1 *focused_layer;

	/* Scene-graph z-order trees: BACKGROUND < BOTTOM < toplevels < TOP < OVERLAY.
	 * layer_tree is indexed by enum zwlr_layer_shell_v1_layer (0..3). */
	struct wlr_scene_tree *layer_tree[4];
	struct wlr_scene_tree *toplevel_tree;

	struct wlr_cursor *cursor;
	struct wlr_xcursor_manager *cursor_mgr;
	struct wl_listener cursor_motion;
	struct wl_listener cursor_motion_absolute;
	struct wl_listener cursor_button;
	struct wl_listener cursor_axis;
	struct wl_listener cursor_frame;

	struct wlr_seat *seat;
	struct wl_listener new_input;
	struct wl_listener request_cursor;
	struct wl_listener request_set_selection;
	struct wl_list keyboards;
	struct wl_list pointers;
	enum wtf_cursor_mode cursor_mode;
	struct wtf_toplevel *grabbed_toplevel;
	double grab_x, grab_y;
	struct wlr_box grab_geobox;
	uint32_t resize_edges;

	struct wlr_output_layout *output_layout;
	struct wl_list outputs;
	struct wl_listener new_output;

	/* The output whose size we report up to the brain. First one wins. */
	struct wlr_output *primary_output;
	int reported_x, reported_y, reported_width, reported_height;

	/* XWayland: managed toplevels join the same brain id-space as xdg; override-
	 * redirect surfaces are unmanaged scene nodes never reported to the brain. */
	struct wlr_xwayland *xwayland;
	struct wl_listener xwayland_ready;
	struct wl_listener new_xwayland_surface;

	/* Stable id allocation for mapped toplevels. */
	int next_id;
};

struct wtf_output {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_output *wlr_output;
	struct wl_listener frame;
	struct wl_listener request_state;
	struct wl_listener destroy;
};

struct wtf_toplevel {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_xdg_toplevel *xdg_toplevel;
	/* XWayland dual-shell: when is_xwayland, xwl_surface is the backing surface
	 * and xdg_toplevel is NULL (and vice-versa). The brain only ever sees ids. */
	bool is_xwayland;
	struct wlr_xwayland_surface *xwl_surface;
	struct wlr_scene_tree *scene_tree;

	int id;          /* stable handle handed to the brain; 0 = unmapped */
	int x, y;        /* last position set by the brain via wtf_configure */
	bool mapped;

	/* --- eye-candy: animate the scene node toward the brain's target --- */
	double anim_x, anim_y;      /* current (sub-pixel) on-screen position */
	int target_x, target_y;     /* destination from wtf_configure */
	double opacity;             /* current opacity */
	double target_opacity;      /* destination opacity (focus-dependent) */
	double applied_opacity;     /* last value pushed to the scene (de-dup) */
	bool anim_init;             /* false until the first wtf_configure */
	int cw, ch;                 /* configured content size (for border sizing) */
	struct wlr_scene_rect *border;  /* colored frame behind the window */

	/* --- E1 per-window style overrides (driven by the F# brain per window) --- */
	float  win_border[4];       /* override border RGBA, used when has_win_border */
	bool   has_win_border;      /* false (calloc) => fall back to global active/inactive */
	double win_opacity;         /* override opacity target, used when has_win_opacity */
	bool   has_win_opacity;     /* false (calloc) => fall back to global active/inactive */
	bool   floating;            /* set by the brain; tiled (false) windows ignore
	                               interactive move/resize (the layout owns their size) */

	struct wl_listener map;
	struct wl_listener unmap;
	struct wl_listener commit;
	struct wl_listener destroy;
	struct wl_listener request_move;
	struct wl_listener request_resize;
	struct wl_listener request_maximize;
	struct wl_listener request_fullscreen;

	/* XWayland-only lifecycle listeners (associate/dissociate model). map/unmap
	 * (above) are registered on xwl_surface->surface->events only while
	 * associated; the rest live for the whole wtf_toplevel lifetime. */
	struct wl_listener associate;
	struct wl_listener dissociate;
	struct wl_listener xwl_destroy;
	struct wl_listener request_configure;
};

/* Override-redirect XWayland surfaces (menus, tooltips, DnD): unmanaged, never
 * given a wtf id, never reported to the brain. Placed verbatim in OVERLAY. */
struct wtf_xwayland_unmanaged {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_xwayland_surface *xsurface;
	struct wlr_scene_tree *scene_tree;
	struct wl_listener associate;
	struct wl_listener dissociate;
	struct wl_listener destroy;
	struct wl_listener request_configure;
	struct wl_listener map;
	struct wl_listener unmap;
};

struct wtf_popup {
	struct wlr_xdg_popup *xdg_popup;
	struct wl_listener commit;
	struct wl_listener destroy;
};

struct wtf_layer_surface {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_layer_surface_v1 *layer_surface;
	struct wlr_scene_layer_surface_v1 *scene;
	struct wlr_output *output;

	struct wl_listener map;
	struct wl_listener unmap;
	struct wl_listener commit;
	struct wl_listener destroy;
	/* NOTE: layer-shell popups (new_popup) are deferred — see CONSTRAINTS. */
};

struct wtf_keyboard {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_keyboard *wlr_keyboard;

	struct wl_listener modifiers;
	struct wl_listener key;
	struct wl_listener destroy;
};

/* A pointer-class device (mouse OR touchpad). Tracked so libinput config that
 * arrives after attach can be re-applied to already-present devices. */
struct wtf_pointer {
	struct wl_list link;
	struct wlr_input_device *device;
	struct wl_listener destroy;
};

/* The single global compositor instance and the brain's callbacks. The event
 * loop is single-threaded, so plain globals are correct here. */
static struct wtf_server server;
static struct wtf_callbacks g_cb;

/* Held for the graceful signal handler so it can break the run-loop without
 * reaching into the (possibly half-built) server struct. Set after the display
 * is created; cleared before teardown so a late SIGINT/SIGTERM cannot call
 * wl_display_terminate on a being-destroyed display. `volatile` since it is
 * read by the async signal handler. */
static struct wl_display *volatile g_display = NULL;

/* Cross-thread wakeup: an external thread (the F# IPC server) writes to this
 * eventfd via wtf_command_notify(); the event loop wakes and calls g_cb.drain()
 * ON THE LOOP THREAD, where it is safe to mutate state and call wlroots. */
static int g_cmd_fd = -1;

static int handle_cmd_fd(int fd, uint32_t mask, void *data) {
	(void)mask;
	(void)data;
	uint64_t v;
	while (read(fd, &v, sizeof(v)) > 0) {
		/* drain all pending notifications */
	}
	if (g_cb.drain) {
		g_cb.drain();
	}
	return 0;
}

void wtf_command_notify(void) {
	if (g_cmd_fd >= 0) {
		uint64_t one = 1;
		ssize_t n = write(g_cmd_fd, &one, sizeof(one));
		(void)n;
	}
}

/* ------------------------------------------------------------------ */
/* helpers                                                            */
/* ------------------------------------------------------------------ */

static struct wtf_toplevel *toplevel_by_id(int id) {
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		if (t->id == id) {
			return t;
		}
	}
	return NULL;
}

/* ---- eye-candy tunables (live-adjustable via the wtf_set_* ops) ---- */
#define WTF_ACTIVE_OPACITY  1.00    /* focused window (always fully opaque) */
static double g_anim_speed = 0.30;        /* per-frame easing factor toward target */
static double g_inactive_opacity = 0.94;  /* unfocused windows (subtle transparency) */
static int g_border_width = 2;            /* window border thickness in px */
static float g_active_border[4]   = {0.54f, 0.71f, 0.98f, 1.0f};  /* focused */
static float g_inactive_border[4] = {0.27f, 0.28f, 0.35f, 1.0f};  /* unfocused */
static int g_corner_radius = 0;           /* rounded corners (0 = sharp) */
static bool g_blur_enabled = false;       /* backdrop blur behind windows */
static struct blur_data g_blur;           /* blur params (init in wtf_run) */

/* ---- input config (xkb keymap + key repeat, set via wtf_set_keymap) ---- */
/* Each xkb field is NULL => let xkb pick its compile default for that field. */
static char *g_kb_rules   = NULL;
static char *g_kb_model   = NULL;
static char *g_kb_layout  = NULL;
static char *g_kb_variant = NULL;
static char *g_kb_options = NULL;
static int   g_repeat_rate  = 25;   /* keys/sec (xkb default-ish) */
static int   g_repeat_delay = 600;  /* ms before repeat kicks in */

/* ---- libinput config (mouse/touchpad, set via wtf_set_libinput_config) ---- */
/* int fields: -1 = leave libinput's own default; 0/1 = off/on; 2 = 3rd option.
 * accel speeds are always applied (0.0 is neutral). Mirrors struct
 * wtf_libinput_config in wtf.h byte-for-byte. */
static struct wtf_libinput_config g_li = {
	.mouse_accel = 0.0, .mouse_accel_profile = -1, .mouse_natural_scroll = -1,
	.tap = -1, .tap_drag = -1, .tp_natural_scroll = -1, .dwt = -1,
	.scroll_method = -1, .click_method = -1,
	.tp_accel = 0.0, .tp_accel_profile = -1,
};

/* Wake the primary output so an animation that isn't otherwise damaging the
 * scene (e.g. a focus-only opacity change) still gets stepped next frame. */
static void schedule_frame(void) {
	if (server.primary_output != NULL) {
		wlr_output_schedule_frame(server.primary_output);
	}
}

/* Recursively set opacity on every buffer under a scene node. */
static void node_set_opacity(struct wlr_scene_node *node, float opacity) {
	if (node->type == WLR_SCENE_NODE_BUFFER) {
		wlr_scene_buffer_set_opacity(wlr_scene_buffer_from_node(node), opacity);
	} else if (node->type == WLR_SCENE_NODE_TREE) {
		struct wlr_scene_tree *tree = wl_container_of(node, tree, node);
		struct wlr_scene_node *child;
		wl_list_for_each(child, &tree->children, link) {
			node_set_opacity(child, opacity);
		}
	}
}

/* Apply scenefx effects (rounded corners + backdrop blur) to every buffer
 * under a node. */
static void node_apply_fx(struct wlr_scene_node *node, int radius, bool blur) {
	if (node->type == WLR_SCENE_NODE_BUFFER) {
		struct wlr_scene_buffer *b = wlr_scene_buffer_from_node(node);
		wlr_scene_buffer_set_corner_radius(b, radius, CORNER_LOCATION_ALL);
		wlr_scene_buffer_set_backdrop_blur(b, blur);
	} else if (node->type == WLR_SCENE_NODE_TREE) {
		struct wlr_scene_tree *tree = wl_container_of(node, tree, node);
		struct wlr_scene_node *child;
		wl_list_for_each(child, &tree->children, link) {
			node_apply_fx(child, radius, blur);
		}
	}
}

/* Re-apply the current corner-radius / blur style to one toplevel. */
static void style_toplevel(struct wtf_toplevel *t) {
	if (t->scene_tree != NULL) {
		node_apply_fx(&t->scene_tree->node, g_corner_radius, g_blur_enabled);
	}
	if (t->border != NULL) {
		int r = g_corner_radius > 0 ? g_corner_radius + g_border_width : 0;
		wlr_scene_rect_set_corner_radius(t->border, r, CORNER_LOCATION_ALL);
	}
}

/* Step every mapped toplevel one frame toward its target position/opacity.
 * Returns true if any animation is still in flight. */
static bool animate_toplevels(void) {
	bool busy = false;
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		if (!t->mapped || !t->anim_init) {
			continue;
		}
		double dx = t->target_x - t->anim_x;
		double dy = t->target_y - t->anim_y;
		if (fabs(dx) > 0.5 || fabs(dy) > 0.5) {
			t->anim_x += dx * g_anim_speed;
			t->anim_y += dy * g_anim_speed;
			busy = true;
		} else {
			t->anim_x = t->target_x;
			t->anim_y = t->target_y;
		}
		int px = (int)lround(t->anim_x);
		int py = (int)lround(t->anim_y);
		/* Place the scene node (buffer origin) at the tile origin. A CSD client's
		 * geometry then sits inset by its shadow margin INSIDE the tile — the shadow
		 * fills the gap and the window fits. (An earlier attempt shifted the node by
		 * -geo to align the geometry box to the tile, but that pushed the buffer's
		 * shadow margin off the screen edge and CLIPPED the window — visibly broken.
		 * The proper way to remove CSD insets is decoration negotiation, not a
		 * position hack.) */
		wlr_scene_node_set_position(&t->scene_tree->node, px, py);
		if (t->border != NULL) {
			wlr_scene_node_set_position(&t->border->node,
				px - g_border_width, py - g_border_width);
		}

		double dop = t->target_opacity - t->opacity;
		if (fabs(dop) > 0.01) {
			t->opacity += dop * g_anim_speed;
			busy = true;
		} else {
			t->opacity = t->target_opacity;
		}
		if (fabs(t->opacity - t->applied_opacity) > 0.004) {
			node_set_opacity(&t->scene_tree->node, (float)t->opacity);
			t->applied_opacity = t->opacity;
		}
	}
	return busy;
}

/* Keyboard-focus + activate a toplevel and raise its scene node. Unlike
 * tinywl this does NOT reorder the toplevels list as a focus stack; the brain
 * owns ordering. */
static void focus_toplevel(struct wtf_toplevel *toplevel) {
	if (toplevel == NULL) {
		return;
	}
	struct wlr_seat *seat = server.seat;
	struct wlr_surface *surface = toplevel->is_xwayland
		? toplevel->xwl_surface->surface
		: toplevel->xdg_toplevel->base->surface;
	struct wlr_surface *prev_surface = seat->keyboard_state.focused_surface;
	if (prev_surface == surface) {
		return;
	}
	if (prev_surface) {
		struct wlr_xdg_toplevel *prev_toplevel =
			wlr_xdg_toplevel_try_from_wlr_surface(prev_surface);
		if (prev_toplevel != NULL) {
			wlr_xdg_toplevel_set_activated(prev_toplevel, false);
		} else {
			struct wlr_xwayland_surface *prev_xwl =
				wlr_xwayland_surface_try_from_wlr_surface(prev_surface);
			if (prev_xwl != NULL) {
				wlr_xwayland_surface_activate(prev_xwl, false);
			}
		}
	}
	struct wlr_keyboard *keyboard = wlr_seat_get_keyboard(seat);
	/* Raise the border first, then the window on top of it (same order as
	 * wtf_configure) so a focused floating window's border isn't left occluded
	 * behind an adjacent window. */
	if (toplevel->border != NULL) {
		wlr_scene_node_raise_to_top(&toplevel->border->node);
	}
	wlr_scene_node_raise_to_top(&toplevel->scene_tree->node);
	if (toplevel->is_xwayland) {
		wlr_xwayland_surface_activate(toplevel->xwl_surface, true);
	} else {
		wlr_xdg_toplevel_set_activated(toplevel->xdg_toplevel, true);
	}
	/* Do NOT steal the keyboard from an EXCLUSIVE layer-shell grab (the omnibox /
	 * a launcher / a lockscreen). focus_toplevel is reached from pointer clicks
	 * AND from the brain's wtf_focus (on every arrange/restyle); if it yanked the
	 * keyboard while the omnibox was up, the omnibox would never see Esc/Enter and
	 * could not be dismissed — exactly the "can't close it, typing blind" bug, with
	 * a toplevel (e.g. gnome-text-editor) silently holding the keyboard behind it.
	 * Visual focus (activation, raise, border, opacity) below still updates; only
	 * the keyboard stays with the layer. layer_surface_unmap restores toplevel
	 * keyboard focus when the grab ends. */
	if (keyboard != NULL && server.focused_layer == NULL) {
		wlr_seat_keyboard_notify_enter(seat, surface,
			keyboard->keycodes, keyboard->num_keycodes, &keyboard->modifiers);
	}

	/* Active window fully opaque + active border; the rest translucent. */
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		bool active = (t == toplevel);
		t->target_opacity = t->has_win_opacity ? t->win_opacity
			: (active ? WTF_ACTIVE_OPACITY : g_inactive_opacity);
		if (t->border != NULL) {
			wlr_scene_rect_set_color(t->border,
				t->has_win_border ? t->win_border
					: (active ? g_active_border : g_inactive_border));
		}
	}
	schedule_frame();

	/* Tell the brain who is focused now. focus_toplevel is reached BOTH from a
	 * pointer click (C-driven) and from the host's wtf_focus (brain-driven); the
	 * host guards `view_focus` so a brain-initiated focus is a no-op, while a
	 * CLICK syncs the brain's focus + re-evaluates focus-dependent style. The
	 * host's view_focus handler does NOT call wtf_focus back, so there is no loop. */
	if (g_cb.view_focus) {
		g_cb.view_focus(toplevel->id);
	}
}

/* Report the primary output's usable area (full output minus layer-shell
 * exclusive zones) to the brain, but only when it actually changed. */
static void report_usable_area(struct wtf_server *srv, struct wlr_box *usable) {
	if (usable->x == srv->reported_x && usable->y == srv->reported_y &&
			usable->width == srv->reported_width &&
			usable->height == srv->reported_height) {
		return;
	}
	srv->reported_x = usable->x;
	srv->reported_y = usable->y;
	srv->reported_width = usable->width;
	srv->reported_height = usable->height;
	if (g_cb.output_resize) {
		g_cb.output_resize(usable->x, usable->y, usable->width, usable->height);
	}
}

static void wallpaper_layout(struct wtf_server *srv);

/* Arrange every layer surface on the primary output, letting wlroots' scene
 * helper apply anchors/margins/exclusive zones and shrink the usable area; then
 * report the resulting usable rectangle to the brain so tiling reflows. */
static void arrange_layers(struct wtf_server *srv) {
	if (srv->primary_output == NULL) {
		return;
	}
	struct wlr_box full_area;
	wlr_output_layout_get_box(srv->output_layout, srv->primary_output, &full_area);
	if (full_area.width == 0 && full_area.height == 0) {
		/* Output not in the layout (yet); fall back to effective res at 0,0. */
		full_area.x = 0;
		full_area.y = 0;
		wlr_output_effective_resolution(srv->primary_output,
			&full_area.width, &full_area.height);
	}

	struct wlr_box usable = full_area;
	struct wtf_layer_surface *ls;
	wl_list_for_each(ls, &srv->layer_surfaces, link) {
		if (ls->output != srv->primary_output) {
			continue;
		}
		wlr_scene_layer_surface_v1_configure(ls->scene, &full_area, &usable);
	}

	report_usable_area(srv, &usable);

	/* The background also tracks the full output box. */
	wallpaper_layout(srv);
}

/* ------------------------------------------------------------------ */
/* wallpaper (BACKGROUND layer): custom data-ptr wlr_buffer + scene node */
/* ------------------------------------------------------------------ */

/* A producer-owned wlr_buffer wrapping a malloc'd copy of RGBA pixels the F#
 * brain decoded/scaled. wlroots 0.18 has no wlr_readonly_data_buffer_create, so
 * we implement the minimal data-ptr buffer interface: the renderer samples the
 * pixels via begin/end_data_ptr_access and uploads them as a texture. */
struct wtf_wallpaper_buffer {
	struct wlr_buffer base;
	void *data;        /* owned malloc copy, freed in destroy */
	uint32_t format;   /* DRM_FORMAT_ABGR8888 (ImageSharp Rgba32 byte order) */
	size_t stride;     /* width * 4 */
};

static void wallpaper_buffer_destroy(struct wlr_buffer *buf) {
	struct wtf_wallpaper_buffer *wb = wl_container_of(buf, wb, base);
	free(wb->data);
	free(wb);
}

static bool wallpaper_buffer_begin_data_ptr_access(struct wlr_buffer *buf,
		uint32_t flags, void **data, uint32_t *format, size_t *stride) {
	(void)flags; /* read-only; we never honour WRITE */
	struct wtf_wallpaper_buffer *wb = wl_container_of(buf, wb, base);
	*data = wb->data;
	*format = wb->format;
	*stride = wb->stride;
	return true;
}

static void wallpaper_buffer_end_data_ptr_access(struct wlr_buffer *buf) {
	(void)buf; /* nothing to release */
}

static const struct wlr_buffer_impl wtf_wallpaper_buffer_impl = {
	.destroy = wallpaper_buffer_destroy,
	.get_dmabuf = NULL,
	.get_shm = NULL,
	.begin_data_ptr_access = wallpaper_buffer_begin_data_ptr_access,
	.end_data_ptr_access = wallpaper_buffer_end_data_ptr_access,
};

/* Build a producer-referenced buffer from a copy of `rgba` (w*h*4 bytes,
 * R,G,B,A order). Returns NULL on allocation failure. */
static struct wlr_buffer *wtf_wallpaper_buffer_create(const unsigned char *rgba,
		int w, int h) {
	if (rgba == NULL || w <= 0 || h <= 0) {
		return NULL;
	}
	struct wtf_wallpaper_buffer *wb = calloc(1, sizeof(*wb));
	if (wb == NULL) {
		return NULL;
	}
	size_t size = (size_t)w * (size_t)h * 4;
	wb->data = malloc(size);
	if (wb->data == NULL) {
		free(wb);
		return NULL;
	}
	memcpy(wb->data, rgba, size);
	wb->format = DRM_FORMAT_ABGR8888;
	wb->stride = (size_t)w * 4;
	wlr_buffer_init(&wb->base, &wtf_wallpaper_buffer_impl, w, h);
	return &wb->base;
}

/* The current wallpaper scene objects (one image node OR one color rect at a
 * time), plus the producer ref on the live image buffer. */
static struct wlr_scene_buffer *g_wallpaper_node;   /* image node */
static struct wlr_buffer *g_wallpaper_buffer;       /* producer ref for ^ */
static struct wlr_scene_rect *g_wallpaper_rect;     /* solid-color node */

/* Resolve the primary output's full box, falling back to its effective
 * resolution if the layout doesn't know it yet (mirrors arrange_layers). */
static bool wallpaper_output_box(struct wtf_server *srv, struct wlr_box *box) {
	if (srv->primary_output == NULL) {
		return false;
	}
	wlr_output_layout_get_box(srv->output_layout, srv->primary_output, box);
	if (box->width == 0 && box->height == 0) {
		box->x = 0;
		box->y = 0;
		wlr_output_effective_resolution(srv->primary_output,
			&box->width, &box->height);
	}
	return box->width > 0 && box->height > 0;
}

/* Position/size whichever wallpaper node exists to fill the full output box and
 * lower it to the bottom of the BACKGROUND tree so real layer-shell background
 * clients stay above it. Safe to call when no wallpaper is set. */
static void wallpaper_layout(struct wtf_server *srv) {
	struct wlr_box box;
	if (!wallpaper_output_box(srv, &box)) {
		return;
	}
	if (g_wallpaper_node != NULL) {
		wlr_scene_node_set_position(&g_wallpaper_node->node, box.x, box.y);
		wlr_scene_buffer_set_dest_size(g_wallpaper_node, box.width, box.height);
		wlr_scene_node_lower_to_bottom(&g_wallpaper_node->node);
	}
	if (g_wallpaper_rect != NULL) {
		wlr_scene_node_set_position(&g_wallpaper_rect->node, box.x, box.y);
		wlr_scene_rect_set_size(g_wallpaper_rect, box.width, box.height);
		wlr_scene_node_lower_to_bottom(&g_wallpaper_rect->node);
	}
}

/* ------------------------------------------------------------------ */
/* keyboard / input (kept from tinywl, with the keybinding hook rewired) */
/* ------------------------------------------------------------------ */

static void keyboard_handle_modifiers(struct wl_listener *listener, void *data) {
	struct wtf_keyboard *keyboard = wl_container_of(listener, keyboard, modifiers);
	wlr_seat_set_keyboard(keyboard->server->seat, keyboard->wlr_keyboard);
	wlr_seat_keyboard_notify_modifiers(keyboard->server->seat,
		&keyboard->wlr_keyboard->modifiers);
}

static void keyboard_handle_key(struct wl_listener *listener, void *data) {
	struct wtf_keyboard *keyboard = wl_container_of(listener, keyboard, key);
	struct wtf_server *srv = keyboard->server;
	struct wlr_keyboard_key_event *event = data;
	struct wlr_seat *seat = srv->seat;

	/* Translate libinput keycode -> xkbcommon. */
	uint32_t keycode = event->keycode + 8;

	/* Keybindings are matched in the FIRST configured xkb layout (group 0),
	 * NOT the active group. With layout "us,ru", switching to the ru group
	 * makes xkb_state_key_get_syms return Cyrillic keysyms (Cyrillic_o, ...)
	 * which the F# chord table (US a-z / digits / named keys) can't name, so
	 * EVERY WM binding (Super+j, Super+Return, ...) would die while ru is the
	 * active layout. Querying group 0 at the CURRENT shift level yields the
	 * US keysym regardless of the active group, so the binds work in any
	 * layout. Clients still receive the raw keycode below and do their own
	 * active-group translation, so typing Cyrillic into apps is unaffected.
	 * Falls back to the active-group syms if the keymap can't be queried. */
	struct xkb_state *xkb_state = keyboard->wlr_keyboard->xkb_state;
	if (xkb_state == NULL) {
		/* No compiled keymap (apply_keymap_to now prevents this, but be defensive):
		 * skip binding resolution and just forward the raw key to the focused client. */
		wlr_seat_set_keyboard(seat, keyboard->wlr_keyboard);
		wlr_seat_keyboard_notify_key(seat, event->time_msec, event->keycode, event->state);
		return;
	}
	struct xkb_keymap *keymap = xkb_state_get_keymap(xkb_state);
	const xkb_keysym_t *syms;
	int nsyms;
	if (keymap != NULL && xkb_keymap_num_layouts_for_key(keymap, keycode) > 0) {
		xkb_level_index_t level = xkb_state_key_get_level(xkb_state, keycode, 0);
		nsyms = xkb_keymap_key_get_syms_by_level(keymap, keycode, 0, level, &syms);
	} else {
		nsyms = xkb_state_key_get_syms(xkb_state, keycode, &syms);
	}

	bool handled = false;
	uint32_t modifiers = wlr_keyboard_get_modifiers(keyboard->wlr_keyboard);

	/* Only offer presses to the brain; releases of swallowed presses would
	 * otherwise leak to clients, but compositor-level bindings are edge
	 * actions and tinywl-style consumers expect press-only here. */
	if (event->state == WL_KEYBOARD_KEY_STATE_PRESSED && g_cb.key) {
		for (int i = 0; i < nsyms; i++) {
			if (g_cb.key(modifiers, syms[i])) {
				handled = true;
			}
		}
	}

	if (!handled) {
		wlr_seat_set_keyboard(seat, keyboard->wlr_keyboard);
		wlr_seat_keyboard_notify_key(seat, event->time_msec,
			event->keycode, event->state);
	}
}

static void keyboard_handle_destroy(struct wl_listener *listener, void *data) {
	struct wtf_keyboard *keyboard = wl_container_of(listener, keyboard, destroy);
	wl_list_remove(&keyboard->modifiers.link);
	wl_list_remove(&keyboard->key.link);
	wl_list_remove(&keyboard->destroy.link);
	wl_list_remove(&keyboard->link);
	free(keyboard);
}

/* Build an xkb keymap from the configured rule-names (NULL fields => xkb
 * default) and apply it plus the configured key-repeat info to one keyboard.
 * Used both on attach and when wtf_set_keymap re-applies to existing kbds. */
static void apply_keymap_to(struct wlr_keyboard *wlr_keyboard) {
	struct xkb_context *context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	if (context == NULL) {
		wlr_log(WLR_ERROR, "xkb_context_new failed; keeping previous keymap");
		wlr_keyboard_set_repeat_info(wlr_keyboard, g_repeat_rate, g_repeat_delay);
		return;
	}
	struct xkb_rule_names names = {
		.rules   = g_kb_rules,
		.model   = g_kb_model,
		.layout  = g_kb_layout,
		.variant = g_kb_variant,
		.options = g_kb_options,
	};
	struct xkb_keymap *keymap = xkb_keymap_new_from_names(context, &names,
		XKB_KEYMAP_COMPILE_NO_FLAGS);
	if (keymap == NULL) {
		/* A bad user layout/variant/options must NOT leave xkb_state NULL — the
		 * key handler dereferences it on the next press (SIGSEGV). Fall back to the
		 * xkb DEFAULT keymap (empty rule-names) so the keyboard always has a valid
		 * map; the user just gets the default layout until they fix the config. */
		wlr_log(WLR_ERROR, "xkb keymap compile failed for layout '%s' variant '%s' "
			"options '%s'; falling back to the default keymap",
			g_kb_layout ? g_kb_layout : "(default)",
			g_kb_variant ? g_kb_variant : "",
			g_kb_options ? g_kb_options : "");
		struct xkb_rule_names empty = {0};
		keymap = xkb_keymap_new_from_names(context, &empty, XKB_KEYMAP_COMPILE_NO_FLAGS);
	}
	if (keymap != NULL) {
		wlr_keyboard_set_keymap(wlr_keyboard, keymap);
		xkb_keymap_unref(keymap);
	}
	xkb_context_unref(context);
	wlr_keyboard_set_repeat_info(wlr_keyboard, g_repeat_rate, g_repeat_delay);
}

static void server_new_keyboard(struct wtf_server *srv,
		struct wlr_input_device *device) {
	struct wlr_keyboard *wlr_keyboard = wlr_keyboard_from_input_device(device);

	struct wtf_keyboard *keyboard = calloc(1, sizeof(*keyboard));
	if (keyboard == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating wtf_keyboard; dropping device");
		return;
	}
	keyboard->server = srv;
	keyboard->wlr_keyboard = wlr_keyboard;

	apply_keymap_to(wlr_keyboard);

	keyboard->modifiers.notify = keyboard_handle_modifiers;
	wl_signal_add(&wlr_keyboard->events.modifiers, &keyboard->modifiers);
	keyboard->key.notify = keyboard_handle_key;
	wl_signal_add(&wlr_keyboard->events.key, &keyboard->key);
	keyboard->destroy.notify = keyboard_handle_destroy;
	wl_signal_add(&device->events.destroy, &keyboard->destroy);

	wlr_seat_set_keyboard(srv->seat, keyboard->wlr_keyboard);

	wl_list_insert(&srv->keyboards, &keyboard->link);
}

/* Apply the stored libinput config to one pointer-class device. Touchpad vs
 * mouse is distinguished by tap finger count (a mouse reports 0), NOT by the
 * wlr device type (a touchpad is also WLR_INPUT_DEVICE_POINTER). Each knob is
 * guarded by libinput's own _is_available / _has_* / _get_methods probe and by
 * the -1 leave-default sentinel. In the nested wayland/X11 backend the device
 * is NOT libinput-backed, so we early-return before touching the handle. */
static void apply_libinput_to(struct wlr_input_device *device) {
	if (!wlr_input_device_is_libinput(device)) {
		return;
	}
	struct libinput_device *li = wlr_libinput_get_device_handle(device);
	if (li == NULL) {
		return;
	}
	bool is_touchpad = libinput_device_config_tap_get_finger_count(li) > 0;

	if (is_touchpad) {
		if (g_li.tap >= 0) {
			libinput_device_config_tap_set_enabled(li, g_li.tap
				? LIBINPUT_CONFIG_TAP_ENABLED
				: LIBINPUT_CONFIG_TAP_DISABLED);
		}
		if (g_li.tap_drag >= 0) {
			libinput_device_config_tap_set_drag_enabled(li, g_li.tap_drag
				? LIBINPUT_CONFIG_DRAG_ENABLED
				: LIBINPUT_CONFIG_DRAG_DISABLED);
		}
		if (g_li.tp_natural_scroll >= 0 &&
				libinput_device_config_scroll_has_natural_scroll(li)) {
			libinput_device_config_scroll_set_natural_scroll_enabled(li,
				g_li.tp_natural_scroll);
		}
		if (g_li.dwt >= 0 && libinput_device_config_dwt_is_available(li)) {
			libinput_device_config_dwt_set_enabled(li, g_li.dwt
				? LIBINPUT_CONFIG_DWT_ENABLED
				: LIBINPUT_CONFIG_DWT_DISABLED);
		}
		if (g_li.scroll_method >= 0) {
			uint32_t avail = libinput_device_config_scroll_get_methods(li);
			enum libinput_config_scroll_method m =
				g_li.scroll_method == 1 ? LIBINPUT_CONFIG_SCROLL_2FG :
				g_li.scroll_method == 2 ? LIBINPUT_CONFIG_SCROLL_EDGE :
				LIBINPUT_CONFIG_SCROLL_NO_SCROLL;
			if (m == LIBINPUT_CONFIG_SCROLL_NO_SCROLL || (avail & m)) {
				libinput_device_config_scroll_set_method(li, m);
			}
		}
		if (g_li.click_method >= 0) {
			uint32_t avail = libinput_device_config_click_get_methods(li);
			enum libinput_config_click_method m =
				g_li.click_method == 1 ? LIBINPUT_CONFIG_CLICK_METHOD_BUTTON_AREAS :
				g_li.click_method == 2 ? LIBINPUT_CONFIG_CLICK_METHOD_CLICKFINGER :
				LIBINPUT_CONFIG_CLICK_METHOD_NONE;
			if (m == LIBINPUT_CONFIG_CLICK_METHOD_NONE || (avail & m)) {
				libinput_device_config_click_set_method(li, m);
			}
		}
		if (libinput_device_config_accel_is_available(li)) {
			libinput_device_config_accel_set_speed(li, g_li.tp_accel);
			if (g_li.tp_accel_profile >= 0) {
				libinput_device_config_accel_set_profile(li,
					g_li.tp_accel_profile == 1
						? LIBINPUT_CONFIG_ACCEL_PROFILE_ADAPTIVE
						: LIBINPUT_CONFIG_ACCEL_PROFILE_FLAT);
			}
		}
	} else {
		if (g_li.mouse_natural_scroll >= 0 &&
				libinput_device_config_scroll_has_natural_scroll(li)) {
			libinput_device_config_scroll_set_natural_scroll_enabled(li,
				g_li.mouse_natural_scroll);
		}
		if (libinput_device_config_accel_is_available(li)) {
			libinput_device_config_accel_set_speed(li, g_li.mouse_accel);
			if (g_li.mouse_accel_profile >= 0) {
				libinput_device_config_accel_set_profile(li,
					g_li.mouse_accel_profile == 1
						? LIBINPUT_CONFIG_ACCEL_PROFILE_ADAPTIVE
						: LIBINPUT_CONFIG_ACCEL_PROFILE_FLAT);
			}
		}
	}
}

static void pointer_handle_destroy(struct wl_listener *listener, void *data) {
	struct wtf_pointer *pointer = wl_container_of(listener, pointer, destroy);
	wl_list_remove(&pointer->destroy.link);
	wl_list_remove(&pointer->link);
	free(pointer);
}

static void server_new_pointer(struct wtf_server *srv,
		struct wlr_input_device *device) {
	wlr_cursor_attach_input_device(srv->cursor, device);

	/* Track it so a later wtf_set_libinput_config can re-apply, then apply the
	 * config we have right now. */
	struct wtf_pointer *pointer = calloc(1, sizeof(*pointer));
	if (pointer == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating wtf_pointer; dropping device");
		return;
	}
	pointer->device = device;
	pointer->destroy.notify = pointer_handle_destroy;
	wl_signal_add(&device->events.destroy, &pointer->destroy);
	wl_list_insert(&srv->pointers, &pointer->link);

	apply_libinput_to(device);
}

static void server_new_input(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, new_input);
	struct wlr_input_device *device = data;
	switch (device->type) {
	case WLR_INPUT_DEVICE_KEYBOARD:
		server_new_keyboard(srv, device);
		break;
	case WLR_INPUT_DEVICE_POINTER:
		server_new_pointer(srv, device);
		break;
	default:
		break;
	}
	uint32_t caps = WL_SEAT_CAPABILITY_POINTER;
	if (!wl_list_empty(&srv->keyboards)) {
		caps |= WL_SEAT_CAPABILITY_KEYBOARD;
	}
	wlr_seat_set_capabilities(srv->seat, caps);
}

static void seat_request_cursor(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, request_cursor);
	struct wlr_seat_pointer_request_set_cursor_event *event = data;
	struct wlr_seat_client *focused_client =
		srv->seat->pointer_state.focused_client;
	if (focused_client == event->seat_client) {
		wlr_cursor_set_surface(srv->cursor, event->surface,
			event->hotspot_x, event->hotspot_y);
	}
}

static void seat_request_set_selection(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, request_set_selection);
	struct wlr_seat_request_set_selection_event *event = data;
	wlr_seat_set_selection(srv->seat, event->source, event->serial);
}

/* ------------------------------------------------------------------ */
/* cursor / interactive move-resize (kept verbatim from tinywl)        */
/* ------------------------------------------------------------------ */

static struct wtf_toplevel *desktop_toplevel_at(struct wtf_server *srv,
		double lx, double ly, struct wlr_surface **surface,
		double *sx, double *sy) {
	struct wlr_scene_node *node = wlr_scene_node_at(
		&srv->scene->tree.node, lx, ly, sx, sy);
	if (node == NULL || node->type != WLR_SCENE_NODE_BUFFER) {
		return NULL;
	}
	struct wlr_scene_buffer *scene_buffer = wlr_scene_buffer_from_node(node);
	struct wlr_scene_surface *scene_surface =
		wlr_scene_surface_try_from_buffer(scene_buffer);
	if (!scene_surface) {
		return NULL;
	}

	*surface = scene_surface->surface;
	struct wlr_scene_tree *tree = node->parent;
	while (tree != NULL && tree->node.data == NULL) {
		tree = tree->node.parent;
	}
	return tree ? tree->node.data : NULL;
}

static void reset_cursor_mode(struct wtf_server *srv) {
	srv->cursor_mode = WTF_CURSOR_PASSTHROUGH;
	srv->grabbed_toplevel = NULL;
}

static void process_cursor_move(struct wtf_server *srv) {
	struct wtf_toplevel *toplevel = srv->grabbed_toplevel;
	wlr_scene_node_set_position(&toplevel->scene_tree->node,
		srv->cursor->x - srv->grab_x,
		srv->cursor->y - srv->grab_y);
	toplevel->x = toplevel->scene_tree->node.x;
	toplevel->y = toplevel->scene_tree->node.y;
}

static void process_cursor_resize(struct wtf_server *srv) {
	struct wtf_toplevel *toplevel = srv->grabbed_toplevel;
	double border_x = srv->cursor->x - srv->grab_x;
	double border_y = srv->cursor->y - srv->grab_y;
	int new_left = srv->grab_geobox.x;
	int new_right = srv->grab_geobox.x + srv->grab_geobox.width;
	int new_top = srv->grab_geobox.y;
	int new_bottom = srv->grab_geobox.y + srv->grab_geobox.height;

	if (srv->resize_edges & WLR_EDGE_TOP) {
		new_top = border_y;
		if (new_top >= new_bottom) {
			new_top = new_bottom - 1;
		}
	} else if (srv->resize_edges & WLR_EDGE_BOTTOM) {
		new_bottom = border_y;
		if (new_bottom <= new_top) {
			new_bottom = new_top + 1;
		}
	}
	if (srv->resize_edges & WLR_EDGE_LEFT) {
		new_left = border_x;
		if (new_left >= new_right) {
			new_left = new_right - 1;
		}
	} else if (srv->resize_edges & WLR_EDGE_RIGHT) {
		new_right = border_x;
		if (new_right <= new_left) {
			new_right = new_left + 1;
		}
	}

	struct wlr_box geo_box;
	wlr_xdg_surface_get_geometry(toplevel->xdg_toplevel->base, &geo_box);
	wlr_scene_node_set_position(&toplevel->scene_tree->node,
		new_left - geo_box.x, new_top - geo_box.y);
	toplevel->x = toplevel->scene_tree->node.x;
	toplevel->y = toplevel->scene_tree->node.y;

	int new_width = new_right - new_left;
	int new_height = new_bottom - new_top;
	wlr_xdg_toplevel_set_size(toplevel->xdg_toplevel, new_width, new_height);
}

static void process_cursor_motion(struct wtf_server *srv, uint32_t time) {
	if (srv->cursor_mode == WTF_CURSOR_MOVE) {
		process_cursor_move(srv);
		return;
	} else if (srv->cursor_mode == WTF_CURSOR_RESIZE) {
		process_cursor_resize(srv);
		return;
	}

	double sx, sy;
	struct wlr_seat *seat = srv->seat;
	struct wlr_surface *surface = NULL;
	struct wtf_toplevel *toplevel = desktop_toplevel_at(srv,
		srv->cursor->x, srv->cursor->y, &surface, &sx, &sy);
	if (!toplevel) {
		wlr_cursor_set_xcursor(srv->cursor, srv->cursor_mgr, "default");
	}
	if (surface) {
		wlr_seat_pointer_notify_enter(seat, surface, sx, sy);
		wlr_seat_pointer_notify_motion(seat, time, sx, sy);
	} else {
		wlr_seat_pointer_clear_focus(seat);
	}
}

static void server_cursor_motion(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, cursor_motion);
	struct wlr_pointer_motion_event *event = data;
	wlr_cursor_move(srv->cursor, &event->pointer->base,
		event->delta_x, event->delta_y);
	process_cursor_motion(srv, event->time_msec);
}

static void server_cursor_motion_absolute(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, cursor_motion_absolute);
	struct wlr_pointer_motion_absolute_event *event = data;
	wlr_cursor_warp_absolute(srv->cursor, &event->pointer->base, event->x, event->y);
	process_cursor_motion(srv, event->time_msec);
}

static void server_cursor_button(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, cursor_button);
	struct wlr_pointer_button_event *event = data;
	wlr_seat_pointer_notify_button(srv->seat,
		event->time_msec, event->button, event->state);
	double sx, sy;
	struct wlr_surface *surface = NULL;
	struct wtf_toplevel *toplevel = desktop_toplevel_at(srv,
		srv->cursor->x, srv->cursor->y, &surface, &sx, &sy);
	if (event->state == WL_POINTER_BUTTON_STATE_RELEASED) {
		reset_cursor_mode(srv);
	} else {
		/* Focus-on-click kept as a convenience; the brain may still override
		 * focus at any time via wtf_focus(). */
		focus_toplevel(toplevel);
	}
}

static void server_cursor_axis(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, cursor_axis);
	struct wlr_pointer_axis_event *event = data;
	wlr_seat_pointer_notify_axis(srv->seat,
		event->time_msec, event->orientation, event->delta,
		event->delta_discrete, event->source, event->relative_direction);
}

static void server_cursor_frame(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, cursor_frame);
	wlr_seat_pointer_notify_frame(srv->seat);
}

/* ------------------------------------------------------------------ */
/* output                                                             */
/* ------------------------------------------------------------------ */

static void output_frame(struct wl_listener *listener, void *data) {
	struct wtf_output *output = wl_container_of(listener, output, frame);
	struct wlr_scene *scene = output->server->scene;
	struct wlr_scene_output *scene_output = wlr_scene_get_scene_output(
		scene, output->wlr_output);

	/* Advance animations before compositing this frame. */
	bool still_animating = animate_toplevels();

	wlr_scene_output_commit(scene_output, NULL);

	struct timespec now;
	clock_gettime(CLOCK_MONOTONIC, &now);
	wlr_scene_output_send_frame_done(scene_output, &now);

	/* Keep frames coming until everything has settled. */
	if (still_animating) {
		wlr_output_schedule_frame(output->wlr_output);
	}
}

static void output_request_state(struct wl_listener *listener, void *data) {
	struct wtf_output *output = wl_container_of(listener, output, request_state);
	const struct wlr_output_event_request_state *event = data;
	wlr_output_commit_state(output->wlr_output, event->state);
	/* Nested Wayland/X11 window was resized: re-arrange layers and tell the
	 * brain the new usable area. */
	if (output->wlr_output == output->server->primary_output) {
		arrange_layers(output->server);
	}
}

static void output_destroy(struct wl_listener *listener, void *data) {
	struct wtf_output *output = wl_container_of(listener, output, destroy);
	struct wtf_server *srv = output->server;

	wl_list_remove(&output->frame.link);
	wl_list_remove(&output->request_state.link);
	wl_list_remove(&output->destroy.link);
	wl_list_remove(&output->link);

	if (srv->primary_output == output->wlr_output) {
		srv->primary_output = NULL;
		srv->reported_x = srv->reported_y = 0;
		srv->reported_width = srv->reported_height = 0;
		/* Promote another output to primary if one remains. */
		if (!wl_list_empty(&srv->outputs)) {
			struct wtf_output *next =
				wl_container_of(srv->outputs.next, next, link);
			srv->primary_output = next->wlr_output;
			arrange_layers(srv);
		}
	}
	free(output);
}

static void server_new_output(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, new_output);
	struct wlr_output *wlr_output = data;

	wlr_output_init_render(wlr_output, srv->allocator, srv->renderer);

	struct wlr_output_state state;
	wlr_output_state_init(&state);
	wlr_output_state_set_enabled(&state, true);

	struct wlr_output_mode *mode = wlr_output_preferred_mode(wlr_output);
	if (mode != NULL) {
		wlr_output_state_set_mode(&state, mode);
	}

	wlr_output_commit_state(wlr_output, &state);
	wlr_output_state_finish(&state);

	struct wtf_output *output = calloc(1, sizeof(*output));
	if (output == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating wtf_output; dropping output");
		return;
	}
	output->wlr_output = wlr_output;
	output->server = srv;

	output->frame.notify = output_frame;
	wl_signal_add(&wlr_output->events.frame, &output->frame);
	output->request_state.notify = output_request_state;
	wl_signal_add(&wlr_output->events.request_state, &output->request_state);
	output->destroy.notify = output_destroy;
	wl_signal_add(&wlr_output->events.destroy, &output->destroy);

	wl_list_insert(&srv->outputs, &output->link);

	struct wlr_output_layout_output *l_output =
		wlr_output_layout_add_auto(srv->output_layout, wlr_output);
	struct wlr_scene_output *scene_output =
		wlr_scene_output_create(srv->scene, wlr_output);
	wlr_scene_output_layout_add_output(srv->scene_layout, l_output, scene_output);

	/* First output to appear becomes the one we report to the brain. */
	if (srv->primary_output == NULL) {
		srv->primary_output = wlr_output;
		arrange_layers(srv);
	}
}

/* ------------------------------------------------------------------ */
/* xdg-shell toplevels / popups                                       */
/* ------------------------------------------------------------------ */

static void xdg_toplevel_map(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, map);

	wl_list_insert(&toplevel->server->toplevels, &toplevel->link);

	/* Assign a stable id and tell the brain. The brain then drives placement
	 * (wtf_configure) and focus (wtf_focus); we do NOT auto-tile or focus. */
	toplevel->id = ++toplevel->server->next_id;
	toplevel->mapped = true;

	/* Start at the target opacity (NOT a 0->fade): the border is a solid color
	 * rect behind the window, so an opacity fade-in reveals it as a bright flash
	 * until the window paints over it. Position still animates via anim_init. A
	 * proper non-flashing open animation lands with the animation engine. */
	toplevel->opacity = g_inactive_opacity;
	toplevel->target_opacity = g_inactive_opacity;
	toplevel->applied_opacity = -1.0;
	toplevel->anim_init = false;

	if (g_cb.view_map) {
		g_cb.view_map(toplevel->id,
			toplevel->xdg_toplevel->app_id,
			toplevel->xdg_toplevel->title);
	}
}

static void xdg_toplevel_unmap(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, unmap);

	if (toplevel == toplevel->server->grabbed_toplevel) {
		reset_cursor_mode(toplevel->server);
	}

	if (toplevel->mapped) {
		toplevel->mapped = false;
		if (g_cb.view_unmap) {
			g_cb.view_unmap(toplevel->id);
		}
	}

	wl_list_remove(&toplevel->link);
}

static void xdg_toplevel_commit(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, commit);

	if (toplevel->xdg_toplevel->base->initial_commit) {
		/* On the initial commit we must reply with a configure. We use 0,0 so
		 * the client picks its own size; the brain re-sizes it via
		 * wtf_configure once it has decided on a layout. */
		wlr_xdg_toplevel_set_size(toplevel->xdg_toplevel, 0, 0);
	}
}

static void xdg_toplevel_destroy(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, destroy);

	/* Defensive: wlroots normally delivers unmap BEFORE destroy, but if a destroy
	 * ever arrives while still mapped, replicate the unmap so the brain and the
	 * toplevels list don't desync (mapped==true implies link is still listed). */
	if (toplevel->mapped) {
		if (toplevel == toplevel->server->grabbed_toplevel) {
			reset_cursor_mode(toplevel->server);
		}
		toplevel->mapped = false;
		if (g_cb.view_unmap) {
			g_cb.view_unmap(toplevel->id);
		}
		wl_list_remove(&toplevel->link);
	}

	wl_list_remove(&toplevel->map.link);
	wl_list_remove(&toplevel->unmap.link);
	wl_list_remove(&toplevel->commit.link);
	wl_list_remove(&toplevel->destroy.link);
	wl_list_remove(&toplevel->request_move.link);
	wl_list_remove(&toplevel->request_resize.link);
	wl_list_remove(&toplevel->request_maximize.link);
	wl_list_remove(&toplevel->request_fullscreen.link);

	if (toplevel->border != NULL) {
		wlr_scene_node_destroy(&toplevel->border->node);
	}
	free(toplevel);
}

static void begin_interactive(struct wtf_toplevel *toplevel,
		enum wtf_cursor_mode mode, uint32_t edges) {
	struct wtf_server *srv = toplevel->server;
	struct wlr_surface *focused_surface =
		srv->seat->pointer_state.focused_surface;
	if (focused_surface == NULL || toplevel->xdg_toplevel->base->surface !=
			wlr_surface_get_root_surface(focused_surface)) {
		return;
	}
	/* Tiled windows are sized by the layout — a free interactive move/resize fights
	 * the tiler and collapses the window. Only floating windows may be grabbed. */
	if (!toplevel->floating) {
		return;
	}
	srv->grabbed_toplevel = toplevel;
	srv->cursor_mode = mode;

	if (mode == WTF_CURSOR_MOVE) {
		srv->grab_x = srv->cursor->x - toplevel->scene_tree->node.x;
		srv->grab_y = srv->cursor->y - toplevel->scene_tree->node.y;
	} else {
		struct wlr_box geo_box;
		wlr_xdg_surface_get_geometry(toplevel->xdg_toplevel->base, &geo_box);

		double border_x = (toplevel->scene_tree->node.x + geo_box.x) +
			((edges & WLR_EDGE_RIGHT) ? geo_box.width : 0);
		double border_y = (toplevel->scene_tree->node.y + geo_box.y) +
			((edges & WLR_EDGE_BOTTOM) ? geo_box.height : 0);
		srv->grab_x = srv->cursor->x - border_x;
		srv->grab_y = srv->cursor->y - border_y;

		srv->grab_geobox = geo_box;
		srv->grab_geobox.x += toplevel->scene_tree->node.x;
		srv->grab_geobox.y += toplevel->scene_tree->node.y;

		srv->resize_edges = edges;
	}
}

static void xdg_toplevel_request_move(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, request_move);
	begin_interactive(toplevel, WTF_CURSOR_MOVE, 0);
}

static void xdg_toplevel_request_resize(struct wl_listener *listener, void *data) {
	struct wlr_xdg_toplevel_resize_event *event = data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, request_resize);
	begin_interactive(toplevel, WTF_CURSOR_RESIZE, event->edges);
}

static void xdg_toplevel_request_maximize(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel =
		wl_container_of(listener, toplevel, request_maximize);
	if (toplevel->xdg_toplevel->base->initialized) {
		wlr_xdg_surface_schedule_configure(toplevel->xdg_toplevel->base);
	}
}

static void xdg_toplevel_request_fullscreen(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel =
		wl_container_of(listener, toplevel, request_fullscreen);
	if (toplevel->xdg_toplevel->base->initialized) {
		wlr_xdg_surface_schedule_configure(toplevel->xdg_toplevel->base);
	}
}

static void server_new_xdg_toplevel(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, new_xdg_toplevel);
	struct wlr_xdg_toplevel *xdg_toplevel = data;

	struct wtf_toplevel *toplevel = calloc(1, sizeof(*toplevel));
	if (toplevel == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating wtf_toplevel; dropping window");
		return;
	}
	toplevel->server = srv;
	toplevel->xdg_toplevel = xdg_toplevel;
	/* Create the view at 0,0; the brain places it later via wtf_configure. */
	toplevel->scene_tree =
		wlr_scene_xdg_surface_create(srv->toplevel_tree, xdg_toplevel->base);
	if (toplevel->scene_tree == NULL) {
		wlr_log(WLR_ERROR, "scene tree creation failed; dropping window");
		free(toplevel);
		return;
	}
	toplevel->scene_tree->node.data = toplevel;
	xdg_toplevel->base->data = toplevel->scene_tree;

	/* Colored focus border: a rect sibling kept just behind the window. */
	toplevel->border = wlr_scene_rect_create(srv->toplevel_tree, 0, 0, g_inactive_border);
	if (toplevel->border != NULL)
		wlr_scene_node_place_below(&toplevel->border->node, &toplevel->scene_tree->node);

	toplevel->map.notify = xdg_toplevel_map;
	wl_signal_add(&xdg_toplevel->base->surface->events.map, &toplevel->map);
	toplevel->unmap.notify = xdg_toplevel_unmap;
	wl_signal_add(&xdg_toplevel->base->surface->events.unmap, &toplevel->unmap);
	toplevel->commit.notify = xdg_toplevel_commit;
	wl_signal_add(&xdg_toplevel->base->surface->events.commit, &toplevel->commit);

	toplevel->destroy.notify = xdg_toplevel_destroy;
	wl_signal_add(&xdg_toplevel->events.destroy, &toplevel->destroy);

	toplevel->request_move.notify = xdg_toplevel_request_move;
	wl_signal_add(&xdg_toplevel->events.request_move, &toplevel->request_move);
	toplevel->request_resize.notify = xdg_toplevel_request_resize;
	wl_signal_add(&xdg_toplevel->events.request_resize, &toplevel->request_resize);
	toplevel->request_maximize.notify = xdg_toplevel_request_maximize;
	wl_signal_add(&xdg_toplevel->events.request_maximize, &toplevel->request_maximize);
	toplevel->request_fullscreen.notify = xdg_toplevel_request_fullscreen;
	wl_signal_add(&xdg_toplevel->events.request_fullscreen, &toplevel->request_fullscreen);
}

/* ------------------------------------------------------------------ */
/* XWayland (managed toplevels + override-redirect unmanaged surfaces)  */
/* ------------------------------------------------------------------ */

/* --- managed X11 toplevels: join the same brain id-space as xdg --- */

static void xwl_map(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, map);
	struct wtf_server *srv = toplevel->server;

	wl_list_insert(&srv->toplevels, &toplevel->link);

	/* Assign a stable id from the SHARED counter so X11 ids never collide with
	 * xdg ids; the brain then drives placement/focus exactly as for xdg. */
	toplevel->id = ++srv->next_id;
	toplevel->mapped = true;

	/* Start at the target opacity (NOT a 0->fade): the border is a solid color
	 * rect behind the window, so an opacity fade-in reveals it as a bright flash
	 * until the window paints over it. Position still animates via anim_init. A
	 * proper non-flashing open animation lands with the animation engine. */
	toplevel->opacity = g_inactive_opacity;
	toplevel->target_opacity = g_inactive_opacity;
	toplevel->applied_opacity = -1.0;
	toplevel->anim_init = false;

	if (g_cb.view_map) {
		g_cb.view_map(toplevel->id,
			toplevel->xwl_surface->class,
			toplevel->xwl_surface->title);
	}
}

static void xwl_unmap(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, unmap);

	if (toplevel == toplevel->server->grabbed_toplevel) {
		reset_cursor_mode(toplevel->server);
	}

	if (toplevel->mapped) {
		toplevel->mapped = false;
		if (g_cb.view_unmap) {
			g_cb.view_unmap(toplevel->id);
		}
	}

	wl_list_remove(&toplevel->link);
}

/* Tear down the scene node + border created in associate. Safe to call when
 * already torn down (NULL-guarded). */
static void xwl_destroy_scene(struct wtf_toplevel *toplevel) {
	if (toplevel->border != NULL) {
		wlr_scene_node_destroy(&toplevel->border->node);
		toplevel->border = NULL;
	}
	if (toplevel->scene_tree != NULL) {
		wlr_scene_node_destroy(&toplevel->scene_tree->node);
		toplevel->scene_tree = NULL;
	}
}

static void xwl_associate(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, associate);
	struct wtf_server *srv = toplevel->server;
	struct wlr_xwayland_surface *xsurface = toplevel->xwl_surface;

	/* The wlr_surface only exists between associate and dissociate; build the
	 * scene node now. The subsurface tree does NOT auto-destroy on dissociate. */
	toplevel->scene_tree =
		wlr_scene_subsurface_tree_create(srv->toplevel_tree, xsurface->surface);
	if (toplevel->scene_tree == NULL) {
		wlr_log(WLR_ERROR, "xwl scene tree creation failed; skipping associate");
		return;
	}
	/* Back-pointer so desktop_toplevel_at() resolves clicks to this toplevel,
	 * exactly like the xdg path. */
	toplevel->scene_tree->node.data = toplevel;

	toplevel->border =
		wlr_scene_rect_create(srv->toplevel_tree, 0, 0, g_inactive_border);
	if (toplevel->border != NULL)
		wlr_scene_node_place_below(&toplevel->border->node, &toplevel->scene_tree->node);

	/* map/unmap listeners live ONLY while associated (the #1 UAF guard). */
	toplevel->map.notify = xwl_map;
	wl_signal_add(&xsurface->surface->events.map, &toplevel->map);
	toplevel->unmap.notify = xwl_unmap;
	wl_signal_add(&xsurface->surface->events.unmap, &toplevel->unmap);
}

static void xwl_dissociate(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, dissociate);

	wl_list_remove(&toplevel->map.link);
	wl_list_remove(&toplevel->unmap.link);
	xwl_destroy_scene(toplevel);
}

static void xwl_request_configure(struct wl_listener *listener, void *data) {
	struct wtf_toplevel *toplevel =
		wl_container_of(listener, toplevel, request_configure);
	struct wlr_xwayland_surface_configure_event *ev = data;

	/* Ack so the client isn't stuck. Before the brain has placed the window
	 * (anim_init == false) we honor the requested geometry; once placed, the
	 * brain's layout wins (wtf_configure re-issues a configure). Simplification:
	 * we always ack the request here; the next wtf_configure overrides it. */
	wlr_xwayland_surface_configure(ev->surface, ev->x, ev->y, ev->width, ev->height);
}

static void xwl_destroy(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_toplevel *toplevel = wl_container_of(listener, toplevel, xwl_destroy);

	/* Defensive: replicate unmap if destroyed while still mapped (see the xdg
	 * path) so the brain + toplevels list stay consistent. */
	if (toplevel->mapped) {
		if (toplevel == toplevel->server->grabbed_toplevel) {
			reset_cursor_mode(toplevel->server);
		}
		toplevel->mapped = false;
		if (g_cb.view_unmap) {
			g_cb.view_unmap(toplevel->id);
		}
		wl_list_remove(&toplevel->link);
	}

	wl_list_remove(&toplevel->associate.link);
	wl_list_remove(&toplevel->dissociate.link);
	wl_list_remove(&toplevel->xwl_destroy.link);
	wl_list_remove(&toplevel->request_configure.link);

	/* If destroyed while still associated, the scene node may still exist. */
	xwl_destroy_scene(toplevel);
	free(toplevel);
}

/* --- override-redirect (unmanaged) X11 surfaces: never reach the brain --- */

static void unmanaged_request_configure(struct wl_listener *listener, void *data) {
	struct wtf_xwayland_unmanaged *u =
		wl_container_of(listener, u, request_configure);
	struct wlr_xwayland_surface_configure_event *ev = data;

	/* Honor the requested geometry verbatim (menus/tooltips position themselves). */
	wlr_xwayland_surface_configure(ev->surface, ev->x, ev->y, ev->width, ev->height);
	if (u->scene_tree != NULL) {
		wlr_scene_node_set_position(&u->scene_tree->node, ev->x, ev->y);
	}
}

static void unmanaged_map(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_xwayland_unmanaged *u = wl_container_of(listener, u, map);
	if (u->scene_tree != NULL) {
		wlr_scene_node_set_enabled(&u->scene_tree->node, true);
	}
}

static void unmanaged_unmap(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_xwayland_unmanaged *u = wl_container_of(listener, u, unmap);
	if (u->scene_tree != NULL) {
		wlr_scene_node_set_enabled(&u->scene_tree->node, false);
	}
}

static void unmanaged_associate(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_xwayland_unmanaged *u = wl_container_of(listener, u, associate);
	struct wtf_server *srv = u->server;
	struct wlr_xwayland_surface *xsurface = u->xsurface;

	/* Place unmanaged surfaces in the OVERLAY layer at their requested geometry. */
	u->scene_tree = wlr_scene_subsurface_tree_create(
		srv->layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY], xsurface->surface);
	if (u->scene_tree == NULL) {
		wlr_log(WLR_ERROR, "unmanaged scene tree creation failed; skipping");
		return;
	}
	/* No wtf_toplevel back-pointer: hit-tests resolve to the surface, not a
	 * managed toplevel (node.data stays NULL so desktop_toplevel_at skips it). */
	wlr_scene_node_set_position(&u->scene_tree->node, xsurface->x, xsurface->y);

	u->map.notify = unmanaged_map;
	wl_signal_add(&xsurface->surface->events.map, &u->map);
	u->unmap.notify = unmanaged_unmap;
	wl_signal_add(&xsurface->surface->events.unmap, &u->unmap);
}

static void unmanaged_dissociate(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_xwayland_unmanaged *u = wl_container_of(listener, u, dissociate);

	wl_list_remove(&u->map.link);
	wl_list_remove(&u->unmap.link);
	if (u->scene_tree != NULL) {
		wlr_scene_node_destroy(&u->scene_tree->node);
		u->scene_tree = NULL;
	}
}

static void unmanaged_destroy(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_xwayland_unmanaged *u = wl_container_of(listener, u, destroy);

	wl_list_remove(&u->associate.link);
	wl_list_remove(&u->dissociate.link);
	wl_list_remove(&u->destroy.link);
	wl_list_remove(&u->request_configure.link);
	if (u->scene_tree != NULL) {
		wlr_scene_node_destroy(&u->scene_tree->node);
		u->scene_tree = NULL;
	}
	wl_list_remove(&u->link);
	free(u);
}

static void server_new_xwayland_surface(struct wl_listener *listener, void *data) {
	struct wtf_server *srv =
		wl_container_of(listener, srv, new_xwayland_surface);
	struct wlr_xwayland_surface *xsurface = data;

	if (xsurface->override_redirect) {
		/* Unmanaged: never given an id, never reported to the brain. */
		struct wtf_xwayland_unmanaged *u = calloc(1, sizeof(*u));
		if (u == NULL) {
			wlr_log(WLR_ERROR, "OOM allocating unmanaged xwl surface; dropping");
			return;
		}
		u->server = srv;
		u->xsurface = xsurface;
		/* Self-referential link: not tracked on any server list (the brain never
		 * sees these), so wl_list_remove() in unmanaged_destroy is a safe no-op. */
		wl_list_init(&u->link);

		u->associate.notify = unmanaged_associate;
		wl_signal_add(&xsurface->events.associate, &u->associate);
		u->dissociate.notify = unmanaged_dissociate;
		wl_signal_add(&xsurface->events.dissociate, &u->dissociate);
		u->destroy.notify = unmanaged_destroy;
		wl_signal_add(&xsurface->events.destroy, &u->destroy);
		u->request_configure.notify = unmanaged_request_configure;
		wl_signal_add(&xsurface->events.request_configure, &u->request_configure);
		return;
	}

	/* Managed: a real tiled toplevel sharing the xdg brain id-space. The scene
	 * node + map/unmap listeners are deferred to the associate event. */
	struct wtf_toplevel *toplevel = calloc(1, sizeof(*toplevel));
	if (toplevel == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating xwl toplevel; dropping window");
		return;
	}
	toplevel->server = srv;
	toplevel->is_xwayland = true;
	toplevel->xwl_surface = xsurface;

	toplevel->associate.notify = xwl_associate;
	wl_signal_add(&xsurface->events.associate, &toplevel->associate);
	toplevel->dissociate.notify = xwl_dissociate;
	wl_signal_add(&xsurface->events.dissociate, &toplevel->dissociate);
	toplevel->xwl_destroy.notify = xwl_destroy;
	wl_signal_add(&xsurface->events.destroy, &toplevel->xwl_destroy);
	toplevel->request_configure.notify = xwl_request_configure;
	wl_signal_add(&xsurface->events.request_configure, &toplevel->request_configure);
}

static void server_xwayland_ready(struct wl_listener *listener, void *data) {
	(void)data;
	struct wtf_server *srv = wl_container_of(listener, srv, xwayland_ready);
	/* DISPLAY is set at create time (see wtf_run). The seat needs the xwm, which
	 * only exists once the server is ready — so set it here. */
	wlr_xwayland_set_seat(srv->xwayland, srv->seat);
}

static void xdg_popup_commit(struct wl_listener *listener, void *data) {
	struct wtf_popup *popup = wl_container_of(listener, popup, commit);
	if (popup->xdg_popup->base->initial_commit) {
		wlr_xdg_surface_schedule_configure(popup->xdg_popup->base);
	}
}

static void xdg_popup_destroy(struct wl_listener *listener, void *data) {
	struct wtf_popup *popup = wl_container_of(listener, popup, destroy);
	wl_list_remove(&popup->commit.link);
	wl_list_remove(&popup->destroy.link);
	free(popup);
}

static void server_new_xdg_popup(struct wl_listener *listener, void *data) {
	struct wlr_xdg_popup *xdg_popup = data;

	/* CRASH GUARD: this fires on the GLOBAL xdg_shell new_popup for EVERY popup.
	 * The parent need NOT be an xdg-surface: a layer-shell popup's parent is a
	 * layer surface (waybar/wofi/mako menus), and the protocol even permits a
	 * deferred/NULL parent. The old code asserted parent!=NULL (abort -> SIGABRT)
	 * and, with asserts compiled out in release (NDEBUG), dereferenced a NULL
	 * `parent->data` -> SIGSEGV — a client-triggerable whole-session crash (this
	 * is the most likely cause of the silent SIGSEGV seen with Electron/1Password
	 * popups). Skip any popup we can't parent into an xdg scene tree; its own
	 * shell still tears it down. (Proper layer-shell popup parenting is deferred.) */
	struct wlr_xdg_surface *parent =
		xdg_popup->parent ? wlr_xdg_surface_try_from_wlr_surface(xdg_popup->parent) : NULL;
	if (parent == NULL || parent->data == NULL) {
		return;
	}
	struct wtf_popup *popup = calloc(1, sizeof(*popup));
	if (popup == NULL) {
		return;
	}
	popup->xdg_popup = xdg_popup;

	struct wlr_scene_tree *parent_tree = parent->data;
	xdg_popup->base->data = wlr_scene_xdg_surface_create(parent_tree, xdg_popup->base);

	popup->commit.notify = xdg_popup_commit;
	wl_signal_add(&xdg_popup->base->surface->events.commit, &popup->commit);
	popup->destroy.notify = xdg_popup_destroy;
	wl_signal_add(&xdg_popup->events.destroy, &popup->destroy);
}

/* ------------------------------------------------------------------ */
/* layer-shell (bars, wallpaper, launchers, notifications)            */
/* ------------------------------------------------------------------ */

static void layer_surface_map(struct wl_listener *listener, void *data) {
	struct wtf_layer_surface *ls = wl_container_of(listener, ls, map);
	struct wtf_server *srv = ls->server;

	arrange_layers(srv);

	/* An EXCLUSIVE layer surface (e.g. a launcher / lockscreen) grabs the
	 * keyboard. ON_DEMAND (focus-on-click) is deferred. */
	if (ls->layer_surface->current.keyboard_interactive ==
			ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_EXCLUSIVE) {
		struct wlr_keyboard *kb = wlr_seat_get_keyboard(srv->seat);
		if (kb != NULL) {
			wlr_seat_keyboard_notify_enter(srv->seat,
				ls->layer_surface->surface, kb->keycodes,
				kb->num_keycodes, &kb->modifiers);
		}
		srv->focused_layer = ls->layer_surface;
	}
}

static void layer_surface_unmap(struct wl_listener *listener, void *data) {
	struct wtf_layer_surface *ls = wl_container_of(listener, ls, unmap);
	struct wtf_server *srv = ls->server;

	if (srv->focused_layer == ls->layer_surface) {
		srv->focused_layer = NULL;
		/* Restore keyboard focus to any mapped toplevel. */
		struct wtf_toplevel *t;
		wl_list_for_each(t, &srv->toplevels, link) {
			if (t->mapped) {
				focus_toplevel(t);
				break;
			}
		}
	}

	arrange_layers(srv);
}

static void layer_surface_commit(struct wl_listener *listener, void *data) {
	struct wtf_layer_surface *ls = wl_container_of(listener, ls, commit);
	struct wtf_server *srv = ls->server;
	struct wlr_layer_surface_v1 *layer_surface = ls->layer_surface;

	/* If the client changed its layer after the initial commit, reparent the
	 * scene node into the matching z-order tree. */
	if (ls->scene != NULL) {
		enum zwlr_layer_shell_v1_layer l = layer_surface->current.layer;
		if ((int)l >= 0 && (int)l <= 3) {
			wlr_scene_node_reparent(&ls->scene->tree->node, srv->layer_tree[l]);
		}
	}

	arrange_layers(srv);
}

static void layer_surface_destroy(struct wl_listener *listener, void *data) {
	struct wtf_layer_surface *ls = wl_container_of(listener, ls, destroy);
	/* Capture the server pointer BEFORE freeing (use-after-free guard). Do NOT
	 * touch ls->scene here — the scene helper installs its own destroy listener
	 * that tears down its tree. */
	struct wtf_server *srv = ls->server;

	if (srv->focused_layer == ls->layer_surface) {
		srv->focused_layer = NULL;
	}

	wl_list_remove(&ls->map.link);
	wl_list_remove(&ls->unmap.link);
	wl_list_remove(&ls->commit.link);
	wl_list_remove(&ls->destroy.link);
	wl_list_remove(&ls->link);
	free(ls);

	arrange_layers(srv);
}

static void server_new_layer_surface(struct wl_listener *listener, void *data) {
	struct wtf_server *srv = wl_container_of(listener, srv, new_layer_surface);
	struct wlr_layer_surface_v1 *layer_surface = data;

	/* The output may be NULL on new_surface; we must assign one before
	 * returning. */
	if (layer_surface->output == NULL) {
		layer_surface->output = srv->primary_output;
	}
	if (layer_surface->output == NULL) {
		/* No output exists yet; we cannot place this surface. */
		wlr_layer_surface_v1_destroy(layer_surface);
		return;
	}

	struct wtf_layer_surface *ls = calloc(1, sizeof(*ls));
	if (ls == NULL) {
		wlr_log(WLR_ERROR, "OOM allocating wtf_layer_surface; dropping surface");
		wlr_layer_surface_v1_destroy(layer_surface);
		return;
	}
	ls->server = srv;
	ls->layer_surface = layer_surface;
	ls->output = layer_surface->output;

	/* Initial placement uses the pending layer (current isn't set until the
	 * first commit). Clamp to a valid tree index. */
	enum zwlr_layer_shell_v1_layer l = layer_surface->pending.layer;
	int li = (int)l;
	if (li < 0) li = 0;
	if (li > 3) li = 3;
	ls->scene = wlr_scene_layer_surface_v1_create(srv->layer_tree[li],
		layer_surface);
	/* CRITICAL: do NOT set node.data on a layer-surface tree. desktop_toplevel_at
	 * walks up the scene graph to the first node whose data is non-NULL and returns
	 * it AS A struct wtf_toplevel * — the invariant across this file is "node.data
	 * set => managed toplevel" (see the xwayland-unmanaged note that keeps it NULL
	 * for the same reason). A layer surface (e.g. the omnibox) is hit-tested on
	 * click; if its node carried `ls`, focus_toplevel/begin_interactive would read
	 * wtf_toplevel fields off a wtf_layer_surface and crash the compositor. The ls
	 * back-pointer isn't needed here: every listener recovers it via wl_container_of,
	 * and pointer events still reach the surface via desktop_toplevel_at's *surface
	 * out-param (resolved from the scene buffer, independent of this tree walk). */

	ls->map.notify = layer_surface_map;
	wl_signal_add(&layer_surface->surface->events.map, &ls->map);
	ls->unmap.notify = layer_surface_unmap;
	wl_signal_add(&layer_surface->surface->events.unmap, &ls->unmap);
	ls->commit.notify = layer_surface_commit;
	wl_signal_add(&layer_surface->surface->events.commit, &ls->commit);
	ls->destroy.notify = layer_surface_destroy;
	wl_signal_add(&layer_surface->events.destroy, &ls->destroy);

	wl_list_insert(&srv->layer_surfaces, &ls->link);
}

/* ================================================================== */
/* Public C ABI (wtf.h)                                               */
/* ================================================================== */

void wtf_configure(int id, int x, int y, int width, int height) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) {
		return;
	}
	t->x = x;
	t->y = y;
	t->target_x = x;
	t->target_y = y;
	t->cw = width;
	t->ch = height;
	if (t->border != NULL) {
		wlr_scene_rect_set_size(t->border,
			width + 2 * g_border_width, height + 2 * g_border_width);
	}
	style_toplevel(t); /* (re)apply rounded corners + blur */
	if (!t->anim_init) {
		/* First placement: start the slide from just below the target so the
		 * window eases into position rather than popping. */
		t->anim_x = x;
		t->anim_y = y + 40;
		t->anim_init = true;
	}
	/* Honor the brain's ascending-z Arrange order: raise this node to the top of
	 * the toplevel_tree as it is configured, so iterating the Arrange list in
	 * order leaves the last entry (topmost floating, then the fullscreen window)
	 * on top. Border first, window second, so the window ends directly above its
	 * own border (the border was created via wlr_scene_node_place_below). Tiled
	 * windows do not overlap, so their relative raise order is irrelevant. This
	 * reorders within toplevel_tree only — the TOP/OVERLAY layer trees still sit
	 * above it, so bars/popups/menus are unaffected. */
	if (t->border != NULL) {
		wlr_scene_node_raise_to_top(&t->border->node);
	}
	wlr_scene_node_raise_to_top(&t->scene_tree->node);

	/* Size is applied immediately (clients can't smoothly resize); only the
	 * scene-node position and opacity animate, in output_frame. */
	if (t->is_xwayland) {
		/* X11 has no smooth resize; configure with the final geometry. The
		 * scene-node move is still handled generically by animate_toplevels.
		 * X11 wire types are int16 pos / uint16 size — CLAMP (don't wrap) so a
		 * brain-driven off-screen position (x = -10000) or a large virtual
		 * multi-monitor coordinate can't overflow into a bogus on-screen spot. */
		int16_t cx = (int16_t)(x < INT16_MIN ? INT16_MIN : (x > INT16_MAX ? INT16_MAX : x));
		int16_t cy = (int16_t)(y < INT16_MIN ? INT16_MIN : (y > INT16_MAX ? INT16_MAX : y));
		uint16_t cw = (uint16_t)(width  < 0 ? 0 : (width  > UINT16_MAX ? UINT16_MAX : width));
		uint16_t ch = (uint16_t)(height < 0 ? 0 : (height > UINT16_MAX ? UINT16_MAX : height));
		wlr_xwayland_surface_configure(t->xwl_surface, cx, cy, cw, ch);
	} else {
		/* Ask tiled (non-floating) windows to render in the TILED state: GTK and
		 * other CSD clients then drop their drop-shadow + rounded corners and draw
		 * a flush rectangle, so neighbors pack edge-to-edge and the geometry inset
		 * collapses to ~0. Floating windows keep their normal (decorated) look. */
		if (t->floating) {
			wlr_xdg_toplevel_set_tiled(t->xdg_toplevel, WLR_EDGE_NONE);
		} else {
			wlr_xdg_toplevel_set_tiled(t->xdg_toplevel, WLR_EDGE_TOP |
				WLR_EDGE_BOTTOM | WLR_EDGE_LEFT | WLR_EDGE_RIGHT);
		}
		wlr_xdg_toplevel_set_size(t->xdg_toplevel, width, height);
	}
	schedule_frame();
}

void wtf_focus(int id) {
	focus_toplevel(toplevel_by_id(id));
}

void wtf_close(int id) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t != NULL) {
		if (t->is_xwayland) {
			wlr_xwayland_surface_close(t->xwl_surface);
		} else {
			wlr_xdg_toplevel_send_close(t->xdg_toplevel);
		}
	}
}

void wtf_set_fullscreen(int id, int on) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) {
		return;
	}
	/* Flip the shell-specific fullscreen protocol flag. Positioning to the full
	 * Screen rect is still the brain's job (its Arrange sends that rect), so this
	 * only changes the client's fullscreen *state* + hides the chrome. */
	if (t->is_xwayland) {
		if (t->xwl_surface != NULL) {
			wlr_xwayland_surface_set_fullscreen(t->xwl_surface, on != 0);
		}
	} else {
		if (t->xdg_toplevel != NULL) {
			wlr_xdg_toplevel_set_fullscreen(t->xdg_toplevel, on != 0);
		}
	}
	/* Hide the colored border while fullscreen (a fullscreen window owns the whole
	 * screen; a frame around it would be wrong); restore it when leaving. */
	if (t->border != NULL) {
		wlr_scene_node_set_enabled(&t->border->node, on == 0);
	}
	schedule_frame();
}

void wtf_spawn(const char *cmd) {
	if (cmd == NULL) {
		return;
	}
	pid_t pid = fork();
	if (pid == 0) {
		/* Child: detach from the compositor and exec the command. */
		setsid();
		execl("/bin/sh", "/bin/sh", "-c", cmd, (void *)NULL);
		_exit(127);
	}
}

void wtf_quit(void) {
	if (server.wl_display != NULL) {
		wl_display_terminate(server.wl_display);
	}
}

void wtf_set_anim_speed(double speed) {
	if (speed < 0.01) speed = 0.01;
	if (speed > 1.0) speed = 1.0;
	g_anim_speed = speed;
}

void wtf_set_inactive_opacity(double opacity) {
	if (opacity < 0.0) opacity = 0.0;
	if (opacity > 1.0) opacity = 1.0;
	g_inactive_opacity = opacity;
	/* Re-target every currently-inactive window so the change is visible now. */
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		if (t->has_win_opacity) continue;  /* per-window override wins */
		if (t->target_opacity < WTF_ACTIVE_OPACITY) {
			t->target_opacity = g_inactive_opacity;
		}
	}
	schedule_frame();
}

void wtf_set_border_width(int width) {
	if (width < 0) width = 0;
	g_border_width = width;
	/* Resize every existing border to match the new width. */
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		if (t->border != NULL) {
			wlr_scene_rect_set_size(t->border,
				t->cw + 2 * width, t->ch + 2 * width);
		}
	}
	schedule_frame();
}

void wtf_set_border_color(int active, double r, double g, double b) {
	float *dst = active ? g_active_border : g_inactive_border;
	dst[0] = (float)r;
	dst[1] = (float)g;
	dst[2] = (float)b;
	dst[3] = 1.0f;
	schedule_frame();
}

/* ---- E1: per-window style overrides (pushed by the brain per window) ---- */

/* Set a per-window border color override (RGBA 0..1). Authoritative over the
 * global active/inactive border until cleared. Border is INSTANT for E1. */
void wtf_set_window_border_color(int id, double r, double g, double b, double a) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) return;
	float nr = (float)r, ng = (float)g, nb = (float)b, na = (float)a;
	/* Cheap C-side de-dup: skip if already set to the same color. */
	if (t->has_win_border &&
		t->win_border[0] == nr && t->win_border[1] == ng &&
		t->win_border[2] == nb && t->win_border[3] == na) {
		return;
	}
	t->win_border[0] = nr;
	t->win_border[1] = ng;
	t->win_border[2] = nb;
	t->win_border[3] = na;
	t->has_win_border = true;
	if (t->border != NULL) {
		wlr_scene_rect_set_color(t->border, t->win_border);
	}
	schedule_frame();
}

/* Set a per-window opacity target override (0..1). Animates via the existing
 * animate_toplevels() lerp, exactly like the global path. */
void wtf_set_window_opacity(int id, double opacity) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) return;
	if (opacity < 0.0) opacity = 0.0;
	if (opacity > 1.0) opacity = 1.0;
	t->win_opacity = opacity;
	t->has_win_opacity = true;
	t->target_opacity = opacity;
	schedule_frame();
}

void wtf_set_floating(int id, int floating) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) return;
	t->floating = (floating != 0);
	/* If a window was being interactively grabbed and is now tiled, drop the grab
	 * so it can't keep collapsing. */
	if (!t->floating && t->server->grabbed_toplevel == t) {
		reset_cursor_mode(t->server);
	}
}

/* Clear BOTH per-window overrides; the window reverts to the global
 * active/inactive path on the next focus/style pass. */
void wtf_clear_window_style(int id) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t == NULL) return;
	t->has_win_border = false;
	t->has_win_opacity = false;
	schedule_frame();
}

void wtf_set_corner_radius(int radius) {
	if (radius < 0) radius = 0;
	g_corner_radius = radius;
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		style_toplevel(t);
	}
	schedule_frame();
}

void wtf_set_blur(int enabled, int radius, int passes) {
	g_blur_enabled = (enabled != 0);
	if (radius > 0) g_blur.radius = radius;
	if (passes > 0) g_blur.num_passes = passes;
	wlr_scene_set_blur_data(server.scene, g_blur);
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		style_toplevel(t);
	}
	schedule_frame();
}

void wtf_set_wallpaper(const unsigned char *rgba, int width, int height) {
	struct wlr_buffer *buf = wtf_wallpaper_buffer_create(rgba, width, height);
	if (buf == NULL) {
		return; /* bad args / OOM: leave any existing wallpaper untouched */
	}
	/* An image replaces any solid-color rect. */
	if (g_wallpaper_rect != NULL) {
		wlr_scene_node_destroy(&g_wallpaper_rect->node);
		g_wallpaper_rect = NULL;
	}
	if (g_wallpaper_node == NULL) {
		g_wallpaper_node =
			wlr_scene_buffer_create(server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND], buf);
	} else {
		wlr_scene_buffer_set_buffer(g_wallpaper_node, buf);
	}
	/* The scene took its own lock; drop the old producer ref so it frees once
	 * the scene releases it, then keep our ref on the new buffer. */
	if (g_wallpaper_buffer != NULL) {
		wlr_buffer_drop(g_wallpaper_buffer);
	}
	g_wallpaper_buffer = buf;
	wallpaper_layout(&server);
	schedule_frame();
}

void wtf_set_wallpaper_color(double r, double g, double b) {
	if (r < 0.0) r = 0.0;
	if (r > 1.0) r = 1.0;
	if (g < 0.0) g = 0.0;
	if (g > 1.0) g = 1.0;
	if (b < 0.0) b = 0.0;
	if (b > 1.0) b = 1.0;
	float color[4] = { (float)r, (float)g, (float)b, 1.0f };
	/* A color replaces any image node + its buffer ref. */
	if (g_wallpaper_node != NULL) {
		wlr_scene_node_destroy(&g_wallpaper_node->node);
		g_wallpaper_node = NULL;
	}
	if (g_wallpaper_buffer != NULL) {
		wlr_buffer_drop(g_wallpaper_buffer);
		g_wallpaper_buffer = NULL;
	}
	if (g_wallpaper_rect == NULL) {
		g_wallpaper_rect = wlr_scene_rect_create(
			server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND], 1, 1, color);
	} else {
		wlr_scene_rect_set_color(g_wallpaper_rect, color);
	}
	wallpaper_layout(&server);
	schedule_frame();
}

void wtf_clear_wallpaper(void) {
	if (g_wallpaper_node != NULL) {
		wlr_scene_node_destroy(&g_wallpaper_node->node);
		g_wallpaper_node = NULL;
	}
	if (g_wallpaper_buffer != NULL) {
		wlr_buffer_drop(g_wallpaper_buffer);
		g_wallpaper_buffer = NULL;
	}
	if (g_wallpaper_rect != NULL) {
		wlr_scene_node_destroy(&g_wallpaper_rect->node);
		g_wallpaper_rect = NULL;
	}
	schedule_frame();
}

/* Replace g_kb_* with a freshly dup'd copy of s, or NULL when s is empty
 * (""), which makes xkb fall back to its compile default for that field. */
static char *kb_dup_or_null(const char *s) {
	return (s != NULL && s[0] != '\0') ? strdup(s) : NULL;
}

void wtf_set_keymap(const char *rules, const char *model, const char *layout,
		const char *variant, const char *options,
		int repeat_rate, int repeat_delay) {
	free(g_kb_rules);
	free(g_kb_model);
	free(g_kb_layout);
	free(g_kb_variant);
	free(g_kb_options);
	g_kb_rules   = kb_dup_or_null(rules);
	g_kb_model   = kb_dup_or_null(model);
	g_kb_layout  = kb_dup_or_null(layout);
	g_kb_variant = kb_dup_or_null(variant);
	g_kb_options = kb_dup_or_null(options);
	g_repeat_rate  = repeat_rate;
	g_repeat_delay = repeat_delay;

	/* Re-apply to keyboards that attached before the config arrived. */
	struct wtf_keyboard *kb;
	wl_list_for_each(kb, &server.keyboards, link) {
		apply_keymap_to(kb->wlr_keyboard);
	}
}

void wtf_set_libinput_config(struct wtf_libinput_config cfg) {
	g_li = cfg;
	/* Re-apply to pointer devices that attached before the config arrived. */
	struct wtf_pointer *pointer;
	wl_list_for_each(pointer, &server.pointers, link) {
		apply_libinput_to(pointer->device);
	}
}

/* GRACEFUL: SIGINT/SIGTERM. wl_display_terminate just clears display->run; the
 * EINTR from this signal breaks wl_event_loop's poll so wl_display_run returns
 * and the NORMAL teardown (incl. wlr_backend_destroy => DRM master drop + VT
 * restore) runs. Only an async-signal-safe flag-clear is performed here. */
static void handle_graceful_signal(int signo) {
	(void)signo;
	if (g_display != NULL) {
		wl_display_terminate(g_display);
	}
}

/* FATAL: SIGSEGV/SIGABRT/SIGFPE/SIGILL/SIGBUS. Async-signal-safe ONLY: write(2),
 * then re-raise to core-dump. NO wlr_*, NO stdio, NO malloc. Real VT/console
 * recovery is the wrapper script (scripts/wtf-session). */
static void handle_fatal_signal(int signo) {
	static const char msg[] =
		"\nWTF: FATAL signal in compositor - crashing; "
		"wtf-session will restore the console.\n";
	ssize_t n = write(STDERR_FILENO, msg, sizeof(msg) - 1);
	(void)n;        /* stderr is redirected to the rotating log by the wrapper,
	                   so this single write covers both stderr and the log. */
	/* Dump the call stack so a silent SIGSEGV is diagnosable from the log.
	 * NB: backtrace()/backtrace_symbols_fd() are NOT formally async-signal-safe —
	 * backtrace_symbols_fd may touch the dynamic loader, and on the very first call
	 * libgcc's unwinder is dlopen'd (which mallocs). We pre-warm the unwinder once
	 * in install_signal_handlers() so this crash-path call never triggers that
	 * dlopen; it remains best-effort and could still misbehave on heap corruption,
	 * but it costs nothing on the common SIGSEGV/SIGABRT path and the addresses
	 * resolve against libwtf_shim.so via addr2line even without exported symbols. */
	static const char bt_hdr[] = "WTF: backtrace (addr2line against libwtf_shim.so):\n";
	n = write(STDERR_FILENO, bt_hdr, sizeof(bt_hdr) - 1);
	(void)n;
	void *frames[48];
	int nframes = backtrace(frames, 48);
	backtrace_symbols_fd(frames, nframes, STDERR_FILENO);
	raise(signo);   /* SA_RESETHAND already restored SIG_DFL => re-delivers the
	                   original signal and core-dumps normally. */
}

/* Install the graceful + fatal handlers via sigaction (not signal()). Called
 * once from wtf_run after g_display is set and before wl_display_run. */
static void install_signal_handlers(void) {
	/* Pre-warm the libgcc unwinder so the FIRST backtrace() call (in the fatal
	 * handler) doesn't have to dlopen libgcc_s.so / malloc on the crash path —
	 * that lazy init is the one genuinely unsafe thing about calling backtrace()
	 * from a signal handler. Doing one throwaway backtrace here forces it now. */
	void *warm[1];
	(void)backtrace(warm, 1);

	struct sigaction sa_term = {0};
	sa_term.sa_handler = handle_graceful_signal;
	sigemptyset(&sa_term.sa_mask);
	sa_term.sa_flags = 0;   /* CRITICAL: NO SA_RESTART. wl_display_terminate does
	                           not wake poll(); we rely on the signal's EINTR to
	                           break poll so the run-loop re-checks display->run.
	                           SA_RESTART would hang the WM on shutdown. */
	sigaction(SIGINT, &sa_term, NULL);
	sigaction(SIGTERM, &sa_term, NULL);

	struct sigaction sa_fatal = {0};
	sa_fatal.sa_handler = handle_fatal_signal;
	sigemptyset(&sa_fatal.sa_mask);
	sa_fatal.sa_flags = SA_RESETHAND | SA_NODEFER; /* auto-reset to SIG_DFL before
	                           entry; raise() then core-dumps. */
	sigaction(SIGSEGV, &sa_fatal, NULL);
	sigaction(SIGABRT, &sa_fatal, NULL);
	sigaction(SIGFPE, &sa_fatal, NULL);
	sigaction(SIGILL, &sa_fatal, NULL);
	sigaction(SIGBUS, &sa_fatal, NULL);
}

/* Deduplicating wlr_log sink (observability). A per-FRAME error — e.g.
 * scenefx's "Failed to use optimized blur" at 60 Hz — floods the session log
 * (41 MB observed live) and drowns every real signal, which violates the
 * "logs are the product" rule. Consecutive identical messages are counted and
 * surfaced as one "repeated N times" line at exponentially spaced checkpoints
 * (1, 2, 4, 8, ...), so a flood costs O(log N) lines while every DISTINCT
 * message still lands immediately and in order. */
static void wtf_wlr_log_handler(enum wlr_log_importance importance,
		const char *fmt, va_list args) {
	static char last[1024];
	static unsigned long repeats = 0;
	static const char *levels[] = { "SILENT", "ERROR", "INFO", "DEBUG" };

	char buf[1024];
	vsnprintf(buf, sizeof(buf), fmt, args);

	struct timespec ts;
	clock_gettime(CLOCK_REALTIME, &ts);
	struct tm tm;
	localtime_r(&ts.tv_sec, &tm);

	if (strcmp(buf, last) == 0) {
		repeats++;
		if ((repeats & (repeats - 1)) == 0) {
			fprintf(stderr, "%02d:%02d:%02d.%03ld [wlr] ... last message repeated %lu times\n",
				tm.tm_hour, tm.tm_min, tm.tm_sec, ts.tv_nsec / 1000000, repeats);
		}
		return;
	}
	if (repeats > 1) {
		fprintf(stderr, "%02d:%02d:%02d.%03ld [wlr] ... last message repeated %lu times total\n",
			tm.tm_hour, tm.tm_min, tm.tm_sec, ts.tv_nsec / 1000000, repeats);
	}
	repeats = 0;
	snprintf(last, sizeof(last), "%s", buf);
	fprintf(stderr, "%02d:%02d:%02d.%03ld [%s] %s\n",
		tm.tm_hour, tm.tm_min, tm.tm_sec, ts.tv_nsec / 1000000,
		(importance >= WLR_SILENT && importance <= WLR_DEBUG)
			? levels[importance] : "?",
		buf);
}

int wtf_run(struct wtf_callbacks cbs) {
	g_cb = cbs;

	wlr_log_init(WLR_INFO, wtf_wlr_log_handler);

	server = (struct wtf_server){0};
	server.next_id = 0;

	server.wl_display = wl_display_create();
	if (server.wl_display == NULL) {
		wlr_log(WLR_ERROR, "failed to create wl_display");
		return 1;
	}
	g_display = server.wl_display;
	server.backend = wlr_backend_autocreate(
		wl_display_get_event_loop(server.wl_display), NULL);
	if (server.backend == NULL) {
		wlr_log(WLR_ERROR, "failed to create wlr_backend");
		return 1;
	}

	/* scenefx needs its own GLES renderer (fx_renderer) for blur / rounded
	 * corners / shadows to render. */
	server.renderer = fx_renderer_create(server.backend);
	if (server.renderer == NULL) {
		wlr_log(WLR_ERROR, "failed to create fx_renderer");
		wlr_backend_destroy(server.backend); /* drop the DRM master / restore the VT */
		return 1;
	}
	wlr_renderer_init_wl_display(server.renderer, server.wl_display);

	server.allocator = wlr_allocator_autocreate(server.backend, server.renderer);
	if (server.allocator == NULL) {
		wlr_log(WLR_ERROR, "failed to create wlr_allocator");
		wlr_renderer_destroy(server.renderer);
		wlr_backend_destroy(server.backend); /* drop the DRM master / restore the VT */
		return 1;
	}

	struct wlr_compositor *compositor =
		wlr_compositor_create(server.wl_display, 5, server.renderer);
	wlr_subcompositor_create(server.wl_display);
	wlr_data_device_manager_create(server.wl_display);

	/* Desktop-shell protocols (Phase 2 #9). These managers are self-contained —
	 * wlroots implements the protocol internally, so creating them is all that is
	 * needed to enable the matching client tooling:
	 *   - screencopy + export-dmabuf: screenshots (grim) + screencast, which is
	 *     what xdg-desktop-portal-wlr uses to serve the Screenshot/ScreenCast
	 *     portals (file-picker portal comes from xdg-desktop-portal-gtk).
	 *   - data-control: clipboard managers (wl-clipboard / clipman).
	 *   - primary-selection: middle-click paste.
	 *   - gamma-control: night light (gammastep / wlsunset). */
	wlr_screencopy_manager_v1_create(server.wl_display);
	wlr_export_dmabuf_manager_v1_create(server.wl_display);
	wlr_data_control_manager_v1_create(server.wl_display);
	wlr_primary_selection_v1_device_manager_create(server.wl_display);
	wlr_gamma_control_manager_v1_create(server.wl_display);

	server.output_layout = wlr_output_layout_create(server.wl_display);

	/* xdg-output (zxdg_output_manager_v1): advertises each output's LOGICAL
	 * position + size to clients. Screenshot/recording/layout tools (grim,
	 * wf-recorder, wlr-randr) need this to learn the output geometry; without it
	 * they capture a 0x0 / mis-placed frame. Backed by the output_layout above so
	 * it stays in sync as outputs are added/removed. */
	wlr_xdg_output_manager_v1_create(server.wl_display, server.output_layout);

	wl_list_init(&server.outputs);
	server.new_output.notify = server_new_output;
	wl_signal_add(&server.backend->events.new_output, &server.new_output);

	server.scene = wlr_scene_create();
	if (server.scene == NULL) {
		wlr_log(WLR_ERROR, "failed to create wlr_scene");
		wlr_backend_destroy(server.backend); /* drop the DRM master / restore the VT */
		return 1;
	}
	server.scene_layout =
		wlr_scene_attach_output_layout(server.scene, server.output_layout);

	/* scenefx: seed default blur parameters (off until wtf_set_blur enables). */
	g_blur = blur_data_get_default();
	wlr_scene_set_blur_data(server.scene, g_blur);

	/* Scene z-order trees, created bottom-to-top so later siblings render above
	 * earlier ones: BACKGROUND < BOTTOM < toplevels < TOP < OVERLAY. */
	server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND] =
		wlr_scene_tree_create(&server.scene->tree);
	server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_BOTTOM] =
		wlr_scene_tree_create(&server.scene->tree);
	server.toplevel_tree = wlr_scene_tree_create(&server.scene->tree);
	server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_TOP] =
		wlr_scene_tree_create(&server.scene->tree);
	server.layer_tree[ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY] =
		wlr_scene_tree_create(&server.scene->tree);

	/* Layer-shell: bars, wallpaper, launchers, notification daemons. */
	wl_list_init(&server.layer_surfaces);
	server.layer_shell = wlr_layer_shell_v1_create(server.wl_display, 4);
	server.new_layer_surface.notify = server_new_layer_surface;
	wl_signal_add(&server.layer_shell->events.new_surface,
		&server.new_layer_surface);

	/* xdg-shell version 3, matching the canonical 0.18 tinywl. */
	wl_list_init(&server.toplevels);
	server.xdg_shell = wlr_xdg_shell_create(server.wl_display, 3);
	server.new_xdg_toplevel.notify = server_new_xdg_toplevel;
	wl_signal_add(&server.xdg_shell->events.new_toplevel, &server.new_xdg_toplevel);
	server.new_xdg_popup.notify = server_new_xdg_popup;
	wl_signal_add(&server.xdg_shell->events.new_popup, &server.new_xdg_popup);

	server.cursor = wlr_cursor_create();
	wlr_cursor_attach_output_layout(server.cursor, server.output_layout);
	server.cursor_mgr = wlr_xcursor_manager_create(NULL, 24);

	server.cursor_mode = WTF_CURSOR_PASSTHROUGH;
	server.cursor_motion.notify = server_cursor_motion;
	wl_signal_add(&server.cursor->events.motion, &server.cursor_motion);
	server.cursor_motion_absolute.notify = server_cursor_motion_absolute;
	wl_signal_add(&server.cursor->events.motion_absolute,
		&server.cursor_motion_absolute);
	server.cursor_button.notify = server_cursor_button;
	wl_signal_add(&server.cursor->events.button, &server.cursor_button);
	server.cursor_axis.notify = server_cursor_axis;
	wl_signal_add(&server.cursor->events.axis, &server.cursor_axis);
	server.cursor_frame.notify = server_cursor_frame;
	wl_signal_add(&server.cursor->events.frame, &server.cursor_frame);

	wl_list_init(&server.keyboards);
	wl_list_init(&server.pointers);
	server.new_input.notify = server_new_input;
	wl_signal_add(&server.backend->events.new_input, &server.new_input);
	server.seat = wlr_seat_create(server.wl_display, "seat0");
	server.request_cursor.notify = seat_request_cursor;
	wl_signal_add(&server.seat->events.request_set_cursor, &server.request_cursor);
	server.request_set_selection.notify = seat_request_set_selection;
	wl_signal_add(&server.seat->events.request_set_selection,
		&server.request_set_selection);

	/* XWayland (lazy: the X server starts on first client connect). Managed X11
	 * toplevels reuse the xdg view_map/view_unmap/wtf_configure/wtf_focus paths;
	 * override-redirect surfaces are unmanaged scene nodes. */
	server.xwayland =
		wlr_xwayland_create(server.wl_display, compositor, true);
	if (server.xwayland == NULL) {
		wlr_log(WLR_ERROR, "failed to create wlr_xwayland; X11 apps unavailable");
	} else {
		/* Set DISPLAY NOW, not in the ready handler: display_name is valid right
		 * after create, but `ready` only fires once Xwayland actually starts —
		 * and in lazy mode that start is triggered BY a client connecting to the
		 * X socket, which can only happen if DISPLAY is already in the env. So
		 * deferring DISPLAY to ready is a chicken-and-egg deadlock for the first
		 * X11 client. Children spawned via wtf_spawn inherit this. */
		setenv("DISPLAY", server.xwayland->display_name, true);
		server.xwayland_ready.notify = server_xwayland_ready;
		wl_signal_add(&server.xwayland->events.ready, &server.xwayland_ready);
		server.new_xwayland_surface.notify = server_new_xwayland_surface;
		wl_signal_add(&server.xwayland->events.new_surface,
			&server.new_xwayland_surface);
	}

	const char *socket = wl_display_add_socket_auto(server.wl_display);
	if (!socket) {
		wlr_backend_destroy(server.backend);
		return 1;
	}

	if (!wlr_backend_start(server.backend)) {
		wlr_backend_destroy(server.backend);
		wl_display_destroy(server.wl_display);
		return 1;
	}

	setenv("WAYLAND_DISPLAY", socket, true);
	wlr_log(WLR_INFO, "WTF compositor running on WAYLAND_DISPLAY=%s", socket);

	/* Cross-thread command channel: register the eventfd so the IPC server can
	 * wake the loop and have g_cb.drain() run here, on the loop thread. */
	g_cmd_fd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
	if (g_cmd_fd >= 0) {
		wl_event_loop_add_fd(wl_display_get_event_loop(server.wl_display),
			g_cmd_fd, WL_EVENT_READABLE, handle_cmd_fd, NULL);
	}

	/* Install signal handlers before the run-loop (and before startup-app spawn)
	 * so SIGINT/SIGTERM run the clean teardown that releases the DRM master and
	 * a fatal fault is logged + core-dumped rather than freezing the screen. */
	install_signal_handlers();

	/* Compositor is live — let the brain spawn its startup clients into it. */
	if (g_cb.ready) {
		g_cb.ready();
	}

	wl_display_run(server.wl_display);

	/* The run-loop has returned: reset the graceful handlers to default and clear
	 * g_display so a late SIGINT/SIGTERM during teardown cannot call
	 * wl_display_terminate on a being-destroyed display. signal() is
	 * async-signal-safe and fine here on the normal path. */
	signal(SIGINT, SIG_DFL);
	signal(SIGTERM, SIG_DFL);
	g_display = NULL;

	/* Teardown. */
	if (server.xwayland != NULL) {
		wlr_xwayland_destroy(server.xwayland);
	}
	wl_display_destroy_clients(server.wl_display);
	wlr_scene_node_destroy(&server.scene->tree.node);
	wlr_xcursor_manager_destroy(server.cursor_mgr);
	wlr_cursor_destroy(server.cursor);
	wlr_allocator_destroy(server.allocator);
	wlr_renderer_destroy(server.renderer);
	wlr_backend_destroy(server.backend);
	wl_display_destroy(server.wl_display);
	return 0;
}
