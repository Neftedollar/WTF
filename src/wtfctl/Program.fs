module wtfctl.Program

// wtfctl — the agent-first control CLI. Talks NDJSON over the WTF unix socket.
// Humans get friendly shortcuts; an LLM can send raw JSON the same way.
//
//   wtfctl state                     pretty-print the world snapshot
//   wtfctl focus next|prev           move focus
//   wtfctl focus app firefox         focus a window by app id
//   wtfctl layout bsp|tall|grid|...  set the current workspace's layout
//   wtfctl workspace 2               switch to workspace 2
//   wtfctl move 2                    send the focused window to workspace 2
//   wtfctl spawn kitty               launch a program
//   wtfctl swap next|prev            move the focused window in the stack
//   wtfctl master 2 | ratio 0.6      tweak the master area
//   wtfctl float                     toggle floating on the focused window
//   wtfctl fullscreen                toggle fullscreen on the focused window
//   wtfctl sinkall                   clear all floating on the current workspace
//   wtfctl close                     close the focused window
//   wtfctl eval "config { gaps 20 }" run F# live (hot-swap config / dispatch cmd)
//   wtfctl tools                     the agent tool manifest (JSON)
//   wtfctl notify "Build done"       send a desktop notification
//   wtfctl ask "tile the browser"    drive WTF in natural language (opt-in LLM)
//   wtfctl '{"cmd":"focus","by":"next"}'   raw JSON passthrough

open System
open System.Text.Json
open System.Text.Json.Nodes
open WTF.Client

let private jsonEsc (s: string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// Translate friendly argv into a JSON command line (or pass raw JSON through).
let toJson (args: string list) : string option =
    match args with
    | [] -> Some "state"
    | first :: _ when first.StartsWith "{" -> Some(String.Join(" ", args)) // raw JSON
    | [ "state" ] | [ "snapshot" ] -> Some "state"
    | [ "focus"; "next" ] -> Some """{"cmd":"focus","by":"next"}"""
    | [ "focus"; "prev" ] -> Some """{"cmd":"focus","by":"prev"}"""
    | [ "focus"; "master" ] -> Some """{"cmd":"focusmaster"}"""
    | [ "focus"; "app"; app ] -> Some(sprintf """{"cmd":"focus","app":"%s"}""" (jsonEsc app))
    | [ "focus"; "id"; id ] -> Some(sprintf """{"cmd":"focus","id":%s}""" id)
    | [ "swap"; "next" ] -> Some """{"cmd":"swap","dir":"next"}"""
    | [ "swap"; "prev" ] -> Some """{"cmd":"swap","dir":"prev"}"""
    | [ "swap"; "master" ] -> Some """{"cmd":"swapmaster"}"""
    | [ "layout"; "next" ] -> Some """{"cmd":"layout","next":true}"""
    | [ "layout"; name ] -> Some(sprintf """{"cmd":"layout","name":"%s"}""" (jsonEsc name))
    | [ "workspace"; "next" ] -> Some """{"cmd":"workspace","next":true}"""
    | [ "workspace"; "prev" ] -> Some """{"cmd":"workspace","prev":true}"""
    | [ "workspace"; tag ] -> Some(sprintf """{"cmd":"workspace","switch":"%s"}""" (jsonEsc tag))
    | [ "move"; tag ] -> Some(sprintf """{"cmd":"workspace","move":"%s"}""" (jsonEsc tag))
    | "spawn" :: rest -> Some(sprintf """{"cmd":"spawn","run":"%s"}""" (jsonEsc (String.Join(" ", rest))))
    | [ "master"; "inc" ] -> Some """{"cmd":"master","inc":true}"""
    | [ "master"; "dec" ] -> Some """{"cmd":"master","dec":true}"""
    | [ "master"; n ] -> Some(sprintf """{"cmd":"master","n":%s}""" n)
    | [ "ratio"; v ] -> Some(sprintf """{"cmd":"ratio","value":%s}""" v)
    | [ "gaps"; "inc" ] -> Some """{"cmd":"gaps","inc":true}"""
    | [ "gaps"; "dec" ] -> Some """{"cmd":"gaps","dec":true}"""
    | [ "gaps"; v ] -> Some(sprintf """{"cmd":"gaps","value":%s}""" v)
    | [ "opacity"; v ] -> Some(sprintf """{"cmd":"opacity","value":%s}""" v)
    | [ "anim"; v ] -> Some(sprintf """{"cmd":"anim","value":%s}""" v)
    | [ "border"; "width"; n ] -> Some(sprintf """{"cmd":"border","width":%s}""" n)
    | [ "border"; "active"; hex ] -> Some(sprintf """{"cmd":"border","active":true,"color":"%s"}""" (jsonEsc hex))
    | [ "border"; "inactive"; hex ] -> Some(sprintf """{"cmd":"border","active":false,"color":"%s"}""" (jsonEsc hex))
    | [ "corners"; n ] -> Some(sprintf """{"cmd":"corners","value":%s}""" n)
    | [ "blur"; "on" ] -> Some """{"cmd":"blur","on":true}"""
    | [ "blur"; "off" ] -> Some """{"cmd":"blur","on":false}"""
    | [ "float" ] -> Some """{"cmd":"float"}"""
    | [ "fullscreen" ] -> Some """{"cmd":"fullscreen"}"""
    | [ "sinkall" ] -> Some """{"cmd":"sinkall"}"""
    | [ "close" ] -> Some """{"cmd":"close"}"""
    // Agent-first surface: discover the curated LLM tool manifest, and notify the
    // user through WTF's own daemon.
    //   wtfctl tools                          the agent tool manifest (JSON)
    //   wtfctl notify "Build done"            send a desktop notification
    //   wtfctl notify "Build done" all green  summary + body
    | [ "tools" ] -> Some """{"tools":true}"""
    //   wtfctl ask "focus the browser and tile it"   drive WTF in natural language
    | "ask" :: rest when not (List.isEmpty rest) ->
        Some(sprintf """{"ask":"%s"}""" (jsonEsc (String.Join(" ", rest))))
    | "notify" :: summary :: rest ->
        Some(sprintf """{"notify":{"summary":"%s","body":"%s"}}""" (jsonEsc summary) (jsonEsc (String.Join(" ", rest))))
    // Live F# REPL into the running WM: a WtfConfig result hot-applies, a Command
    // or Command list dispatches, anything else returns its value/diagnostics.
    //   wtfctl eval "config { gaps 20 }"      hot-swap the whole config
    //   wtfctl eval "SetGaps 20"              dispatch one command
    //   wtfctl eval "[ SetLayout \"bsp\"; IncGaps ]"   dispatch a list
    | [ "eval"; code ] -> Some(sprintf """{"eval":"%s"}""" (jsonEsc code))
    | _ -> None

// The NDJSON socket client now lives in WTF.Client.Socket (shared with the status
// bar). These thin aliases keep the call sites + behavior below byte-identical.
let socketPath () = Socket.socketPath ()
let send (line: string) : string = Socket.send line

let pretty (json: string) =
    try
        (JsonNode.Parse json).ToJsonString(JsonSerializerOptions(WriteIndented = true))
    with _ -> json

[<EntryPoint>]
let main argv =
    match toJson (List.ofArray argv) with
    | None ->
        eprintfn "wtfctl: unrecognized command. Try: state | focus next | layout bsp | workspace 2 | spawn kitty | eval \"config { gaps 20 }\""
        2
    | Some line ->
        try
            printfn "%s" (pretty (send line))
            0
        with ex ->
            eprintfn "wtfctl: cannot reach WTF socket (%s): %s" (socketPath ()) ex.Message
            1
