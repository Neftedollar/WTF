module WTF.Core.Tests.BridgeTests

// LoopBridge is pure of wlroots (ConcurrentQueue + TaskCompletionSource + caller
// callbacks) yet had zero tests. The load-bearing invariant is that every Drain
// path ALWAYS completes the reply TCS — including the exception branch — because
// Submit/Call block forever on reply.Task.Result otherwise. We test the queue
// mechanics, the notify contract, per-item resilience, and (regression) that the
// error reply is well-formed JSON even for messages with backslashes/newlines.

open System.Text.Json
open System.Threading.Tasks
open Xunit
open WTF.Host.Bridge

// In production `notify` wakes the loop thread which later calls Drain. In a unit
// test we make `notify` drain synchronously: by the time Submit/Call reads
// reply.Task.Result the TCS is already completed, so there is no thread to race.

[<Fact>]
let ``Submit enqueues and Drain completes the reply with the handler result`` () =
    let bridge = LoopBridge()
    let notify () = bridge.Drain(fun line -> line + "!")
    Assert.Equal("hi!", bridge.Submit(notify, "hi"))

[<Fact>]
let ``Submit invokes notify exactly once`` () =
    let bridge = LoopBridge()
    let mutable n = 0
    let notify () = n <- n + 1; bridge.Drain(id)
    bridge.Submit(notify, "x") |> ignore
    Assert.Equal(1, n)

[<Fact>]
let ``Post invokes notify exactly once and defers the action until DrainActions`` () =
    let bridge = LoopBridge()
    let mutable n = 0
    let mutable ran = false
    bridge.Post((fun () -> n <- n + 1), (fun () -> ran <- true))
    Assert.Equal(1, n)
    Assert.False(ran)            // deferred — no drain happened yet
    bridge.DrainActions()
    Assert.True(ran)

[<Fact>]
let ``Call invokes notify exactly once and returns the closure result`` () =
    let bridge = LoopBridge()
    let mutable n = 0
    let notify () = n <- n + 1; bridge.DrainCalls()
    Assert.Equal("ok", bridge.Call(notify, (fun () -> "ok")))
    Assert.Equal(1, n)

[<Fact>]
let ``Drain on an empty queue is a no-op`` () =
    let bridge = LoopBridge()
    let mutable called = false
    bridge.Drain(fun _ -> called <- true; "x")
    Assert.False(called)
    // and the other two drains are likewise harmless on empty queues
    bridge.DrainActions()
    bridge.DrainCalls()

[<Fact>]
let ``DrainActions runs queued actions in FIFO order`` () =
    let bridge = LoopBridge()
    let order = System.Collections.Generic.List<int>()
    bridge.Post(ignore, (fun () -> order.Add 1))
    bridge.Post(ignore, (fun () -> order.Add 2))
    bridge.Post(ignore, (fun () -> order.Add 3))
    bridge.DrainActions()
    Assert.Equal<int list>([ 1; 2; 3 ], List.ofSeq order)

[<Fact>]
let ``DrainActions is resilient - a throwing action does not skip later actions`` () =
    let bridge = LoopBridge()
    let ran = System.Collections.Generic.List<int>()
    bridge.Post(ignore, (fun () -> ran.Add 1))
    bridge.Post(ignore, (fun () -> failwith "boom"))
    bridge.Post(ignore, (fun () -> ran.Add 2))
    bridge.DrainActions()
    Assert.Equal<int list>([ 1; 2 ], List.ofSeq ran)   // both good actions ran

// ---------------------------------------------------------------------------
//  Anti-deadlock invariant: a throwing handler MUST still complete the TCS so a
//  blocked Submit/Call returns (with an error payload) instead of hanging.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a throwing handler still completes Submit with an error payload`` () =
    let bridge = LoopBridge()
    let notify () = bridge.Drain(fun _ -> failwith "kaboom")
    let reply = bridge.Submit(notify, "x")
    use doc = JsonDocument.Parse reply        // must be well-formed JSON
    Assert.Equal("kaboom", doc.RootElement.GetProperty("error").GetString())

[<Fact>]
let ``a throwing closure still completes Call with an error payload`` () =
    let bridge = LoopBridge()
    let notify () = bridge.DrainCalls()
    let reply = bridge.Call(notify, (fun () -> failwith "splat"))
    use doc = JsonDocument.Parse reply
    Assert.Equal("splat", doc.RootElement.GetProperty("error").GetString())

// ---------------------------------------------------------------------------
//  Regression for the escaping bug: messages with backslashes / control chars
//  used to produce invalid JSON (only " was escaped). errorJson now delegates to
//  System.Text.Json so the control-socket reply always parses.
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("bad \\ path")>]            // backslash
[<InlineData("line1\nline2")>]          // newline
[<InlineData("tab\there")>]             // tab
[<InlineData("quote\"inside")>]         // double quote
[<InlineData("\\\n\t\"all of it")>]     // everything at once
let ``errorJson produces parseable JSON for nasty messages`` (msg: string) =
    let json = errorJson msg
    use doc = JsonDocument.Parse json     // throws if malformed — that was the bug
    Assert.Equal(msg, doc.RootElement.GetProperty("error").GetString())

[<Fact>]
let ``a handler throwing a backslash message yields parseable JSON over Submit`` () =
    let bridge = LoopBridge()
    let notify () = bridge.Drain(fun _ -> failwith @"C:\nope\bad")
    let reply = bridge.Submit(notify, "x")
    use doc = JsonDocument.Parse reply
    Assert.Equal(@"C:\nope\bad", doc.RootElement.GetProperty("error").GetString())

// ---------------------------------------------------------------------------
//  Liveness under concurrency: every Submit must get its OWN handler result back
//  no matter how the enqueue/drain interleave (proves no TCS is ever orphaned).
// ---------------------------------------------------------------------------

[<Fact>]
let ``concurrent Submits each receive their own correct reply`` () =
    let bridge = LoopBridge()
    // each Submit drains synchronously on its own thread; the last drain after the
    // last enqueue guarantees the queue is fully drained.
    let notify () = bridge.Drain(fun line -> line + "-done")
    let results =
        [| 0 .. 49 |]
        |> Array.map (fun i -> Task.Run(fun () -> bridge.Submit(notify, string i)))
    Task.WaitAll(results |> Array.map (fun t -> t :> Task), 5000) |> ignore
    for i in 0 .. 49 do
        Assert.Equal(sprintf "%d-done" i, results.[i].Result)
