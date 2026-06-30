module WTF.Host.Wallpaper

// The DECODE lives HERE, in the host — not in WTF.Core (the brain stays pure +
// ImageSharp-free; config is just data). This module loads an image off disk,
// scales it to the current output size per the chosen mode, and hands the raw
// RGBA32 bytes to the C shim (which wraps them in a wlr_buffer). Everything is
// best-effort: a missing/unreadable image LOGS and falls back, never throws — so
// it can never crash or block onReady / onOutputResize.

open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open WTF.Core

// Cache the LOADED ORIGINAL image (keyed by its resolved path) so an output
// resize re-scales from the in-memory original instead of re-reading disk. Only
// one wallpaper is ever active, so a single slot suffices.
let mutable private cached: (string * Image<Rgba32>) option = None

/// Expand a leading `~` to the user's home directory (config stores `~/pics/...`).
let internal expand (path: string) : string =
    if path = "~" || path.StartsWith("~/") then
        let home = System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
        home + path.Substring(1)
    else
        path

/// Map a WallpaperMode to ImageSharp's ResizeMode. Crop = cover+crop (Fill),
/// Pad = contain+letterbox (Fit), Stretch = exact, BoxPad = centered-with-pad
/// (Center). Tile has no direct ResizeMode; we fall back to Crop (cover) so the
/// output is still fully covered — a true tiling loop can refine this later.
let internal resizeModeOf (mode: WallpaperMode) : ResizeMode =
    match mode with
    | Fill -> ResizeMode.Crop
    | Fit -> ResizeMode.Pad
    | Stretch -> ResizeMode.Stretch
    | Center -> ResizeMode.BoxPad
    | Tile -> ResizeMode.Crop

/// Drop the cached decoded image (when switching away from an Image wallpaper) so
/// a long-running session doesn't retain a full-resolution bitmap it no longer uses.
let private clearCache () =
    cached |> Option.iter (fun (_, img) -> img.Dispose())
    cached <- None

/// Load (or reuse the cached) original image for `path`. Returns None on any
/// failure (missing file, bad format, decode error) after logging.
let private loadOriginal (path: string) : Image<Rgba32> option =
    match cached with
    | Some (p, img) when p = path -> Some img
    | _ ->
        cached |> Option.iter (fun (_, img) -> img.Dispose())
        cached <- None
        try
            let img = Image.Load<Rgba32>(path)
            cached <- Some(path, img)
            Some img
        with ex ->
            eprintfn "WTF: wallpaper load failed for %s: %s" path ex.Message
            None

/// Decode + scale `path` to exactly w x h and push the RGBA32 bytes to the C
/// shim. Returns true on success, false (after logging) on any failure.
let internal pushImage (path: string) (mode: WallpaperMode) (w: int) (h: int) : bool =
    if w <= 0 || h <= 0 then
        false
    else
        match loadOriginal path with
        | None -> false
        | Some original ->
            try
                // Clone so the cached ORIGINAL is preserved for the next resize.
                let opts =
                    ResizeOptions(
                        Size = Size(w, h),
                        Mode = resizeModeOf mode,
                        Position = AnchorPositionMode.Center
                    )
                use img = original.Clone(fun x -> x.Resize(opts) |> ignore)
                // Crop/Stretch/Pad/BoxPad all yield an exact w x h canvas. Guard
                // anyway: a final exact-size pad keeps the contract if a mode ever
                // returns a different canvas.
                if img.Width <> w || img.Height <> h then
                    img.Mutate(fun x ->
                        x.Resize(ResizeOptions(Size = Size(w, h), Mode = ResizeMode.Pad)) |> ignore)
                // Rgba32 -> bytes in memory order R,G,B,A, stride = w*4.
                let buf = Array.zeroCreate<byte> (w * h * 4)
                img.CopyPixelDataTo(buf)
                Ffi.wtf_set_wallpaper(buf, w, h)
                true
            with ex ->
                eprintfn "WTF: wallpaper scale failed for %s: %s" path ex.Message
                false

/// Apply the configured wallpaper into the BACKGROUND layer at output size w x h.
/// Best-effort: a Color parses + pushes a solid rect; an Image decodes+scales and,
/// on any failure, falls back to clearing the wallpaper; NoWallpaper clears.
let apply (wp: Wallpaper) (w: int) (h: int) : unit =
    match wp with
    | NoWallpaper ->
        clearCache ()
        Ffi.wtf_clear_wallpaper ()
    | Color hex ->
        clearCache () // not an image anymore — release the decoded original
        match Protocol.hexColor hex with
        | Some (r, g, b) -> Ffi.wtf_set_wallpaper_color (r, g, b)
        | None ->
            eprintfn "WTF: wallpaper color %s is not a valid hex; clearing" hex
            Ffi.wtf_clear_wallpaper ()
    | Image (path, mode) ->
        if not (pushImage (expand path) mode w h) then
            // Bad/missing image: fall back to no wallpaper (never crash startup).
            Ffi.wtf_clear_wallpaper ()
