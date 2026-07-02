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
wtfctl reload                   # re-read config.fsx and apply live
```

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
