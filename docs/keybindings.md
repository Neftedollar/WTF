# Keybindings

## Chord syntax

A binding is a chord string mapped to a semantic command:

```fsharp
bind "M-S-j" SwapNext
```

Modifiers, always in the order `M C A S`:

| Letter | Key |
|---|---|
| `M` | the mod key (`modKey` in your config: `Super` by default, or `Alt`) |
| `C` | Ctrl |
| `A` | Alt |
| `S` | Shift |

Keys — you can bind **any** key:

- letters `a`–`z`, digits `0`–`9`;
- named keys: `Return`, `space`, `Tab`, `Escape`, `BackSpace`, `Delete`, `Insert`,
  `comma`, `period`, `minus`, `equal`, `plus`, arrows (`Left`/`Right`/`Up`/`Down`),
  navigation (`Home`, `End`, `PageUp`, `PageDown`, `Print`, `Menu`), function keys
  `F1`–`F24`, and punctuation (`slash`, `backslash`, `semicolon`, `apostrophe`,
  `bracketleft`, `bracketright`, `grave`);
- **anything else by its raw xkb keysym** in lowercase hex — e.g.
  `bind "0x1008ff14" (Spawn "playerctl play-pause")` for `XF86AudioPlay`. Find a
  key's name or keysym by running `wev` (or `xev`) and pressing it.

```fsharp
bind "Print"        screenshotArea
bind "M-F1"         (Spawn "kitty -e man wtf")
bind "0x1008ff14"   (Spawn "playerctl play-pause")   // XF86AudioPlay by raw keysym
```

Only bare modifier presses (Super, Shift, …) and dead keys aren't bindable on
their own. Note: the volume/mute/brightness `XF86Audio*` keys are already handled
by the WM (they drive the MPRIS player / `wpctl`), so binding those is redundant.

Bindings are resolved against **xkb group 0** (your first layout), so they keep
working while you're typing in a second layout — `M-j` is `M-j` even when the
active layout is Russian.

## The default map

These are the built-in defaults (also active in safe mode). Your
`config.fsx` `keys` block **replaces** this map, so copy what you want to keep.

### Launch & close

| Chord | Command | Effect |
|---|---|---|
| `M-Return` | `Spawn "kitty"` | terminal |
| `M-p` | `Spawn "wofi --show drun"` | launcher (seed config uses `wtf-omnibox`) |
| `M-S-c` | `CloseFocused` | close the focused window |
| `M-S-q` | *(hard-wired)* | quit WTF — clean exit, no restart |

### Focus & stack

| Chord | Command |
|---|---|
| `M-j` / `M-k` | `Focus NextWindow` / `Focus PrevWindow` |
| `M-m` | `FocusMaster` |
| `M-S-Return` | `SwapMaster` — promote focused window to master |
| `M-S-j` / `M-S-k` | `SwapNext` / `SwapPrev` |

### Layouts

| Chord | Command |
|---|---|
| `M-space` | `NextLayout` — cycle |
| `M-t` / `M-w` / `M-b` / `M-g` / `M-f` | `SetLayout` `tall` / `wide` / `bsp` / `grid` / `full` |
| `M-h` / `M-l` | `SetRatio 0.4` / `SetRatio 0.6` — master width |
| `M-period` / `M-comma` | `IncMaster` / `DecMaster` |
| `M-equal` / `M-minus` | `IncGaps` / `DecGaps` |
| `M-S-space` | `ToggleFloat` |
| `M-S-f` | `ToggleFullscreen` |

### Workspaces

| Chord | Command |
|---|---|
| `M-1` … `M-9` | `SwitchWorkspace "1"` … `"9"` |
| `M-S-1` … `M-S-9` | `MoveToWorkspace "1"` … `"9"` |
| `M-Tab` | `NextWorkspace` |

### Session & history

| Chord | Command |
|---|---|
| `M-z` / `M-S-z` | `Undo` / `Redo` (window-arrangement history) |
| `M-S-s` | `SaveSession` |
| `M-S-r` | `ReloadConfig` — re-read `config.fsx` from disk *(seed config)* |

## Singleton launches: `once`

Mashing a launcher key shouldn't stack ten launchers. Wrap any `Spawn` in
`once` and a new instance starts only if the previous one has exited:

```fsharp
bind "M-p" (once (Spawn "wtf-omnibox"))
```

## The full command vocabulary

Anything bindable is also scriptable via [wtfctl](wtfctl.md). Commands:

`Focus (NextWindow|PrevWindow|ByApp "x"|ById n|Focused)` · `FocusMaster` ·
`SwapNext` · `SwapPrev` · `SwapMaster` · `ToggleFloat` · `ToggleFullscreen` ·
`SinkAll` · `CloseFocused` · `Spawn cmd` · `SpawnOnce cmd` ·
`SwitchWorkspace tag` · `MoveToWorkspace tag` · `NextWorkspace` ·
`PrevWorkspace` · `SetLayout name` · `NextLayout` · `SetMaster n` ·
`IncMaster` · `DecMaster` · `SetRatio f` · `SetGaps n` · `IncGaps` ·
`DecGaps` · `SetInactiveOpacity f` · `SetAnimationSpeed f` ·
`SetBorderWidth n` · `SetBorderColor …` · `SetCornerRadius n` ·
`SetBlur b` · `Undo` · `Redo` · `SaveSession` · `LoadSession` · `ReloadConfig`
