module WTF.Host.SessionIO

open System
open System.IO
open WTF.Core

/// $XDG_CONFIG_HOME/wtf/session.json, falling back to ~/.config/wtf/session.json.
let sessionPath () : string =
    let cfgHome =
        match Environment.GetEnvironmentVariable "XDG_CONFIG_HOME" with
        | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config")
        | x -> x
    Path.Combine(cfgHome, "wtf", "session.json")

/// Persist the canonical World. mkdir -p, write to a .tmp, then atomically
/// move it over the target so a crash mid-write can't truncate the session.
let save (w: World) : unit =
    try
        let path = sessionPath ()
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let tmp = path + ".tmp"
        File.WriteAllText(tmp, Session.toJson w)
        File.Move(tmp, path, true)
    with _ -> ()

/// Read and decode the saved session, or None if absent/unreadable/incompatible.
let load () : World option =
    try
        let path = sessionPath ()
        if File.Exists path then Session.ofJson (File.ReadAllText path) else None
    with _ -> None
