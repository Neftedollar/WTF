module WTF.Core.Tests.ConfigTests

open Xunit
open WTF.Core

[<Fact>]
let ``config CE assembles the expected record`` () =
    let cfg =
        config {
            modKey "Alt"
            terminal "kitty"
            gaps 12
            defaultLayout "bsp"
            keys (keymap {
                bind "M-Return" (Spawn "kitty")
                bind "M-j" (Focus NextWindow)
            })
        }
    Assert.Equal("Alt", cfg.ModKey)
    Assert.Equal("kitty", cfg.Terminal)
    Assert.Equal(12, cfg.Gaps)
    Assert.Equal("bsp", cfg.DefaultLayout)
    Assert.Equal(2, cfg.Keys.Length)

[<Fact>]
let ``keymap preserves bind order and Keymap.lookup resolves`` () =
    let cfg =
        config {
            keys (keymap {
                bind "M-Return" (Spawn "foot")
                bind "M-space" (SetLayout "bsp")
            })
        }
    Assert.Equal<(string * Command) list>(
        [ "M-Return", Spawn "foot"; "M-space", SetLayout "bsp" ], cfg.Keys)
    Assert.Equal(Some(SetLayout "bsp"), Keymap.lookup cfg "M-space")
    Assert.Equal(None, Keymap.lookup cfg "M-q")

[<Fact>]
let ``manage hook sends a matching window to its workspace`` () =
    let cfg =
        config {
            manageHook (manage {
                rule (appIs "firefox") (ShiftToWorkspace "2")
                rule (titleContains "Picture-in-Picture") FloatWindow
            })
        }
    let world = World.empty (Rect.create 0 0 1920 1080)
    let ff = { Id = 1; AppId = "firefox"; Title = "web"; Floating = false }
    let world', _ = Manage.onAdd cfg ff world
    // firefox should have been moved to workspace "2"
    Assert.Equal<int list>([ 1 ], World.stackOf "2" world' |> Option.get |> Stack.toList)
    Assert.Equal(None, World.stackOf "1" world')

[<Fact>]
let ``manage hook leaves unmatched windows on the current workspace`` () =
    let rules = manage { rule (appIs "firefox") (ShiftToWorkspace "2") }
    let cfg = config { manageHook rules }
    let world = World.empty (Rect.create 0 0 1920 1080)
    let term = { Id = 9; AppId = "foot"; Title = "shell"; Floating = false }
    let world', _ = Manage.onAdd cfg term world
    Assert.Equal<int list>([ 9 ], World.stackOf "1" world' |> Option.get |> Stack.toList)

[<Fact>]
let ``input defaults are sane`` () =
    let i = WtfConfig.defaults.Input
    Assert.Equal("us", i.Keyboard.Layout)
    Assert.Equal(25, i.Keyboard.RepeatRate)
    Assert.Equal(600, i.Keyboard.RepeatDelay)
    Assert.True(i.Touchpad.Tap)
    Assert.True(i.Touchpad.NaturalScroll)
    Assert.True(i.Touchpad.DisableWhileTyping)
    Assert.Equal("two-finger", i.Touchpad.ScrollMethod)
    Assert.Equal("button-areas", i.Touchpad.ClickMethod)
    Assert.Equal("", i.Mouse.AccelProfile)

[<Fact>]
let ``input CE round-trips into WtfConfig.Input`` () =
    let cfg =
        config {
            input (inputDevices {
                keyboard { layout "us,ru"; options "grp:alt_shift_toggle" }
                touchpad { tap true; naturalScroll true; disableWhileTyping true }
                mouse { accelProfile "flat"; accelSpeed 0.2 }
            })
        }
    Assert.Equal("us,ru", cfg.Input.Keyboard.Layout)
    Assert.Equal("grp:alt_shift_toggle", cfg.Input.Keyboard.Options)
    Assert.True(cfg.Input.Touchpad.Tap)
    Assert.Equal("flat", cfg.Input.Mouse.AccelProfile)
    Assert.Equal(0.2, cfg.Input.Mouse.AccelSpeed)

[<Fact>]
let ``default wallpaper is the Catppuccin base color`` () =
    Assert.Equal(Color "#1e1e2e", WtfConfig.defaults.Wallpaper)

[<Fact>]
let ``wallpaper CE round-trips an image choice`` () =
    let cfg = config { wallpaper (Image("/x.png", Fill)) }
    Assert.Equal(Image("/x.png", Fill), cfg.Wallpaper)

[<Fact>]
let ``wallpaper CE round-trips a solid color`` () =
    let cfg = config { wallpaper (Color "#abcdef") }
    Assert.Equal(Color "#abcdef", cfg.Wallpaper)

[<Fact>]
let ``agent CE builds an ordered command program`` () =
    let program =
        agent {
            focusApp "firefox"
            layout "bsp"
            ratio 0.65
            moveTo "2"
        }
    Assert.Equal<Command list>(
        [ Focus(ByApp "firefox"); SetLayout "bsp"; SetRatio 0.65; MoveToWorkspace "2" ],
        program)

[<Fact>]
let ``agent program runs through the reducer`` () =
    let world =
        Reducer.applyMany
            [ AddWindow { Id = 1; AppId = "foot"; Title = "t"; Floating = false }
              AddWindow { Id = 2; AppId = "firefox"; Title = "w"; Floating = false } ]
            (World.empty (Rect.create 0 0 1920 1080))
        |> fst
    let program = agent { focusApp "firefox"; layout "grid" }
    let world', _ = Reducer.applyMany program world
    Assert.Equal(Some 2, World.focusedWindow world')
    Assert.Equal("grid", (World.currentWorkspace world').Layout)

// ============================================================================
//  WtfConfig.defaults full contract — a flipped default must be caught.
// ============================================================================

[<Fact>]
let ``WtfConfig.defaults pins every default value`` () =
    let d = WtfConfig.defaults
    Assert.Equal("Super", d.ModKey)
    Assert.Equal("foot", d.Terminal)
    Assert.Equal<string list>([ for i in 1..9 -> string i ], d.Workspaces)
    Assert.Equal("tall", d.DefaultLayout)
    Assert.Equal(6, d.Gaps)
    Assert.Equal(2, d.BorderWidth)
    Assert.Equal(0.94, d.InactiveOpacity)
    Assert.Equal(0.30, d.AnimSpeed)
    Assert.Equal("#89b4fa", d.ActiveBorder)
    Assert.Equal("#45475a", d.InactiveBorder)
    Assert.Equal(0, d.CornerRadius)
    Assert.False(d.Blur)
    Assert.Equal(1.0, d.Scale)
    Assert.Equal(64, d.HistoryLimit)
    Assert.Empty(d.Keys)
    Assert.Empty(d.ManageHook)
    Assert.Empty(d.StartupApps)

// ============================================================================
//  ConfigBuilder custom operations — each op writes ITS OWN field (guards
//  against a field-miswire, e.g. scale -> wrong record field).
// ============================================================================

[<Fact>]
let ``each ConfigBuilder appearance op writes its own field`` () =
    let c =
        config {
            borderWidth 5
            inactiveOpacity 0.5
            animSpeed 0.7
            activeBorder "#111111"
            inactiveBorder "#222222"
            cornerRadius 9
            blur true
            scale 2.0
            historyLimit 7
            workspaces [ "a"; "b" ]
        }
    Assert.Equal(5, c.BorderWidth)
    Assert.Equal(0.5, c.InactiveOpacity)
    Assert.Equal(0.7, c.AnimSpeed)
    Assert.Equal("#111111", c.ActiveBorder)
    Assert.Equal("#222222", c.InactiveBorder)
    Assert.Equal(9, c.CornerRadius)
    Assert.True(c.Blur)
    Assert.Equal(2.0, c.Scale)
    Assert.Equal(7, c.HistoryLimit)
    Assert.Equal<string list>([ "a"; "b" ], c.Workspaces)
    // untouched fields keep their defaults
    Assert.Equal("Super", c.ModKey)

[<Fact>]
let ``keys, manageHook and startup APPEND across multiple blocks`` () =
    let c =
        config {
            keys (keymap { bind "M-a" (Spawn "a") })
            keys (keymap { bind "M-b" (Spawn "b") })
            startup [ "x" ]
            startup [ "y"; "z" ]
            manageHook (manage { rule (appIs "p") (ShiftToWorkspace "2") })
            manageHook (manage { rule (appIs "q") FloatWindow })
        }
    Assert.Equal<(string * Command) list>([ "M-a", Spawn "a"; "M-b", Spawn "b" ], c.Keys)
    Assert.Equal<string list>([ "x"; "y"; "z" ], c.StartupApps)
    Assert.Equal(2, c.ManageHook.Length)

// ============================================================================
//  Manage.onAdd — FloatWindow rule, first-match-wins, explicit NoAction.
// ============================================================================

[<Fact>]
let ``manage FloatWindow rule actually floats the new window`` () =
    let cfg = config { manageHook (manage { rule (appIs "mpv") FloatWindow }) }
    let world = World.empty (Rect.create 0 0 1920 1080)
    let mpv = { Id = 1; AppId = "mpv"; Title = "video"; Floating = false }
    let world', _ = Manage.onAdd cfg mpv world
    let ws = World.currentWorkspace world'
    Assert.True(Map.containsKey 1 ws.Floating)
    Assert.True((Map.find 1 world'.Windows).Floating) // mirror flag in lockstep

[<Fact>]
let ``manage applies the FIRST matching rule only`` () =
    // both rules match; the first (ShiftToWorkspace "2") must win over FloatWindow.
    let cfg =
        config {
            manageHook (manage {
                rule anyWindow (ShiftToWorkspace "2")
                rule anyWindow FloatWindow
            })
        }
    let world = World.empty (Rect.create 0 0 1920 1080)
    let w = { Id = 1; AppId = "x"; Title = "x"; Floating = false }
    let world', _ = Manage.onAdd cfg w world
    Assert.Equal<int list>([ 1 ], World.stackOf "2" world' |> Option.get |> Stack.toList)
    Assert.True(Map.isEmpty (world'.Workspaces |> List.find (fun ws -> ws.Tag = "2")).Floating)

[<Fact>]
let ``manage explicit NoAction leaves the window plainly tiled`` () =
    let cfg = config { manageHook (manage { rule (appIs "foot") NoAction }) }
    let world = World.empty (Rect.create 0 0 1920 1080)
    let term = { Id = 7; AppId = "foot"; Title = "shell"; Floating = false }
    let world', _ = Manage.onAdd cfg term world
    let ws = World.currentWorkspace world'
    Assert.Equal<int list>([ 7 ], ws.Stack |> Option.get |> Stack.toList)
    Assert.True(Map.isEmpty ws.Floating)

// ============================================================================
//  InputBuilder composition: omission keeps the default, order-independent,
//  last-of-duplicate-block wins.
// ============================================================================

[<Fact>]
let ``inputDevices omitting a sub-block keeps that block's default`` () =
    let i = inputDevices { mouse { accelProfile "flat" } }
    Assert.Equal("flat", i.Mouse.AccelProfile)
    // keyboard + touchpad untouched -> defaults
    Assert.Equal(WtfConfig.defaults.Input.Keyboard, i.Keyboard)
    Assert.Equal(WtfConfig.defaults.Input.Touchpad, i.Touchpad)

[<Fact>]
let ``inputDevices sub-block order does not matter`` () =
    let a = inputDevices { keyboard { layout "us,ru" }; mouse { accelSpeed 0.3 } }
    let b = inputDevices { mouse { accelSpeed 0.3 }; keyboard { layout "us,ru" } }
    Assert.Equal(a, b)

[<Fact>]
let ``inputDevices two keyboard blocks -> last wins`` () =
    let i = inputDevices { keyboard { layout "us" }; keyboard { layout "de" } }
    Assert.Equal("de", i.Keyboard.Layout)

// ============================================================================
//  AgentBuilder — the operations not covered by the existing ordered-program test.
// ============================================================================

[<Fact>]
let ``agent CE maps the remaining ops and preserves order`` () =
    let program =
        agent {
            focusNext
            focusPrev
            spawn "foot"
            workspace "2"
            master 3
            close
        }
    Assert.Equal<Command list>(
        [ Focus NextWindow; Focus PrevWindow; Spawn "foot"; SwitchWorkspace "2"; SetMaster 3; CloseFocused ],
        program)

// ============================================================================
//  Bar / omnibox client-UI config (CE builders + the "ui" wire contract).
// ============================================================================

[<Fact>]
let ``barConfig CE overrides only what is set`` () =
    let b = barConfig { name "bottom"; position Bottom; accent "#f38ba8"; right [ Clock "ddd HH:mm" ] }
    Assert.Equal("bottom", b.Name)
    Assert.Equal(Bottom, b.Position)
    Assert.Equal("#f38ba8", ColorSpec.resolve Palette.defaultPalette b.Accent)
    Assert.Equal<BarSegment list>([ Clock "ddd HH:mm" ], b.Right)
    // untouched knobs keep the defaults
    Assert.Equal(BarConfig.defaults.Height, b.Height)
    Assert.Equal<BarSegment list>(BarConfig.defaults.Left, b.Left)

[<Fact>]
let ``omniboxConfig CE overrides only what is set`` () =
    let o = omniboxConfig { width 720; prompt "λ"; selection "#f38ba8" }
    Assert.Equal(720, o.Width)
    Assert.Equal("λ", o.Prompt)
    Assert.Equal("#f38ba8", ColorSpec.resolve Palette.defaultPalette o.Selection)
    Assert.Equal(OmniboxConfig.defaults.Height, o.Height)

[<Fact>]
let ``config CE: bar sets a single entry, bars a list`` () =
    let one = config { bar (barConfig { position Bottom }) }
    Assert.Equal(1, one.Bars.Length)
    Assert.Equal(Bottom, one.Bars.Head.Position)
    let two = config { bars [ barConfig { name "top" }; barConfig { name "side"; position Left } ] }
    Assert.Equal<string list>([ "top"; "side" ], two.Bars |> List.map (fun b -> b.Name))

[<Fact>]
let ``ClientUi.json emits the wire contract shape`` () =
    let bars = [ { BarConfig.defaults with Name = "main"; Position = Right; Left = [ Workspaces; Label "λ" ] } ]
    let node = ClientUi.json Palette.defaultPalette bars OmniboxConfig.defaults
    let s = node.ToJsonString()
    Assert.Contains("\"bars\":[{", s)
    Assert.Contains("\"name\":\"main\"", s)
    Assert.Contains("\"position\":\"right\"", s)
    Assert.Contains("\"workspaces\"", s)
    Assert.Contains("{\"label\":\"\\u03BB\"", s.Replace("λ", "\\u03BB")) // Label survives (raw or escaped)
    Assert.Contains("\"omnibox\":{", s)
    Assert.Contains("\"prompt\":\"\\u003E\"", s.Replace("\">\"", "\"\\u003E\"")) // '>' raw or escaped

[<Fact>]
let ``ColorSpec palette function is resolved into the wire hex`` () =
    // A palette-driven background must serialize as the RESOLVED hex, not a
    // function — so the client (which never sees the palette) gets a plain color.
    let pal = { Palette.defaultPalette with Base = { R = 1.0; G = 0.0; B = 0.0; A = 1.0 } }
    let bars = [ { BarConfig.defaults with Background = OfPalette(fun p -> Color.toHex p.Base) } ]
    let s = (ClientUi.json pal bars OmniboxConfig.defaults).ToJsonString()
    Assert.Contains("\"background\":\"#ff0000\"", s)

[<Fact>]
let ``ColorSpec.resolve degrades a throwing palette function to the client default`` () =
    // TOTAL: a pathological user function must not unwind the whole snapshot; it
    // yields the EMPTY sentinel (not a valid hex), so the client's parseHex fails
    // and it falls back to its built-in default — the element stays VISIBLE.
    let bad = OfPalette(fun _ -> failwith "boom")
    Assert.Equal("", ColorSpec.resolve Palette.defaultPalette bad)
    Assert.Equal("#1e1e2eeb", ColorSpec.resolve Palette.defaultPalette (Fixed "#1e1e2eeb"))

[<Fact>]
let ``barConfig CE accepts a palette function for a color`` () =
    // Guards the MAIN user path: the overloaded custom-operation must route a
    // lambda into OfPalette (not just the direct DU construction the other tests
    // use). A compiler/overload regression would surface here, not silently.
    let pal = { Palette.defaultPalette with Base = { R = 0.0; G = 1.0; B = 0.0; A = 1.0 } }
    let b = barConfig { background (fun p -> Color.toHex p.Base) }
    Assert.Equal("#00ff00", ColorSpec.resolve pal b.Background)
