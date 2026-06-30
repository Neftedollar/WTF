# Editing your WTF config with autocomplete

Your window manager is configured in F# — `~/.config/wtf/config.fsx` is a real
program the WM compiles at launch (the xMonad idea). WTF ships a **config Type
Provider** that makes that file *strongly-typed and machine-aware*: in an editor
with the F# language server you get autocomplete and typo-checking driven by
**your** machine.

- Type `Layouts.` → the valid layout names (`Bsp`, `Tall`, `Wide`, `Grid`, `Full`).
- Type `Apps.` → **your installed** `.desktop` apps, e.g. `Apps.Firefox.AppId`.
- Type `Xkb.Layouts.` / `Xkb.Options.` → keyboard layouts / options.

A typo like `SetLayout "tll"`, or a rule for an app you don't have installed,
becomes a **compile error** — a red squiggle in the editor, *and* a config-load
error in the WM (which then falls back to the built-in default so the WM still
starts). This is the same FSharp.Compiler.Service that the WM uses to load the
config, so what the editor flags is exactly what the WM would reject.

## TL;DR

```sh
wtf-edit            # ensures the F# LSP is installed, fixes the #r paths, opens $EDITOR
wtf-edit --setup    # just verify the LSP + print per-editor setup snippets
```

`wtf-edit` (installed to `/usr/local/bin` by `scripts/install.sh`):

1. ensures **fsautocomplete** (the standard F# language server) is installed,
   running `dotnet tool install -g fsautocomplete` if it is missing;
2. makes sure `config.fsx` exists and its two `#r` lines point at the **real**
   on-disk `WTF.Core.dll` + `WTF.TypeProviders.dll` (so the LSP can resolve them);
3. opens it in `$VISUAL` / `$EDITOR`.

## How it works (honest framing)

- The **autocomplete is editor-time**: it comes from `fsautocomplete`, which loads
  the Type Provider exactly like the compiler does. There is no WTF-specific LSP —
  we enable the existing F# one.
- For a bare `.fsx`, the language server resolves types from the **`#r` directives
  at the top of the file**. That is why `config.fsx` references both
  `WTF.Core.dll` (the config DSL) and `WTF.TypeProviders.dll` (the providers), and
  why `wtf-edit` keeps those paths correct.
- The **validation is config-load-time**: when the WM loads `config.fsx` through
  FCS, the same provider runs, so a bad config is caught there too (graceful
  fallback to the default).
- The provider reads your machine state (installed apps, layouts, xkb) **at the
  moment the editor / WM loads the script**. Install a new app, then reload the
  file (or restart the LSP) to see it in `Apps.`.

The two `#r` lines are guarded by `#if !WTF_RUNTIME`. When the WM loads the file
it defines `WTF_RUNTIME` and injects its own references, so the dev/installed
paths in the file are only used by the editor and by `dotnet fsi`.

## Per-editor setup

You only need **fsautocomplete** on your `PATH` (a `dotnet tool install -g
fsautocomplete` puts it in `~/.dotnet/tools`). Then point your editor's F# LSP at
it.

### VSCode (Ionide)

Install the **Ionide-fsharp** extension — it bundles/uses fsautocomplete. Open the
config folder:

```sh
code ~/.config/wtf
```

Open `config.fsx`; IntelliSense for a bare script comes from the file's `#r`
lines (kept correct by `wtf-edit`).

### Neovim (nvim-lspconfig)

```lua
require('lspconfig').fsautocomplete.setup {
  -- the LSP binary installed by `dotnet tool install -g fsautocomplete`
  cmd = { 'fsautocomplete', '--adaptive-lsp-server-enabled' },
  filetypes = { 'fsharp' },
  root_dir = require('lspconfig.util').root_pattern('.config/wtf', '*.fsx'),
}
```

Make sure `*.fsx` is detected as `fsharp` (nvim does this by default).

### Emacs (eglot or lsp-mode)

With **eglot**:

```elisp
(require 'fsharp-mode)
(with-eval-after-load 'eglot
  (add-to-list 'eglot-server-programs
               '(fsharp-mode . ("fsautocomplete"))))
(add-hook 'fsharp-mode-hook #'eglot-ensure)
```

`lsp-mode` + `lsp-fsharp` works too and uses fsautocomplete out of the box.

### Helix (`languages.toml`)

```toml
[language-server.fsautocomplete]
command = "fsautocomplete"

[[language]]
name = "fsharp"
language-servers = ["fsautocomplete"]
```

## Troubleshooting

- **No completions / squiggles everywhere** — the `#r` paths can't be resolved.
  Run `wtf-edit` (it rewrites them to the detected dlls) and confirm the two
  `WTF.Core.dll` / `WTF.TypeProviders.dll` files exist.
- **`Apps.` is empty** — the provider scans `/usr/share/applications`,
  `~/.local/share/applications` and `$XDG_DATA_DIRS/applications`. A missing dir is
  skipped silently (the provider never throws). Reload the file after installing an
  app.
- **`fsautocomplete: command not found`** — `dotnet tool install -g fsautocomplete`,
  then add `~/.dotnet/tools` to your `PATH`.
