# WTF NativeAOT build

WTF builds two ways. The **default (JIT) build is unchanged** and ships every
feature. The **AOT build** is an additive `-p:WtfAot=true` flavor that trades the
reflection/JIT-only subsystems for a small, fast-starting native binary.

## Feature matrix

| Capability                                                      | JIT build (default)                      | AOT build (`-p:WtfAot=true`)        |
|-----------------------------------------------------------------|------------------------------------------|-------------------------------------|
| Core tiling WM (layouts, workspaces, keybinds, undo/redo, sessions) | yes                                  | yes                                 |
| Wallpaper (solid + image)                                       | yes (ImageSharp)                         | yes (ImageSharp)                    |
| C shim / wlroots interop                                        | yes                                      | yes                                 |
| Agent control socket (query / act / tools)                      | yes                                      | yes                                 |
| `{"notify"}` socket verb                                        | yes (D-Bus notification)                 | degraded (no-op "notified")         |
| config.fsx hot-reload + live REPL `{"eval"}` (FCS)              | yes                                      | NO — built-in default config only   |
| Reflective layout plugins (`~/.config/wtf/plugins`)            | yes                                      | NO — built-in layouts only          |
| D-Bus desktop shell (notifications, battery, network, MPRIS media keys) | yes                             | NO                                  |
| In-process LLM agent (`{"ask"}`, Anthropic)                    | yes                                      | NO (`"agent disabled (AOT build)"`) |
| Startup / runtime                                               | self-contained .NET (~76 MB), JIT warmup | native binary, fast start, low mem  |
| Reconfigure                                                     | edit config.fsx, hot-reloads live        | recompile the binary (xMonad-style) |

The AOT build drops **FSharp.Compiler.Service**, **Tmds.DBus**,
**Microsoft.Extensions.AI** and **Anthropic.SDK** from the binary because they rely
on JIT / reflection-emit / dynamic-code that NativeAOT cannot include. The
configuration tradeoff mirrors xMonad: the JIT build is `xmonad.hs` you reload live;
the AOT build is a statically compiled WM you rebuild to reconfigure.

## How the flavors are wired

* `WTF.Host.fsproj` makes the `WTF.Config`, `WTF.Plugins`, `WTF.Desktop` and
  `WTF.Agent` `ProjectReference`s conditional on `'$(WtfAot)' != 'true'`. That is
  what actually keeps the reflection packages out of the ILC closure. `WTF.Core`
  is referenced unconditionally — it is the pure brain and the heart of the binary.
* The `WtfAot` PropertyGroup defines `WTF_NO_FCS;WTF_NO_PLUGINS;WTF_NO_DESKTOP;WTF_NO_AGENT`
  and sets `IsAotCompatible` + `InvariantGlobalization`.
* In `Program.fs`, every call into those four subsystems is routed through a small
  set of `#if`-guarded host-local shims (`loadConfig`, `startWatching`, `handleEval`,
  `desktopSnapshot`, `desktopNotify`, `desktopStart`, `tryHandleMedia`, `loadPlugins`,
  and the agent block). Each has a JIT body that calls the real subsystem and an AOT
  no-op/fallback body, so the rest of the file is identical across both builds and
  the **default JIT build is byte-for-byte unchanged**.

`WTF_NO_FCS` and `WTF_NO_PLUGINS` reuse the existing `IConfigEngine`/`NullConfigEngine`
and `IPluginLoader`/`NullPluginLoader` seams; `WTF_NO_DESKTOP`/`WTF_NO_AGENT` are
new host-only symbols (the host references Desktop/Agent directly).

## Build the lean graph (no clang needed)

    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
    dotnet build src/WTF.Host/WTF.Host.fsproj -c Release -p:WtfAot=true

This compiles the exact AOT dependency closure (Core + lean Host + FSharp.Core +
ImageSharp — verify with `ls src/WTF.Host/bin/Release/net10.0/*.dll`) and runs the
gating. It is the **verifiable proof the code is AOT-shaped**. It does NOT invoke
ILC/clang.

## Produce the native binary (needs clang)

NativeAOT links the native image with **clang** on Linux (gcc is not sufficient). On
Debian/Ubuntu:

    sudo apt install clang zlib1g-dev
    bash scripts/aot-publish.sh        # publishes + prints the real binary size

Without clang the script prints the install hint and stops; nothing else is blocked.
The script never fabricates a size — it prints `du -h` of the binary only after a
real publish.

## Analyzer / readiness note (honest)

`IsAotCompatible` enables the Roslyn ILLink trim/AOT analyzers, but those analyzers
only attach to **C#** compilations. WTF.Core and WTF.Host are **F#**, so
`dotnet build -p:WtfAot=true` emits **no IL2xxx/IL3xxx warnings** — the analyzer
signal the flag normally gives is not available at build time for this codebase. The
real IL-level AOT warnings come from **ILC during `dotnet publish -p:PublishAot=true`**,
which runs before the clang link step (so it needs clang to complete). The
verifiable readiness signal without clang is therefore: (1) the lean graph compiles,
(2) the reflection packages are absent from the output closure, (3) WTF.Core is pure
(no Reflection.Emit / Assembly.Load / dynamic; JSON via the reflection-free
`System.Text.Json.Nodes` API). `IsAotCompatible` is still set on Core: it propagates
`IsTrimmable`, documents intent, and makes the eventual publish treat ILC warnings as
build-relevant.

## Callback interop note

The six C->F# compositor callbacks (`onViewMap`/`onViewUnmap`/`onKey`/`onOutputResize`/
`onReady`/`onDrain`) use rooted **concrete delegates** +
`Marshal.GetFunctionPointerForDelegate` (`Program.fs`). This is NativeAOT-compatible:
ILC synthesizes the reverse-pinvoke marshalling thunk at compile time because every
delegate type is statically known and instantiated (no dynamic code → no IL3050), and
all signatures are blittable (int/uint32/nativeint/double; no string/array marshalling
in the ABI). The delegates are GC-rooted for the whole run (`let d* = …` across
`wtf_run` + `GC.KeepAlive`). `[<UnmanagedCallersOnly>]` would shave one thunk per
event but is fragile in F# (no clean `&Method` / `delegate*<…>` managed-function-pointer
syntax), so the delegate approach is kept by design. Finally confirmed by the ILC pass
during PublishAot.
