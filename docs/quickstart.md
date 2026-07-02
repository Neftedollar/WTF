# Quickstart

You just logged into WTF for the first time. The seeded config starts a status
bar and a terminal; windows tile automatically — no title bars, no overlapping,
the screen is always fully used.

`M` below is your **mod key** — `Super` (the Windows/Cmd key) by default.

## The ten keys you need on day one

| Chord | Action |
|---|---|
| `M-Return` | open a terminal |
| `M-p` | open the launcher (omnibox) |
| `M-j` / `M-k` | focus next / previous window |
| `M-S-j` / `M-S-k` | move the focused window down / up the stack |
| `M-S-c` | close the focused window |
| `M-1` … `M-9` | switch to workspace 1–9 |
| `M-S-1` … `M-S-9` | send the focused window to workspace 1–9 |
| `M-space` | cycle layouts |
| `M-S-space` | float / un-float the focused window |
| `M-S-q` | **quit WTF** (clean exit back to the greeter) |

The full map is in [Keybindings](keybindings.md).

## How tiling works

Every workspace has a **layout** and a **stack** of windows. The default
layout, `tall`, puts one *master* window on the left and stacks the rest on the
right:

```
┌────────────┬──────┐
│            │  2   │
│     1      ├──────┤
│  (master)  │  3   │
└────────────┴──────┘
```

- `M-h` / `M-l` — narrow / widen the master area (to 40% / 60%)
- `M-S-Return` — promote the focused window to master
- `M-period` / `M-comma` — more / fewer master windows
- `M-t` `M-w` `M-b` `M-g` `M-f` — jump straight to `tall`, `wide`, `bsp`,
  `grid`, `full`
- `M-equal` / `M-minus` — grow / shrink the gaps

New windows are placed by your **manage rules** — the seed config sends
`firefox` to workspace 2 and floats Picture-in-Picture windows.

## Change something — right now

Open your config and see the hot-reload:

```sh
wtf-edit          # opens ~/.config/wtf/config.fsx with F# autocomplete
```

Change `gaps 8` to `gaps 20`, save — the layout reflows instantly. A typo
doesn't kill anything: the config fails to compile, the error lands in the log,
and the last good config stays active. `M-S-r` (or `wtfctl reload`) re-reads it
on demand.

Try live control from a terminal, no config edit needed:

```sh
wtfctl state           # the whole WM state as JSON
wtfctl layout bsp      # switch the current workspace's layout
wtfctl gaps 16
wtfctl focus app firefox
```

## If it ever crashes

You lose nothing but the session: WTF restarts itself (up to 3 times in 10
seconds), then relaunches once in **safe mode** (default config, minimal
visuals), then returns you to the greeter. Every session writes a log to
`~/.local/state/wtf/`. `M-S-q` is always the clean way out. Details in
[Troubleshooting](troubleshooting.md).

## Next steps

- Make the config yours: [Configuration](configuration.md)
- Rice it — shadows, blur, dynamic wallpapers: [Appearance](appearance.md)
- Script it or hook up an agent: [wtfctl](wtfctl.md)
