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
open WTF.Client.DesktopEntry

[<Literal>]
let Width = 640
[<Literal>]
let Height = 400
[<Literal>]
let RowHeight = 30
[<Literal>]
let InputHeight = 40
[<Literal>]
let MaxRows = 11   // (Height - InputHeight) / RowHeight, with a little slack

// ---- palette -----------------------------------------------------------------
let private cBg = Color.FromRgba(24uy, 24uy, 37uy, 244uy)        // mantle
let private cInputBg = Color.FromRgba(49uy, 50uy, 68uy, 255uy)   // surface0
let private cText = Color.FromRgba(205uy, 214uy, 244uy, 255uy)
let private cDim = Color.FromRgba(127uy, 132uy, 156uy, 255uy)    // overlay1
let private cSelBg = Color.FromRgba(137uy, 180uy, 250uy, 255uy)  // blue
let private cOnSel = Color.FromRgba(24uy, 24uy, 37uy, 255uy)
let private cPrompt = Color.FromRgba(166uy, 227uy, 161uy, 255uy) // green

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
let private allEntries : Entry list = DesktopEntry.scan (DesktopEntry.defaultDirs ())
let mutable private query : string = ""
let mutable private ranked : Entry list = Fuzzy.rank "" allEntries
let mutable private selected : int = 0
let private surface = new Render.Surface()
let mutable private handle : Panel.Handle option = None

let private reRank () =
    ranked <- Fuzzy.rank query allEntries
    selected <- if ranked.IsEmpty then 0 else min selected (ranked.Length - 1)

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
let private render (buf: nativeint) (w: int) (h: int) (stride: int) =
    let fontOpt = Render.font 16.0f
    let smallOpt = Render.font 12.0f
    surface.Draw(
        w, h,
        fun ctx ->
            Render.fillRect ctx cBg 0.0f 0.0f (float32 w) (float32 h)
            match fontOpt with
            | None -> ()
            | Some font ->
                let padX = 14.0f
                // --- input row -------------------------------------------------
                Render.fillRect ctx cInputBg 0.0f 0.0f (float32 w) (float32 InputHeight)
                let inY = (float32 InputHeight - 16.0f) / 2.0f - 1.0f
                Render.drawText ctx font cPrompt padX inY ">"
                let promptW = Render.measureWidth font "> "
                let shown = if query = "" then "" else query
                Render.drawText ctx font cText (padX + promptW) inY shown
                // a simple caret after the query text
                let qW = Render.measureWidth font shown
                Render.fillRect ctx cText (padX + promptW + qW + 1.0f) (inY + 1.0f) 2.0f 18.0f
                if query = "" then
                    Render.drawText ctx font cDim (padX + promptW + 12.0f) inY "type to search apps…"

                // --- results list ---------------------------------------------
                let rows = min MaxRows ranked.Length
                // keep the selected row visible by scrolling the window
                let first =
                    if selected < rows then 0
                    else selected - rows + 1
                for i in 0 .. rows - 1 do
                    let idx = first + i
                    if idx < ranked.Length then
                        let e = ranked.[idx]
                        let top = float32 (InputHeight + i * RowHeight)
                        let isSel = (idx = selected)
                        if isSel then
                            Render.fillRect ctx cSelBg 0.0f top (float32 w) (float32 RowHeight)
                        let fg = if isSel then cOnSel else cText
                        let ty = top + (float32 RowHeight - 16.0f) / 2.0f - 1.0f
                        Render.drawText ctx font fg padX ty e.Name
                        // right-aligned dim command hint
                        match smallOpt with
                        | Some small ->
                            let hint = DesktopEntry.stripFieldCodes e.Exec
                            let hint =
                                if hint.Length > 40 then hint.Substring(0, 39) + "…" else hint
                            let hw = Render.measureWidth small hint
                            let hintFg = if isSel then cOnSel else cDim
                            Render.drawText ctx small hintFg (float32 w - padX - hw) (top + (float32 RowHeight - 12.0f) / 2.0f - 1.0f) hint
                        | None -> ()
    )
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
        if not ranked.IsEmpty && selected >= 0 && selected < ranked.Length then
            launch ranked.[selected]
        h.Quit()
    | KEY_BackSpace ->
        if query.Length > 0 then
            query <- query.Substring(0, query.Length - 1)
            reRank ()
            h.RequestRedraw()
    | KEY_Up ->
        if selected > 0 then selected <- selected - 1
        h.RequestRedraw()
    | KEY_Down | KEY_Tab ->
        if selected < ranked.Length - 1 then selected <- selected + 1
        h.RequestRedraw()
    | _ ->
        if isPrintable codepoint then
            query <- query + string (Char.ConvertFromUtf32(int codepoint))
            reRank ()
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
    cfg.Width <- Width
    cfg.Height <- Height
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
