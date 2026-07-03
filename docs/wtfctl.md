# wtfctl & the control socket

WTF is **agent-first**: the whole WM state is one JSON document, and every
action is a semantic command on a local socket. `wtfctl` is the human-friendly
CLI over that socket; scripts and LLM agents can speak the same protocol
directly.

## Everyday commands

```sh
wtfctl state                    # pretty-print the whole WM state (JSON)
wtfctl focus next|prev          # move focus
wtfctl focus app firefox        # focus by app id
wtfctl focus master
wtfctl swap next|prev           # move the focused window in the stack
wtfctl swap master
wtfctl layout bsp               # tall|wide|bsp|grid|full|<your custom>
wtfctl layout next
wtfctl workspace 2              # switch workspace (also: next|prev)
wtfctl move 2                   # send focused window to workspace 2
wtfctl float                    # toggle floating
wtfctl fullscreen               # toggle fullscreen
wtfctl sinkall                  # un-float everything on this workspace
wtfctl close
wtfctl spawn kitty
wtfctl master 2 | master inc | master dec
wtfctl ratio 0.6
wtfctl reload                   # re-read config.fsx and apply live (no restart)
wtfctl restart                  # restart the whole compositor into a fresh build
```

`reload` re-reads `config.fsx` in place — instant, keeps every window. `restart`
tears the compositor down and the `wtf-session` wrapper re-execs it, so a newly
**built/installed** WTF binary takes effect without a logout or reboot; windows
close (Wayland has no compositor hand-off) and the layout is restored from the
saved session. It is the apply step of the dev loop — see
[Dogfooding WTF](#dogfooding-wtf-hot-rebuildrestart) below.

Live appearance, no config edit:

```sh
wtfctl gaps 16 | gaps inc | gaps dec
wtfctl opacity 0.9              # inactive-window opacity
wtfctl anim 0.5                 # animation speed
wtfctl border width 3
wtfctl border active "#ff8800"
wtfctl border inactive "#333333"
wtfctl corners 12
wtfctl blur on|off
```

Desktop integration:

```sh
wtfctl notify "Build done" all tests green    # desktop notification
```

## Run F# against the live WM

`wtfctl eval` sends F# source to the WM's embedded compiler — the same engine
that loads your config. Hot-swap config fragments or dispatch commands:

```sh
wtfctl eval 'config { gaps 20 }'         # apply a partial config live
```

## Raw JSON (for scripts & agents)

Every friendly verb is sugar for one NDJSON line on the socket at
`$XDG_RUNTIME_DIR/wtf.sock`. Send a line, get the resulting state snapshot
back:

```sh
wtfctl '{"cmd":"focus","app":"firefox"}'

# no wtfctl needed:
echo '{"cmd":"workspace","switch":"2"}' | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/wtf.sock
echo 'state' | socat - UNIX-CONNECT:$XDG_RUNTIME_DIR/wtf.sock | jq .
```

The `state` snapshot contains every workspace, its layout and window stack,
window app-ids/titles/floating flags, the focused window, screen geometry, and
a live desktop block (notifications, battery, network, media players).

## For LLM agents

```sh
wtfctl tools                    # curated tool manifest (JSON) for agents
wtfctl ask "put the browser on workspace 2 and focus it"   # opt-in NL driver
```

`wtfctl tools` returns a machine-readable manifest of the WM's commands so an
agent can discover the vocabulary. `ask` routes a natural-language request
through a configured LLM (opt-in; requires API credentials).

The protocol is deliberately semantic — an agent says
`{"cmd":"focus","app":"firefox"}`, never "press Super+J" — so automations
survive any keybinding changes.

Protocol safety: requests are capped at 1 MB per line and 32 concurrent
clients; a malformed line returns `{"error": …}` and never disturbs the
session.

## Dogfooding WTF (hot rebuild/restart)

Running WTF as your daily compositor while hacking on it? Iterate without a
reboot. From a terminal **inside a WTF session**:

```sh
~/Dev/WTF/scripts/wtf-reload    # rebuild from your checkout, install, restart in place
```

`wtf-reload` runs `wtf-update` (rebuild the vendored scenefx + C shim + managed
host, then install **atomically** over the live prefix — a temp file per target
plus a rename, so the running compositor's mmap'd libraries are never truncated)
and then `wtfctl restart`. The updater **self-tests** the freshly installed
build and **rolls back** to the previous one if it can't even start, so a broken
build never leaves you crash-looping at login. To rebuild without applying yet,
run `scripts/wtf-update` alone and `wtfctl restart` when ready.

Under the hood `restart` makes the host exit with code **42**; the `wtf-session`
wrapper treats that as "re-exec me" (distinct from a crash). A build that hits
the restart path immediately and repeatedly (e.g. a stray `exit 42` in
`config.fsx`) can't spin forever: after a few instant re-execs the wrapper's
loop guard routes to safe mode and then a fallback shell.

Recovery if a build is bad: the wrapper escalates to `WTF_SAFE_MODE=1` and then
drops to a shell; a VT (`Ctrl+Alt+F3`) always lets you run `scripts/wtf-update`
to install a fix; and your display manager still lists GNOME (or your previous
session) as a fallback. See also
[troubleshooting](troubleshooting.md#wtf-wont-start-after-a-rebuild).
