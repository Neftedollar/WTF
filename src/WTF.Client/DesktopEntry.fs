namespace WTF.Client

open System
open System.IO

/// PURE parsing of freedesktop .desktop files for the omnibox launcher, plus the
/// Exec field-code stripping rules from the Desktop Entry spec. `parse` is pure
/// over a file's text; `scan` is the (graceful) IO walk over the XDG app dirs.
module DesktopEntry =

    type Entry =
        { Name: string
          Exec: string
          Icon: string option
          Terminal: bool
          FilePath: string }

    /// Remove Exec field codes per the freedesktop Desktop Entry spec. Codes are
    /// substituted with launch context we do not have (files/urls/icon/name), so
    /// every one is stripped; `%%` collapses to a literal `%`. Deprecated codes
    /// (%d %D %n %N %v %m) are removed too. Resulting whitespace is collapsed.
    let stripFieldCodes (exec: string) : string =
        if String.IsNullOrEmpty exec then
            ""
        else
            let sb = System.Text.StringBuilder(exec.Length)
            let mutable i = 0
            while i < exec.Length do
                let c = exec.[i]
                if c = '%' && i + 1 < exec.Length then
                    let n = exec.[i + 1]
                    match n with
                    | '%' -> sb.Append('%') |> ignore // literal percent
                    | 'f' | 'u' | 'F' | 'U' | 'i' | 'c' | 'k'
                    | 'v' | 'm' | 'd' | 'D' | 'n' | 'N' -> () // drop the code
                    | _ -> () // unknown code: drop the '%' and the letter
                    i <- i + 2
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            // Collapse runs of whitespace introduced by removed codes, then trim.
            let collapsed =
                sb.ToString().Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            String.Join(" ", collapsed)

    let private isTrue (v: string) =
        v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)

    /// Parse ONE .desktop file's text. Reads only the [Desktop Entry] group
    /// (ignores [Desktop Action ...] etc.). Returns None unless Type=Application
    /// with a Name and Exec, and NoDisplay/Hidden are not true.
    let parse (content: string) : Entry option =
        let lines = content.Replace("\r\n", "\n").Split('\n')
        let mutable inGroup = false
        let mutable typ = None
        let mutable name = None
        let mutable exec = None
        let mutable icon = None
        let mutable terminal = false
        let mutable noDisplay = false
        let mutable hidden = false
        for raw in lines do
            let line = raw.Trim()
            if line.StartsWith "#" || line = "" then
                ()
            elif line.StartsWith "[" && line.EndsWith "]" then
                // Group header — we only consume the main [Desktop Entry] group.
                inGroup <- (line = "[Desktop Entry]")
            elif inGroup then
                let idx = line.IndexOf '='
                if idx > 0 then
                    let key = line.Substring(0, idx).Trim()
                    let value = line.Substring(idx + 1).Trim()
                    match key with
                    | "Type" -> typ <- Some value
                    | "Name" when name.IsNone -> name <- Some value
                    | "Exec" when exec.IsNone -> exec <- Some value
                    | "Icon" -> icon <- Some value
                    | "Terminal" -> terminal <- isTrue value
                    | "NoDisplay" -> noDisplay <- isTrue value
                    | "Hidden" -> hidden <- isTrue value
                    | _ -> ()
        match typ, name, exec with
        | Some "Application", Some n, Some e when not noDisplay && not hidden && n <> "" && e <> "" ->
            Some
                { Name = n
                  Exec = e
                  Icon = (icon |> Option.filter (fun s -> s <> ""))
                  Terminal = terminal
                  FilePath = "" }
        | _ -> None

    /// Walk the given dirs for *.desktop files and parse them. Freedesktop
    /// precedence: an entry's "desktop file id" (its path relative to the apps
    /// dir, with '/' -> '-') is unique; the FIRST dir that provides an id wins.
    /// GRACEFUL: an unreadable file or dir is skipped, never throws.
    let scan (dirs: string list) : Entry list =
        let seen = System.Collections.Generic.HashSet<string>()
        let acc = System.Collections.Generic.List<Entry>()
        for dir in dirs do
            try
                if Directory.Exists dir then
                    let files =
                        Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories)
                        |> Seq.sort
                    for file in files do
                        try
                            // desktop-file id = path under `dir`, separators -> '-'.
                            let rel =
                                Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '-')
                            if seen.Add rel then
                                match parse (File.ReadAllText file) with
                                | Some e -> acc.Add { e with FilePath = file }
                                | None -> ()
                        with _ -> ()
            with _ -> ()
        List.ofSeq acc

    /// The standard XDG application directories, highest precedence first:
    /// ~/.local/share/applications, then each $XDG_DATA_DIRS/applications
    /// (default /usr/local/share + /usr/share).
    let defaultDirs () : string list =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let dataHome =
            match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
            | null | "" -> Path.Combine(home, ".local", "share")
            | d -> d
        let dataDirs =
            match Environment.GetEnvironmentVariable "XDG_DATA_DIRS" with
            | null | "" -> "/usr/local/share:/usr/share"
            | d -> d
        let parts =
            dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        (Path.Combine(dataHome, "applications"))
        :: (parts |> List.map (fun d -> Path.Combine(d, "applications")))
