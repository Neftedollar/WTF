module WTF.Client.Tests

open System
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsCheck.Xunit
open SixLabors.ImageSharp
open WTF.Client
open WTF.Client.DesktopEntry
open WTF.Client.BarModel

// ---------------------------------------------------------------------------
// DesktopEntry.parse — read [Desktop Entry], require Type/Name/Exec, skip hidden
// ---------------------------------------------------------------------------

let private firefoxDesktop =
    "[Desktop Entry]\n\
     Type=Application\n\
     Name=Firefox\n\
     Exec=firefox %u\n\
     Icon=firefox\n\
     Terminal=false\n\
     [Desktop Action new-window]\n\
     Name=New Window\n\
     Exec=firefox --new-window\n"

[<Fact>]
let ``parse reads the main group only`` () =
    match parse firefoxDesktop with
    | Some e ->
        Assert.Equal("Firefox", e.Name)
        Assert.Equal("firefox %u", e.Exec) // the [Desktop Action] Exec is ignored
        Assert.Equal(Some "firefox", e.Icon)
        Assert.False(e.Terminal)
    | None -> Assert.True(false, "expected Firefox to parse")

[<Fact>]
let ``parse honours Terminal=true`` () =
    let txt = "[Desktop Entry]\nType=Application\nName=htop\nExec=htop\nTerminal=true\n"
    match parse txt with
    | Some e -> Assert.True(e.Terminal)
    | None -> Assert.True(false, "expected htop to parse")

[<Fact>]
let ``parse skips NoDisplay=true`` () =
    let txt = "[Desktop Entry]\nType=Application\nName=Hidden\nExec=foo\nNoDisplay=true\n"
    Assert.Equal(None, parse txt)

[<Fact>]
let ``parse skips Hidden=true`` () =
    let txt = "[Desktop Entry]\nType=Application\nName=Gone\nExec=foo\nHidden=true\n"
    Assert.Equal(None, parse txt)

[<Fact>]
let ``parse requires Type=Application`` () =
    let txt = "[Desktop Entry]\nType=Link\nName=Site\nExec=foo\n"
    Assert.Equal(None, parse txt)

[<Fact>]
let ``parse requires Name and Exec`` () =
    Assert.Equal(None, parse "[Desktop Entry]\nType=Application\nExec=foo\n")
    Assert.Equal(None, parse "[Desktop Entry]\nType=Application\nName=NoExec\n")

// ---------------------------------------------------------------------------
// stripFieldCodes — remove every field code; %% survives as a literal %
// ---------------------------------------------------------------------------

[<Fact>]
let ``stripFieldCodes removes every code`` () =
    Assert.Equal("firefox", stripFieldCodes "firefox %u")
    Assert.Equal("gimp", stripFieldCodes "gimp %F")
    Assert.Equal("mpv", stripFieldCodes "mpv %U")
    Assert.Equal("app", stripFieldCodes "app %f %i %c %k %d %D %n %N %v %m")

[<Fact>]
let ``stripFieldCodes preserves a literal percent`` () =
    Assert.Equal("echo 50%", stripFieldCodes "echo 50%%")

[<Fact>]
let ``stripFieldCodes keeps fixed arguments`` () =
    Assert.Equal("foot -e nvim", stripFieldCodes "foot -e nvim %F")

// ---------------------------------------------------------------------------
// Fuzzy — subsequence match, best wins, prefix beats mid-word, empty -> all
// ---------------------------------------------------------------------------

let private entry name = { Name = name; Exec = name; Icon = None; Terminal = false; FilePath = "" }

[<Fact>]
let ``score is None for a non-subsequence`` () =
    Assert.Equal(None, Fuzzy.score "xyz" "Firefox")

[<Fact>]
let ``score is Some for a subsequence`` () =
    Assert.True((Fuzzy.score "fox" "Firefox").IsSome)

[<Fact>]
let ``rank puts the best match first`` () =
    let entries = [ entry "Thunderbird"; entry "Firefox"; entry "Files"; entry "Foliate" ]
    let ranked = Fuzzy.rank "fox" entries
    Assert.Equal("Firefox", (List.head ranked).Name)

[<Fact>]
let ``rank drops non-subsequence candidates`` () =
    let entries = [ entry "Firefox"; entry "Calculator" ]
    let ranked = Fuzzy.rank "fox" entries
    Assert.DoesNotContain(entries.[1], ranked)
    Assert.Single(ranked) |> ignore

[<Fact>]
let ``rank prefers a prefix over a mid-word match`` () =
    let entries = [ entry "LibreCalc"; entry "Calculator" ]
    let ranked = Fuzzy.rank "cal" entries
    Assert.Equal("Calculator", (List.head ranked).Name)

[<Fact>]
let ``rank with an empty query returns all entries in name order`` () =
    let entries = [ entry "Zathura"; entry "Audacity"; entry "Mpv" ]
    let ranked = Fuzzy.rank "" entries
    Assert.Equal<string list>([ "Audacity"; "Mpv"; "Zathura" ], ranked |> List.map (fun e -> e.Name))

// ---------------------------------------------------------------------------
// BarModel — snapshot JSON + clock -> segments
// ---------------------------------------------------------------------------

let private snapshot =
    """{"current":"2",
        "workspaces":[
          {"tag":"1","windows":[10,11]},
          {"tag":"2","windows":[]},
          {"tag":"3","windows":[20]}
        ],
        "desktop":{
          "battery":{"percent":83,"state":"Discharging"},
          "network":{"state":"connected","primary":"wlan0"},
          "players":[
            {"status":"Paused","title":"Old","artist":"X"},
            {"status":"Playing","title":"Song","artist":"Band"}
          ]
        }}"""

let private fixedNow = DateTime(2026, 6, 30, 14, 5, 0)

[<Fact>]
let ``build marks the current workspace and occupancy`` () =
    let m = build fixedNow snapshot
    Assert.Equal<Segment list>(
        [ Workspace("1", false, true)
          Workspace("2", true, false)
          Workspace("3", false, true) ],
        m.Left
    )

[<Fact>]
let ``build clock comes from the passed-in time`` () =
    let m = build fixedNow snapshot
    Assert.Contains(Clock "14:05", m.Right)

[<Fact>]
let ``build picks the playing player, network, battery`` () =
    let m = build fixedNow snapshot
    Assert.Contains(Player("Playing", "Song", "Band"), m.Right)
    Assert.Contains(Network "wlan0", m.Right)
    Assert.Contains(Battery(83, "Discharging"), m.Right)

[<Fact>]
let ``build right segments are ordered player, network, battery, clock`` () =
    let m = build fixedNow snapshot
    Assert.Equal<Segment list>(
        [ Player("Playing", "Song", "Band"); Network "wlan0"; Battery(83, "Discharging"); Clock "14:05" ],
        m.Right
    )

[<Fact>]
let ``build degrades to clock-only on garbage JSON`` () =
    let m = build fixedNow "}{ not json"
    Assert.Equal<Segment list>([], m.Left)
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

[<Fact>]
let ``build tolerates a snapshot with no desktop object`` () =
    let json = """{"current":"1","workspaces":[{"tag":"1","windows":[5]}]}"""
    let m = build fixedNow json
    Assert.Equal<Segment list>([ Workspace("1", true, true) ], m.Left)
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

[<Fact>]
let ``build ignores a non-playing-only player set`` () =
    let json =
        """{"current":"1","workspaces":[],"desktop":{"players":[{"status":"Paused","title":"T","artist":"A"}]}}"""
    let m = build fixedNow json
    Assert.DoesNotContain(Player("Paused", "T", "A"), m.Right)
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

// ===========================================================================
// ADDED COVERAGE
// ===========================================================================

// ---------------------------------------------------------------------------
// BarModel — graceful degradation: ONE malformed sub-value must NOT collapse
// the whole bar (regression for the broken try-boundary in getStr/getInt).
// ---------------------------------------------------------------------------

[<Fact>]
let ``build keeps valid workspaces when sub-values are wrong-typed`` () =
    // network as a bare string, battery as an array, players as an object —
    // each previously threw InvalidOperationException out of the single outer
    // try, nuking the perfectly-valid workspaces. They must now just drop.
    let json =
        """{"current":"1",
            "workspaces":[{"tag":"1","windows":[5]},{"tag":"2","windows":[]}],
            "desktop":{"network":"garbage","battery":[1,2],"players":{}}}"""
    let m = build fixedNow json
    Assert.Equal<Segment list>(
        [ Workspace("1", true, true); Workspace("2", false, false) ],
        m.Left
    )
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

[<Fact>]
let ``build skips a bare-number workspace element without collapsing`` () =
    let json = """{"current":"2","workspaces":[123,{"tag":"2","windows":[9]}]}"""
    let m = build fixedNow json
    Assert.Equal<Segment list>([ Workspace("2", true, true) ], m.Left)

[<Fact>]
let ``build tolerates a wrong-typed desktop container`` () =
    // desktop itself is a string, not an object: workspaces must survive.
    let json = """{"current":"1","workspaces":[{"tag":"1","windows":[]}],"desktop":"nope"}"""
    let m = build fixedNow json
    Assert.Equal<Segment list>([ Workspace("1", true, false) ], m.Left)
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

// ---------------------------------------------------------------------------
// BarModel — clock is InvariantCulture (':' is the locale separator placeholder)
// ---------------------------------------------------------------------------

[<Fact>]
let ``build formats the clock with a colon even under fi-FI`` () =
    let prev = Threading.Thread.CurrentThread.CurrentCulture
    try
        Threading.Thread.CurrentThread.CurrentCulture <- Globalization.CultureInfo "fi-FI"
        let m = build fixedNow snapshot
        Assert.Contains(Clock "14:05", m.Right) // not "14.05"
    finally
        Threading.Thread.CurrentThread.CurrentCulture <- prev

// ---------------------------------------------------------------------------
// BarModel — non-object / null / empty top-level all degrade to clock-only
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("null")>]
[<InlineData("[1,2,3]")>]
[<InlineData("42")>]
[<InlineData("\"hello\"")>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``build returns clock-only fallback for non-object top-level`` (json: string) =
    let m = build fixedNow json
    Assert.Equal<Segment list>([], m.Left)
    Assert.Equal<Segment list>([ Clock "14:05" ], m.Right)

// ---------------------------------------------------------------------------
// BarModel — network / player / battery selection rules
// ---------------------------------------------------------------------------

[<Fact>]
let ``build network prefers primary, else falls back to state`` () =
    let withState = """{"workspaces":[],"desktop":{"network":{"state":"connected","primary":""}}}"""
    Assert.Contains(Network "connected", (build fixedNow withState).Right)
    let withPrimary = """{"workspaces":[],"desktop":{"network":{"state":"connected","primary":"eth0"}}}"""
    Assert.Contains(Network "eth0", (build fixedNow withPrimary).Right)

[<Fact>]
let ``build drops network when neither primary nor state are present`` () =
    let json = """{"workspaces":[],"desktop":{"network":{}}}"""
    Assert.Equal<Segment list>([ Clock "14:05" ], (build fixedNow json).Right)

[<Fact>]
let ``build picks the first Playing player, case-insensitively`` () =
    let json =
        """{"workspaces":[],"desktop":{"players":[
            {"status":"paused","title":"P","artist":"A"},
            {"status":"playing","title":"First","artist":"B"},
            {"status":"Playing","title":"Second","artist":"C"}]}}"""
    let m = build fixedNow json
    Assert.Contains(Player("playing", "First", "B"), m.Right)
    Assert.DoesNotContain(Player("Playing", "Second", "C"), m.Right)

[<Fact>]
let ``build omits the player segment when nothing is Playing`` () =
    let json = """{"workspaces":[],"desktop":{"players":[{"status":"Stopped","title":"T","artist":"A"}]}}"""
    Assert.Equal<Segment list>([ Clock "14:05" ], (build fixedNow json).Right)

[<Fact>]
let ``build truncates a float battery percent to an int`` () =
    let json = """{"workspaces":[],"desktop":{"battery":{"percent":83.7,"state":"Charging"}}}"""
    Assert.Contains(Battery(83, "Charging"), (build fixedNow json).Right)

[<Fact>]
let ``build drops battery when percent is a quoted string`` () =
    let json = """{"workspaces":[],"desktop":{"battery":{"percent":"90","state":"Charging"}}}"""
    Assert.Equal<Segment list>([ Clock "14:05" ], (build fixedNow json).Right)

[<Fact>]
let ``build clamps an out-of-range battery percent into 0..100`` () =
    let hi = """{"workspaces":[],"desktop":{"battery":{"percent":150,"state":"Full"}}}"""
    Assert.Contains(Battery(100, "Full"), (build fixedNow hi).Right)
    let lo = """{"workspaces":[],"desktop":{"battery":{"percent":-5,"state":"Dead"}}}"""
    Assert.Contains(Battery(0, "Dead"), (build fixedNow lo).Right)

// ---------------------------------------------------------------------------
// BarModel — Right ordering with partial data, always ending in Clock
// ---------------------------------------------------------------------------

[<Fact>]
let ``build right keeps documented order for subsets and ends with clock`` () =
    let onlyBat = """{"workspaces":[],"desktop":{"battery":{"percent":50,"state":"x"}}}"""
    Assert.Equal<Segment list>([ Battery(50, "x"); Clock "14:05" ], (build fixedNow onlyBat).Right)
    let netBat =
        """{"workspaces":[],"desktop":{"network":{"primary":"w"},"battery":{"percent":50,"state":"x"}}}"""
    Assert.Equal<Segment list>(
        [ Network "w"; Battery(50, "x"); Clock "14:05" ],
        (build fixedNow netBat).Right
    )

[<Fact>]
let ``build with an empty workspaces array yields empty Left`` () =
    let json = """{"current":"1","workspaces":[],"desktop":{}}"""
    Assert.Equal<Segment list>([], (build fixedNow json).Left)

// ---------------------------------------------------------------------------
// DesktopEntry.stripFieldCodes — edge cases + quoted-whitespace preservation
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("foo %", "foo %")>]           // lone trailing '%' is kept verbatim
[<InlineData("foo %%%%", "foo %%")>]       // doubled %% collapse pairwise
[<InlineData("foo %z bar", "foo bar")>]    // unknown code dropped
[<InlineData("app %i", "app")>]
[<InlineData("app %c", "app")>]
[<InlineData("app %k", "app")>]
[<InlineData("app %f --flag %u file", "app --flag file")>]
let ``stripFieldCodes edge cases`` (input: string) (expected: string) =
    Assert.Equal(expected, stripFieldCodes input)

[<Fact>]
let ``stripFieldCodes preserves whitespace inside quoted args`` () =
    // regression: the old global whitespace-collapse corrupted quoted runs.
    Assert.Equal("sh -c \"printf 'a  b'\"", stripFieldCodes "sh -c \"printf 'a  b'\" %f")
    Assert.Equal("x 'a\tb'", stripFieldCodes "x   'a\tb'   %u")

// ---------------------------------------------------------------------------
// DesktopEntry.parse — group handling, '=' in values, trimming
// ---------------------------------------------------------------------------

[<Fact>]
let ``parse ignores keys before the Desktop Entry header`` () =
    let txt = "Name=Ghost\nExec=ghost\n[Desktop Entry]\nType=Application\nName=Real\nExec=real %u\n"
    match parse txt with
    | Some e ->
        Assert.Equal("Real", e.Name)
        Assert.Equal("real %u", e.Exec)
    | None -> Assert.True(false, "expected Real")

[<Fact>]
let ``parse ignores a Desktop Action group preceding Desktop Entry`` () =
    let txt =
        "[Desktop Action foo]\nName=ActionName\nExec=action\n[Desktop Entry]\nType=Application\nName=Main\nExec=main\n"
    match parse txt with
    | Some e ->
        Assert.Equal("Main", e.Name)
        Assert.Equal("main", e.Exec)
    | None -> Assert.True(false, "expected Main")

[<Fact>]
let ``parse keeps the tail of a value containing an equals`` () =
    let txt = "[Desktop Entry]\nType=Application\nName=App\nExec=foo --opt=bar\n"
    match parse txt with
    | Some e -> Assert.Equal("foo --opt=bar", e.Exec)
    | None -> Assert.True(false, "expected App")

[<Fact>]
let ``parse trims spaces around the equals and the value`` () =
    let txt = "[Desktop Entry]\nType=Application\nName = Spaced Name \nExec= cmd \n"
    match parse txt with
    | Some e ->
        Assert.Equal("Spaced Name", e.Name)
        Assert.Equal("cmd", e.Exec)
    | None -> Assert.True(false, "expected Spaced Name")

[<Fact>]
let ``parse second Desktop Entry group does not override first-wins values`` () =
    let txt =
        "[Desktop Entry]\nType=Application\nName=First\nExec=first\n[Desktop Entry]\nName=Second\nExec=second\n"
    match parse txt with
    | Some e ->
        Assert.Equal("First", e.Name)
        Assert.Equal("first", e.Exec)
    | None -> Assert.True(false, "expected First")

// ---------------------------------------------------------------------------
// DesktopEntry.parse — first-wins duplicates + localized keys
// ---------------------------------------------------------------------------

[<Fact>]
let ``parse first Name and Exec win over duplicates`` () =
    let txt = "[Desktop Entry]\nType=Application\nName=One\nName=Two\nExec=e1\nExec=e2\n"
    match parse txt with
    | Some e ->
        Assert.Equal("One", e.Name)
        Assert.Equal("e1", e.Exec)
    | None -> Assert.True(false, "expected One")

[<Fact>]
let ``parse ignores a localized Name when a plain Name exists`` () =
    let txt = "[Desktop Entry]\nType=Application\nName[de]=Deutsch\nName=Plain\nExec=e\n"
    match parse txt with
    | Some e -> Assert.Equal("Plain", e.Name)
    | None -> Assert.True(false, "expected Plain")

[<Fact>]
let ``parse returns None when only a localized Name is present`` () =
    let txt = "[Desktop Entry]\nType=Application\nName[de]=Deutsch\nExec=e\n"
    Assert.Equal(None, parse txt)

// ---------------------------------------------------------------------------
// DesktopEntry.parse — missing/edge required fields
// ---------------------------------------------------------------------------

[<Fact>]
let ``parse returns None for empty Name or empty Exec`` () =
    Assert.Equal(None, parse "[Desktop Entry]\nType=Application\nName=\nExec=foo\n")
    Assert.Equal(None, parse "[Desktop Entry]\nType=Application\nName=foo\nExec=\n")

[<Fact>]
let ``parse returns None when Type is missing entirely`` () =
    Assert.Equal(None, parse "[Desktop Entry]\nName=foo\nExec=bar\n")

[<Fact>]
let ``parse NoDisplay is case-insensitive`` () =
    Assert.Equal(None, parse "[Desktop Entry]\nType=Application\nName=N\nExec=e\nNoDisplay=TRUE\n")

[<Fact>]
let ``parse Hidden=1 does not hide because only 'true' counts`` () =
    match parse "[Desktop Entry]\nType=Application\nName=N\nExec=e\nHidden=1\n" with
    | Some e -> Assert.Equal("N", e.Name)
    | None -> Assert.True(false, "Hidden=1 should NOT hide (only 'true' hides)")

// ---------------------------------------------------------------------------
// Fuzzy.score — subsequence invariants
// ---------------------------------------------------------------------------

[<Fact>]
let ``score empty query is Some 0`` () =
    Assert.Equal(Some 0, Fuzzy.score "" "anything")

[<Fact>]
let ``score empty candidate with a non-empty query is None`` () =
    Assert.Equal(None, Fuzzy.score "x" "")

[<Fact>]
let ``score of a query longer than the candidate is None`` () =
    Assert.Equal(None, Fuzzy.score "firefoxx" "Firefox")

[<Fact>]
let ``score is case-insensitive in both directions`` () =
    Assert.True((Fuzzy.score "FOX" "firefox").IsSome)
    Assert.True((Fuzzy.score "fox" "FIREFOX").IsSome)

[<Fact>]
let ``score of a reversed subsequence is None`` () =
    Assert.Equal(None, Fuzzy.score "xof" "Firefox")

// ---------------------------------------------------------------------------
// Fuzzy.score — scoring guarantees (relative orderings, not magic numbers)
// ---------------------------------------------------------------------------

[<Fact>]
let ``score prefix beats interior for the same letters`` () =
    Assert.True(Fuzzy.score "cal" "Calculator" > Fuzzy.score "cal" "LibreCalc")

[<Fact>]
let ``score word-boundary start beats an interior match`` () =
    Assert.True(Fuzzy.score "pl" "Media Player" > Fuzzy.score "pl" "applet")

[<Fact>]
let ``score CamelCase hump beats a plain interior match`` () =
    Assert.True(Fuzzy.score "c" "LibreCalc" > Fuzzy.score "c" "abcdef")

[<Fact>]
let ``score rewards a contiguous run over a scattered one`` () =
    Assert.True(Fuzzy.score "abc" "abcxyz" > Fuzzy.score "abc" "axbxc")

[<Fact>]
let ``score gives a full leading-prefix bonus`` () =
    Assert.True(Fuzzy.score "fire" "firefox" > Fuzzy.score "fire" "afirefox")

// ---------------------------------------------------------------------------
// Fuzzy.rank — tie-breaking + edge inputs (incl. the IsNullOrWhiteSpace vs
// IsNullOrEmpty divergence between rank and score)
// ---------------------------------------------------------------------------

[<Fact>]
let ``rank breaks ties on the lowercased name`` () =
    let ranked = Fuzzy.rank "a" [ entry "aYa"; entry "aXa" ]
    Assert.Equal<string list>([ "aXa"; "aYa" ], ranked |> List.map (fun e -> e.Name))

[<Fact>]
let ``rank with a whitespace-only query returns all entries in name order`` () =
    let entries = [ entry "Zeta"; entry "alpha"; entry "Beta" ]
    let ranked = Fuzzy.rank "   " entries
    Assert.Equal<string list>([ "alpha"; "Beta"; "Zeta" ], ranked |> List.map (fun e -> e.Name))

[<Fact>]
let ``rank with a trailing-space query filters by subsequence`` () =
    // "fox " is non-whitespace so the score path runs; the trailing space is not a
    // subsequence of "Firefox", so it is dropped (pins the rank/score divergence).
    Assert.Empty(Fuzzy.rank "fox " [ entry "Firefox" ])

[<Fact>]
let ``rank returns empty for an empty list and for a no-match query`` () =
    Assert.Empty(Fuzzy.rank "fox" [])
    Assert.Empty(Fuzzy.rank "zzz" [ entry "Firefox" ])

// ---- FsCheck properties ------------------------------------------------------

[<Property>]
let ``prop score empty query is always Some 0`` (candidate: string) =
    Fuzzy.score "" candidate = Some 0

[<Property>]
let ``prop a string is always a subsequence of itself`` (s: string) =
    (Fuzzy.score s s).IsSome

// ---------------------------------------------------------------------------
// DesktopEntry.scan — precedence + graceful IO over real temp dirs
// ---------------------------------------------------------------------------

let private writeFile (path: string) (content: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, content)

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "wtf-scan-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

[<Fact>]
let ``scan earlier dir wins for the same desktop id`` () =
    let d1, d2 = tempDir (), tempDir ()
    try
        writeFile (Path.Combine(d1, "foo.desktop")) "[Desktop Entry]\nType=Application\nName=FromOne\nExec=one\n"
        writeFile (Path.Combine(d2, "foo.desktop")) "[Desktop Entry]\nType=Application\nName=FromTwo\nExec=two\n"
        let names = DesktopEntry.scan [ d1; d2 ] |> List.map (fun e -> e.Name)
        Assert.Contains("FromOne", names)
        Assert.DoesNotContain("FromTwo", names)
    finally
        Directory.Delete(d1, true)
        Directory.Delete(d2, true)

[<Fact>]
let ``scan maps a nested id to the dashed form for dedupe`` () =
    let d1, d2 = tempDir (), tempDir ()
    try
        writeFile (Path.Combine(d1, "sub", "bar.desktop")) "[Desktop Entry]\nType=Application\nName=Nested\nExec=a\n"
        writeFile (Path.Combine(d2, "sub-bar.desktop")) "[Desktop Entry]\nType=Application\nName=Dashed\nExec=b\n"
        let names = DesktopEntry.scan [ d1; d2 ] |> List.map (fun e -> e.Name)
        Assert.Contains("Nested", names)
        Assert.DoesNotContain("Dashed", names) // same id 'sub-bar.desktop' -> d1 wins
    finally
        Directory.Delete(d1, true)
        Directory.Delete(d2, true)

[<Fact>]
let ``scan populates FilePath with the absolute path`` () =
    let d = tempDir ()
    try
        let f = Path.Combine(d, "app.desktop")
        writeFile f "[Desktop Entry]\nType=Application\nName=App\nExec=app\n"
        match DesktopEntry.scan [ d ] with
        | [ e ] -> Assert.Equal(f, e.FilePath)
        | other -> Assert.True(false, sprintf "expected one entry, got %d" other.Length)
    finally
        Directory.Delete(d, true)

[<Fact>]
let ``scan skips a garbage file and a missing dir without throwing`` () =
    let d = tempDir ()
    try
        writeFile (Path.Combine(d, "good.desktop")) "[Desktop Entry]\nType=Application\nName=Good\nExec=g\n"
        writeFile (Path.Combine(d, "bad.desktop")) "this is not a desktop file at all {{{"
        let entries = DesktopEntry.scan [ d; Path.Combine(d, "does-not-exist") ]
        Assert.Contains("Good", entries |> List.map (fun e -> e.Name))
        Assert.Single(entries) |> ignore
    finally
        Directory.Delete(d, true)

// ---------------------------------------------------------------------------
// Render — measureWidth + primitives (font may be None on CI -> graceful skip)
// ---------------------------------------------------------------------------

[<Fact>]
let ``measureWidth is 0 for empty/null and positive for text`` () =
    match Render.font 16.0f with
    | None -> () // no font available headless: graceful skip
    | Some f ->
        Assert.Equal(0.0f, Render.measureWidth f "")
        Assert.Equal(0.0f, Render.measureWidth f null)
        Assert.True(Render.measureWidth f "abc" > 0.0f)

[<Fact>]
let ``drawing primitives never throw on degenerate inputs`` () =
    use surf = new Render.Surface()
    surf.Draw(
        4, 4,
        fun ctx ->
            Render.fillRect ctx Color.Red 0.0f 0.0f 0.0f 4.0f      // w<=0 -> no-op
            Render.fillRect ctx Color.Red 0.0f 0.0f 4.0f -1.0f     // h<=0 -> no-op
            Render.fillRoundedRect ctx Color.Red 0.0f 0.0f 4.0f 4.0f 0.3f // radius<=0.5 -> plain rect
            Render.fillRoundedRect ctx Color.Red 0.0f 0.0f 4.0f 4.0f 2.0f // proper pill
    )
    Assert.True(true)

// ---------------------------------------------------------------------------
// Render.Surface.Blit — ARGB8888-shm == Bgra32 memory (no channel swap),
// padded-stride row path, and no-op guards.
// ---------------------------------------------------------------------------

[<Fact>]
let ``Surface blit writes B,G,R,A with no channel swap`` () =
    use surf = new Render.Surface()
    surf.Draw(2, 2, fun ctx -> Render.fillRect ctx (Color.FromRgba(200uy, 100uy, 50uy, 255uy)) 0.0f 0.0f 2.0f 2.0f)
    let ptr = Marshal.AllocHGlobal(2 * 2 * 4)
    try
        surf.Blit(ptr, 2, 2, 2 * 4)
        Assert.Equal(50uy, Marshal.ReadByte(ptr, 0))   // B
        Assert.Equal(100uy, Marshal.ReadByte(ptr, 1))  // G
        Assert.Equal(200uy, Marshal.ReadByte(ptr, 2))  // R
        Assert.Equal(255uy, Marshal.ReadByte(ptr, 3))  // A
    finally
        Marshal.FreeHGlobal ptr

[<Fact>]
let ``Surface blit honours a padded stride row-by-row`` () =
    use surf = new Render.Surface()
    surf.Draw(2, 1, fun ctx -> Render.fillRect ctx (Color.FromRgba(10uy, 20uy, 30uy, 255uy)) 0.0f 0.0f 2.0f 1.0f)
    let stride = 2 * 4 + 8 // padded beyond w*4
    let buf = Marshal.AllocHGlobal stride
    try
        for i in 0 .. stride - 1 do
            Marshal.WriteByte(buf, i, 0xAAuy) // poison
        surf.Blit(buf, 2, 1, stride)
        Assert.Equal(30uy, Marshal.ReadByte(buf, 0)) // B
        Assert.Equal(20uy, Marshal.ReadByte(buf, 1)) // G
        Assert.Equal(10uy, Marshal.ReadByte(buf, 2)) // R
        Assert.Equal(255uy, Marshal.ReadByte(buf, 3)) // A
        Assert.Equal(0xAAuy, Marshal.ReadByte(buf, 8)) // padding untouched
    finally
        Marshal.FreeHGlobal buf

[<Fact>]
let ``Surface blit no-ops on null ptr and size mismatch`` () =
    use surf = new Render.Surface()
    surf.Draw(2, 2, fun ctx -> Render.fillRect ctx Color.Black 0.0f 0.0f 2.0f 2.0f)
    surf.Blit(0n, 2, 2, 8) // null ptr -> no-op, no throw
    let p = Marshal.AllocHGlobal(3 * 3 * 4)
    try
        surf.Blit(p, 3, 3, 12) // canvas is 2x2 -> size mismatch -> no-op
    finally
        Marshal.FreeHGlobal p
    Assert.True(true)

// ---------------------------------------------------------------------------
// Panel FFI ABI — guard against silent struct drift vs wtf_panel.h
// ---------------------------------------------------------------------------

[<Fact>]
let ``Panel.Config ABI is ns plus ten ints, sequential`` () =
    let t = typeof<Panel.Config>
    let fields = t.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public)
    Assert.Equal(11, fields.Length)
    Assert.Equal(1, fields |> Array.filter (fun f -> f.FieldType = typeof<string>) |> Array.length)
    Assert.Equal(10, fields |> Array.filter (fun f -> f.FieldType = typeof<int>) |> Array.length)
    Assert.True(t.IsLayoutSequential)

[<Fact>]
let ``Panel.Callbacks ABI is four function pointers, sequential`` () =
    let t = typeof<Panel.Callbacks>
    let fields = t.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public)
    Assert.Equal(4, fields.Length)
    Assert.True(fields |> Array.forall (fun f -> f.FieldType = typeof<nativeint>))
    Assert.True(t.IsLayoutSequential)

// ============================================================================
//  ClientConfig — the "ui" wire-contract parser (defensive, total).
// ============================================================================

open WTF.Client.ClientConfig

let private uiSnapshot = """
{"current":"1","workspaces":[],
 "ui":{"bars":[
   {"name":"main","position":"bottom","height":32,"fontSize":15.0,
    "background":"#11111bcc","foreground":"#cdd6f4","dim":"#6c7086","accent":"#f38ba8",
    "left":["workspaces",{"label":"WTF"}],
    "right":[{"clock":"ddd HH:mm"},"battery"]},
   {"name":"side","position":"left","height":30}],
  "omnibox":{"width":720,"height":420,"rowHeight":34,"fontSize":18.0,
   "background":"#181825f4","selection":"#a6e3a1","prompt":"λ","placeholder":"go"}}}
"""

[<Fact>]
let ``barOfSnapshot picks the named bar and parses every field`` () =
    let b = barOfSnapshot (Some "main") uiSnapshot
    Assert.Equal(SideBottom, b.Side)
    Assert.Equal(32, b.Height)
    Assert.Equal(15.0f, b.FontSize)
    Assert.Equal<SegmentSpec list>([ SWorkspaces; SLabel "WTF" ], b.Left)
    Assert.Equal<SegmentSpec list>([ SClock "ddd HH:mm"; SBattery ], b.Right)

[<Fact>]
let ``barOfSnapshot None takes the first bar; named picks by name`` () =
    Assert.Equal(SideBottom, (barOfSnapshot None uiSnapshot).Side)
    Assert.Equal(SideLeft, (barOfSnapshot (Some "side") uiSnapshot).Side)
    // unknown name -> defaults, not a crash
    Assert.Equal(barDefaults, barOfSnapshot (Some "nope") uiSnapshot)

[<Fact>]
let ``barOfSnapshot is total on garbage and on snapshots without ui`` () =
    Assert.Equal(barDefaults, barOfSnapshot None "")
    Assert.Equal(barDefaults, barOfSnapshot None "{not json")
    Assert.Equal(barDefaults, barOfSnapshot None """{"current":"1"}""")
    Assert.Equal(barDefaults, barOfSnapshot None """{"ui":{"bars":[]}}""")
    Assert.Equal(barDefaults, barOfSnapshot None """{"ui":{"bars":42}}""")

[<Fact>]
let ``omniboxOfSnapshot parses set fields and defaults the rest`` () =
    let o = omniboxOfSnapshot uiSnapshot
    Assert.Equal(720, o.Width)
    Assert.Equal(34, o.RowHeight)
    Assert.Equal("λ", o.Prompt)
    Assert.Equal("go", o.Placeholder)
    Assert.Equal(omniboxDefaults.InputBg, o.InputBg)   // untouched -> default
    Assert.Equal(omniboxDefaults, omniboxOfSnapshot "")

[<Fact>]
let ``parseHex handles rgb, rgba, missing hash and rejects garbage`` () =
    Assert.True((parseHex "#89b4fa").IsSome)
    Assert.True((parseHex "11111bcc").IsSome)
    Assert.Equal(None, parseHex "#123")
    Assert.Equal(None, parseHex "not-a-color")
    Assert.Equal(None, parseHex null)

[<Fact>]
let ``buildWith honors configured segments and clock format`` () =
    let now = DateTime(2026, 7, 2, 23, 45, 0)
    let m = WTF.Client.BarModel.buildWith [ SLabel "hi" ] [ SClock "HH.mm" ] now ""
    Assert.Equal<WTF.Client.BarModel.Segment list>([ WTF.Client.BarModel.Text "hi" ], m.Left)
    Assert.Equal<WTF.Client.BarModel.Segment list>([ WTF.Client.BarModel.Clock "23.45" ], m.Right)
    // a garbage clock format degrades to HH:mm instead of throwing
    let g = WTF.Client.BarModel.buildWith [] [ SClock "\\invalid\\" ] now ""
    Assert.Equal<WTF.Client.BarModel.Segment list>([ WTF.Client.BarModel.Clock "23:45" ], g.Right)
