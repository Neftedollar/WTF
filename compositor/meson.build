project(
	'wtf-shim',
	'c',
	version: '0.1.0',
	default_options: ['c_std=c11', 'warning_level=2'],
)

add_project_arguments('-DWLR_USE_UNSTABLE', language: 'c')

# ---- dependencies (pkg-config names) ----
cc           = meson.get_compiler('c')
m_dep        = cc.find_library('m', required: false)  # lround/fabs (animations)
# scenefx provides the effect-capable scene graph (blur/rounded/shadows). It
# replaces wlroots' wlr_scene, so it must be listed BEFORE wlroots so its
# symbols win. Built locally into compositor/.scenefx (see scripts).
scenefx      = dependency('scenefx-0.2')
wlroots      = dependency('wlroots-0.18')
wayland_server = dependency('wayland-server')
xkbcommon    = dependency('xkbcommon')
pixman       = dependency('pixman-1')
# libinput: the shim calls libinput_device_config_* directly to apply per-device
# pointer/touchpad settings, and uses <wlr/backend/libinput.h> to get the handle.
libinput     = dependency('libinput')

# ---- generate xdg-shell protocol code via wayland-scanner ----
# wlroots requires the compositor to generate and provide the protocol glue
# itself (see tinywl's Makefile/meson.build). wlr/types/wlr_xdg_shell.h
# #includes "xdg-shell-protocol.h", so we must put the generated header on the
# include path. We also generate the private-code .c so the protocol's
# wl_interface symbols are available.
wayland_protos = dependency('wayland-protocols')
wl_protocol_dir = wayland_protos.get_variable('pkgdatadir')

wayland_scanner_dep = dependency('wayland-scanner', native: true)
wayland_scanner = find_program(
	wayland_scanner_dep.get_variable('wayland_scanner'),
	native: true,
)

xdg_shell_xml = wl_protocol_dir / 'stable/xdg-shell/xdg-shell.xml'

xdg_shell_server_header = custom_target(
	'xdg-shell-protocol.h',
	input: xdg_shell_xml,
	output: 'xdg-shell-protocol.h',
	command: [wayland_scanner, 'server-header', '@INPUT@', '@OUTPUT@'],
)

xdg_shell_code = custom_target(
	'xdg-shell-protocol.c',
	input: xdg_shell_xml,
	output: 'xdg-shell-protocol.c',
	command: [wayland_scanner, 'private-code', '@INPUT@', '@OUTPUT@'],
)

# ---- generate wlr-layer-shell protocol code via wayland-scanner ----
# wlr-protocols pkg-config is not present on this box and wlroots does not
# install the generated header, so we vendor the XML and generate the glue
# ourselves. <wlr/types/wlr_layer_shell_v1.h> #includes
# "wlr-layer-shell-unstable-v1-protocol.h", so the server-header output name
# must match exactly.
layer_shell_xml = meson.current_source_dir() / 'protocols/wlr-layer-shell-unstable-v1.xml'

layer_shell_server_header = custom_target(
	'wlr-layer-shell-unstable-v1-protocol.h',
	input: layer_shell_xml,
	output: 'wlr-layer-shell-unstable-v1-protocol.h',
	command: [wayland_scanner, 'server-header', '@INPUT@', '@OUTPUT@'],
)

layer_shell_code = custom_target(
	'wlr-layer-shell-unstable-v1-protocol.c',
	input: layer_shell_xml,
	output: 'wlr-layer-shell-unstable-v1-protocol.c',
	command: [wayland_scanner, 'private-code', '@INPUT@', '@OUTPUT@'],
)

# ---- the shared library the F# host P/Invokes (libwtf_shim.so) ----
shared_library(
	'wtf_shim',
	[
		'wtf-shim.c',
		xdg_shell_server_header,
		xdg_shell_code,
		layer_shell_server_header,
		layer_shell_code,
	],
	dependencies: [
		scenefx,
		wlroots,
		wayland_server,
		xkbcommon,
		pixman,
		libinput,
		m_dep,
	],
	# wtf.h lives next to this meson.build; the generated header lands in the
	# build dir, which meson adds to the include path automatically.
	include_directories: include_directories('.'),
	install: true,
)
