# SpiralLayout — an example WTF layout plugin

This is a worked example of WTF's **layout plugin** system (#13): a compiled
.NET assembly that adds a custom tiling layout to a running WTF, **by name**,
exactly like the built-in `tall` / `wide` / `bsp` / `grid` / `full`.

It implements a **Fibonacci spiral** layout: the focused window takes half the
screen, each subsequent window takes half of the remaining area (rotating the
cut direction so windows wind inward), and the last window fills the remainder.

## How it works

A plugin is any .NET class library that:

1. references `WTF.Core` (for `Rect`, `Stack`, `Layout`, `LayoutFactory`,
   `IWtfLayoutPlugin`) **with `<Private>false</Private>`** — see below;
2. defines one or more `Layout<WindowId>` functions
   (`Rect -> Stack<WindowId> -> (WindowId * Rect) list`);
3. exposes them through a parameterless-ctor class implementing
   `WTF.Core.IWtfLayoutPlugin`:

   ```fsharp
   type SpiralPlugin() =
       interface IWtfLayoutPlugin with
           member _.Name = "SpiralLayout"
           member _.Layouts = [ "spiral", spiralFactory ]   // (name, factory) pairs
   ```

At startup WTF scans `~/.config/wtf/plugins/`, loads each `*.dll`, finds the
`IWtfLayoutPlugin` types, and registers their `(name, factory)` pairs into the
live layout Registry. The loader owns registration — the plugin just declares
what it wants registered.

### The `<Private>false</Private>` rule (important)

The plugin **must not** ship its own copy of `WTF.Core.dll` (or
`FSharp.Core.dll`) beside it. WTF loads each plugin into the **default**
`AssemblyLoadContext`, where the host's `WTF.Core` is **already loaded**; the
plugin's `WTF.Core` reference resolves to that same copy, giving its
`IWtfLayoutPlugin` / `LayoutFactory` the **same type identity** as the host's —
which is what makes registration land in the *live* Registry. If a second
`WTF.Core.dll` were copied next to the plugin it would be a *different* identity
and the layout would silently fail to register. The `.fsproj` here pins this:

```xml
<ProjectReference Include="..\..\src\WTF.Core\WTF.Core.fsproj">
  <Private>false</Private>
</ProjectReference>
<PackageReference Update="FSharp.Core" ExcludeAssets="runtime" />
```

## Build & install

```sh
dotnet build examples/SpiralLayout -c Release
cp examples/SpiralLayout/bin/Release/net10.0/SpiralLayout.dll ~/.config/wtf/plugins/
```

(Create `~/.config/wtf/plugins/` first if it doesn't exist. Only
`SpiralLayout.dll` is copied — by design no `WTF.Core.dll` / `FSharp.Core.dll`
is emitted beside it.)

Then bind it (or set it as a workspace's layout) in `~/.config/wtf/config.fsx`:

```fsharp
bind "M-S-y" (SetLayout "spiral")
```

Restart WTF. The log prints `loaded layout "spiral" from SpiralLayout.dll`, and
`Super+Shift+Y` switches the current workspace to the spiral layout.

## Any .NET language

F# is the shipped template, but the contract is language-agnostic. A **C#**
class library works identically — same `Private=false` project reference, same
`IWtfLayoutPlugin` implementation:

```csharp
public sealed class CenteredMasterPlugin : IWtfLayoutPlugin
{
    public string Name => "CenteredMaster";
    public FSharpList<Tuple<string, LayoutFactory>> Layouts => /* ... */;
}
```

That is the "**.NET as a platform**" payoff: any compiled .NET assembly can
extend WTF at runtime.

## Notes

- **Graceful**: a bad / incompatible / throwing plugin `.dll` is logged and
  skipped — it never crashes or blocks WM startup.
- **Safe mode** (`WTF_SAFE_MODE=1`) skips plugin loading entirely (built-in
  layouts only).
- **Unload is not supported**: plugins load into the non-collectible default
  ALC (required for shared `WTF.Core` identity). Restart WTF to pick up a
  changed plugin.
- **AOT** (#15): runtime assembly loading is JIT-only; a NativeAOT build
  (`WTF_NO_PLUGINS`) compiles the loader out and falls back to built-in layouts.
