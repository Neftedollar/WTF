module WTF.Desktop.Tests.DesktopJsonTests

open System.Text.Json.Nodes
open Xunit
open WTF.Desktop
open WTF.Desktop.Models

let private boolAt (n: JsonNode) = n.GetValue<bool>()
let private strAt (n: JsonNode) = n.GetValue<string>()
let private f64At (n: JsonNode) = n.GetValue<float>()
let private u32At (n: JsonNode) = n.GetValue<uint32>()
let private i64At (n: JsonNode) = n.GetValue<int64>()

[<Fact>]
let ``empty state renders the expected shape with nulls`` () =
    let j = DesktopJson.render DesktopState.empty
    Assert.Equal(0, j["notifications"].AsArray().Count)
    Assert.True(isNull j["battery"])
    Assert.True(isNull j["network"])
    Assert.Equal(0, j["players"].AsArray().Count)
    Assert.False(boolAt (j["power"]["preparingForSleep"]))
    Assert.False(boolAt (j["power"]["sessionLocked"]))

[<Fact>]
let ``notifications render id, summary, actions and expiry`` () =
    let store, id =
        NotificationStore.add 0L 5000 0u "Firefox" "web-browser" "Hi" "there"
            [ ("default", "Open"); ("dismiss", "Dismiss") ] (Some 1uy) 1000
            NotificationStore.empty
    let state = { DesktopState.empty with Notifications = store }
    let j = DesktopJson.render state
    let arr = j["notifications"].AsArray()
    Assert.Equal(1, arr.Count)
    let n = arr[0]
    Assert.Equal(id, u32At (n["id"]))
    Assert.Equal("Firefox", strAt (n["appName"]))
    Assert.Equal("Hi", strAt (n["summary"]))
    Assert.Equal(1000L, i64At (n["expiresAt"]))
    let acts = n["actions"].AsArray()
    Assert.Equal(2, acts.Count)
    Assert.Equal("default", strAt (acts[0]["key"]))
    Assert.Equal("Open", strAt (acts[0]["label"]))

[<Fact>]
let ``never-expiring notification renders null expiresAt`` () =
    let store, _ =
        NotificationStore.add 0L 5000 0u "app" "icon" "s" "b" [] None 0
            NotificationStore.empty
    let j = DesktopJson.render { DesktopState.empty with Notifications = store }
    Assert.True(isNull ((j["notifications"]).[0].["expiresAt"]))

[<Fact>]
let ``battery, network and players render their fields`` () =
    let state =
        { DesktopState.empty with
            Battery = Some { Present = true; Percentage = 87.5; State = "discharging" }
            Network = Some { State = "connected-global"; Connectivity = "full"; Primary = Some "Wired" }
            Players =
                [ { Bus = "org.mpris.MediaPlayer2.spotify"
                    Identity = "Spotify"
                    Status = "Playing"
                    Title = "Song"
                    Artist = "Band"
                    CanControl = true } ] }
    let j = DesktopJson.render state
    Assert.True(boolAt (j["battery"]["present"]))
    Assert.Equal(87.5, f64At (j["battery"]["percent"]))
    Assert.Equal("discharging", strAt (j["battery"]["state"]))
    Assert.Equal("connected-global", strAt (j["network"]["state"]))
    Assert.Equal("Wired", strAt (j["network"]["primary"]))
    let p = j["players"].AsArray()[0]
    Assert.Equal("Spotify", strAt (p["identity"]))
    Assert.Equal("Playing", strAt (p["status"]))
    Assert.True(boolAt (p["canControl"]))

[<Fact>]
let ``network with no primary renders null primary`` () =
    let state =
        { DesktopState.empty with
            Network = Some { State = "disconnected"; Connectivity = "none"; Primary = None } }
    let j = DesktopJson.render state
    Assert.True(isNull (j["network"]["primary"]))

[<Fact>]
let ``notification renders its body field`` () =
    let store, _ =
        NotificationStore.add 0L 5000 0u "app" "icon" "summary-text" "the-body-text" [] None 0
            NotificationStore.empty
    let j = DesktopJson.render { DesktopState.empty with Notifications = store }
    Assert.Equal("the-body-text", strAt (j["notifications"].[0].["body"]))

[<Fact>]
let ``notification with no actions renders an empty array, not null`` () =
    let store, _ =
        NotificationStore.add 0L 5000 0u "app" "icon" "s" "b" [] None 0 NotificationStore.empty
    let j = DesktopJson.render { DesktopState.empty with Notifications = store }
    let acts = j["notifications"].[0].["actions"]
    Assert.False(isNull acts)
    Assert.Equal(0, acts.AsArray().Count)

[<Fact>]
let ``multiple notifications render newest-first (Active order)`` () =
    let s1, id1 = NotificationStore.add 0L 5000 0u "app" "icon" "first" "b" [] None 0 NotificationStore.empty
    let s2, id2 = NotificationStore.add 0L 5000 0u "app" "icon" "second" "b" [] None 0 s1
    let j = DesktopJson.render { DesktopState.empty with Notifications = s2 }
    let arr = j["notifications"].AsArray()
    Assert.Equal(2, arr.Count)
    // Newest (id2/"second") first, matching NotificationStore.Active order.
    Assert.Equal(id2, u32At (arr.[0].["id"]))
    Assert.Equal("second", strAt (arr.[0].["summary"]))
    Assert.Equal(id1, u32At (arr.[1].["id"]))

[<Fact>]
let ``multiple players render in list order with all fields`` () =
    let mkP bus identity = { Bus = bus; Identity = identity; Status = "Playing"; Title = "T"; Artist = "A"; CanControl = true }
    let state = { DesktopState.empty with Players = [ mkP "bus.one" "One"; mkP "bus.two" "Two" ] }
    let arr = (DesktopJson.render state).["players"].AsArray()
    Assert.Equal(2, arr.Count)
    Assert.Equal("bus.one", strAt (arr.[0].["bus"]))
    Assert.Equal("One", strAt (arr.[0].["identity"]))
    Assert.Equal("T", strAt (arr.[0].["title"]))
    Assert.Equal("A", strAt (arr.[0].["artist"]))
    Assert.Equal("bus.two", strAt (arr.[1].["bus"]))

[<Fact>]
let ``empty state serializes to valid re-parseable JSON with null battery and network`` () =
    let json = (DesktopJson.render DesktopState.empty).ToJsonString()
    let reparsed = JsonNode.Parse json
    Assert.True(isNull reparsed.["battery"])
    Assert.True(isNull reparsed.["network"])
    Assert.Equal(0, reparsed.["notifications"].AsArray().Count)
    Assert.Equal(0, reparsed.["players"].AsArray().Count)

[<Fact>]
let ``fully-populated state serializes to valid re-parseable JSON`` () =
    let store, _ =
        NotificationStore.add 0L 5000 0u "Firefox" "icon" "Hi" "body" [ ("k", "L") ] (Some 2uy) 1000
            NotificationStore.empty
    let state =
        { Notifications = store
          Battery = Some { Present = true; Percentage = 87.5; State = "discharging" }
          Power = { PreparingForSleep = true; SessionLocked = false }
          Network = Some { State = "connected-global"; Connectivity = "full"; Primary = Some "Wired" }
          Players = [ { Bus = "b"; Identity = "I"; Status = "Playing"; Title = "T"; Artist = "A"; CanControl = true } ] }
    let json = (DesktopJson.render state).ToJsonString()
    let reparsed = JsonNode.Parse json
    Assert.Equal(87.5, f64At (reparsed.["battery"].["percent"]))
    Assert.Equal("Hi", strAt (reparsed.["notifications"].[0].["summary"]))

[<Fact>]
let ``non-finite battery percent serializes to 0 instead of crashing`` () =
    // Regression: JsonValue.Create(NaN) makes ToJsonString throw by default.
    for bad in [ nan; infinity; -infinity ] do
        let state = { DesktopState.empty with Battery = Some { Present = true; Percentage = bad; State = "unknown" } }
        let json = (DesktopJson.render state).ToJsonString() // must not throw
        let reparsed = JsonNode.Parse json
        Assert.Equal(0.0, f64At (reparsed.["battery"].["percent"]))
