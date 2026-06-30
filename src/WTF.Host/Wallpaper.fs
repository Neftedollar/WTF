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
let internal loadOriginal (path: string) : Image<Rgba32> option =
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

// =====================================================================
// PALETTE EXTRACTION (impure; host-only — WTF.Core stays ImageSharp-free).
// A wallpaper's dominant colors are quantized via DETERMINISTIC median-cut
// (no Random/Date.now), then STRUCTURED into a generative Palette by
// `Palette.ofColors`. The raw Color list never escapes this module.
// =====================================================================

/// One sRGB sample (0..1) collected from the downscaled image, for median-cut.
type private Px = { R: float; G: float; B: float }

/// DETERMINISTIC median-cut quantization to (at most) `n` boxes. Start with one
/// box over all pixels; repeatedly split the box with the largest single-channel
/// range at the MEDIAN along that channel; stop at `n` boxes or when nothing is
/// splittable. Each box's average is one dominant Color (A=1). No RNG, no time —
/// chosen over k-means precisely because it needs no random init, so the result
/// is reproducible. F#'s Array.sortBy is stable, so ties resolve deterministically.
let private medianCut (n: int) (pixels: Px[]) : Color.Color list =
    if pixels.Length = 0 || n <= 0 then
        []
    else
        // (longest-channel index, its range) for a box.
        let longest (box: Px[]) =
            let mutable rmin, rmax = 1.0, 0.0
            let mutable gmin, gmax = 1.0, 0.0
            let mutable bmin, bmax = 1.0, 0.0
            for p in box do
                rmin <- min rmin p.R; rmax <- max rmax p.R
                gmin <- min gmin p.G; gmax <- max gmax p.G
                bmin <- min bmin p.B; bmax <- max bmax p.B
            let dr, dg, db = rmax - rmin, gmax - gmin, bmax - bmin
            if dr >= dg && dr >= db then 0, dr
            elif dg >= db then 1, dg
            else 2, db
        let chanOf i (p: Px) = match i with | 0 -> p.R | 1 -> p.G | _ -> p.B
        let boxes = System.Collections.Generic.List<Px[]>()
        boxes.Add pixels
        let mutable go = true
        while go && boxes.Count < n do
            // Pick the splittable box (>1 pixel) with the largest channel range.
            let mutable bestI = -1
            let mutable bestRange = 0.0
            let mutable bestChan = 0
            for i in 0 .. boxes.Count - 1 do
                if boxes.[i].Length > 1 then
                    let chan, range = longest boxes.[i]
                    if range > bestRange then
                        bestRange <- range
                        bestI <- i
                        bestChan <- chan
            if bestI < 0 then
                go <- false // nothing left to split (all boxes uniform / singletons)
            else
                let box = boxes.[bestI]
                let sorted = box |> Array.sortBy (chanOf bestChan)
                let mid = sorted.Length / 2
                boxes.[bestI] <- sorted.[.. mid - 1]
                boxes.Add sorted.[mid ..]
        // Average each box -> a Color, then SORT by luminance ascending so the
        // returned list order is stable across runs (tightens the fixture test;
        // `Palette.ofColors` is order-independent regardless).
        [ for box in boxes ->
            let cnt = float box.Length
            let mutable r, g, b = 0.0, 0.0, 0.0
            for p in box do
                r <- r + p.R; g <- g + p.G; b <- b + p.B
            Color.ofRgbTuple (r / cnt, g / cnt, b / cnt) ] // A = 1
        |> List.sortBy Color.relativeLuminance

/// Extract the `n` dominant colors of the wallpaper at `path`. BEST-EFFORT: a
/// missing/unreadable image returns [] (the caller falls back to the built-in
/// palette); the whole body is wrapped so it can never throw. The image is
/// downscaled to a fixed 64x64 (Max, aspect-preserving) with the fixed Triangle
/// sampler — a deterministic resample (no Random, no time) so the fixture test is
/// reproducible — then quantized via median-cut. Fully-transparent pixels (A==0)
/// are skipped.
let internal dominantColors (n: int) (path: string) : Color.Color list =
    try
        match loadOriginal path with
        | None -> []
        | Some original ->
            // Fixed size + fixed sampler => deterministic downscale.
            use small =
                original.Clone(fun x ->
                    x.Resize(
                        ResizeOptions(
                            Size = Size(64, 64),
                            Mode = ResizeMode.Max,
                            Sampler = KnownResamplers.Triangle
                        ))
                    |> ignore)
            let w, h = small.Width, small.Height
            let buf = Array.zeroCreate<byte> (w * h * 4) // R,G,B,A memory order
            small.CopyPixelDataTo buf
            let pixels = System.Collections.Generic.List<Px>(w * h)
            let mutable i = 0
            while i < buf.Length do
                let a = buf.[i + 3]
                if a <> 0uy then
                    pixels.Add
                        { R = float buf.[i] / 255.0
                          G = float buf.[i + 1] / 255.0
                          B = float buf.[i + 2] / 255.0 }
                i <- i + 4
            medianCut n (pixels.ToArray())
    with ex ->
        eprintfn "WTF: wallpaper palette extraction failed for %s: %s" path ex.Message
        []

/// The STRUCTURED palette for a wallpaper choice (the user-facing result — the
/// raw dominant list never escapes). An Image extracts 8 dominant colors and
/// structures them via `Palette.ofColors` (falling back to the built-in default
/// when extraction yields nothing); a solid Color becomes a one-seed palette; no
/// wallpaper is the built-in default. Pure data out — the host then passes it into
/// each RenderContext.
let paletteOf (wp: Wallpaper) : Palette.Palette =
    match wp with
    | NoWallpaper -> Palette.defaultPalette
    | Color hex ->
        Color.ofHex hex
        |> Option.map (fun c -> Palette.ofColors [ c ])
        |> Option.defaultValue Palette.defaultPalette
    | Image (path, _) ->
        match dominantColors 8 (expand path) with
        | [] -> Palette.defaultPalette
        | cs -> Palette.ofColors cs

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
