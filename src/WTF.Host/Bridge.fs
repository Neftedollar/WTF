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

    /// Any thread: enqueue a request, wake the loop, block for its reply.
    member _.Submit(notify: unit -> unit, line: string) : string =
        let reply = TaskCompletionSource<string>()
        pending.Enqueue(line, reply)
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
