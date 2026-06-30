namespace WTF.TypeProviders

open System
open System.IO
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open WTF.TypeProviders.Runtime

// ===========================================================================
// Config Type Providers (#15): make ~/.config/wtf/config.fsx strongly-typed &
// machine-aware. Three erasing providers built with the SAME #14 machinery
// (ProvidedTypes.fs + the RuntimeTypes erasure records):
//   * Apps<Dir>     — one nested type per installed .desktop app
//   * Layouts<...>  — the built-in layout names + best-effort plugin layouts
//   * Xkb<LstPath>  — keyboard layouts/options parsed from evdev.lst
//
// CRITICAL DESIGN CONSTRAINT: a Type Provider that throws at design time breaks
// BOTH the editor (FsAutoComplete) AND our FCS config load. Every scan therefore
// degrades to "provide nothing for that source" rather than raising — exactly
// like Introspection.parseFile returning [] on broken XML.
//
// The pure scan/sanitize/strip logic lives in internal modules so the test
// project (granted InternalsVisibleTo by DBusIntrospectionProvider.fs) can
// white-box it WITHOUT an editor — same pattern as DBusSig/Introspection.
// ===========================================================================

/// Shared .NET-identifier sanitizer + deduper. The DBus `Introspection.sanitize`
/// only splits on '.'/'-' and does NOT dedup, but app names carry spaces, parens
/// and other punctuation and two .desktop files can collapse to the same name, so
/// the Apps/Xkb providers need this stronger, collision-aware variant.
module internal Ident =

    /// Split on any run of non-alphanumeric chars, PascalCase each segment, concat.
    /// Prefix '_' when the result is empty or starts with a digit so it always
    /// stays a valid .NET identifier (pinned by the "never starts with a digit"
    /// property test, mirroring #14's sanitize property).
    let sanitize (name: string) : string =
        let name = if isNull name then "" else name
        // Walk the chars, breaking into alphanumeric runs at any non-alphanumeric.
        let segments = ResizeArray<string>()
        let cur = System.Text.StringBuilder()
        let flush () =
            if cur.Length > 0 then
                segments.Add(cur.ToString())
                cur.Clear() |> ignore
        for c in name do
            if Char.IsLetterOrDigit c then cur.Append c |> ignore
            else flush ()
        flush ()
        let pascal =
            segments
            |> Seq.map (fun part ->
                string (Char.ToUpperInvariant part.[0]) + part.Substring(1))
            |> String.concat ""
        if pascal.Length = 0 then "_"
        elif Char.IsDigit pascal.[0] then "_" + pascal
        else pascal

    /// Make `candidate` unique against the already-emitted `used` set by appending
    /// "_2", "_3", … on collision. Two .desktop files can sanitize to the same
    /// identifier (e.g. "Foo Bar" and "Foo-Bar"); the second gets "_2". The chosen
    /// name is added to `used` (mutated) so subsequent calls keep deduping.
    let dedup (used: System.Collections.Generic.HashSet<string>) (candidate: string) : string =
        if used.Add candidate then candidate
        else
            let mutable n = 2
            let mutable next = candidate + "_" + string n
            while not (used.Add next) do
                n <- n + 1
                next <- candidate + "_" + string n
            next

/// Pure .desktop-entry handling (no TP types). A dependency-free INI-ish parse —
/// no System.Text.Json (we stay netstandard2.0 + dependency free like #14).
module internal Desktop =

    /// The fields of the [Desktop Entry] group we care about.
    type Entry =
        { Name: string
          Exec: string
          Type: string
          NoDisplay: bool
          Hidden: bool
          StartupWMClass: string }

    let private boolVal (v: string) = v.Trim().ToLowerInvariant() = "true"

    /// Parse the [Desktop Entry] group of a .desktop file. We read key=value lines
    /// from the `[Desktop Entry]` header until the next `[group]`; comments/blank
    /// lines are ignored; keys/values are trimmed. Localised keys (Name[de]=) are
    /// ignored — only the unlocalised key is taken.
    let parseEntry (contents: string) : Entry =
        let lines =
            if isNull contents then [||]
            else contents.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
        let mutable inGroup = false
        let mutable name = ""
        let mutable exec = ""
        let mutable typ = ""
        let mutable noDisplay = false
        let mutable hidden = false
        let mutable wmClass = ""
        for raw in lines do
            let line = raw.Trim()
            if line.Length = 0 || line.StartsWith "#" then ()
            elif line.StartsWith "[" && line.EndsWith "]" then
                inGroup <- (line = "[Desktop Entry]")
            elif inGroup then
                let eq = line.IndexOf '='
                if eq > 0 then
                    let key = line.Substring(0, eq).Trim()
                    let value = line.Substring(eq + 1).Trim()
                    match key with
                    | "Name" -> name <- value
                    | "Exec" -> exec <- value
                    | "Type" -> typ <- value
                    | "NoDisplay" -> noDisplay <- boolVal value
                    | "Hidden" -> hidden <- boolVal value
                    | "StartupWMClass" -> wmClass <- value
                    | _ -> ()
        { Name = name; Exec = exec; Type = typ
          NoDisplay = noDisplay; Hidden = hidden; StartupWMClass = wmClass }

    /// Keep only real, visible applications: Type=Application AND not NoDisplay AND
    /// not Hidden. This is the rule the fixture's hidden.desktop / notanapp.desktop
    /// prove are skipped.
    let includeEntry (e: Entry) : bool =
        e.Type = "Application" && not e.NoDisplay && not e.Hidden

    /// The app-id used by window rules: StartupWMClass if non-empty, else the
    /// desktop-file id (basename minus ".desktop").
    let appId (fileBaseName: string) (e: Entry) : string =
        if not (String.IsNullOrWhiteSpace e.StartupWMClass) then e.StartupWMClass.Trim()
        else fileBaseName

    /// Strip the FreeDesktop Exec field codes (%f %F %u %U %d %D %n %N %i %c %k %v
    /// %m), unescape %% -> %, and collapse the resulting double spaces. Per the
    /// Desktop Entry spec, unknown codes are left as-is except the deprecated set.
    let stripExec (exec: string) : string =
        if String.IsNullOrEmpty exec then ""
        else
            let sb = System.Text.StringBuilder()
            let mutable i = 0
            while i < exec.Length do
                if exec.[i] = '%' && i + 1 < exec.Length then
                    match exec.[i + 1] with
                    | '%' -> sb.Append('%') |> ignore; i <- i + 2
                    | 'f' | 'F' | 'u' | 'U' | 'd' | 'D'
                    | 'n' | 'N' | 'i' | 'c' | 'k' | 'v' | 'm' -> i <- i + 2
                    | other -> sb.Append('%').Append(other) |> ignore; i <- i + 2
                else
                    sb.Append(exec.[i]) |> ignore
                    i <- i + 1
            // collapse runs of whitespace to single spaces, trim ends.
            let collapsed =
                System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ")
            collapsed.Trim()

    /// A scanned, kept application.
    type App = { AppId: string; Exec: string; Name: string }

    /// Scan ONE directory for visible *.desktop applications. A missing dir -> [];
    /// an unreadable/garbage individual file is skipped (try/with per file) rather
    /// than aborting the whole scan. Stable, sorted-by-Name order.
    let scanDir (dir: string) : App list =
        try
            if String.IsNullOrEmpty dir || not (Directory.Exists dir) then []
            else
                Directory.EnumerateFiles(dir, "*.desktop")
                |> Seq.choose (fun path ->
                    try
                        let contents = File.ReadAllText path
                        let e = parseEntry contents
                        if includeEntry e then
                            let baseName = Path.GetFileNameWithoutExtension path
                            Some { AppId = appId baseName e
                                   Exec = stripExec e.Exec
                                   Name = (if String.IsNullOrWhiteSpace e.Name then baseName else e.Name) }
                        else None
                    with _ -> None)
                |> Seq.sortBy (fun a -> a.Name, a.AppId)
                |> Seq.toList
        with _ -> []

    /// The live XDG application directories, in precedence order:
    /// /usr/share/applications, ~/.local/share/applications, then each
    /// $XDG_DATA_DIRS entry + "/applications". Earlier dirs win on appId collision.
    let liveDirs () : string list =
        try
            let home = Environment.GetEnvironmentVariable "HOME"
            let xdgData = Environment.GetEnvironmentVariable "XDG_DATA_DIRS"
            [ yield "/usr/share/applications"
              if not (String.IsNullOrEmpty home) then
                  yield Path.Combine(home, ".local/share/applications")
              if not (String.IsNullOrEmpty xdgData) then
                  for d in xdgData.Split(':') do
                      if not (String.IsNullOrWhiteSpace d) then
                          yield Path.Combine(d.Trim(), "applications") ]
            |> List.distinct
        with _ -> [ "/usr/share/applications" ]

    /// Scan all live dirs, deduping by AppId with earlier dirs winning. Graceful.
    let scanLive () : App list =
        let seen = System.Collections.Generic.HashSet<string>()
        [ for dir in liveDirs () do
            for app in scanDir dir do
                if seen.Add app.AppId then yield app ]

/// Pure evdev.lst parse for the Xkb provider.
module internal Xkb =

    /// Parse evdev.lst into (layouts, options) lists of (code, description). The
    /// file is line-oriented: `! layout` / `! option` / `! variant` / `! model`
    /// opens a section; subsequent `  <code><ws><Description>` rows belong to it
    /// until the next `!` line. Graceful: missing/garbage input -> ([], []).
    let parseLst (contents: string) : (string * string) list * (string * string) list =
        try
            let lines =
                if isNull contents then [||]
                else contents.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
            let layouts = ResizeArray<string * string>()
            let options = ResizeArray<string * string>()
            let mutable section = ""
            for raw in lines do
                let line = raw.Trim()
                if line.Length = 0 then ()
                elif line.StartsWith "!" then
                    section <- line.Substring(1).Trim().ToLowerInvariant()
                else
                    // split code/description on the first whitespace run.
                    let ws = line |> Seq.tryFindIndex Char.IsWhiteSpace
                    let code, desc =
                        match ws with
                        | Some idx -> line.Substring(0, idx), line.Substring(idx).Trim()
                        | None -> line, ""
                    if code.Length > 0 then
                        match section with
                        | "layout" -> layouts.Add(code, desc)
                        | "option" -> options.Add(code, desc)
                        | _ -> ()
            List.ofSeq layouts, List.ofSeq options
        with _ -> [], []

    /// The default system evdev.lst path.
    let defaultLstPath = "/usr/share/X11/xkb/rules/evdev.lst"

/// Pure layout discovery for the Layouts provider. (Named LayoutScan, NOT
/// "Layouts", so this compiled module does not collide with the provided erased
/// type `WTF.TypeProviders.Layouts` in the same namespace — a same-name module
/// would shadow the provider at the consumer's `Layouts<...>` reference.)
module internal LayoutScan =

    /// The built-in layout names registered by WTF.Core (World.fs). Kept in sync
    /// with the Registry built-ins: tall/wide/bsp/grid/full.
    let builtIns = [ "Tall", "tall"; "Wide", "wide"; "Bsp", "bsp"; "Grid", "grid"; "Full", "full" ]

    /// Best-effort plugin-layout discovery WITHOUT loading any assembly (loading a
    /// user dll inside the editor is unsafe and not AOT-honest). We look for a cheap
    /// sidecar convention in `pluginsDir`:
    ///   * a `layouts.txt` manifest, one layout name per line, OR
    ///   * `*.layout` marker files (the basename is the layout name).
    /// Absent dir / convention -> []. Never reflects into *.dll. Graceful.
    let scanPlugins (pluginsDir: string) : string list =
        try
            if String.IsNullOrEmpty pluginsDir || not (Directory.Exists pluginsDir) then []
            else
                let manifest = Path.Combine(pluginsDir, "layouts.txt")
                let fromManifest =
                    if File.Exists manifest then
                        try
                            File.ReadAllLines manifest
                            |> Array.map (fun l -> l.Trim())
                            |> Array.filter (fun l -> l.Length > 0 && not (l.StartsWith "#"))
                            |> List.ofArray
                        with _ -> []
                    else []
                let fromMarkers =
                    try
                        Directory.EnumerateFiles(pluginsDir, "*.layout")
                        |> Seq.map Path.GetFileNameWithoutExtension
                        |> List.ofSeq
                    with _ -> []
                (fromManifest @ fromMarkers) |> List.distinct
        with _ -> []

    /// The default plugins dir: ~/.config/wtf/plugins.
    let defaultPluginsDir () =
        try
            let home = Environment.GetEnvironmentVariable "HOME"
            if String.IsNullOrEmpty home then "" else Path.Combine(home, ".config/wtf/plugins")
        with _ -> ""

// ---------------------------------------------------------------------------
// The providers. Three [<TypeProvider>] classes in the SAME assembly as
// DBusIntrospectionProvider (one TP dll for FCS/FSAC/FSI to load). The single
// [<assembly: TypeProviderAssembly>] in DBusIntrospectionProvider.fs covers all.
// ---------------------------------------------------------------------------

[<TypeProvider>]
type AppsProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let ns = "WTF.TypeProviders"
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let appInfoTy = typeof<AppInfo>

    let literalProp (name: string) (value: string) =
        ProvidedProperty(name, typeof<string>, isStatic = true,
                         getterCode = fun _ -> <@@ value @@>)

    let resolvePath (dir: string) =
        if String.IsNullOrEmpty dir then dir
        elif Path.IsPathRooted dir then dir
        else Path.Combine(config.ResolutionFolder, dir)

    let rootType =
        ProvidedTypeDefinition(asm, ns, "Apps", baseType = Some typeof<obj>, hideObjectMethods = true)

    let buildAppType (used: System.Collections.Generic.HashSet<string>) (app: Desktop.App) =
        let memberName = Ident.dedup used (Ident.sanitize app.Name)
        let t = ProvidedTypeDefinition(memberName, baseType = Some appInfoTy, hideObjectMethods = true)
        t.AddMember(literalProp "AppId" app.AppId)
        t.AddMember(literalProp "Exec" app.Exec)
        t.AddMember(literalProp "Name" app.Name)
        t

    let buildInstance (typeName: string) (dir: string) =
        // GRACEFUL: never let an exception escape the instantiation function.
        let provided =
            ProvidedTypeDefinition(asm, ns, typeName, baseType = Some typeof<obj>, hideObjectMethods = true)
        try
            let apps =
                if String.IsNullOrEmpty dir then Desktop.scanLive ()
                else Desktop.scanDir (resolvePath dir)
            let used = System.Collections.Generic.HashSet<string>()
            for app in apps do
                provided.AddMember(buildAppType used app)
        with _ -> ()
        provided

    do
        rootType.DefineStaticParameters(
            parameters = [ ProvidedStaticParameter("Dir", typeof<string>, "") ],
            instantiationFunction =
                (fun typeName args ->
                    let dir = try args.[0] :?> string with _ -> ""
                    buildInstance typeName dir))
        this.AddNamespace(ns, [ rootType ])

[<TypeProvider>]
type LayoutsProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let ns = "WTF.TypeProviders"
    let asm = System.Reflection.Assembly.GetExecutingAssembly()

    let literalProp (name: string) (value: string) =
        ProvidedProperty(name, typeof<string>, isStatic = true,
                         getterCode = fun _ -> <@@ value @@>)

    let resolvePath (dir: string) =
        if String.IsNullOrEmpty dir then dir
        elif Path.IsPathRooted dir then dir
        else Path.Combine(config.ResolutionFolder, dir)

    let rootType =
        ProvidedTypeDefinition(asm, ns, "Layouts", baseType = Some typeof<obj>, hideObjectMethods = true)

    let buildInstance (typeName: string) (pluginsDir: string) =
        let provided =
            ProvidedTypeDefinition(asm, ns, typeName, baseType = Some typeof<obj>, hideObjectMethods = true)
        try
            let used = System.Collections.Generic.HashSet<string>()
            for (memberName, value) in LayoutScan.builtIns do
                used.Add memberName |> ignore
                provided.AddMember(literalProp memberName value)
            let dir = if String.IsNullOrEmpty pluginsDir then LayoutScan.defaultPluginsDir () else resolvePath pluginsDir
            for name in LayoutScan.scanPlugins dir do
                let memberName = Ident.dedup used (Ident.sanitize name)
                provided.AddMember(literalProp memberName name)
        with _ -> ()
        provided

    do
        // Put the built-ins on the ROOT type too, so a bare `Layouts.Bsp` (zero
        // config, no static arg) works — F# binds a static-arg reference that
        // equals the declared default to the un-instantiated root, which would
        // otherwise be empty. `Layouts<pluginsDir>` then layers plugin layouts on
        // top via the instantiation function.
        for (memberName, value) in LayoutScan.builtIns do
            rootType.AddMember(literalProp memberName value)
        rootType.DefineStaticParameters(
            parameters = [ ProvidedStaticParameter("PluginsDir", typeof<string>, "") ],
            instantiationFunction =
                (fun typeName args ->
                    let dir = try args.[0] :?> string with _ -> ""
                    buildInstance typeName dir))
        this.AddNamespace(ns, [ rootType ])

[<TypeProvider>]
type XkbProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let ns = "WTF.TypeProviders"
    let asm = System.Reflection.Assembly.GetExecutingAssembly()

    let literalProp (name: string) (value: string) =
        ProvidedProperty(name, typeof<string>, isStatic = true,
                         getterCode = fun _ -> <@@ value @@>)

    let resolvePath (path: string) =
        if String.IsNullOrEmpty path then path
        elif Path.IsPathRooted path then path
        else Path.Combine(config.ResolutionFolder, path)

    let rootType =
        ProvidedTypeDefinition(asm, ns, "Xkb", baseType = Some typeof<obj>, hideObjectMethods = true)

    /// Build a nested container. The member NAME comes from `nameOf` (the human
    /// description for layouts — `Xkb.Layouts.Russian`; the code itself for options,
    /// whose codes are already mnemonic — `Xkb.Options.GrpAltShiftToggle`); the
    /// member VALUE is always the xkb code that feeds the keyboard{} block.
    let buildContainer (cname: string) (nameOf: string * string -> string) (entries: (string * string) list) =
        let c = ProvidedTypeDefinition(cname, baseType = Some typeof<obj>, hideObjectMethods = true)
        let used = System.Collections.Generic.HashSet<string>()
        for (code, desc) in entries do
            let memberName = Ident.dedup used (Ident.sanitize (nameOf (code, desc)))
            c.AddMember(literalProp memberName code)
        c

    let byDesc (code, desc) = if String.IsNullOrWhiteSpace desc then code else desc
    let byCode (code, _desc) = code

    let buildInstance (typeName: string) (lstPath: string) =
        let provided =
            ProvidedTypeDefinition(asm, ns, typeName, baseType = Some typeof<obj>, hideObjectMethods = true)
        try
            let path = if String.IsNullOrEmpty lstPath then Xkb.defaultLstPath else resolvePath lstPath
            let contents = try File.ReadAllText path with _ -> ""
            let layouts, options = Xkb.parseLst contents
            provided.AddMember(buildContainer "Layouts" byDesc layouts)
            provided.AddMember(buildContainer "Options" byCode options)
        with _ ->
            // Still provide empty containers so member access at least resolves shape.
            provided.AddMember(buildContainer "Layouts" byDesc [])
            provided.AddMember(buildContainer "Options" byCode [])
        provided

    do
        rootType.DefineStaticParameters(
            parameters = [ ProvidedStaticParameter("LstPath", typeof<string>, "") ],
            instantiationFunction =
                (fun typeName args ->
                    let path = try args.[0] :?> string with _ -> ""
                    buildInstance typeName path))
        this.AddNamespace(ns, [ rootType ])
