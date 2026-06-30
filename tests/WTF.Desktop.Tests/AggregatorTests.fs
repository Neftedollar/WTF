module WTF.Desktop.Tests.AggregatorTests

open System.Threading
open Xunit
open WTF.Desktop
open WTF.Desktop.Models

// A cheap, deterministic mutation: push a never-expiring notification.
let private push (summary: string) (s: DesktopState) =
    let store, _ = NotificationStore.add 0L 5000 0u "app" "icon" summary "body" [] None 0 s.Notifications
    { s with Notifications = store }

[<Fact>]
let ``Snapshot of a fresh aggregator is DesktopState.empty`` () =
    let agg = Aggregator()
    Assert.Equal(DesktopState.empty, agg.Snapshot())

[<Fact>]
let ``Update applies the transform and Snapshot reflects it`` () =
    let agg = Aggregator()
    agg.Update(fun s -> { s with Power = { s.Power with SessionLocked = true } })
    Assert.True(agg.Snapshot().Power.SessionLocked)

[<Fact>]
let ``Update is cumulative across calls`` () =
    let agg = Aggregator()
    agg.Update(push "a")
    agg.Update(push "b")
    Assert.Equal(2, agg.Snapshot().Notifications.Active.Length)

[<Fact>]
let ``Mutate applies the new state AND returns the computed value atomically`` () =
    let agg = Aggregator()
    // Add through Mutate, surfacing the allocated id, exactly like the daemon does.
    let id =
        agg.Mutate(fun s ->
            let store, id = NotificationStore.add 0L 5000 0u "app" "icon" "s" "b" [] None 0 s.Notifications
            { s with Notifications = store }, id)
    Assert.Equal(1u, id)
    // The mutation is visible in the snapshot, tied to the same returned id.
    let active = agg.Snapshot().Notifications.Active
    Assert.Equal(1, active.Length)
    Assert.Equal(id, active.Head.Id)

[<Fact>]
let ``Snapshot returns the immutable state value, unaffected by later mutation`` () =
    let agg = Aggregator()
    agg.Update(push "first")
    let before = agg.Snapshot()
    agg.Update(push "second")
    // The previously captured snapshot is an immutable value: still one item.
    Assert.Equal(1, before.Notifications.Active.Length)
    Assert.Equal(2, agg.Snapshot().Notifications.Active.Length)

// --- Thread-safety contract: the documented "every callback mutates under the
//     lock" invariant. N real threads (not pool tasks, to avoid pool-ramp delay),
//     released together by a gate, each append once -> exactly N, no lost updates. ---

[<Fact>]
let ``concurrent Update appends suffer no lost updates`` () =
    let agg = Aggregator()
    let n = 100
    use gate = new ManualResetEventSlim(false)
    let threads =
        [ for i in 1 .. n ->
            let t = Thread(fun () ->
                gate.Wait()
                agg.Update(push (string i)))
            t.Start()
            t ]
    gate.Set()
    threads |> List.iter (fun t -> t.Join())
    Assert.Equal(n, agg.Snapshot().Notifications.Active.Length)

[<Fact>]
let ``concurrent Mutate allocates n distinct ids with no collisions`` () =
    let agg = Aggregator()
    let n = 100
    use gate = new ManualResetEventSlim(false)
    let ids = Array.zeroCreate<uint32> n
    let threads =
        [ for i in 0 .. n - 1 ->
            let t = Thread(fun () ->
                gate.Wait()
                ids.[i] <-
                    agg.Mutate(fun s ->
                        let store, id =
                            NotificationStore.add 0L 5000 0u "app" "icon" "s" "b" [] None 0 s.Notifications
                        { s with Notifications = store }, id))
            t.Start()
            t ]
    gate.Set()
    threads |> List.iter (fun t -> t.Join())
    Assert.Equal(n, Array.distinct ids |> Array.length)
    Assert.Equal(n, agg.Snapshot().Notifications.Active.Length)
