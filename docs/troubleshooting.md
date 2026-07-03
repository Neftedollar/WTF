# Troubleshooting

## Where the logs are

Every session writes a complete log:

```
~/.local/state/wtf/session-<timestamp>.log
```

The newest ~10 logs are kept. Everything is in there: compositor startup,
wlroots/GPU messages, config compile errors, crash reports with **backtraces**,
and the session wrapper's own restart decisions (look for `[HH:MM:SS]`-stamped
lines). Repeated messages are deduplicated (`… last message repeated N times`),
so the log stays readable.

Quick triage:

```sh
ls -t ~/.local/state/wtf/ | head -1                  # newest log
grep -E "host exited|FATAL|error|threw" ~/.local/state/wtf/session-*.log | tail -20
```

## The session lifecycle (what happens on a crash)

The login manager runs `wtf-session`, not the raw compositor. On an abnormal
exit it:

1. **restarts** WTF — up to 3 times within a 10-second window (and at most 20
   times per session lifetime),
2. then relaunches once in **safe mode** (`WTF_SAFE_MODE=1`),
3. then restores the console and returns you to the greeter.

Exit codes in the log: `rc=0` clean quit (`M-S-q`), `rc=139` SIGSEGV,
`rc=134` SIGABRT, `rc=135` SIGBUS, `rc=137` SIGKILL. Fatal signals dump a
backtrace into the log (resolve with `addr2line -e /usr/local/lib/wtf/libwtf_shim.so`).

## Safe mode

Safe mode is WTF with the guard rails up: your `config.fsx` is **not loaded**
(built-in defaults — `Super` mod, `kitty` terminal, `us` layout), eye-candy is
off, startup apps are skipped. You'll notice it by the missing gaps/colors and
default keybinds.

Getting out: fix `~/.config/wtf/config.fsx` (run
`dotnet fsi ~/.config/wtf/config.fsx` to see the compile error, or check the
session log), then `M-S-q` and log in again.

## Common issues

**My config change didn't apply.**
It didn't compile. The error is in the session log; the last good config stays
active. `wtf-edit` shows errors inline as you type.

**Keybinds stopped working after switching to a non-Latin layout.**
They shouldn't — binds resolve against your *first* layout. If you hit this,
check that your first `layout` entry is the Latin one (`layout "us,ru"`, not
`"ru,us"`).

**Language switching doesn't work.**
You need both layouts and a toggle in your config:
`keyboard { layout "us,ru"; options "grp:alt_shift_toggle" }`. A broken
layout string falls back to the default keymap — check the log for
`xkb keymap compile failed`.

**Screenshots are black / portals time out.**
Install `xdg-desktop-portal-wlr` (+ `-gtk` for file pickers). `grim` works
out of the box. If you launch WTF outside a display manager, `wtf-session`
exports `XDG_CURRENT_DESKTOP=wtf` for portal routing — use it rather than
raw `wtf`.

**An app window looks different from the others (own shadows/title bar).**
GTK4/libadwaita apps can't drop their headerbar (no server-side decorations in
GTK4); they still tile correctly and lose their shadows when tiled. Everything
else (terminals, Qt, Electron, Firefox) is asked to drop client decorations
automatically.

**A monitor unplug/replug did something weird.**
Bars and wallpaper clients on the unplugged output are closed (they should
auto-respawn if started from `startup`); windows stay in their workspaces. If
a bar doesn't come back, `wtfctl spawn wtf-bar`.

**The screen is black on a hybrid-GPU laptop / after docking.**
Outputs that fail to initialize are skipped with a log line
(`init_render failed` / `modeset failed`) and the next working output becomes
primary. Check the log to see which output was skipped.

**Something is frozen.**
Config compilation and heavy wallpaper decoding run off the main thread, so
the session shouldn't freeze on the software side.

The one hardware case that *can* freeze it is a **stuck DRM page-flip**: the
GPU stops accepting frames and every atomic commit fails with
`Atomic commit failed: Device or resource busy`. WTF now detects this — after
a run of failed commits it logs

```
output eDP-1 WEDGED: 30 consecutive failed atomic commits (stuck page-flip / EBUSY) — attempting modeset recovery
```

and forces a modeset to reset the connector. On success you'll see
`output eDP-1 RECOVERED after N failed commits`; if it can't recover it keeps
a throttled `still wedged` heartbeat in the log rather than freezing silently.

If your log shows this on a laptop's built-in **Intel** panel (`eDP-1`), the
usual root cause is **PSR (Panel Self Refresh)**. Disable it by adding
`i915.enable_psr=0` to the kernel command line (GRUB: `GRUB_CMDLINE_LINUX_DEFAULT`,
then `sudo update-grub` and reboot). Slightly higher power draw, no more freeze.

If a freeze ever outlasts recovery, the escape hatch is unchanged: switch to a
TTY (`Ctrl+Alt+F3`), check the newest log, and `kill <wtf pid>` — the wrapper
restarts or falls back cleanly.

## WTF won't start after a rebuild

If you dogfood WTF and a `wtf-reload` / `wtf-update` leaves it not starting:

- `wtf-update` **self-tests** the freshly installed build and **rolls back** to
  the previous one when the self-test fails, so a bad build normally never
  reaches your session at all — the command exits non-zero and nothing is
  applied. Read its output.
- If a broken build *does* run (e.g. it starts then crashes), the `wtf-session`
  wrapper retries a few times, then escalates to `WTF_SAFE_MODE=1` (all eye-candy
  and the user `config.fsx` bypassed), then drops to a fallback shell. The newest
  `~/.local/state/wtf/session-*.log` has the reason.
- The session log distinguishes an intentional restart from a crash: a line
  `host exited rc=42` is a `wtfctl restart` / `wtf-reload` re-exec, **not** a
  crash. Repeated *instant* rc=42 exits trigger the loop guard
  (`rc=42 loop guard: … treating as a crash`) — usually a stray `exit 42` or
  `wtfctl restart` reachable at startup from `config.fsx` or a startup app.
- To recover from anywhere: switch to a TTY (`Ctrl+Alt+F3`), log in, and run
  `~/Dev/WTF/scripts/wtf-update` to build + install a fix. Your display manager
  also still lists GNOME (or your previous session) as a fallback login.

## Environment variables

| Variable | Effect |
|---|---|
| `WTF_SAFE_MODE=1` | safe mode: default config, minimal visuals, no startup apps |
| `WTF_DEBUG_KEYS=1` | log unbound key presses (off by default — it's a keylogger) |
| `WTF_HOST=/path` | which binary `wtf-session` launches |
| `WTF_SELFTEST=1` | JIT the managed assemblies and exit 0 without starting the compositor (used by `wtf-update` to validate a build before applying it) |

## Reporting a bug

Attach: the newest `session-*.log`, your `config.fsx`, `uname -m`, GPU/driver,
and the output of `wtfctl state` if the session is still alive. If there was a
crash, the log already contains the signal and backtrace.
