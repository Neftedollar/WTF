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
#include <time.h>
#include <unistd.h>
#include <wayland-server-core.h>
#include <wlr/backend.h>
#include <wlr/render/allocator.h>
#include <wlr/render/wlr_renderer.h>
#include <wlr/types/wlr_cursor.h>
#include <wlr/types/wlr_compositor.h>
#include <wlr/types/wlr_data_device.h>
#include <wlr/types/wlr_input_device.h>
#include <wlr/types/wlr_keyboard.h>
#include <wlr/types/wlr_output.h>
#include <wlr/types/wlr_output_layout.h>
#include <wlr/types/wlr_pointer.h>
#include <scenefx/types/wlr_scene.h>
#include <scenefx/types/fx/blur_data.h>
#include <scenefx/types/fx/corner_location.h>
#include <scenefx/render/fx_renderer/fx_renderer.h>
#include <wlr/types/wlr_seat.h>
#include <wlr/types/wlr_subcompositor.h>
#include <wlr/types/wlr_xcursor_manager.h>
#include <wlr/types/wlr_xdg_shell.h>
#include <wlr/util/box.h>
#include <wlr/util/log.h>
#include <xkbcommon/xkbcommon.h>

#include "wtf.h"

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
	int reported_width, reported_height;

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

	struct wl_listener map;
	struct wl_listener unmap;
	struct wl_listener commit;
	struct wl_listener destroy;
	struct wl_listener request_move;
	struct wl_listener request_resize;
	struct wl_listener request_maximize;
	struct wl_listener request_fullscreen;
};

struct wtf_popup {
	struct wlr_xdg_popup *xdg_popup;
	struct wl_listener commit;
	struct wl_listener destroy;
};

struct wtf_keyboard {
	struct wl_list link;
	struct wtf_server *server;
	struct wlr_keyboard *wlr_keyboard;

	struct wl_listener modifiers;
	struct wl_listener key;
	struct wl_listener destroy;
};

/* The single global compositor instance and the brain's callbacks. The event
 * loop is single-threaded, so plain globals are correct here. */
static struct wtf_server server;
static struct wtf_callbacks g_cb;

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
	struct wlr_surface *surface = toplevel->xdg_toplevel->base->surface;
	struct wlr_surface *prev_surface = seat->keyboard_state.focused_surface;
	if (prev_surface == surface) {
		return;
	}
	if (prev_surface) {
		struct wlr_xdg_toplevel *prev_toplevel =
			wlr_xdg_toplevel_try_from_wlr_surface(prev_surface);
		if (prev_toplevel != NULL) {
			wlr_xdg_toplevel_set_activated(prev_toplevel, false);
		}
	}
	struct wlr_keyboard *keyboard = wlr_seat_get_keyboard(seat);
	wlr_scene_node_raise_to_top(&toplevel->scene_tree->node);
	wlr_xdg_toplevel_set_activated(toplevel->xdg_toplevel, true);
	if (keyboard != NULL) {
		wlr_seat_keyboard_notify_enter(seat, surface,
			keyboard->keycodes, keyboard->num_keycodes, &keyboard->modifiers);
	}

	/* Active window fully opaque + active border; the rest translucent. */
	struct wtf_toplevel *t;
	wl_list_for_each(t, &server.toplevels, link) {
		bool active = (t == toplevel);
		t->target_opacity = active ? WTF_ACTIVE_OPACITY : g_inactive_opacity;
		if (t->border != NULL) {
			wlr_scene_rect_set_color(t->border,
				active ? g_active_border : g_inactive_border);
		}
	}
	schedule_frame();
}

/* Report the primary output's effective (layout) size to the brain, but only
 * when it actually changed. */
static void report_output_size(struct wlr_output *wlr_output) {
	if (wlr_output == NULL || wlr_output != server.primary_output) {
		return;
	}
	struct wlr_box box;
	wlr_output_layout_get_box(server.output_layout, wlr_output, &box);
	if (box.width == 0 && box.height == 0) {
		/* Output not in the layout (yet); fall back to effective res. */
		wlr_output_effective_resolution(wlr_output, &box.width, &box.height);
	}
	if (box.width == server.reported_width &&
			box.height == server.reported_height) {
		return;
	}
	server.reported_width = box.width;
	server.reported_height = box.height;
	if (g_cb.output_resize) {
		g_cb.output_resize(box.width, box.height);
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
	const xkb_keysym_t *syms;
	int nsyms = xkb_state_key_get_syms(
		keyboard->wlr_keyboard->xkb_state, keycode, &syms);

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

static void server_new_keyboard(struct wtf_server *srv,
		struct wlr_input_device *device) {
	struct wlr_keyboard *wlr_keyboard = wlr_keyboard_from_input_device(device);

	struct wtf_keyboard *keyboard = calloc(1, sizeof(*keyboard));
	keyboard->server = srv;
	keyboard->wlr_keyboard = wlr_keyboard;

	struct xkb_context *context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	struct xkb_keymap *keymap = xkb_keymap_new_from_names(context, NULL,
		XKB_KEYMAP_COMPILE_NO_FLAGS);

	wlr_keyboard_set_keymap(wlr_keyboard, keymap);
	xkb_keymap_unref(keymap);
	xkb_context_unref(context);
	wlr_keyboard_set_repeat_info(wlr_keyboard, 25, 600);

	keyboard->modifiers.notify = keyboard_handle_modifiers;
	wl_signal_add(&wlr_keyboard->events.modifiers, &keyboard->modifiers);
	keyboard->key.notify = keyboard_handle_key;
	wl_signal_add(&wlr_keyboard->events.key, &keyboard->key);
	keyboard->destroy.notify = keyboard_handle_destroy;
	wl_signal_add(&device->events.destroy, &keyboard->destroy);

	wlr_seat_set_keyboard(srv->seat, keyboard->wlr_keyboard);

	wl_list_insert(&srv->keyboards, &keyboard->link);
}

static void server_new_pointer(struct wtf_server *srv,
		struct wlr_input_device *device) {
	wlr_cursor_attach_input_device(srv->cursor, device);
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
	/* Nested Wayland/X11 window was resized: tell the brain the new size. */
	report_output_size(output->wlr_output);
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
		srv->reported_width = srv->reported_height = 0;
		/* Promote another output to primary if one remains. */
		if (!wl_list_empty(&srv->outputs)) {
			struct wtf_output *next =
				wl_container_of(srv->outputs.next, next, link);
			srv->primary_output = next->wlr_output;
			report_output_size(next->wlr_output);
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
		report_output_size(wlr_output);
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

	/* Start transparent so the first wtf_configure fades + slides it in. */
	toplevel->opacity = 0.0;
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
	toplevel->server = srv;
	toplevel->xdg_toplevel = xdg_toplevel;
	/* Create the view at 0,0; the brain places it later via wtf_configure. */
	toplevel->scene_tree =
		wlr_scene_xdg_surface_create(&srv->scene->tree, xdg_toplevel->base);
	toplevel->scene_tree->node.data = toplevel;
	xdg_toplevel->base->data = toplevel->scene_tree;

	/* Colored focus border: a rect sibling kept just behind the window. */
	toplevel->border = wlr_scene_rect_create(&srv->scene->tree, 0, 0, g_inactive_border);
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

	struct wtf_popup *popup = calloc(1, sizeof(*popup));
	popup->xdg_popup = xdg_popup;

	struct wlr_xdg_surface *parent =
		wlr_xdg_surface_try_from_wlr_surface(xdg_popup->parent);
	assert(parent != NULL);
	struct wlr_scene_tree *parent_tree = parent->data;
	xdg_popup->base->data = wlr_scene_xdg_surface_create(parent_tree, xdg_popup->base);

	popup->commit.notify = xdg_popup_commit;
	wl_signal_add(&xdg_popup->base->surface->events.commit, &popup->commit);
	popup->destroy.notify = xdg_popup_destroy;
	wl_signal_add(&xdg_popup->events.destroy, &popup->destroy);
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
	/* Size is applied immediately (clients can't smoothly resize); only the
	 * scene-node position and opacity animate, in output_frame. */
	wlr_xdg_toplevel_set_size(t->xdg_toplevel, width, height);
	schedule_frame();
}

void wtf_focus(int id) {
	focus_toplevel(toplevel_by_id(id));
}

void wtf_close(int id) {
	struct wtf_toplevel *t = toplevel_by_id(id);
	if (t != NULL) {
		wlr_xdg_toplevel_send_close(t->xdg_toplevel);
	}
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

int wtf_run(struct wtf_callbacks cbs) {
	g_cb = cbs;

	wlr_log_init(WLR_INFO, NULL);

	server = (struct wtf_server){0};
	server.next_id = 0;

	server.wl_display = wl_display_create();
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
		return 1;
	}
	wlr_renderer_init_wl_display(server.renderer, server.wl_display);

	server.allocator = wlr_allocator_autocreate(server.backend, server.renderer);
	if (server.allocator == NULL) {
		wlr_log(WLR_ERROR, "failed to create wlr_allocator");
		return 1;
	}

	wlr_compositor_create(server.wl_display, 5, server.renderer);
	wlr_subcompositor_create(server.wl_display);
	wlr_data_device_manager_create(server.wl_display);

	server.output_layout = wlr_output_layout_create(server.wl_display);

	wl_list_init(&server.outputs);
	server.new_output.notify = server_new_output;
	wl_signal_add(&server.backend->events.new_output, &server.new_output);

	server.scene = wlr_scene_create();
	server.scene_layout =
		wlr_scene_attach_output_layout(server.scene, server.output_layout);

	/* scenefx: seed default blur parameters (off until wtf_set_blur enables). */
	g_blur = blur_data_get_default();
	wlr_scene_set_blur_data(server.scene, g_blur);

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
	server.new_input.notify = server_new_input;
	wl_signal_add(&server.backend->events.new_input, &server.new_input);
	server.seat = wlr_seat_create(server.wl_display, "seat0");
	server.request_cursor.notify = seat_request_cursor;
	wl_signal_add(&server.seat->events.request_set_cursor, &server.request_cursor);
	server.request_set_selection.notify = seat_request_set_selection;
	wl_signal_add(&server.seat->events.request_set_selection,
		&server.request_set_selection);

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

	/* Compositor is live — let the brain spawn its startup clients into it. */
	if (g_cb.ready) {
		g_cb.ready();
	}

	wl_display_run(server.wl_display);

	/* Teardown. */
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
