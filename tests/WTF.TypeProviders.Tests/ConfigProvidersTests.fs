module WTF.TypeProviders.ConfigProvidersTests

open System
open System.IO
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open WTF.TypeProviders

// ---------------------------------------------------------------------------
// White-box tests of the internal Ident / Desktop / Xkb / Layouts modules
// (reachable via InternalsVisibleTo) + hermetic consumer tests that drive the
// real Apps/Layouts/Xkb Type Providers against committed fixtures. Mirrors the
// #14 InternalTests.fs pattern — verifiable WITHOUT an editor.
// ---------------------------------------------------------------------------

// === Ident.sanitize / dedup ================================================

[<Theory>]
[<InlineData("Firefox", "Firefox")>]
[<InlineData("Foot", "Foot")>]
[<InlineData("GNOME Settings (Tweaks)", "GNOMESettingsTweaks")>]
[<InlineData("foo-bar", "FooBar")>]
[<InlineData("foo_bar", "FooBar")>]
[<InlineData("foo.bar", "FooBar")>]
[<InlineData("a b  c", "ABC")>]
let ``Ident.sanitize: spaces/parens/punct collapse to PascalCase`` (input: string) (expected: string) =
    Assert.Equal(expected, Ident.sanitize input)

[<Fact>]
let ``Ident.sanitize: leading digit is prefixed with underscore`` () =
    Assert.Equal("_0ad", Ident.sanitize "0ad")
    Assert.Equal("_2048", Ident.sanitize "2048")

[<Fact>]
let ``Ident.sanitize: empty / all-punct yields a valid underscore identifier`` () =
    Assert.Equal("_", Ident.sanitize "")
    Assert.Equal("_", Ident.sanitize "   ")
    Assert.Equal("_", Ident.sanitize "()-.")

[<Fact>]
let ``Ident.dedup: collisions get _2, _3 suffixes`` () =
    let used = System.Collections.Generic.HashSet<string>()
    Assert.Equal("Foo", Ident.dedup used "Foo")
    Assert.Equal("Foo_2", Ident.dedup used "Foo")
    Assert.Equal("Foo_3", Ident.dedup used "Foo")
    Assert.Equal("Bar", Ident.dedup used "Bar")

[<Property>]
let ``Ident.sanitize: result never starts with a digit`` () =
    let anyChar = Gen.elements (['a'..'z'] @ ['A'..'Z'] @ ['0'..'9'] @ [ ' '; '-'; '_'; '.'; '('; ')'; '/' ])
    let nameGen = Gen.listOf anyChar |> Gen.map (List.toArray >> String)
    Prop.forAll (Arb.fromGen nameGen) (fun name ->
        let s = Ident.sanitize name
        s.Length > 0 && not (Char.IsDigit s.[0]))

// === Desktop.stripExec ======================================================

[<Theory>]
[<InlineData("/usr/lib/firefox/firefox %u", "/usr/lib/firefox/firefox")>]
[<InlineData("gnome-tweaks %F", "gnome-tweaks")>]
[<InlineData("foot", "foot")>]
[<InlineData("env FOO=1 app %U --flag", "env FOO=1 app --flag")>]
[<InlineData("app %f %i %c", "app")>]
[<InlineData("echo 100%% done", "echo 100% done")>]
[<InlineData("\"/opt/My App/run\" %u", "\"/opt/My App/run\"")>]
let ``Desktop.stripExec: field codes removed, %% unescaped, spaces collapsed`` (input: string) (expected: string) =
    Assert.Equal(expected, Desktop.stripExec input)

[<Fact>]
let ``Desktop.stripExec: empty / null is empty`` () =
    Assert.Equal("", Desktop.stripExec "")
    Assert.Equal("", Desktop.stripExec null)

[<Fact>]
let ``Desktop.stripExec: unknown percent code is preserved`` () =
    Assert.Equal("app %z", Desktop.stripExec "app %z")

// === Desktop.parseEntry + includeEntry ======================================

[<Fact>]
let ``Desktop.parseEntry: reads the Desktop Entry group`` () =
    let txt = "[Desktop Entry]\nName=Firefox\nExec=/usr/lib/firefox/firefox %u\nType=Application\nStartupWMClass=firefox\n"
    let e = Desktop.parseEntry txt
    Assert.Equal("Firefox", e.Name)
    Assert.Equal("/usr/lib/firefox/firefox %u", e.Exec)
    Assert.Equal("Application", e.Type)
    Assert.Equal("firefox", e.StartupWMClass)
    Assert.False(e.NoDisplay)

[<Fact>]
let ``Desktop.parseEntry: ignores comments, blanks, and other groups`` () =
    let txt = "# a comment\n\n[Desktop Entry]\nName=Foo\n\n[Desktop Action New]\nName=ShouldBeIgnored\n"
    let e = Desktop.parseEntry txt
    Assert.Equal("Foo", e.Name)

[<Fact>]
let ``Desktop.includeEntry: Application kept; NoDisplay/Hidden/non-Application skipped`` () =
    let mk t nd h : Desktop.Entry = { Name = "x"; Exec = ""; Type = t; NoDisplay = nd; Hidden = h; StartupWMClass = "" }
    Assert.True(Desktop.includeEntry (mk "Application" false false))
    Assert.False(Desktop.includeEntry (mk "Application" true false))
    Assert.False(Desktop.includeEntry (mk "Application" false true))
    Assert.False(Desktop.includeEntry (mk "Link" false false))
    Assert.False(Desktop.includeEntry (mk "Directory" false false))

// === Desktop.appId ==========================================================

[<Fact>]
let ``Desktop.appId: StartupWMClass wins, else basename`` () =
    let withClass : Desktop.Entry = { Name = "x"; Exec = ""; Type = "Application"; NoDisplay = false; Hidden = false; StartupWMClass = "firefox" }
    let noClass = { withClass with StartupWMClass = "" }
    Assert.Equal("firefox", Desktop.appId "org.mozilla.firefox" withClass)
    Assert.Equal("foot", Desktop.appId "foot" noClass)

// === Desktop.scanDir on the fixture dir =====================================

let private appsDir = Path.Combine(AppContext.BaseDirectory, "data", "applications")

[<Fact>]
let ``Desktop.scanDir: returns exactly the visible apps`` () =
    let apps = Desktop.scanDir appsDir
    let names = apps |> List.map (fun a -> a.Name) |> Set.ofList
    Assert.Contains("Firefox", names)
    Assert.Contains("Foot", names)
    Assert.Contains("GNOME Settings (Tweaks)", names)
    // Absence is how we prove the filter: hidden + the Link entry must NOT appear.
    Assert.DoesNotContain("HiddenThing", names)
    Assert.DoesNotContain("Example Link", names)
    Assert.Equal(3, apps.Length)

[<Fact>]
let ``Desktop.scanDir: firefox AppId from StartupWMClass + Exec stripped`` () =
    let apps = Desktop.scanDir appsDir
    let ff = apps |> List.find (fun a -> a.Name = "Firefox")
    Assert.Equal("firefox", ff.AppId)
    Assert.Equal("/usr/lib/firefox/firefox", ff.Exec)

[<Fact>]
let ``Desktop.scanDir: foot AppId falls back to the desktop-file id`` () =
    let apps = Desktop.scanDir appsDir
    let foot = apps |> List.find (fun a -> a.Name = "Foot")
    Assert.Equal("foot", foot.AppId)
    Assert.Equal("foot", foot.Exec)

[<Fact>]
let ``Desktop.scanDir: non-existent dir is graceful (empty)`` () =
    Assert.Empty(Desktop.scanDir (Path.Combine(appsDir, "does-not-exist")))
    Assert.Empty(Desktop.scanDir "")
    Assert.Empty(Desktop.scanDir null)

// === Xkb.parseLst ===========================================================

[<Fact>]
let ``Xkb.parseLst: layouts and options extracted with code/description split`` () =
    let txt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "evdev.lst"))
    let layouts, options = Xkb.parseLst txt
    Assert.Contains(("us", "English (US)"), layouts)
    Assert.Contains(("ru", "Russian"), layouts)
    Assert.Contains(("de", "German"), layouts)
    Assert.Contains(("grp:alt_shift_toggle", "Alt+Shift"), options)
    // model/variant sections must NOT leak into layouts/options.
    Assert.DoesNotContain(("pc105", "Generic 105-key PC"), layouts)
    Assert.DoesNotContain(("dvorak", "us: English (Dvorak)"), layouts)

[<Fact>]
let ``Xkb.parseLst: missing / garbage input degrades to empty`` () =
    Assert.Equal<(string * string) list * (string * string) list>(([], []), Xkb.parseLst "")
    Assert.Equal<(string * string) list * (string * string) list>(([], []), Xkb.parseLst null)
    let layouts, options = Xkb.parseLst "no sections here\njust text"
    Assert.Empty(layouts)
    Assert.Empty(options)

// === Layouts.scanPlugins ====================================================

[<Fact>]
let ``Layouts.scanPlugins: missing dir is graceful (empty)`` () =
    Assert.Empty(LayoutScan.scanPlugins "")
    Assert.Empty(LayoutScan.scanPlugins (Path.Combine(AppContext.BaseDirectory, "no-such-plugins")))

[<Fact>]
let ``Layouts.scanPlugins: reads a layouts.txt manifest and *.layout markers`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        File.WriteAllText(Path.Combine(dir, "layouts.txt"), "# header\nspiral\ncolumns\n")
        File.WriteAllText(Path.Combine(dir, "monocle.layout"), "")
        let names = LayoutScan.scanPlugins dir |> Set.ofList
        Assert.Contains("spiral", names)
        Assert.Contains("columns", names)
        Assert.Contains("monocle", names)
    finally
        try Directory.Delete(dir, true) with _ -> ()

// ===========================================================================
// HERMETIC CONSUMER TESTS — the #14-style proof the providers emit real types
// at compile time from the scanned fixture dir/file.
// ===========================================================================

type A = Apps<"data/applications">

[<Fact>]
let ``Apps TP: Firefox AppId (StartupWMClass) + Exec (stripped) + Name`` () =
    Assert.Equal("firefox", A.Firefox.AppId)
    Assert.Equal("/usr/lib/firefox/firefox", A.Firefox.Exec)
    Assert.Equal("Firefox", A.Firefox.Name)

[<Fact>]
let ``Apps TP: Foot AppId falls back to desktop-file id`` () =
    Assert.Equal("foot", A.Foot.AppId)
    Assert.Equal("Foot", A.Foot.Name)

[<Fact>]
let ``Apps TP: a spaces/parens Name sanitizes to a PascalCase member`` () =
    Assert.Equal("gnome-tweaks", A.GNOMESettingsTweaks.AppId)
    Assert.Equal("GNOME Settings (Tweaks)", A.GNOMESettingsTweaks.Name)

// Bare `Layouts` (zero config, no static arg) exposes the built-ins from the
// root type; `Layouts<pluginsDir>` would layer plugin layouts on top.
type L = Layouts

[<Fact>]
let ``Layouts TP: built-in members are typo-proof literals`` () =
    Assert.Equal("tall", L.Tall)
    Assert.Equal("wide", L.Wide)
    Assert.Equal("bsp", L.Bsp)
    Assert.Equal("grid", L.Grid)
    Assert.Equal("full", L.Full)

type K = Xkb<"data/evdev.lst">

[<Fact>]
let ``Xkb TP: layout + option literals from evdev.lst`` () =
    Assert.Equal("ru", K.Layouts.Russian)
    Assert.Equal("us", K.Layouts.EnglishUS)
    Assert.Equal("grp:alt_shift_toggle", K.Options.GrpAltShiftToggle)

// === RuntimeTypes erasure round-trip (mirrors #14's contract test) ==========

[<Fact>]
let ``RuntimeTypes: provided app literals erase to a baked AppInfo`` () =
    let app: WTF.TypeProviders.Runtime.AppInfo =
        { AppId = A.Firefox.AppId; Exec = A.Firefox.Exec; Name = A.Firefox.Name }
    Assert.Equal("firefox", app.AppId)
    Assert.Equal("/usr/lib/firefox/firefox", app.Exec)
    Assert.Equal("Firefox", app.Name)

[<Fact>]
let ``RuntimeTypes: Xkb literals erase to a baked XkbEntry`` () =
    let entry: WTF.TypeProviders.Runtime.XkbEntry =
        { Code = K.Layouts.Russian; Description = "Russian" }
    Assert.Equal("ru", entry.Code)
