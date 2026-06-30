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
