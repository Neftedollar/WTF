// ~/.config/wtf/config.fsx  — your window manager, configured in F#.
// This is the xMonad idea: the config IS code, in the WM's own language.
//   dotnet fsi examples/config.fsx
#r "../src/WTF.Core/bin/Debug/net10.0/WTF.Core.dll"
open WTF.Core

// ---- 1. Keybindings: every chord compiles to a semantic Command ----
let myKeys =
    keymap {
        bind "M-Return"  (Spawn "foot")
        bind "M-p"       (Spawn "wofi --show drun")
        bind "M-j"       (Focus NextWindow)
        bind "M-k"       (Focus PrevWindow)
        bind "M-S-j"     SwapNext
        bind "M-S-k"     SwapPrev
        bind "M-S-c"     CloseFocused
        bind "M-space"   (SetLayout "bsp")
        bind "M-t"       (SetLayout "tall")
        bind "M-g"       (SetLayout "grid")
        bind "M-h"       (SetRatio 0.4)
        bind "M-l"       (SetRatio 0.6)
        bind "M-comma"   (SetMaster 2)
        bind "M-1"       (SwitchWorkspace "1")
        bind "M-2"       (SwitchWorkspace "2")
        bind "M-S-2"     (MoveToWorkspace "2")
    }

// ---- 2. ManageHook: rules for where new windows go ----
let myManage =
    manage {
        rule (appIs "firefox")              (ShiftToWorkspace "2")
        rule (appIs "Spotify")              (ShiftToWorkspace "9")
        rule (titleContains "Picture-in-Picture") FloatWindow
    }

// ---- 3. The whole config, assembled declaratively ----
let myConfig =
    config {
        modKey "Super"
        terminal "foot"
        defaultLayout "tall"
        keys myKeys
        manageHook myManage
        startup [ "waybar"; "foot" ]

        // ---- ricing: appearance (all also live-tunable via wtfctl) ----
        gaps 8
        borderWidth 2
        activeBorder "#89b4fa"      // Catppuccin blue
        inactiveBorder "#45475a"
        inactiveOpacity 0.92        // unfocused windows slightly transparent
        animSpeed 0.30              // window slide/fade speed
        cornerRadius 10             // rounded corners (scenefx)
        blur true                   // backdrop blur behind windows (scenefx)
    }

printfn "Loaded WTF config:"
printfn "  mod=%s terminal=%s gaps=%d layout=%s"
    myConfig.ModKey myConfig.Terminal myConfig.Gaps myConfig.DefaultLayout
printfn "  %d keybindings, %d manage rules, startup: %A"
    myConfig.Keys.Length myConfig.ManageHook.Length myConfig.StartupApps

// ---- 4. Agent-first: an LLM can drive the SAME object model declaratively ----
printfn "\nSimulating an agent program against this config..."
let world = World.empty (Rect.create 0 0 1920 1080)
let world1, _ =
    [ "foot"; "firefox"; "code" ]
    |> List.mapi (fun i app -> { Id = i + 1; AppId = app; Title = app; Floating = false })
    |> List.fold (fun (w, _) info -> Manage.onAdd myConfig info w) (world, [])

// firefox was auto-shifted to ws "2" by the manage hook:
printfn "  ws1 windows: %A" (World.stackOf "1" world1 |> Option.map Stack.toList)
printfn "  ws2 windows: %A" (World.stackOf "2" world1 |> Option.map Stack.toList)

let agentProgram = agent { workspace "2"; layout "grid"; spawn "mpv" }
let world2, effects = Reducer.applyMany agentProgram world1
printfn "  after agent { workspace \"2\"; layout \"grid\"; spawn \"mpv\" }:"
printfn "    current=%s layout=%s effects=%A"
    world2.Current (World.currentWorkspace world2).Layout effects

// A keybind resolves to the same Command an agent would issue:
printfn "  keybind M-space resolves to: %A" (Keymap.lookup myConfig "M-space")
