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
