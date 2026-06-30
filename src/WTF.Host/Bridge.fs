module WTF.Host.Bridge

open System.Collections.Concurrent
open System.Threading.Tasks

/// Marshal a request from any thread onto the compositor's event-loop thread.
///
/// Threading model: the wlroots event loop is a single-threaded owner. All world
/// mutation and every `wtf_*` call MUST run on that thread. IPC connections,
/// however, arrive on other threads. So a request is enqueued here, the loop is
/// woken via an eventfd (`notify`), and it is actually handled inside `drain`,
/// which the compositor calls back ON the loop thread.
///
/// Why not a `MailboxProcessor`? Its body runs on a thread-pool thread, which is
/// exactly the thread that may NOT touch wlroots. The loop thread is already the
/// single-owner "actor"; we just hand work to it. The agent abstraction would
/// add a second thread we'd then have to marshal *off* of — strictly worse.
type LoopBridge() =
    let pending = ConcurrentQueue<string * TaskCompletionSource<string>>()
    // Fire-and-forget closures (config hot-reload / REPL-produced config+commands)
    // queued from the FSI worker thread to run ON the loop thread. No reply: the
    // submitter already answered the socket, the application is best-effort.
    let actions = ConcurrentQueue<unit -> unit>()
    // Closures-with-reply queued from off-loop threads (the LLM agent's tool
    // dispatch) to run ON the loop thread and return a string. Like `Submit` but
    // the work is an arbitrary closure (a typed Command dispatch + snapshot)
    // instead of a re-parsed socket line.
    let calls = ConcurrentQueue<(unit -> string) * TaskCompletionSource<string>>()

    /// Any thread: enqueue a request, wake the loop, block for its reply.
    member _.Submit(notify: unit -> unit, line: string) : string =
        let reply = TaskCompletionSource<string>()
        pending.Enqueue(line, reply)
        notify ()
        reply.Task.Result

    /// Any thread: enqueue a closure to run on the loop thread, wake the loop, and
    /// return immediately. Used by the FSI worker to apply a hot-swapped config /
    /// dispatch REPL commands without ever touching wlroots off the loop thread.
    member _.Post(notify: unit -> unit, action: unit -> unit) : unit =
        actions.Enqueue action
        notify ()

    /// Any thread: enqueue a closure to run ON the loop thread, wake the loop, and
    /// block for its string reply. Used by the off-loop LLM agent to dispatch a
    /// typed Command (and read back a snapshot) on the only thread allowed to touch
    /// wlroots/World, without round-tripping through the socket line parser.
    member _.Call(notify: unit -> unit, action: unit -> string) : string =
        let reply = TaskCompletionSource<string>()
        calls.Enqueue(action, reply)
        notify ()
        reply.Task.Result

    /// Loop thread only: handle every queued request and complete its reply.
    member _.Drain(handle: string -> string) : unit =
        let mutable item = Unchecked.defaultof<string * TaskCompletionSource<string>>
        while pending.TryDequeue(&item) do
            let line, reply = item
            try
                reply.SetResult(handle line)
            with ex ->
                reply.SetResult(sprintf """{"error":"%s"}""" (ex.Message.Replace("\"", "'")))

    /// Loop thread only: run every queued fire-and-forget action.
    member _.DrainActions() : unit =
        let mutable a = Unchecked.defaultof<unit -> unit>
        while actions.TryDequeue(&a) do
            try a () with ex -> eprintfn "WTF: loop action failed: %s" ex.Message

    /// Loop thread only: run every queued closure-with-reply and complete it.
    member _.DrainCalls() : unit =
        let mutable item = Unchecked.defaultof<(unit -> string) * TaskCompletionSource<string>>
        while calls.TryDequeue(&item) do
            let action, reply = item
            try
                reply.SetResult(action ())
            with ex ->
                reply.SetResult(sprintf """{"error":"%s"}""" (ex.Message.Replace("\"", "'")))
