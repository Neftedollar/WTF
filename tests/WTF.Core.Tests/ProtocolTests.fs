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
