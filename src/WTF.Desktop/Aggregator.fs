namespace WTF.Desktop

open WTF.Desktop.Models

/// Thread-safe holder of the live `DesktopState`. EVERY D-Bus callback runs on a
/// Tmds I/O (background) thread and mutates the state through here under a lock;
/// the single-threaded WM loop reads via `Snapshot` from `handleOnLoop`. Nothing
/// here ever touches wlroots or the `World` — it only reads/stores desktop-shell
/// state, so no LoopBridge is needed (see the threading note in Desktop.fs).
type Aggregator() =
    let gate = obj ()
    let mutable state = DesktopState.empty

    /// Read the current immutable state (safe from any thread).
    member _.Snapshot() : DesktopState = lock gate (fun () -> state)

    /// Apply a pure transform to the state under the lock.
    member _.Update(f: DesktopState -> DesktopState) : unit =
        lock gate (fun () -> state <- f state)

    /// Apply a transform that also yields a value computed under the SAME lock
    /// (notification store ops must report an allocated id / removed list / "was
    /// present" atomically with the mutation).
    member _.Mutate(f: DesktopState -> DesktopState * 'a) : 'a =
        lock gate (fun () ->
            let s', r = f state
            state <- s'
            r)
