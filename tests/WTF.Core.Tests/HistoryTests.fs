module WTF.Core.Tests.HistoryTests

open Xunit
open FsCheck.Xunit
open WTF.Core

let private pushAll start xs =
    (History.create 64 start, xs) ||> List.fold (fun h x -> History.push x h)

[<Fact>]
let ``undo after push returns the prior present`` () =
    let h = pushAll 0 [ 1; 2 ]
    match History.undo h with
    | Some(h', p) ->
        Assert.Equal(1, p)
        Assert.Equal(1, h'.Present)
    | None -> failwith "expected Some"

[<Fact>]
let ``redo inverts undo back to the same present`` () =
    let h = pushAll 0 [ 1; 2; 3 ]
    let h1, _ = History.undo h |> Option.get
    let h2, v = History.redo h1 |> Option.get
    Assert.Equal(3, v)
    Assert.Equal(h.Present, h2.Present)
    Assert.Equal<int list>(h.Past, h2.Past)
    Assert.Equal<int list>(h.Future, h2.Future)

[<Fact>]
let ``push clears the future (redo branch is invalidated)`` () =
    let h = pushAll 0 [ 1; 2 ]
    let h1, _ = History.undo h |> Option.get
    Assert.True(History.canRedo h1)
    let h2 = History.push 9 h1
    Assert.False(History.canRedo h2)
    Assert.Empty(h2.Future)

[<Fact>]
let ``empty history undo and redo are None`` () =
    let h = History.create 64 0
    Assert.False(History.canUndo h)
    Assert.False(History.canRedo h)
    Assert.True((History.undo h).IsNone)
    Assert.True((History.redo h).IsNone)

[<Property>]
let ``past never exceeds the limit`` (k: int) =
    let limit = 8
    let n = abs k % 50
    let h = (History.create limit 0, [ 1..n ]) ||> List.fold (fun acc x -> History.push x acc)
    List.length h.Past <= limit

[<Property>]
let ``redo composed with undo is identity after any pushes`` (xs: int list) =
    let h = pushAll 0 xs
    match History.undo h with
    | Some(h1, _) ->
        match History.redo h1 with
        | Some(h2, _) -> h2.Present = h.Present && h2.Past = h.Past && h2.Future = h.Future
        | None -> false
    | None -> List.isEmpty xs // nothing to undo only when no pushes happened

[<Property>]
let ``canUndo iff a push happened`` (xs: int list) =
    let h = pushAll 0 xs
    History.canUndo h = not (List.isEmpty xs)

// =====================================================================
//  Coverage additions: limit edges (0 / negative), undo-count cap, and
//  the Past bound under arbitrary push/undo/redo interleavings.
// =====================================================================

[<Fact>]
let ``a zero limit never retains any past`` () =
    let h = (History.create 0 0, [ 1; 2; 3 ]) ||> List.fold (fun acc x -> History.push x acc)
    Assert.Empty(h.Past)
    Assert.False(History.canUndo h)
    Assert.True((History.undo h).IsNone)

[<Fact>]
let ``a negative limit is clamped to zero`` () =
    let h = History.create -5 0
    Assert.Equal(0, h.Limit)
    let h1 = History.push 1 h
    Assert.Empty(h1.Past)
    Assert.False(History.canUndo h1)

[<Fact>]
let ``you can undo at most Limit times and dropped states are gone`` () =
    let limit = 3
    // push 1..6 onto a present of 0; only the 3 most-recent priors survive in Past
    let h = (History.create limit 0, [ 1..6 ]) ||> List.fold (fun acc x -> History.push x acc)
    Assert.Equal(limit, List.length h.Past)
    // undo exactly Limit times, then no more
    let mutable cur = h
    for _ in 1..limit do
        match History.undo cur with
        | Some(h', _) -> cur <- h'
        | None -> failwith "expected to undo"
    Assert.True((History.undo cur).IsNone) // 4th undo fails
    // the oldest survivor was the present just before the last 3 pushes (i.e. 3)
    Assert.Equal(3, cur.Present)

[<Property>]
let ``Past stays within Limit across arbitrary push undo redo interleavings`` (ops: bool list) =
    // map a bool stream to a deterministic op sequence: every 3rd kind cycles
    let limit = 4
    let step (h, i) flag =
        let h' =
            match (i % 3), flag with
            | 0, _ -> History.push i h
            | 1, _ -> History.undo h |> Option.map fst |> Option.defaultValue h
            | _, _ -> History.redo h |> Option.map fst |> Option.defaultValue h
        (h', i + 1)
    let h, _ = (List.fold step (History.create limit 0, 1) ops)
    List.length h.Past <= limit

// =====================================================================
//  History.map — folds an out-of-band delta into EVERY snapshot so the
//  undo timeline stays consistent with the live window set (the host
//  uses it on surface map/unmap and monitor resize). See the map doc.
// =====================================================================

[<Fact>]
let ``map applies the transform to past, present and future`` () =
    // present=2, undo once -> present=1, future=[2]; past holds 0.
    let h = pushAll 0 [ 1; 2 ]
    let h1, _ = History.undo h |> Option.get
    Assert.Equal<int list>([ 0 ], h1.Past)
    Assert.Equal(1, h1.Present)
    Assert.Equal<int list>([ 2 ], h1.Future)
    let m = History.map (fun x -> x + 100) h1
    Assert.Equal<int list>([ 100 ], m.Past)
    Assert.Equal(101, m.Present)
    Assert.Equal<int list>([ 102 ], m.Future)
    Assert.Equal(h1.Limit, m.Limit) // structure/limit preserved

[<Property>]
let ``map with identity is a no-op`` (xs: int list) =
    let h = pushAll 0 xs
    let m = History.map id h
    m.Present = h.Present && m.Past = h.Past && m.Future = h.Future

// ---- the regression this whole change exists for: undo must never rewind
// ---- the live WINDOW SET, only the user's layout deltas over it. ----

let private screen = Rect.create 0 0 1920 1080
let private win id = { Id = id; AppId = sprintf "app%d" id; Title = ""; Floating = false }
let private addWin info w = Reducer.apply (AddWindow info) w |> fst
let private removeWin id w = Reducer.apply (RemoveWindow id) w |> fst
let private onWs tag id (w: World) =
    (w.Workspaces |> List.find (fun ws -> ws.Tag = tag)).Stack
    |> Option.map (Stack.toList >> List.contains id)
    |> Option.defaultValue false

/// Model the host's onViewMap fold: graft the surface into EVERY snapshot at the
/// workspace it ACTUALLY mapped on (derived from the live world), then re-anchor
/// Present on the live world. This is exactly what rebaseHistory does in the host.
let private foldMap (info: WindowInfo) (live: World) (h: History<World>) =
    let landed =
        live.Workspaces
        |> List.find (fun ws ->
            match ws.Stack with
            | Some s -> List.contains info.Id (Stack.toList s)
            | None -> false)
    History.map (World.graftWindowAt landed.Tag info (Map.tryFind info.Id landed.Floating)) h
    |> fun h -> { h with Present = live }

[<Fact>]
let ``undo after an out-of-band map keeps the window (no orphan)`` () =
    // Model the host: history is created BEFORE any window maps; a map is not an
    // undo point, so it's folded into every snapshot, with Present re-anchored on
    // the live world. A later undoable command pushes a real step.
    let w0 = World.empty screen
    let h0 = History.create 64 w0
    let w1 = addWin (win 1) w0                          // window 1 maps out-of-band
    let h1 = foldMap (win 1) w1 h0
    let w2 = addWin (win 2) w1                          // window 2 maps out-of-band
    let h2 = foldMap (win 2) w2 h1
    // user issues an undoable command (a swap) -> push a real step
    let w3 = Reducer.apply (Focus(ById 1)) w2 |> fst |> Reducer.apply (SwapWith 2) |> fst
    let h3 = History.push w3 h2
    // undo -> the restored world must still contain BOTH mapped windows
    match History.undo h3 with
    | Some(_, restored) ->
        Assert.True(Map.containsKey 1 restored.Windows, "window 1 orphaned by undo")
        Assert.True(Map.containsKey 2 restored.Windows, "window 2 orphaned by undo")
    | None -> failwith "expected an undo point"

[<Fact>]
let ``undo pins a mapped window to its real workspace, not the snapshot's current`` () =
    // The regression behind the wrong-workspace teleport: a plain AddWindow fold
    // would insert into each PAST snapshot's Current (ws1 here), but the window
    // actually mapped on ws2. graftWindowAt pins it to ws2 in every snapshot.
    let w0 = World.empty screen |> addWin (win 1)       // window 1 on ws1
    // an undoable change is recorded while Current = ws1
    let h0 = History.push (Reducer.apply (SetGaps 4) w0 |> fst) (History.create 64 w0)
    Assert.Equal("1", (List.head h0.Past).Current)      // the past snapshot's Current is ws1
    // live world switches to ws2 (undoable) and window 2 maps there
    let live = h0.Present |> Reducer.apply (SwitchWorkspace "2") |> fst |> addWin (win 2)
    let h1 = foldMap (win 2) live h0
    // undo back across the (folded) timeline -> window 2 must be on ws2, never ws1
    match History.undo h1 with
    | Some(_, restored) ->
        Assert.True(onWs "2" 2 restored, "window 2 should be pinned to ws2")
        Assert.False(onWs "1" 2 restored, "window 2 teleported onto ws1 by undo")
    | None -> failwith "expected an undo point"

[<Fact>]
let ``graftWindowAt preserves a floating window's rect and mirror`` () =
    let r = Rect.create 10 10 100 100
    let g = World.empty screen |> World.graftWindowAt "1" (win 5) (Some r)
    let ws1 = g.Workspaces |> List.find (fun w -> w.Tag = "1")
    Assert.Equal(Some r, Map.tryFind 5 ws1.Floating)          // float rect kept
    Assert.True((Map.find 5 g.Windows).Floating)              // WindowInfo mirror set
    Assert.True(onWs "1" 5 g)                                 // still on the stack

[<Fact>]
let ``graftWindowAt is idempotent — an already-stacked id is not double-inserted`` () =
    let w = World.empty screen |> addWin (win 1)             // window 1 on ws1
    let g = World.graftWindowAt "2" (win 1) None w           // try to graft it onto ws2
    let count =
        g.Workspaces
        |> List.collect (fun ws -> ws.Stack |> Option.map Stack.toList |> Option.defaultValue [])
        |> List.filter ((=) 1)
        |> List.length
    Assert.Equal(1, count)                                    // exactly one instance
    Assert.False(onWs "2" 1 g)                                // not moved to ws2

[<Fact>]
let ``undo after an out-of-band unmap does not resurrect a ghost`` () =
    let w0 = World.empty screen |> addWin (win 1) |> addWin (win 2)
    let h0 = History.create 64 w0
    // an undoable command records a step while BOTH windows are live
    let w1 = Reducer.apply (Focus(ById 1)) w0 |> fst |> Reducer.apply (SwapWith 2) |> fst
    let h1 = History.push w1 h0
    // window 2 closes out-of-band -> purge it from every snapshot
    let w2 = removeWin 2 w1
    let h2 = History.map (removeWin 2) h1 |> fun h -> { h with Present = w2 }
    // undo -> the restored world must NOT contain the dead window
    match History.undo h2 with
    | Some(_, restored) ->
        Assert.False(Map.containsKey 2 restored.Windows, "dead window 2 resurrected by undo")
        Assert.True(Map.containsKey 1 restored.Windows)
    | None -> failwith "expected an undo point"
