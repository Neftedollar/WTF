module WTF.Host.Heif

// Minimal libheif binding for DYNAMIC (macOS-style) wallpapers: an Apple
// dynamic wallpaper is one .heic containing N top-level images (the frames of
// the day). ImageSharp cannot decode HEIC, so the frames are pulled out here
// via the system libheif (present on any desktop with HEIF thumbnailers) and
// handed to the existing ImageSharp scale/palette pipeline as Image<Rgba32>.
// Everything is BEST-EFFORT and total: any native failure logs and yields [] —
// a bad wallpaper file must never crash or block the session.

open System.Runtime.InteropServices
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats

/// struct heif_error — returned BY VALUE by most libheif calls.
/// { enum code; enum subcode; const char *message; } — code 0 = Ok.
[<StructLayout(LayoutKind.Sequential)>]
type internal HeifError =
    struct
        val Code: int
        val Subcode: int
        val Message: nativeint
    end

module internal Native =
    [<Literal>]
    let Lib = "libheif.so.1"

    // enum values are part of libheif's stable ABI (heif.h):
    [<Literal>]
    let ColorspaceRgb = 1        // heif_colorspace_RGB
    [<Literal>]
    let ChromaInterleavedRgba = 11 // heif_chroma_interleaved_RGBA
    [<Literal>]
    let ChannelInterleaved = 10  // heif_channel_interleaved

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint heif_context_alloc()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void heif_context_free(nativeint ctx)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern HeifError heif_context_read_from_file(nativeint ctx, string filename, nativeint options)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int heif_context_get_number_of_top_level_images(nativeint ctx)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int heif_context_get_list_of_top_level_image_IDs(nativeint ctx, uint32[] ids, int count)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern HeifError heif_context_get_image_handle(nativeint ctx, uint32 id, nativeint& handle)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void heif_image_handle_release(nativeint handle)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern HeifError heif_decode_image(nativeint handle, nativeint& img, int colorspace, int chroma, nativeint options)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern void heif_image_release(nativeint img)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int heif_image_get_primary_width(nativeint img)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int heif_image_get_primary_height(nativeint img)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint heif_image_get_plane_readonly(nativeint img, int channel, int& stride)

let internal errText (e: HeifError) : string =
    if e.Message = 0n then sprintf "code %d/%d" e.Code e.Subcode
    else Marshal.PtrToStringAnsi e.Message

/// Decode ONE top-level image (by handle) to an ImageSharp Rgba32 image.
let private decodeHandle (handle: nativeint) : Image<Rgba32> option =
    let mutable img = 0n
    let err = Native.heif_decode_image(handle, &img, Native.ColorspaceRgb, Native.ChromaInterleavedRgba, 0n)
    if err.Code <> 0 || img = 0n then
        eprintfn "WTF: heif frame decode failed: %s" (errText err)
        None
    else
        try
            let w = Native.heif_image_get_primary_width img
            let h = Native.heif_image_get_primary_height img
            let mutable stride = 0
            let plane = Native.heif_image_get_plane_readonly(img, Native.ChannelInterleaved, &stride)
            if w <= 0 || h <= 0 || plane = 0n || stride < w * 4 then
                eprintfn "WTF: heif frame has no interleaved plane (%dx%d stride %d)" w h stride
                None
            else
                // The plane's stride may exceed w*4 (row padding): copy row by
                // row into a tight buffer for ImageSharp.
                let tight = Array.zeroCreate<byte> (w * h * 4)
                for row in 0 .. h - 1 do
                    Marshal.Copy(plane + nativeint (row * stride), tight, row * w * 4, w * 4)
                Some(Image.LoadPixelData<Rgba32>(tight, w, h))
        finally
            Native.heif_image_release img

/// Decode ALL top-level images of a .heic in stored order (Apple dynamic
/// wallpapers store the frames of the day sequentially). [] on any failure.
let decodeFrames (path: string) : Image<Rgba32> list =
    try
        let ctx = Native.heif_context_alloc ()
        if ctx = 0n then
            eprintfn "WTF: heif_context_alloc failed"
            []
        else
            try
                let err = Native.heif_context_read_from_file(ctx, path, 0n)
                if err.Code <> 0 then
                    eprintfn "WTF: heif read failed for %s: %s" path (errText err)
                    []
                else
                    let n = Native.heif_context_get_number_of_top_level_images ctx
                    if n <= 0 then
                        eprintfn "WTF: %s contains no images" path
                        []
                    else
                        let ids = Array.zeroCreate<uint32> n
                        let got = Native.heif_context_get_list_of_top_level_image_IDs(ctx, ids, n)
                        [ for i in 0 .. got - 1 do
                            let mutable handle = 0n
                            let herr = Native.heif_context_get_image_handle(ctx, ids.[i], &handle)
                            if herr.Code <> 0 || handle = 0n then
                                eprintfn "WTF: heif handle %d failed for %s: %s" i path (errText herr)
                            else
                                try
                                    match decodeHandle handle with
                                    | Some img -> yield img
                                    | None -> ()
                                finally
                                    Native.heif_image_handle_release handle ]
            finally
                Native.heif_context_free ctx
    with ex ->
        // DllNotFoundException (no libheif on this machine) lands here too.
        eprintfn "WTF: heif decode failed for %s: %s" path ex.Message
        []
