namespace WTF.Client

open System
open System.Runtime.InteropServices
open SixLabors.Fonts
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open SixLabors.ImageSharp.Drawing
open SixLabors.ImageSharp.Drawing.Processing

/// The .NET-strength pixel path for the client apps, mirroring WTF.Host/Wallpaper.fs:
/// best-effort, never throws/crashes. The wl_shm buffer is WL_SHM_FORMAT_ARGB8888,
/// native-endian 0xAARRGGBB = memory bytes B,G,R,A on x86 — which is EXACTLY
/// SixLabors Image<Bgra32> memory order, so there is NO channel swap (the opposite
/// of Wallpaper.fs, whose shim buffer is documented as DRM ABGR = bytes R,G,B,A,
/// hence Rgba32). Render into a cached Image<Bgra32> with ImageSharp.Drawing, then
/// Marshal.Copy the bytes to the unmanaged shm pointer the C helper handed up.
module Render =

    // --- font (graceful, mirroring Wallpaper.loadOriginal's try/cache) ----------
    let private fontCollection = FontCollection()
    let mutable private fontFamily: FontFamily option = None
    let mutable private fontLoaded = false

    let private dejavuPaths =
        [ "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
          "/usr/share/fonts/dejavu/DejaVuSans.ttf"
          "/usr/share/fonts/TTF/DejaVuSans.ttf" ]

    /// Resolve a usable font family ONCE: try the system "DejaVu Sans", else load a
    /// known DejaVu .ttf off disk, else fall back to the first system family. If
    /// nothing is found, returns None and the caller skips text (never throws).
    let private resolveFamily () : FontFamily option =
        if fontLoaded then
            fontFamily
        else
            fontLoaded <- true
            let fromSystem =
                try
                    match SystemFonts.TryGet "DejaVu Sans" with
                    | true, fam -> Some fam
                    | _ -> None
                with _ -> None
            let fromDisk () =
                dejavuPaths
                |> List.tryPick (fun p ->
                    try
                        if IO.File.Exists p then Some(fontCollection.Add p) else None
                    with _ -> None)
            let anySystem () =
                try SystemFonts.Families |> Seq.tryHead with _ -> None
            fontFamily <-
                match fromSystem with
                | Some f -> Some f
                | None ->
                    match fromDisk () with
                    | Some f -> Some f
                    | None -> anySystem ()
            fontFamily

    /// A font at `size` px, or None when no font is available (skip text).
    let font (size: float32) : Font option =
        resolveFamily () |> Option.map (fun fam -> fam.CreateFont(size, FontStyle.Regular))

    /// Measured width of `text` at `font` (0.0 when no font / empty).
    let measureWidth (f: Font) (text: string) : float32 =
        if String.IsNullOrEmpty text then
            0.0f
        else
            try (TextMeasurer.MeasureSize(text, TextOptions f)).Width
            with _ -> 0.0f

    // --- drawing primitives ------------------------------------------------------

    /// Fill an axis-aligned rectangle.
    let fillRect (ctx: IImageProcessingContext) (color: Color) (x: float32) (y: float32) (w: float32) (h: float32) =
        if w > 0.0f && h > 0.0f then
            ctx.Fill(color, RectangularPolygon(x, y, w, h)) |> ignore

    /// Fill a rounded rectangle (pill) — composed from two bands + four corner
    /// circles so it relies only on RectangularPolygon/EllipsePolygon (no clipping).
    let fillRoundedRect (ctx: IImageProcessingContext) (color: Color) (x: float32) (y: float32) (w: float32) (h: float32) (radius: float32) =
        if w > 0.0f && h > 0.0f then
            let r = min radius (min (w / 2.0f) (h / 2.0f))
            if r <= 0.5f then
                fillRect ctx color x y w h
            else
                ctx.Fill(color, RectangularPolygon(x + r, y, w - 2.0f * r, h)) |> ignore
                ctx.Fill(color, RectangularPolygon(x, y + r, w, h - 2.0f * r)) |> ignore
                ctx.Fill(color, EllipsePolygon(x + r, y + r, r)) |> ignore
                ctx.Fill(color, EllipsePolygon(x + w - r, y + r, r)) |> ignore
                ctx.Fill(color, EllipsePolygon(x + r, y + h - r, r)) |> ignore
                ctx.Fill(color, EllipsePolygon(x + w - r, y + h - r, r)) |> ignore

    /// Draw text at (x,y) top-left in the given font + color (no-op on failure).
    let drawText (ctx: IImageProcessingContext) (f: Font) (color: Color) (x: float32) (y: float32) (text: string) =
        if not (String.IsNullOrEmpty text) then
            try ctx.DrawText(text, f, color, PointF(x, y)) |> ignore
            with _ -> ()

    // --- the cached surface + shm blit ------------------------------------------

    /// A reusable Bgra32 canvas that resizes to the configured buffer size and
    /// blits to the unmanaged shm pointer. Mirrors Wallpaper.fs's cache discipline:
    /// the Image is reused across frames and only reallocated when the size changes.
    type Surface() =
        let mutable img: Image<Bgra32> option = None
        let mutable scratch: byte[] = Array.empty

        /// Ensure the backing image is exactly w x h (reallocating only on change).
        member _.Ensure(w: int, h: int) =
            if w > 0 && h > 0 then
                match img with
                | Some i when i.Width = w && i.Height = h -> ()
                | _ ->
                    img |> Option.iter (fun i -> i.Dispose())
                    img <- Some(new Image<Bgra32>(w, h))
                    scratch <- Array.zeroCreate (w * h * 4)

        /// Draw into the canvas via an ImageSharp Mutate pipeline (best-effort).
        member this.Draw(w: int, h: int, draw: IImageProcessingContext -> unit) =
            this.Ensure(w, h)
            match img with
            | Some i ->
                try i.Mutate(fun ctx -> draw ctx)
                with ex -> eprintfn "WTF: panel draw failed: %s" ex.Message
            | None -> ()

        /// Copy the canvas pixels to the shm buffer the C helper handed up. ARGB8888
        /// shm == Bgra32 memory => no channel swap. Fast path when stride == w*4
        /// (always true for the client-owned pool); a row-by-row safety path covers
        /// any padded stride. Never throws.
        member _.Blit(ptr: nativeint, w: int, h: int, stride: int) =
            match img with
            | Some i when i.Width = w && i.Height = h && ptr <> 0n ->
                try
                    if scratch.Length < w * h * 4 then
                        scratch <- Array.zeroCreate (w * h * 4)
                    i.CopyPixelDataTo(Span<byte>(scratch, 0, w * h * 4))
                    if stride = w * 4 then
                        Marshal.Copy(scratch, 0, ptr, w * h * 4)
                    else
                        let rowBytes = w * 4
                        for y in 0 .. h - 1 do
                            Marshal.Copy(scratch, y * rowBytes, ptr + nativeint (y * stride), rowBytes)
                with ex -> eprintfn "WTF: panel blit failed: %s" ex.Message
            | _ -> ()

        interface IDisposable with
            member _.Dispose() = img |> Option.iter (fun i -> i.Dispose())
