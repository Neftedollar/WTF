module WTF.Omnibox.Program

// WTF.Omnibox — the application launcher executable. A centered OVERLAY
// layer-surface with EXCLUSIVE keyboard interactivity. It discovers .desktop apps
// from the XDG dirs (PURE DesktopEntry.scan), fuzzy-ranks the typed query (PURE
// Fuzzy.rank), renders the input + ranked list with ImageSharp, and on Enter
// launches the selected entry (Exec field codes stripped) detached, then quits.
// Esc quits.
//
// "F# brain, C body": libwtf_panel does ONLY the Wayland/layer-shell/shm/keyboard
// plumbing + key decode; every decision (ranking, selection, rendering, launch)
// is here. GRACEFUL: no compositor exits cleanly; no font still lists apps; a bad
// Exec never crashes the loop.

open System
open System.Diagnostics
open SixLabors.ImageSharp
open WTF.Client
open WTF.Client.ClientConfig
open WTF.Client.DesktopEntry

// ---- styling: served by the WM from config.fsx (ui.omnibox in the snapshot),
// fetched ONCE at launch (the omnibox is short-lived by design). No WM / no
// "ui" => the built-in defaults, pixel-identical to the pre-config look.
let private ui : OmniboxUi = omniboxOfSnapshot (defaultArg (Socket.trySend "state") "")

// ---- xkb keysyms we care about -----------------------------------------------
[<Literal>]
let KEY_Escape = 0xff1bu
[<Literal>]
let KEY_Return = 0xff0du
[<Literal>]
let KEY_KP_Enter = 0xff8du
[<Literal>]
let KEY_BackSpace = 0xff08u
[<Literal>]
let KEY_Up = 0xff52u
[<Literal>]
let KEY_Down = 0xff54u
[<Literal>]
let KEY_Tab = 0xff09u

// ---- mutable UI state (single-threaded: all touched on the wl loop thread) ----
// The query/ranked/selection live in the PURE OmniboxModel now (shared with the
// in-process overlay); this shell owns the mutable cell + the Wayland plumbing.
let private allEntries : Entry list = DesktopEntry.scan (DesktopEntry.defaultDirs ())
let mutable private model : OmniboxModel.Model = OmniboxModel.init allEntries
let private surface = new Render.Surface()
let mutable private handle : Panel.Handle option = None

/// Launch a desktop entry detached: strip Exec field codes, wrap terminal apps in
/// the terminal, run via /bin/sh -c so quoting/args behave. Never throws.
let private launch (e: Entry) =
    try
        let cmd = DesktopEntry.stripFieldCodes e.Exec
        if not (String.IsNullOrWhiteSpace cmd) then
            let full =
                if e.Terminal then
                    let term =
                        match Environment.GetEnvironmentVariable "TERMINAL" with
                        | null | "" -> "foot"
                        | t -> t
                    sprintf "%s -e %s" term cmd
                else
                    cmd
            let psi = ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add "-c"
            psi.ArgumentList.Add full
            psi.UseShellExecute <- false
            Process.Start psi |> ignore
    with ex ->
        eprintfn "wtf-omnibox: launch failed: %s" ex.Message

// ---- rendering ---------------------------------------------------------------
// The composition now lives in WTF.Client.OmniboxRender (shared with the
// in-process overlay); this shell only feeds it the live model + styling.
let private render (buf: nativeint) (w: int) (h: int) (stride: int) =
    OmniboxRender.draw surface ui model w h
    surface.Blit(buf, w, h, stride)

// ---- keyboard ----------------------------------------------------------------
let private isPrintable (cp: uint32) =
    // Exclude UTF-16 surrogate code points: Char.ConvertFromUtf32 throws on them,
    // and this runs inside the reverse-P/Invoke `key` callback where an exception
    // crossing the native boundary is undefined behaviour.
    cp >= 0x20u && cp <> 0x7fu && cp < 0x110000u && not (cp >= 0xD800u && cp <= 0xDFFFu)

let private key (keysym: uint32) (codepoint: uint32) =
    let h = handle.Value
    match keysym with
    | KEY_Escape ->
        h.Quit()
    | KEY_Return | KEY_KP_Enter ->
        OmniboxModel.selected model |> Option.iter launch
        h.Quit()
    | KEY_BackSpace ->
        model <- OmniboxModel.backspace model
        h.RequestRedraw()
    | KEY_Up ->
        model <- OmniboxModel.up model
        h.RequestRedraw()
    | KEY_Down | KEY_Tab ->
        model <- OmniboxModel.down model
        h.RequestRedraw()
    | _ ->
        if isPrintable codepoint then
            model <- OmniboxModel.typeText (Char.ConvertFromUtf32(int codepoint)) model
            h.RequestRedraw()

let private configure (_w: int) (_h: int) = ()

let private closed () =
    handle |> Option.iter (fun h -> h.Quit())

// ---- entry point -------------------------------------------------------------
[<EntryPoint>]
let main _argv =
    let h = Panel.Handle(render, key, configure, closed)
    handle <- Some h

    let mutable cfg = Panel.Config()
    cfg.Ns <- "wtf-omnibox"
    cfg.Layer <- Panel.LayerOverlay
    cfg.Anchor <- 0                       // centered (no anchor)
    cfg.Width <- ui.Width
    cfg.Height <- ui.Height
    cfg.ExclusiveZone <- 0
    cfg.Keyboard <- Panel.KeyboardExclusive
    cfg.MarginTop <- 0
    cfg.MarginRight <- 0
    cfg.MarginBottom <- 0
    cfg.MarginLeft <- 0

    eprintfn "wtf-omnibox: %d applications discovered" allEntries.Length

    match h.Init cfg with
    | rc when rc < 0 ->
        eprintfn "wtf-omnibox: cannot start (no Wayland display / missing wl_shm / missing layer-shell). rc=%d" rc
        2
    | _ ->
        let rc = h.Run()
        (surface :> IDisposable).Dispose()
        if rc < 0 then 1 else 0
