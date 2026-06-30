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
