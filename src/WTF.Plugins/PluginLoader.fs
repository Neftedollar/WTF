namespace WTF.Plugins

// =============================================================================
// The plugin LOADER — the ".NET as a platform" payoff (#13).
//
// A user drops a compiled .dll (any .NET language) implementing
// `WTF.Core.IWtfLayoutPlugin` into ~/.config/wtf/plugins/. At startup this loader
// scans that dir, loads each assembly, instantiates the plugin types, and feeds
// their (name, factory) pairs into the live `WTF.Core.Registry` — so their custom
// layouts become available by name, exactly like the built-ins.
//
// WTF.Core stays pure: ALL the reflection / IO / AssemblyLoadContext lives HERE.
//
// THE ALC TYPE-IDENTITY GOTCHA (the #1 thing to get right):
//   A plugin references WTF.Core. If we loaded it into a SEPARATE
//   AssemblyLoadContext that also loaded its OWN copy of WTF.Core, the plugin's
//   IWtfLayoutPlugin / LayoutFactory would be DIFFERENT Type identities from the
//   host's — the `:?> IWtfLayoutPlugin` cast would fail, and even if it didn't,
//   `Registry.register` would land in a DIFFERENT Registry the host never reads.
//   SOLUTION: load each plugin into `AssemblyLoadContext.Default` via
//   LoadFromAssemblyPath. The host already loaded WTF.Core (and FSharp.Core) into
//   Default; when the plugin's metadata reference to WTF.Core is resolved, Default
//   reuses the already-loaded host copy by simple name — SAME Type identity. The
//   example plugin references WTF.Core with <Private>false</Private> so it does
//   NOT drop a second WTF.Core.dll beside itself.
//   Belt-and-suspenders: we subscribe `Default.Resolving` ONCE and, for any
//   request whose simple name matches an already-loaded assembly (e.g. WTF.Core,
//   FSharp.Core), return the host's loaded instance — so even a stray copied dll
//   in the plugins dir is ignored.
//   Default ALC is NOT collectible, so plugin UNLOAD is unsupported for now. A
//   collectible ALC would force its own Core copy and break shared identity —
//   explicitly rejected. (Mirrors how FsiConfigEngine injects the host's WTF.Core
//   path + --define:WTF_RUNTIME to avoid a second Core identity in FSI.)
//
// AOT-isolation seam (#15): the host depends ONLY on `IPluginLoader` +
// `PluginLoader.create`. `ReflectionPluginLoader` is the ONLY type touching
// reflection/ALC, and it is compiled out under WTF_NO_PLUGINS (the AOT build),
// leaving `NullPluginLoader`. Mirrors WTF_NO_FCS / IConfigEngine in WTF.Config.
// =============================================================================

open System

/// The seam the host wires to. `LoadAll` is best-effort + NEVER throws: it loads
/// whatever it can and skips the rest. Implemented by `ReflectionPluginLoader`
/// (normal JIT build) and `NullPluginLoader` (AOT / safe-mode fallback).
type IPluginLoader =
    /// Scan the plugins dir, load every valid plugin, and `Registry.register`
    /// each layout. Called ONCE at startup, BEFORE the first arrange, so config /
    /// keybinds referencing a plugin layout name resolve. NEVER throws.
    abstract member LoadAll: unit -> unit

/// Resolve the plugins directory: $XDG_CONFIG_HOME/wtf/plugins, falling back to
/// ~/.config/wtf/plugins (same shape as `ConfigPath.resolve`).
module PluginPath =
    let resolve () : string =
        let baseDir =
            match Environment.GetEnvironmentVariable "XDG_CONFIG_HOME" with
            | null | "" ->
                IO.Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".config")
            | d -> d
        IO.Path.Combine(baseDir, "wtf", "plugins")

/// No-op loader: used under WTF_NO_PLUGINS (AOT — no reflective load path linked)
/// and under WTF_SAFE_MODE (minimal known-good: built-in layouts only).
type NullPluginLoader() =
    interface IPluginLoader with
        member _.LoadAll() = ()

#if !WTF_NO_PLUGINS
open System.IO
open System.Reflection
open System.Runtime.Loader
open WTF.Core

/// Reflection/ALC-backed loader. Loads each *.dll in the plugins dir into the
/// Default ALC (shared WTF.Core identity), discovers IWtfLayoutPlugin types, and
/// registers their layouts. GRACEFUL: per-assembly AND per-type try/with — a
/// bad / incompatible / throwing dll logs + is skipped, never crashing startup.
type ReflectionPluginLoader(pluginDir: string) =

    let log (msg: string) = eprintfn "WTF.Plugins: %s" msg

    // Install the Default.Resolving guard exactly ONCE per process. For any
    // dependency request whose simple name matches an assembly ALREADY loaded in
    // the Default context (notably WTF.Core / FSharp.Core), hand back the host's
    // loaded instance — so the plugin's WTF.Core reference can NEVER resolve to a
    // stray copy beside the plugin, guaranteeing shared Type identity.
    static let mutable guardInstalled = false
    static let installGuard () =
        if not guardInstalled then
            guardInstalled <- true
            AssemblyLoadContext.Default.add_Resolving (
                Func<AssemblyLoadContext, AssemblyName, Assembly>(fun _ name ->
                    AppDomain.CurrentDomain.GetAssemblies()
                    |> Array.tryFind (fun a ->
                        not a.IsDynamic
                        && String.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    |> Option.toObj))

    /// Register one plugin instance's layouts into the live host Registry.
    let registerPlugin (file: string) (plugin: IWtfLayoutPlugin) =
        // Track names as we go (not a once-snapshotted set) so an INTRA-plugin
        // duplicate (two Layouts entries with the SAME name) also warns — the
        // second silently overwriting the first is exactly the kind of collision
        // the warning exists to surface.
        let mutable existing = Registry.names () |> Set.ofList
        for (name, factory) in plugin.Layouts do
            // A collision with a built-in (or an earlier-registered layout)
            // overwrites: last-registered wins — warn so it is diagnosable.
            if existing.Contains name then
                log (sprintf "WARNING: layout \"%s\" from %s overrides an existing layout (last wins)"
                        name (Path.GetFileName file))
            Registry.register name factory
            existing <- existing.Add name
            log (sprintf "loaded layout \"%s\" from %s (plugin: %s)"
                    name (Path.GetFileName file) plugin.Name)

    /// Register an in-process SURFACE plugin (2c): a bar or an overlay. Same
    /// last-wins-with-warning discipline as layouts, but the collision key is the
    /// surface Name and the sink is SurfaceRegistry (which the host reads to drive
    /// wtf_set_bar / wtf_set_overlay).
    let registerBar (file: string) (p: IWtfBarPlugin) =
        if SurfaceRegistry.hasBar p.Name then
            log (sprintf "WARNING: bar surface \"%s\" from %s overrides an existing bar (last wins)"
                    p.Name (Path.GetFileName file))
        SurfaceRegistry.registerBar p
        log (sprintf "loaded bar surface \"%s\" from %s" p.Name (Path.GetFileName file))

    let registerOverlay (file: string) (p: IWtfOverlayPlugin) =
        if SurfaceRegistry.hasOverlay p.Name then
            log (sprintf "WARNING: overlay surface \"%s\" from %s overrides an existing overlay (last wins)"
                    p.Name (Path.GetFileName file))
        SurfaceRegistry.registerOverlay p
        log (sprintf "loaded overlay surface \"%s\" from %s" p.Name (Path.GetFileName file))

    /// Register a WORKSPACE-TYPE plugin (#5): its named types flow into
    /// WorkspaceRegistry (which the reducer resolves per workspace). Same
    /// last-wins-with-warning discipline; collision key is the type name. NOTE:
    /// "stack" is a built-in and overriding it warns like any other.
    let registerWorkspace (file: string) (p: IWtfWorkspacePlugin) =
        for (name, arranger) in p.WorkspaceTypes do
            if WorkspaceRegistry.has name then
                log (sprintf "WARNING: workspace type \"%s\" from %s overrides an existing type (last wins)"
                        name (Path.GetFileName file))
            WorkspaceRegistry.register name arranger
            log (sprintf "loaded workspace type \"%s\" from %s (plugin: %s)"
                    name (Path.GetFileName file) p.Name)

    /// Register an in-process EFFECT plugin (#6): its named strategies flow into
    /// EffectRegistry (which the host resolves by the config's `effectStrategy`
    /// name). Same last-wins-with-warning discipline; collision key is the strategy
    /// name. NOTE: "none" is a built-in and overriding it warns like any other.
    let registerEffect (file: string) (p: IWtfEffectPlugin) =
        for (name, strategy) in p.Strategies do
            if EffectRegistry.has name then
                log (sprintf "WARNING: effect strategy \"%s\" from %s overrides an existing strategy (last wins)"
                        name (Path.GetFileName file))
            EffectRegistry.register name strategy
            log (sprintf "loaded effect strategy \"%s\" from %s (plugin: %s)"
                    name (Path.GetFileName file) p.Name)

    /// Load + register every plugin in ONE assembly. Per-type try/with so one bad
    /// type (e.g. a throwing ctor) does not abort the rest of the assembly.
    let loadAssembly (file: string) =
        try
            let asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath file)
            // GetTypes can throw ReflectionTypeLoadException if SOME types fail to
            // load; recover the ones that did load.
            let types =
                try asm.GetTypes()
                with :? ReflectionTypeLoadException as ex ->
                    ex.Types |> Array.filter (fun t -> not (isNull t))
            for t in types do
                try
                    // A type may implement ANY of the extension interfaces (even
                    // several at once — a layout that is also a bar); discover all
                    // three in the SAME scan and register the ONE instance into
                    // every registry it satisfies.
                    let isLayout = typeof<IWtfLayoutPlugin>.IsAssignableFrom t
                    let isBar = typeof<IWtfBarPlugin>.IsAssignableFrom t
                    let isOverlay = typeof<IWtfOverlayPlugin>.IsAssignableFrom t
                    let isEffect = typeof<IWtfEffectPlugin>.IsAssignableFrom t
                    let isWorkspace = typeof<IWtfWorkspacePlugin>.IsAssignableFrom t
                    if (isLayout || isBar || isOverlay || isEffect || isWorkspace)
                       && not t.IsAbstract
                       && not t.IsInterface then
                        // A plugin type with NO public parameterless ctor can't be
                        // reflectively instantiated — skip it, but LOG so the user
                        // gets a diagnostic (otherwise their extension silently
                        // never appears with no clue why).
                        if t.GetConstructor(Type.EmptyTypes) = null then
                            log (sprintf "skipped type %s in %s: no public parameterless constructor (plugin types need one)"
                                    t.FullName (Path.GetFileName file))
                        else
                            let instance = Activator.CreateInstance t
                            // Per-FACET try/with: a type may satisfy several interfaces
                            // (a layout that is also a bar); one facet's registration
                            // throwing — e.g. a user `Layouts` getter that raises — must
                            // not suppress the OTHERS, which the single outer try would
                            // (it skips the whole type). Extends the existing per-assembly
                            // / per-type graceful discipline one level down.
                            let facet kind (reg: unit -> unit) =
                                try reg ()
                                with ex ->
                                    log (sprintf "skipped %s facet of %s in %s: %s"
                                            kind t.FullName (Path.GetFileName file) ex.Message)
                            match instance with :? IWtfLayoutPlugin as p -> facet "layout" (fun () -> registerPlugin file p) | _ -> ()
                            match instance with :? IWtfBarPlugin as p -> facet "bar" (fun () -> registerBar file p) | _ -> ()
                            match instance with :? IWtfOverlayPlugin as p -> facet "overlay" (fun () -> registerOverlay file p) | _ -> ()
                            match instance with :? IWtfEffectPlugin as p -> facet "effect" (fun () -> registerEffect file p) | _ -> ()
                            match instance with :? IWtfWorkspacePlugin as p -> facet "workspace" (fun () -> registerWorkspace file p) | _ -> ()
                with ex ->
                    log (sprintf "skipped type %s in %s: %s" t.FullName (Path.GetFileName file) ex.Message)
        with ex ->
            // BadImageFormatException (not a managed dll), missing deps, etc.
            log (sprintf "skipped %s: %s" (Path.GetFileName file) ex.Message)

    interface IPluginLoader with
        member _.LoadAll() =
            try
                if not (Directory.Exists pluginDir) then
                    log (sprintf "no plugins dir (%s) — skipping" pluginDir)
                else
                    installGuard ()
                    let dlls = Directory.GetFiles(pluginDir, "*.dll")
                    if dlls.Length = 0 then
                        log (sprintf "no plugin dlls in %s" pluginDir)
                    else
                        for file in dlls do
                            loadAssembly file
            with ex ->
                // Absolute last-resort guard: LoadAll NEVER throws.
                log (sprintf "plugin scan failed: %s" ex.Message)
#endif

/// Factory: the host calls this. Returns the reflective loader in the normal JIT
/// build; the Null loader in safe-mode (WTF_SAFE_MODE=1 — built-in layouts only,
/// so a bad plugin can't wedge recovery) or when reflection is compiled out
/// (WTF_NO_PLUGINS, the AOT build). Mirrors `ConfigEngine.create`.
module PluginLoader =

    let private safeMode () =
        Environment.GetEnvironmentVariable "WTF_SAFE_MODE" = "1"

    /// Loader for the user's standard plugin dir ($XDG_CONFIG_HOME/wtf/plugins).
    let create () : IPluginLoader =
#if WTF_NO_PLUGINS
        NullPluginLoader() :> IPluginLoader
#else
        if safeMode () then NullPluginLoader() :> IPluginLoader
        else ReflectionPluginLoader(PluginPath.resolve ()) :> IPluginLoader
#endif

    /// Loader bound to an explicit dir (used by tests). The reflective loader
    /// when available (safe-mode is irrelevant for a direct path); Null under AOT.
    let createForPath (dir: string) : IPluginLoader =
#if WTF_NO_PLUGINS
        ignore dir
        NullPluginLoader() :> IPluginLoader
#else
        ReflectionPluginLoader(dir) :> IPluginLoader
#endif
