namespace WTF.Client

open System.Text.Json.Nodes
open SixLabors.ImageSharp

/// PURE parser for the "ui" object the WM splices into the agent-socket
/// snapshot (serialized by WTF.Core.ClientUi — a wire contract, not a shared
/// assembly: the clients deliberately do not reference WTF.Core). TOTAL: any
/// missing/garbage field falls back to the built-in default, so a bar/omnibox
/// running against an older WM (no "ui" yet) or a hostile snapshot renders
/// exactly the pre-config look and never crashes.
module ClientConfig =

    /// Bar segment spec, mirroring WTF.Core.BarSegment on the wire:
    /// "workspaces" | "battery" | "network" | "player" | {"clock": fmt} | {"label": text}
    type SegmentSpec =
        | SWorkspaces
        | SClock of string
        | SBattery
        | SNetwork
        | SPlayer
        | SLabel of string

    type Side =
        | SideTop
        | SideBottom
        | SideLeft
        | SideRight

    type BarUi =
        { Side: Side
          Height: int              // thickness (height for top/bottom, width for left/right)
          FontSize: float32
          Bg: Color
          Fg: Color
          Dim: Color
          Accent: Color
          Left: SegmentSpec list
          Right: SegmentSpec list }

    type OmniboxUi =
        { Width: int
          Height: int
          RowHeight: int
          FontSize: float32
          Bg: Color
          InputBg: Color
          Fg: Color
          Dim: Color
          Selection: Color
          Prompt: string
          PromptColor: Color
          Placeholder: string }

    /// Parse "#rrggbb" / "#rrggbbaa" (leading '#' optional); None on anything else.
    let parseHex (s: string) : Color option =
        let hex = if isNull s then "" else s.TrimStart '#'
        let byteAt i =
            match System.Byte.TryParse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) with
            | true, b -> Some b
            | _ -> None
        match hex.Length with
        | 6 ->
            match byteAt 0, byteAt 2, byteAt 4 with
            | Some r, Some g, Some b -> Some(Color.FromRgba(r, g, b, 255uy))
            | _ -> None
        | 8 ->
            match byteAt 0, byteAt 2, byteAt 4, byteAt 6 with
            | Some r, Some g, Some b, Some a -> Some(Color.FromRgba(r, g, b, a))
            | _ -> None
        | _ -> None

    // Defaults == the constants that were compiled into the clients before the
    // config existed, so "no ui in snapshot" is pixel-identical to the old look.
    let barDefaults =
        { Side = SideTop
          Height = 28
          FontSize = 14.0f
          Bg = Color.FromRgba(30uy, 30uy, 46uy, 235uy)
          Fg = Color.FromRgba(205uy, 214uy, 244uy, 255uy)
          Dim = Color.FromRgba(108uy, 112uy, 134uy, 255uy)
          Accent = Color.FromRgba(137uy, 180uy, 250uy, 255uy)
          Left = [ SWorkspaces ]
          Right = [ SPlayer; SNetwork; SBattery; SClock "HH:mm" ] }

    let omniboxDefaults =
        { Width = 640
          Height = 400
          RowHeight = 30
          FontSize = 16.0f
          Bg = Color.FromRgba(24uy, 24uy, 37uy, 244uy)
          InputBg = Color.FromRgba(49uy, 50uy, 68uy, 255uy)
          Fg = Color.FromRgba(205uy, 214uy, 244uy, 255uy)
          Dim = Color.FromRgba(127uy, 132uy, 156uy, 255uy)
          Selection = Color.FromRgba(137uy, 180uy, 250uy, 255uy)
          Prompt = ">"
          PromptColor = Color.FromRgba(166uy, 227uy, 161uy, 255uy)
          Placeholder = "type to search apps…" }

    // --- defensive JSON getters (same discipline as BarModel) ---
    let private getStr (n: JsonNode) (key: string) : string option =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> None
            | v -> (try Some(v.GetValue<string>()) with _ -> None)
        | _ -> None

    let private getInt (n: JsonNode) (key: string) : int option =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> None
            | v -> (try Some(v.GetValue<int>()) with _ -> (try Some(int (v.GetValue<float>())) with _ -> None))
        | _ -> None

    let private getFloat (n: JsonNode) (key: string) : float32 option =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> None
            | v -> (try Some(float32 (v.GetValue<float>())) with _ -> None)
        | _ -> None

    let private getColor (n: JsonNode) (key: string) (fallback: Color) : Color =
        getStr n key |> Option.bind parseHex |> Option.defaultValue fallback

    let private segment (n: JsonNode) : SegmentSpec option =
        match n with
        | null -> None
        | :? JsonValue as v ->
            match (try Some(v.GetValue<string>()) with _ -> None) with
            | Some "workspaces" -> Some SWorkspaces
            | Some "battery" -> Some SBattery
            | Some "network" -> Some SNetwork
            | Some "player" -> Some SPlayer
            | _ -> None
        | :? JsonObject as o ->
            match getStr o "clock", getStr o "label" with
            | Some fmt, _ -> Some(SClock fmt)
            | _, Some text -> Some(SLabel text)
            | _ -> None
        | _ -> None

    let private segments (n: JsonNode) (key: string) (fallback: SegmentSpec list) : SegmentSpec list =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | :? JsonArray as arr -> arr |> Seq.choose segment |> List.ofSeq
            | _ -> fallback
        | _ -> fallback

    /// The "ui" node of an already-parsed snapshot root (null-safe).
    let private uiNode (root: JsonNode) : JsonNode =
        match root with
        | :? JsonObject as o -> o.["ui"]
        | _ -> null

    let private child (n: JsonNode) (key: string) : JsonNode =
        match n with
        | :? JsonObject as o -> o.[key]
        | _ -> null

    let private barOfNode (b: JsonNode) : BarUi =
                let d = barDefaults
                { Side =
                    match getStr b "position" with
                    | Some "bottom" -> SideBottom
                    | Some "left" -> SideLeft
                    | Some "right" -> SideRight
                    | _ -> SideTop
                  Height = defaultArg (getInt b "height") d.Height |> max 12 |> min 128
                  FontSize = defaultArg (getFloat b "fontSize") d.FontSize |> max 6.0f |> min 64.0f
                  Bg = getColor b "background" d.Bg
                  Fg = getColor b "foreground" d.Fg
                  Dim = getColor b "dim" d.Dim
                  Accent = getColor b "accent" d.Accent
                  Left = segments b "left" d.Left
                  Right = segments b "right" d.Right }

    /// Parse ONE bar config out of a full snapshot JSON string, selected by
    /// name (None => the first bar). Total: no ui/bars/match => defaults.
    let barOfSnapshot (name: string option) (snapshotJson: string) : BarUi =
        match (try Some(JsonNode.Parse snapshotJson) with _ -> None) with
        | None -> barDefaults
        | Some root ->
            match child (uiNode root) "bars" with
            | :? JsonArray as arr when arr.Count > 0 ->
                let picked =
                    match name with
                    | Some n -> arr |> Seq.tryFind (fun b -> getStr b "name" = Some n)
                    | None -> Some arr.[0]
                match picked with
                | Some b -> barOfNode b
                | None -> barDefaults
            | _ -> barDefaults

    /// Parse the omnibox config out of a full snapshot JSON string. Total.
    let omniboxOfSnapshot (snapshotJson: string) : OmniboxUi =
        match (try Some(JsonNode.Parse snapshotJson) with _ -> None) with
        | None -> omniboxDefaults
        | Some root ->
            let o = child (uiNode root) "omnibox"
            if isNull o then omniboxDefaults
            else
                let d = omniboxDefaults
                { Width = defaultArg (getInt o "width") d.Width |> max 200 |> min 3840
                  Height = defaultArg (getInt o "height") d.Height |> max 100 |> min 2160
                  RowHeight = defaultArg (getInt o "rowHeight") d.RowHeight |> max 16 |> min 96
                  FontSize = defaultArg (getFloat o "fontSize") d.FontSize |> max 6.0f |> min 64.0f
                  Bg = getColor o "background" d.Bg
                  InputBg = getColor o "inputBackground" d.InputBg
                  Fg = getColor o "foreground" d.Fg
                  Dim = getColor o "dim" d.Dim
                  Selection = getColor o "selection" d.Selection
                  Prompt = defaultArg (getStr o "prompt") d.Prompt
                  PromptColor = getColor o "promptColor" d.PromptColor
                  Placeholder = defaultArg (getStr o "placeholder") d.Placeholder }
