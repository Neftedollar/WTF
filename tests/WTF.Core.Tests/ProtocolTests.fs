module WTF.Core.Tests.ProtocolTests

open Xunit
open WTF.Core

[<Theory>]
[<InlineData("""{"cmd":"focus","by":"next"}""")>]
[<InlineData("""{"cmd":"focus","app":"firefox"}""")>]
[<InlineData("""{"cmd":"layout","name":"bsp"}""")>]
[<InlineData("""{"cmd":"workspace","switch":"3"}""")>]
[<InlineData("""{"cmd":"spawn","run":"foot"}""")>]
[<InlineData("""{"cmd":"master","n":2}""")>]
[<InlineData("""{"cmd":"ratio","value":0.6}""")>]
let ``valid commands parse`` (json: string) =
    Assert.True((Protocol.parse json).IsSome)

[<Fact>]
let ``focus selectors decode correctly`` () =
    Assert.Equal(Some(Focus NextWindow), Protocol.parse """{"cmd":"focus","by":"next"}""")
    Assert.Equal(Some(Focus(ByApp "firefox")), Protocol.parse """{"cmd":"focus","app":"firefox"}""")
    Assert.Equal(Some(Focus(ById 7)), Protocol.parse """{"cmd":"focus","id":7}""")

[<Fact>]
let ``garbage and unknown commands return None`` () =
    Assert.Equal(None, Protocol.parse "not json")
    Assert.Equal(None, Protocol.parse """{"cmd":"explode"}""")

[<Fact>]
let ``hexColor parses 6- and 3-digit hex`` () =
    Assert.Equal(Some(1.0, 1.0, 1.0), Protocol.hexColor "#ffffff")
    Assert.Equal(Some(0.0, 0.0, 0.0), Protocol.hexColor "000000")
    Assert.Equal(Some(1.0, 1.0, 1.0), Protocol.hexColor "#fff")
    Assert.Equal(None, Protocol.hexColor "nope")

[<Fact>]
let ``border commands parse to actions`` () =
    Assert.Equal(Some(Protocol.Act(SetBorderWidth 4)), Protocol.parseRequest """{"cmd":"border","width":4}""")
    match Protocol.parseRequest """{"cmd":"border","active":true,"color":"#ffffff"}""" with
    | Some(Protocol.Act(SetBorderColor(true, r, g, b))) ->
        Assert.Equal(1.0, r); Assert.Equal(1.0, g); Assert.Equal(1.0, b)
    | other -> failwithf "unexpected: %A" other

[<Fact>]
let ``scenefx appearance commands parse`` () =
    Assert.Equal(Some(Protocol.Act(SetCornerRadius 12)), Protocol.parseRequest """{"cmd":"corners","value":12}""")
    Assert.Equal(Some(Protocol.Act(SetBlur true)), Protocol.parseRequest """{"cmd":"blur","on":true}""")
    Assert.Equal(Some(Protocol.Act(SetBlur false)), Protocol.parseRequest """{"cmd":"blur"}""")

[<Fact>]
let ``parseRequest distinguishes queries from actions`` () =
    Assert.Equal(Some Protocol.Query, Protocol.parseRequest "state")
    Assert.Equal(Some Protocol.Query, Protocol.parseRequest "")
    Assert.Equal(Some Protocol.Query, Protocol.parseRequest """{"cmd":"state"}""")
    Assert.Equal(Some(Protocol.Act(SetLayout "bsp")), Protocol.parseRequest """{"cmd":"layout","name":"bsp"}""")
    Assert.Equal(None, Protocol.parseRequest """{"cmd":"explode"}""")

[<Fact>]
let ``parseRequest recognizes the agent-first verbs`` () =
    Assert.Equal(Some Protocol.Tools, Protocol.parseRequest """{"tools":true}""")
    Assert.Equal(None, Protocol.parseRequest """{"tools":false}""") // not a manifest request
    Assert.Equal(Some(Protocol.Ask "tidy up"), Protocol.parseRequest """{"ask":"tidy up"}""")
    Assert.Equal(Some(Protocol.Notify("hi", "")), Protocol.parseRequest """{"notify":{"summary":"hi"}}""")
    Assert.Equal(Some(Protocol.Notify("hi", "there")), Protocol.parseRequest """{"notify":{"summary":"hi","body":"there"}}""")
    Assert.Equal(None, Protocol.parseRequest """{"notify":{"body":"no summary"}}""")
    // The existing eval / command / query doors are unaffected.
    Assert.Equal(Some(Protocol.Eval "config { gaps 20 }"), Protocol.parseRequest """{"eval":"config { gaps 20 }"}""")

[<Fact>]
let ``snapshot is valid JSON exposing windows and arrange`` () =
    let screen = Rect.create 0 0 1920 1080
    let w =
        Reducer.applyMany
            [ AddWindow { Id = 1; AppId = "foot"; Title = "shell"; Floating = false }
              AddWindow { Id = 2; AppId = "firefox"; Title = "web"; Floating = false } ]
            (World.empty screen)
        |> fst
    let json = Protocol.snapshot w
    let doc = System.Text.Json.JsonDocument.Parse json // throws if invalid
    Assert.Equal("1", doc.RootElement.GetProperty("current").GetString())
    Assert.Equal(2, doc.RootElement.GetProperty("arrange").GetArrayLength())

// ============================================================================
//  parseRequest: typed-mismatch / malformed-shape must return None, NEVER throw.
//  (Regression for the control-socket crash: `str` was unguarded outside the
//  generic `parse` try/with, so a hostile line like {"eval":123} threw.)
// ============================================================================

[<Theory>]
[<InlineData("""{"eval":123}""")>]
[<InlineData("""{"ask":123}""")>]
[<InlineData("""{"cmd":123}""")>]
[<InlineData("""{"notify":{"summary":123}}""")>]
[<InlineData("""{"notify":42}""")>]
[<InlineData("""{"notify":{}}""")>]
[<InlineData("""{"notify":{"body":"no summary"}}""")>]
let ``parseRequest returns None (not a throw) on typed-mismatch input`` (json: string) =
    // A plain Assert.Equal already converts a thrown exception into a test
    // failure, which is exactly the regression we are guarding against.
    Assert.Equal(None, Protocol.parseRequest json)

[<Fact>]
let ``parseRequest degrades a malformed notify body to empty (summary still wins)`` () =
    // summary is the ONLY required notify field; a present-but-wrong-typed body
    // gracefully falls back to "" (mirrors AgentTools.toToolCall's notify path).
    Assert.Equal(Some(Protocol.Notify("x", "")), Protocol.parseRequest """{"notify":{"summary":"x","body":123}}""")

// ============================================================================
//  parse: command-verb coverage (round-trip each verb to its Command).
// ============================================================================

[<Fact>]
let ``parse maps every action verb to the right Command`` () =
    let eq exp s = Assert.Equal(exp, Protocol.parse s)
    eq (Some SwapNext) """{"cmd":"swap"}"""
    eq (Some SwapPrev) """{"cmd":"swap","dir":"prev"}"""
    eq (Some SwapMaster) """{"cmd":"swap","dir":"master"}"""
    eq (Some SwapNext) """{"cmd":"swap","dir":"garbage"}"""   // unknown dir -> next
    eq (Some SwapMaster) """{"cmd":"swapmaster"}"""
    eq (Some FocusMaster) """{"cmd":"focusmaster"}"""
    eq (Some ToggleFloat) """{"cmd":"float"}"""
    eq (Some ToggleFullscreen) """{"cmd":"fullscreen"}"""
    eq (Some SinkAll) """{"cmd":"sinkall"}"""
    eq (Some CloseFocused) """{"cmd":"close"}"""
    eq (Some(MoveToWorkspace "4")) """{"cmd":"workspace","move":"4"}"""
    eq (Some NextWorkspace) """{"cmd":"workspace","next":true}"""
    eq (Some PrevWorkspace) """{"cmd":"workspace","prev":true}"""
    eq (Some NextLayout) """{"cmd":"layout","next":true}"""
    eq (Some IncMaster) """{"cmd":"master","inc":true}"""
    eq (Some DecMaster) """{"cmd":"master","dec":true}"""
    eq (Some(SetMaster 3)) """{"cmd":"master","n":3}"""
    eq (Some IncGaps) """{"cmd":"gaps","inc":true}"""
    eq (Some DecGaps) """{"cmd":"gaps","dec":true}"""
    eq (Some(SetGaps 10)) """{"cmd":"gaps","value":10}"""
    eq (Some(SetInactiveOpacity 0.8)) """{"cmd":"opacity","value":0.8}"""
    eq (Some(SetAnimationSpeed 0.5)) """{"cmd":"anim","value":0.5}"""
    eq (Some(SetBlur false)) """{"cmd":"blur"}"""
    eq (Some Undo) """{"cmd":"undo"}"""
    eq (Some Redo) """{"cmd":"redo"}"""
    eq (Some SaveSession) """{"cmd":"session","save":true}"""
    eq (Some LoadSession) """{"cmd":"session","restore":true}"""

[<Theory>]
// verbs whose required argument/flag is missing -> None
[<InlineData("""{"cmd":"workspace"}""")>]
[<InlineData("""{"cmd":"layout"}""")>]
[<InlineData("""{"cmd":"master"}""")>]
[<InlineData("""{"cmd":"ratio"}""")>]
[<InlineData("""{"cmd":"gaps"}""")>]
[<InlineData("""{"cmd":"corners"}""")>]
[<InlineData("""{"cmd":"session"}""")>]
let ``parse returns None when a required field is absent`` (json: string) =
    Assert.Equal(None, Protocol.parse json)

[<Theory>]
// integer fields given a fractional number must reject (no silent truncation).
[<InlineData("""{"cmd":"master","n":2.7}""")>]
[<InlineData("""{"cmd":"gaps","value":3.5}""")>]
[<InlineData("""{"cmd":"corners","value":1.5}""")>]
[<InlineData("""{"cmd":"border","width":2.5}""")>]
let ``parse rejects fractional values in integer fields`` (json: string) =
    Assert.Equal(None, Protocol.parse json)

// ============================================================================
//  hexColor edge cases.
// ============================================================================

[<Fact>]
let ``hexColor rejects wrong-length and invalid-hex inputs`` () =
    Assert.Equal(None, Protocol.hexColor "#ffff")     // 4 digits
    Assert.Equal(None, Protocol.hexColor "#fffff")    // 5 digits
    Assert.Equal(None, Protocol.hexColor "#xyz")      // invalid hex, 3 digits
    Assert.Equal(None, Protocol.hexColor "#gggggg")   // invalid hex, 6 digits
    Assert.Equal(None, Protocol.hexColor "")          // empty
    Assert.Equal(None, Protocol.hexColor "#")         // hash only

[<Fact>]
let ``hexColor rejects multiple leading hashes (regression)`` () =
    // "##fff"/"###ffffff" used to strip ALL '#' and parse as white.
    Assert.Equal(None, Protocol.hexColor "##fff")
    Assert.Equal(None, Protocol.hexColor "###ffffff")

[<Fact>]
let ``hexColor parses uppercase and computes per-channel values`` () =
    match Protocol.hexColor "#ABCDEF" with
    | Some _ -> ()
    | None -> failwith "uppercase hex should parse"
    match Protocol.hexColor "#ff8000" with
    | Some(r, g, b) ->
        Assert.Equal(1.0, r)
        Assert.True(abs (g - 0.50196) < 1e-4, sprintf "g=%f" g)
        Assert.Equal(0.0, b)
    | None -> failwith "expected Some"

// ============================================================================
//  snapshot: floating / fullscreen / desktop exposure + the snapshotWith None
//  byte-identity invariant.
// ============================================================================

[<Fact>]
let ``snapshot exposes floating geometry and the fullscreen id`` () =
    let screen = Rect.create 0 0 1920 1080
    let w =
        Reducer.applyMany
            [ AddWindow { Id = 1; AppId = "a"; Title = "a"; Floating = false }
              AddWindow { Id = 2; AppId = "b"; Title = "b"; Floating = false }
              AddWindow { Id = 3; AppId = "c"; Title = "c"; Floating = false }
              Focus(ById 2); ToggleFloat
              Focus(ById 3); ToggleFullscreen ]
            (World.empty screen)
        |> fst
    let doc = System.Text.Json.JsonDocument.Parse(Protocol.snapshot w)
    let ws0 = doc.RootElement.GetProperty("workspaces").[0]
    let floating = ws0.GetProperty("floating")
    Assert.Equal(1, floating.GetArrayLength())
    Assert.Equal(2, floating.[0].GetProperty("id").GetInt32())
    Assert.True(floating.[0].GetProperty("w").GetInt32() > 0)
    Assert.Equal(3, ws0.GetProperty("fullscreen").GetInt32())

[<Fact>]
let ``snapshotWith None is byte-identical to snapshot`` () =
    let screen = Rect.create 0 0 800 600
    let w =
        Reducer.applyMany
            [ AddWindow { Id = 1; AppId = "a"; Title = "a"; Floating = false } ]
            (World.empty screen)
        |> fst
    Assert.Equal(Protocol.snapshot w, Protocol.snapshotWith None w)
    Assert.Equal(Protocol.snapshotLine w, Protocol.snapshotLineWith None w)

[<Fact>]
let ``snapshotWith Some splices the extra node under desktop`` () =
    let screen = Rect.create 0 0 800 600
    let extra = System.Text.Json.Nodes.JsonObject()
    extra["tray"] <- System.Text.Json.Nodes.JsonValue.Create "ok"
    let json = Protocol.snapshotWith (Some extra) (World.empty screen)
    let doc = System.Text.Json.JsonDocument.Parse json
    Assert.Equal("ok", doc.RootElement.GetProperty("desktop").GetProperty("tray").GetString())

[<Fact>]
let ``snapshot of an empty world is valid JSON with an empty arrange`` () =
    let json = Protocol.snapshot (World.empty (Rect.create 0 0 640 480))
    let doc = System.Text.Json.JsonDocument.Parse json
    Assert.Equal(0, doc.RootElement.GetProperty("arrange").GetArrayLength())
