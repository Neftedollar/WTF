module WTF.Plugins.Tests.PluginLoaderTests

// =============================================================================
// The plugin LOAD test — proves the ".NET as a platform" extension point (#13)
// end to end:
//   * build the example SpiralLayout plugin (forced by the fsproj ProjectReference),
//   * copy ONLY its dll into a fresh temp dir (asserting no WTF.Core.dll /
//     FSharp.Core.dll is emitted beside it — the <Private>false</Private> proof),
//   * point the loader at that dir and run LoadAll,
//   * assert the "spiral" layout actually registered in the LIVE host Registry AND
//     arranges windows with the expected spiral geometry.
// A correct `Registry.resolve "spiral"` PROVES the ALC type identity is right: the
// plugin's `register` reached the HOST's Registry (a separate WTF.Core identity
// would have registered into a different Registry, so resolve would be None).
//
// Plus the GRACEFUL test: a garbage dll (BadImageFormatException) alongside the
// good plugin is logged + skipped, LoadAll never throws, and "spiral" still loads.
// =============================================================================

open System
open System.IO
open Xunit
open WTF.Core
open WTF.Plugins

// --- locate the built example plugin dll on disk ---------------------------

/// Walk up from a starting dir until we find the repo root (contains WTF.slnx).
let private repoRoot () =
    let mutable dir = DirectoryInfo(AppContext.BaseDirectory)
    while not (isNull dir) && not (File.Exists(Path.Combine(dir.FullName, "WTF.slnx"))) do
        dir <- dir.Parent
    if isNull dir then failwith "could not locate repo root (WTF.slnx) from test base dir"
    dir.FullName

/// The build config (Debug/Release) the test itself was built in — the example
/// plugin is forced to build in the SAME config via the ProjectReference.
let private buildConfig () =
    let baseDir = AppContext.BaseDirectory.Replace('\\', '/')
    if baseDir.Contains "/Release/" then "Release" else "Debug"

/// Absolute path to the freshly-built SpiralLayout.dll.
let private spiralDllPath () =
    let root = repoRoot ()
    let preferred =
        Path.Combine(root, "examples", "SpiralLayout", "bin", buildConfig (), "net10.0", "SpiralLayout.dll")
    if File.Exists preferred then preferred
    else
        // Fallback: any config that happens to be built.
        let baseBin = Path.Combine(root, "examples", "SpiralLayout", "bin")
        match
            (if Directory.Exists baseBin then
                Directory.GetFiles(baseBin, "SpiralLayout.dll", SearchOption.AllDirectories)
             else [||])
            |> Array.tryHead
        with
        | Some p -> p
        | None -> failwithf "SpiralLayout.dll not built (looked under %s)" baseBin

/// Make a fresh empty temp plugins dir.
let private freshDir () =
    let d = Path.Combine(Path.GetTempPath(), "wtf-plugins-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

// --- tests -----------------------------------------------------------------

[<Fact>]
let ``example plugin emits only its own dll (Private=false proof)`` () =
    let dll = spiralDllPath ()
    let dir = Path.GetDirectoryName dll
    // The plugin's build output must NOT contain a second WTF.Core / FSharp.Core
    // copy — otherwise the plugin would load a distinct WTF.Core identity at runtime.
    Assert.False(File.Exists(Path.Combine(dir, "WTF.Core.dll")),
        "WTF.Core.dll must NOT be emitted beside the plugin (set <Private>false</Private>)")
    Assert.False(File.Exists(Path.Combine(dir, "FSharp.Core.dll")),
        "FSharp.Core.dll must NOT be emitted beside the plugin (ExcludeAssets=runtime)")

[<Fact>]
let ``loads the spiral plugin and it arranges windows correctly`` () =
    // "spiral" is a PLUGIN layout, not a built-in (the built-ins are
    // tall/wide/bsp/grid/full). We don't assert its absence up front because the
    // process-global Registry is shared across tests in this class (another test
    // may have loaded it first) — the post-conditions below are what matter.
    let dir = freshDir ()
    File.Copy(spiralDllPath (), Path.Combine(dir, "SpiralLayout.dll"))

    // Run the real loader against the temp dir.
    let loader = PluginLoader.createForPath dir
    loader.LoadAll()

    // 1) The plugin layout registered into the LIVE host Registry.
    Assert.Contains("spiral", Registry.names ())

    // 2) resolve succeeds (proves shared WTF.Core identity — wrong identity =>
    //    register hit a different Registry => resolve None here).
    let layout = Registry.resolve "spiral" 1 0.5
    Assert.True(layout.IsSome, "spiral should resolve from the live Registry")

    // 3) it produces the expected spiral geometry for 3 windows on 1920x1080.
    let stack = (Stack.ofList [ 1; 2; 3 ]).Value
    let rects = layout.Value (Rect.create 0 0 1920 1080) stack
    let expected =
        [ 1, Rect.create 0 0 960 1080      // focused: left half
          2, Rect.create 960 0 960 540     // top of the remainder
          3, Rect.create 960 540 960 540 ] // bottom of the remainder
    Assert.Equal<(WindowId * Rect) list>(expected, rects)

    Directory.Delete(dir, true)

[<Fact>]
let ``bad and incompatible dlls are skipped gracefully`` () =
    let dir = freshDir ()
    // A garbage "dll" — not a managed assembly => BadImageFormatException on load.
    File.WriteAllBytes(Path.Combine(dir, "bad.dll"), [| 0uy; 1uy; 2uy; 3uy; 4uy; 5uy |])
    // A zero-byte dll, another way to be unloadable.
    File.WriteAllBytes(Path.Combine(dir, "empty.dll"), [||])
    // ...alongside the good plugin.
    File.Copy(spiralDllPath (), Path.Combine(dir, "SpiralLayout.dll"))

    let loader = PluginLoader.createForPath dir
    // Must NOT throw despite the bad dlls.
    let ex = Record.Exception(fun () -> loader.LoadAll())
    Assert.Null(ex)

    // The good plugin still registered; the bad ones were simply skipped.
    Assert.Contains("spiral", Registry.names ())

    Directory.Delete(dir, true)

[<Fact>]
let ``missing plugins dir is a graceful no-op`` () =
    let missing = Path.Combine(Path.GetTempPath(), "wtf-plugins-absent-" + Guid.NewGuid().ToString("N"))
    Assert.False(Directory.Exists missing)
    let loader = PluginLoader.createForPath missing
    let ex = Record.Exception(fun () -> loader.LoadAll())
    Assert.Null(ex)

[<Fact>]
let ``an existing but empty plugins dir is a graceful no-op`` () =
    let dir = freshDir ()
    // No *.dll files at all.
    let loader = PluginLoader.createForPath dir
    let names0 = Registry.names ()
    let ex = Record.Exception(fun () -> loader.LoadAll())
    Assert.Null(ex)
    // Nothing was added to the live Registry.
    Assert.Equal<string list>(names0, Registry.names ())
    Directory.Delete(dir, true)

[<Fact>]
let ``non-dll files and subdirectories are ignored (GetFiles is non-recursive)`` () =
    let dir = freshDir ()
    File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a plugin")
    File.WriteAllText(Path.Combine(dir, "config.fsx"), "let x = 1")
    // A subdirectory that itself contains a .dll must NOT be scanned (non-recursive).
    let sub = Directory.CreateDirectory(Path.Combine(dir, "nested")).FullName
    File.Copy(spiralDllPath (), Path.Combine(sub, "SpiralLayout.dll"))
    let loader = PluginLoader.createForPath dir
    let ex = Record.Exception(fun () -> loader.LoadAll())
    Assert.Null(ex)
    Directory.Delete(dir, true)

[<Fact>]
let ``a managed dll with no IWtfLayoutPlugin type registers nothing`` () =
    // WTF.Core.dll is a valid managed assembly that defines the IWtfLayoutPlugin
    // INTERFACE but no concrete plugin — loading it must register nothing.
    let dir = freshDir ()
    let coreDll = Path.Combine(AppContext.BaseDirectory, "WTF.Core.dll")
    Assert.True(File.Exists coreDll, "WTF.Core.dll should sit beside the test assembly")
    File.Copy(coreDll, Path.Combine(dir, "WTF.Core.dll"))
    let before = Registry.names ()
    let loader = PluginLoader.createForPath dir
    let ex = Record.Exception(fun () -> loader.LoadAll())
    Assert.Null(ex)
    Assert.Equal<string list>(before, Registry.names ())
    Directory.Delete(dir, true)

// --- the test-fixture assembly: multiple layouts / collision / bad ctors -----

/// Absolute path to the freshly-built FixturePlugins.dll (located like the spiral).
let private fixtureDllPath () =
    let root = repoRoot ()
    let preferred =
        Path.Combine(root, "examples", "FixturePlugins", "bin", buildConfig (), "net10.0", "FixturePlugins.dll")
    if File.Exists preferred then preferred
    else
        let baseBin = Path.Combine(root, "examples", "FixturePlugins", "bin")
        match
            (if Directory.Exists baseBin then
                Directory.GetFiles(baseBin, "FixturePlugins.dll", SearchOption.AllDirectories)
             else [||])
            |> Array.tryHead
        with
        | Some p -> p
        | None -> failwithf "FixturePlugins.dll not built (looked under %s)" baseBin

[<Fact>]
let ``fixture assembly registers multiple layouts, overrides a built-in, and skips bad types`` () =
    let dir = freshDir ()
    File.Copy(fixtureDllPath (), Path.Combine(dir, "FixturePlugins.dll"))
    let loader = PluginLoader.createForPath dir
    try
        // Must not throw despite the throwing-ctor and no-ctor plugin types.
        let ex = Record.Exception(fun () -> loader.LoadAll())
        Assert.Null(ex)

        // 1) BOTH layouts of the multi-layout plugin registered.
        Assert.Contains("fixture_alpha", Registry.names ())
        Assert.Contains("fixture_beta", Registry.names ())
        // and the intra-plugin duplicate name registered (last wins, no crash).
        Assert.Contains("fixture_dup", Registry.names ())

        // 2) The bad types were SKIPPED — their layouts must NOT appear.
        Assert.DoesNotContain("fixture_throwing_should_not_appear", Registry.names ())
        Assert.DoesNotContain("fixture_noctor_should_not_appear", Registry.names ())

        // 3) The "tall" collision: last-registered (the plugin's marker) wins, so
        //    resolve "tall" now yields the marker geometry (every window 7x7@0,0) —
        //    proving the plugin's register reached the LIVE host Registry.
        let layout = Registry.resolve "tall" 1 0.5
        Assert.True(layout.IsSome, "tall should still resolve")
        let stack = (Stack.ofList [ 10; 20 ]).Value
        let rects = layout.Value (Rect.create 0 0 1920 1080) stack
        let expected = [ 10, Rect.create 0 0 7 7; 20, Rect.create 0 0 7 7 ]
        Assert.Equal<(WindowId * Rect) list>(expected, rects)
    finally
        // RESTORE the built-in "tall" so this process-global override doesn't leak
        // into other tests (the Registry has no unregister; re-register the builtin).
        Registry.register "tall" (fun n r -> Layout.tall n r)
        Directory.Delete(dir, true)

[<Fact>]
let ``PluginPath.resolve honours XDG_CONFIG_HOME and falls back to ~/.config`` () =
    let prev = Environment.GetEnvironmentVariable "XDG_CONFIG_HOME"
    try
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg/here")
        Assert.Equal(Path.Combine("/xdg/here", "wtf", "plugins"), PluginPath.resolve ())
        // Empty is treated like unset -> ~/.config/wtf/plugins.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "")
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        Assert.Equal(Path.Combine(home, ".config", "wtf", "plugins"), PluginPath.resolve ())
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev)
