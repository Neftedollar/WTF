namespace WTF.Desktop

open System.Text.Json.Nodes
open WTF.Desktop.Models

/// PURE rendering of a live `DesktopState` into a `JsonObject` (BCL
/// System.Text.Json only, no Tmds.DBus). The Host splices this under the
/// snapshot's "desktop" key via `Protocol.snapshotLineWith`, so an agent or a
/// future bar can read the shell state. Unit-testable without a bus.
module DesktopJson =

    let private optStr (v: string option) : JsonNode =
        match v with
        | Some s -> JsonValue.Create s
        | None -> null

    let private optI64 (v: int64 option) : JsonNode =
        match v with
        | Some n -> JsonValue.Create n
        | None -> null

    /// Render `{ notifications, battery, power, network, players }`.
    let render (s: DesktopState) : JsonObject =
        let root = JsonObject()

        let notifs = JsonArray()
        for n in s.Notifications.Active do
            let o = JsonObject()
            o["id"] <- JsonValue.Create n.Id
            o["appName"] <- JsonValue.Create n.AppName
            o["summary"] <- JsonValue.Create n.Summary
            o["body"] <- JsonValue.Create n.Body
            let acts = JsonArray()
            for (key, label) in n.Actions do
                let a = JsonObject()
                a["key"] <- JsonValue.Create key
                a["label"] <- JsonValue.Create label
                acts.Add a
            o["actions"] <- acts
            o["expiresAt"] <- optI64 n.ExpiresAtMs
            notifs.Add o
        root["notifications"] <- notifs

        root["battery"] <-
            match s.Battery with
            | Some b ->
                let o = JsonObject()
                o["present"] <- JsonValue.Create b.Present
                // System.Text.Json rejects non-finite floats by default, which would
                // crash the Host's snapshot serialization. The contract is 0..100, so
                // sanitize NaN/Infinity to 0.0 rather than emit an unserializable node.
                o["percent"] <- JsonValue.Create(if System.Double.IsFinite b.Percentage then b.Percentage else 0.0)
                o["state"] <- JsonValue.Create b.State
                o :> JsonNode
            | None -> null

        let power = JsonObject()
        power["preparingForSleep"] <- JsonValue.Create s.Power.PreparingForSleep
        power["sessionLocked"] <- JsonValue.Create s.Power.SessionLocked
        root["power"] <- power

        root["network"] <-
            match s.Network with
            | Some n ->
                let o = JsonObject()
                o["state"] <- JsonValue.Create n.State
                o["connectivity"] <- JsonValue.Create n.Connectivity
                o["primary"] <- optStr n.Primary
                o :> JsonNode
            | None -> null

        let players = JsonArray()
        for p in s.Players do
            let o = JsonObject()
            o["bus"] <- JsonValue.Create p.Bus
            o["identity"] <- JsonValue.Create p.Identity
            o["status"] <- JsonValue.Create p.Status
            o["title"] <- JsonValue.Create p.Title
            o["artist"] <- JsonValue.Create p.Artist
            o["canControl"] <- JsonValue.Create p.CanControl
            players.Add o
        root["players"] <- players

        root
