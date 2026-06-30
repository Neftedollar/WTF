module WTF.Config.Tests.TypeProviderConfigTests

// Proves the FCS config loader (#11) handles a STRONGLY-TYPED config (#15): a
// config.fsx that references the WTF config Type Provider (`Layouts` / `Apps`)
// loads cleanly through the SAME FsiEvaluationSession path the WM uses at startup.
//
// The contract under test (ConfigEngine.ensureSession): the loader injects
// `#r WTF.TypeProviders.dll` (found as a sibling of WTF.Core.dll — this test
// project's ProjectReference puts it there) so the user file does NOT need its
// own `#r` for the provider. The provided literal members (`Layouts.Bsp` => "bsp",
// `Apps.<App>.AppId` => the scanned app-id) must flow through the eval and land in
// the returned WtfConfig — that's what these assertions check.
//
// These run with WTF_RUNTIME defined (the engine always passes it), so the seed's
// dev `#r` guards are exercised exactly as in the real WM.

open System
open System.IO
open Xunit
open WTF.Core
open WTF.Config

let private defaultCfg =
    { WtfConfig.defaults with Gaps = 99; ModKey = "DEFAULT_MARKER" }

let private writeTemp (content: string) : string =
    let p = Path.Combine(Path.GetTempPath(), sprintf "wtf-tpcfg-%s.fsx" (Guid.NewGuid().ToString("N")))
    File.WriteAllText(p, content)
    p

let private load (path: string) : WtfConfig =
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load()

[<Fact>]
let ``a config using the Layouts Type Provider loads through FCS`` () =
    // `Layouts.Bsp` is a provided literal => "bsp". No `#r` in the file: the loader
    // injects the TP reference. If this loads with DefaultLayout="bsp", the provider
    // was resolved + its literal baked in through the real load path.
    let path =
        writeTemp """
open WTF.TypeProviders
let wtfConfig =
    config {
        gaps 41
        defaultLayout Layouts.Bsp
    }
"""
    let cfg = load path
    File.Delete path
    Assert.Equal(41, cfg.Gaps)
    Assert.Equal("bsp", cfg.DefaultLayout)        // the provided literal flowed through

[<Fact>]
let ``a config using the Apps Type Provider scans a fixture dir at load time`` () =
    // Hermetic: write a .desktop fixture into a temp dir, point `Apps<dir>` at it,
    // and prove the scanned app-id (from StartupWMClass) reaches the WtfConfig.
    let dir = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "wtf-tpapps-" + Guid.NewGuid().ToString("N"))).FullName
    File.WriteAllText(Path.Combine(dir, "testbrowser.desktop"),
        "[Desktop Entry]\nName=Test Browser\nExec=/usr/bin/testbrowser %u\nType=Application\nStartupWMClass=testbrowser\n")
    // A NoDisplay entry that must be skipped (not strictly needed for the assert,
    // but keeps the fixture honest about the include filter).
    File.WriteAllText(Path.Combine(dir, "hidden.desktop"),
        "[Desktop Entry]\nName=Hidden\nExec=/usr/bin/hidden\nType=Application\nNoDisplay=true\n")
    let template =
        """
open WTF.TypeProviders
type MyApps = Apps<"__DIR__">
let wtfConfig =
    config {
        gaps 23
        // terminal carries the scanned app-id so the test can observe it; the real
        // use is `rule (appIs MyApps.TestBrowser.AppId) ...` in a manage hook.
        terminal MyApps.TestBrowser.AppId
    }
"""
    let path = writeTemp (template.Replace("__DIR__", dir))
    let cfg = load path
    File.Delete path
    Directory.Delete(dir, true)
    Assert.Equal(23, cfg.Gaps)
    Assert.Equal("testbrowser", cfg.Terminal)     // StartupWMClass app-id, scanned at load time
