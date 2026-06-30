module WTF.Client.Tests

open System
open Xunit
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
