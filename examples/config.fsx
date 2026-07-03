// ~/.config/wtf/config.fsx  — your window manager, configured in F#.
// This is the xMonad idea: the config IS code, in the WM's own language.
//
//   * The WM loads this file at startup via the F# Compiler Service. It must end
//     with a binding named `wtfConfig` (NOT `config`, which is the CE builder):
//         let wtfConfig = config { ... }
//     The runtime loader injects its own `#r WTF.Core` + `open WTF.Core` and
//     defines WTF_RUNTIME, so the dev `#r` below is skipped under the WM.
//   * You can also run it standalone to type-check / preview it:
//         dotnet fsi examples/config.fsx
//     There WTF_RUNTIME is undefined, so the dev `#r` provides WTF.Core.
//
//   * STRONGLY-TYPED + MACHINE-AWARE (#15): the second assembly below is the WTF
//     config Type Provider. With it referenced, an editor running the F# LSP
//     (FsAutoComplete) gives you autocomplete + typo-proofing driven by YOUR
//     machine: type `Layouts.` to see the valid layouts, or `Apps.` to see your
//     INSTALLED apps (e.g. `Apps.Firefox.AppId`). A typo like `SetLayout "tll"`
//     or an uninstalled app becomes a COMPILE ERROR — caught in the editor AND at
//     WM config-load time (the loader compiles this file through FCS). Run
//     `wtf-edit` to open this file with the LSP set up; see docs/CONFIG-EDITING.md.
//     The WM's loader injects its own `#r` for BOTH assemblies under WTF_RUNTIME,
//     so the two dev `#r` lines below are skipped when loaded by the WM.
#if !WTF_RUNTIME
#r "../src/WTF.Core/bin/Debug/net10.0/WTF.Core.dll"
#r "../src/WTF.TypeProviders/bin/Debug/netstandard2.0/WTF.TypeProviders.dll"
#endif
open WTF.Core
open WTF.TypeProviders   // the config Type Provider: Apps / Layouts / Xkb

// ---- 1. Keybindings: every chord compiles to a semantic Command ----
// NOTE: `keys` REPLACES the built-in map, so this seed covers the full day-one
// set from docs/quickstart.md. Trim or rebind freely — it's your program.
let myKeys =
    keymap {
        // launch & close
        bind "M-Return"  (Spawn "foot")
        bind "M-p"       ToggleOmnibox                 // in-process launcher overlay
        //   (or `once (Spawn "wtf-omnibox")` for the standalone client)
        bind "M-S-c"     CloseFocused
        // focus & stack
        bind "M-j"       (Focus NextWindow)
        bind "M-k"       (Focus PrevWindow)
        bind "M-m"       FocusMaster
        bind "M-S-j"     SwapNext
        bind "M-S-k"     SwapPrev
        bind "M-S-Return" SwapMaster    // promote the focused window to master
        // Layout names come from the `Layouts` Type Provider — autocompleted and
        // typo-proof. `Layouts.Bsp` is the literal "bsp"; a wrong name won't compile.
        bind "M-space"   NextLayout     // cycle
        bind "M-t"       (SetLayout Layouts.Tall)
        bind "M-w"       (SetLayout Layouts.Wide)
        bind "M-b"       (SetLayout Layouts.Bsp)
        bind "M-g"       (SetLayout Layouts.Grid)
        bind "M-f"       (SetLayout Layouts.Full)
        bind "M-h"       (SetRatio 0.4)
        bind "M-l"       (SetRatio 0.6)
        bind "M-period"  IncMaster
        bind "M-comma"   DecMaster
        bind "M-equal"   IncGaps
        bind "M-minus"   DecGaps
        bind "M-S-space" ToggleFloat
        bind "M-S-f"     ToggleFullscreen
        // workspaces (M-1..9 / M-S-1..9 are GENERATED below — config is code)
        bind "M-Tab"     NextWorkspace
        // session & history
        bind "M-z"       Undo
        bind "M-S-z"     Redo
        bind "M-S-r"     ReloadConfig   // re-read this config live (also auto on save)
    }

// Config is code: generate the 18 workspace binds instead of typing them out.
let workspaceKeys =
    [ for i in 1 .. 9 do
        yield sprintf "M-%d" i,   SwitchWorkspace (string i)
        yield sprintf "M-S-%d" i, MoveToWorkspace (string i) ]

// ---- 2. ManageHook: rules for where new windows go ----
let myManage =
    manage {
        rule (appIs "firefox")              (ShiftToWorkspace "2")
        rule (appIs "Spotify")              (ShiftToWorkspace "9")
        rule (titleContains "Picture-in-Picture") FloatWindow

        // ---- machine-aware rules via the `Apps` Type Provider ----
        // `Apps.` lists YOUR installed .desktop apps; `Apps.Firefox.AppId` is the
        // window app-id those apps match on. Uncomment + rename to an app you have
        // (autocomplete `Apps.` in an LSP editor to see the exact names). It is
        // commented so this seed compiles on ANY machine — an app you don't have
        // installed would be a compile error (which is the whole point: typo-proof).
        //   rule (appIs Apps.Firefox.AppId)  (ShiftToWorkspace "2")
        //   rule (appIs Apps.Foot.AppId)     (ShiftToWorkspace "1")
    }

// ---- 3. The whole config, assembled declaratively ----
// MUST be bound to `wtfConfig` — that is the name the WM's loader reads.
let wtfConfig =
    config {
        modKey "Super"
        terminal "foot"
        defaultLayout Layouts.Tall   // typo-proof layout name from the Type Provider
        keys (myKeys @ workspaceKeys)
        manageHook myManage
        startup [ "wtf-bar"; "foot" ]

        // ---- ricing: appearance (all also live-tunable via wtfctl) ----
        gaps 8
        borderWidth 2
        activeBorder "#89b4fa"      // Catppuccin blue
        inactiveBorder "#45475a"
        // ---- OR: frames auto-contrast against the wallpaper's palette ----
        // The most contrasting color OF the wallpaper's own palette; on a
        // monochrome wallpaper (grays / solid) falls back to YOUR color:
        // borderColor (fun ctx ->
        //     let accent = ctx.Palette |> Palette.contrastAccentOr (Color.ofHexOr Color.white "#f38ba8")
        //     let c = if ctx.Focused then accent else Color.mix 0.7 accent ctx.Palette.Overlay
        //     Color.toHex c)
        inactiveOpacity 0.92        // unfocused windows slightly transparent
        animSpeed 0.30              // window slide/fade speed
        cornerRadius 10             // rounded corners (scenefx)
        blur true                   // backdrop blur behind windows (scenefx)
        // glass true               // watercolor glass frames: tinted frost, backdrop shows through
        // glassTint 0.35           // how strongly the frame color reads over the frost (0..1)
        // glassFrost true          // true = frosted (blurred) backdrop; false = sharp
        // glassRefraction 0.0      // px of edge lensing (subtle; needs high DPI to shine)

        // ---- focus glow: the FOCUSED frame emits a halo in its own color ----
        // (activeBorder drives the hue — change it and the glow follows)
        // glow true
        // glowSigma 20.0           // halo spread in px (bigger = softer, wider)
        // glowIntensity 0.6        // halo strength 0..1

        // ---- macOS-style drop shadow under every window (scenefx) ----
        shadow true
        // shadowSigma 24.0         // blur spread in px
        // shadowColor "#000000"    // shadow color
        // shadowOpacity 0.45       // shadow alpha 0..1
        // shadowOffset 0 8         // (dx, dy) px; light from above => dy > 0

        // ---- wallpaper: solid color, image, or a DYNAMIC (time-of-day) .heic ----
        // The image is DECODED in the F# host (ImageSharp) and its raw pixels are
        // handed to C — it scales to the output and re-scales on resize. A leading
        // `~` expands to your home dir. A missing/bad image logs + falls back.
        // Dynamic = a macOS dynamic wallpaper (multi-frame .heic, libheif): the
        // frame matching the time of day is shown and switches automatically.
        wallpaper (Color "#1e1e2e")                  // solid Catppuccin base
        // wallpaper (Image ("~/pics/bg.png", Fill)) // Fill|Fit|Stretch|Center|Tile
        // wallpaper (Dynamic ("~/pics/catalina.heic", Fill))

        // ---- bar & omnibox styling (optional; defaults look like this seed) ----
        // Colors/segments/font restyle a RUNNING bar live on save; position and
        // height apply when the bar starts. Bars render IN-PROCESS by default
        // (`embedded true`); `embedded false` falls back to a standalone `wtf-bar`
        // you launch from `startup`. Multiple bars: `bars [ ... ]` with names —
        // embedded ones need no launcher; each `embedded false` entry wants one
        // `wtf-bar --name <n>`. Left/Right = vertical bars. See
        // docs/configuration.md#bar--omnibox-styling.
        //
        // Every color takes a fixed hex OR a palette function (fun p -> …) — the
        // SAME wallpaper palette the borders read, re-resolved each snapshot so a
        // dynamic .heic re-tints the bar through the day. `glass true` frosts the
        // panel (backdrop blur); translucency is just the alpha in `background`.
        // bar (barConfig {
        //     position Top
        //     embedded true                                          // in-process (default); false = standalone wtf-bar
        //     refreshMs 300                                           // poll/redraw cadence; repaints only on change
        //     glass true
        //     background (fun p -> Color.toHexA 0.45 p.Base)          // translucent, from wallpaper
        //     foreground (fun p -> Color.toHex p.Text)
        //     accent     (fun p -> Palette.accent 0.5 p |> Color.toHex) // workspace pills
        //     right [
        //         // Custom F# widget: a function of the live state (Windows,
        //         // FocusedApp, Battery, Network, Player, Workspace, Time …).
        //         Custom (fun c -> sprintf "%d win" c.Windows.Length)
        //         // Shell widget: poll a command, show its first stdout line.
        //         script "~/bin/cpu.sh" 2000
        //         Player; Battery; Clock "ddd HH:mm"
        //     ]
        // })
        // omnibox (omniboxConfig {
        //     glass true
        //     selection   (fun p -> Palette.accent 0.4 p |> Color.toHex)
        //     promptColor (fun p -> Palette.accent 0.7 p |> Color.toHex)
        //     prompt "λ"
        // })

        // ---- input devices: applied per device type as each attaches ----
        // `input` plugs an InputConfig; build it with the `inputDevices { ... }`
        // CE composing keyboard/mouse/touchpad sub-blocks (any may be omitted).
        input (inputDevices {
            // KEYBOARD: xkb layout/variant/options/model/rules + key repeat.
            //   layout "us,ru" + options "grp:alt_shift_toggle" => switch layouts
            //   with Alt+Shift. "ctrl:nocaps" => CapsLock acts as Ctrl. Empty
            //   string on any field => use the xkb default for that field.
            keyboard {
                layout "us,ru"
                options "grp:alt_shift_toggle"
                repeatRate 25           // keys/sec
                repeatDelay 600         // ms before repeat kicks in
            }
            // MOUSE / pointer.
            //   accelProfile: "flat" | "adaptive" | "" (leave libinput default)
            //   accelSpeed:   -1.0..1.0 (0.0 = neutral)
            mouse {
                accelProfile "flat"
                accelSpeed 0.2
                naturalScroll false
            }
            // TOUCHPAD.
            //   scrollMethod: "two-finger" | "edge" | "none" | "" (leave default)
            //   clickMethod:  "button-areas" | "clickfinger" | "" (leave default)
            //   accelProfile: "flat" | "adaptive" | "" (leave default)
            touchpad {
                tap true
                tapDrag true
                naturalScroll true
                disableWhileTyping true
                scrollMethod "two-finger"
                clickMethod "button-areas"
            }
        })
    }

// ---- Standalone preview (skipped when loaded by the WM) ----
// Under `dotnet fsi examples/config.fsx` this prints a summary + an agent demo;
// the runtime loader defines WTF_RUNTIME so none of this runs inside the WM.
#if !WTF_RUNTIME
printfn "Loaded WTF config:"
printfn "  mod=%s terminal=%s gaps=%d layout=%s"
    wtfConfig.ModKey wtfConfig.Terminal wtfConfig.Gaps wtfConfig.DefaultLayout
printfn "  %d keybindings, %d manage rules, startup: %A"
    wtfConfig.Keys.Length wtfConfig.ManageHook.Length wtfConfig.StartupApps

// ---- Agent-first: an LLM can drive the SAME object model declaratively ----
printfn "\nSimulating an agent program against this config..."
let world = World.empty (Rect.create 0 0 1920 1080)
let world1, _ =
    [ "foot"; "firefox"; "code" ]
    |> List.mapi (fun i app -> { Id = i + 1; AppId = app; Title = app; Floating = false })
    |> List.fold (fun (w, _) info -> Manage.onAdd wtfConfig info w) (world, [])

// firefox was auto-shifted to ws "2" by the manage hook:
printfn "  ws1 windows: %A" (World.stackOf "1" world1 |> Option.map Stack.toList)
printfn "  ws2 windows: %A" (World.stackOf "2" world1 |> Option.map Stack.toList)

let agentProgram = agent { workspace "2"; layout "grid"; spawn "mpv" }
let world2, effects = Reducer.applyMany agentProgram world1
printfn "  after agent { workspace \"2\"; layout \"grid\"; spawn \"mpv\" }:"
printfn "    current=%s layout=%s effects=%A"
    world2.Current (World.currentWorkspace world2).Layout effects

// A keybind resolves to the same Command an agent would issue:
printfn "  keybind M-space resolves to: %A" (Keymap.lookup wtfConfig "M-space")
#endif
