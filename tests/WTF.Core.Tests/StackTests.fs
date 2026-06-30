module WTF.Core.Tests.StackTests

open Xunit
open FsCheck.Xunit
open WTF.Core

// index of the focused element in visual order (= number of elements above it)
let private focusIndex (s: Stack<'a>) = List.length s.Up
let private removeAt k xs =
    xs |> List.mapi (fun i v -> i, v) |> List.filter (fun (i, _) -> i <> k) |> List.map snd

// The zipper's defining invariant: moving the cursor never reorders windows.

[<Property>]
let ``focusDown preserves visual order`` (s: Stack<int>) =
    Stack.toList (Stack.focusDown s) = Stack.toList s

[<Property>]
let ``focusUp preserves visual order`` (s: Stack<int>) =
    Stack.toList (Stack.focusUp s) = Stack.toList s

[<Property>]
let ``focusUp undoes focusDown`` (s: Stack<int>) =
    Stack.focusUp (Stack.focusDown s) = s

[<Property>]
let ``insertUp focuses the new element and grows by one`` (s: Stack<int>) (x: int) =
    let s' = Stack.insertUp x s
    s'.Focus = x && Stack.length s' = Stack.length s + 1

[<Property>]
let ``ofList / toList round-trips`` (xs: int list) =
    match Stack.ofList xs with
    | None -> xs = []
    | Some s -> Stack.toList s = xs

[<Property>]
let ``swapDown preserves the set of windows`` (s: Stack<int>) =
    Set.ofList (Stack.toList (Stack.swapDown s)) = Set.ofList (Stack.toList s)

[<Property>]
let ``delete shrinks by one or empties`` (s: Stack<int>) =
    match Stack.delete s with
    | Some s' -> Stack.length s' = Stack.length s - 1
    | None -> Stack.length s = 1

// =====================================================================
//  focus — the serialization inverse (rebuild a zipper from visual order
//  + focused value), absence, and the documented duplicate behaviour.
// =====================================================================

[<Property>]
let ``focus restores a serialized zipper for distinct elements`` (s: Stack<int>) =
    let xs = Stack.toList s
    // the inverse only round-trips unambiguously when values are unique
    List.distinct xs <> xs
    || (Stack.ofList xs |> Option.get |> Stack.focus s.Focus = s)

[<Property>]
let ``focus on an absent value is a no-op`` (s: Stack<int>) =
    let xs = Stack.toList s
    let absent = Seq.initInfinite id |> Seq.find (fun c -> not (List.contains c xs))
    Stack.focus absent s = s

[<Fact>]
let ``focus lands on the first occurrence under duplicates`` () =
    // visual order [7; 7; 9]; focusing 7 must select the topmost 7
    let s = { Focus = 7; Up = []; Down = [ 7; 9 ] }
    let f = Stack.focus 7 s
    Assert.Equal<int list>([], f.Up)
    Assert.Equal(7, f.Focus)
    Assert.Equal<int list>([ 7; 9 ], f.Down)

// =====================================================================
//  ofList / insertUp — exact focus position in visual order.
// =====================================================================

[<Property>]
let ``ofList focuses the first (top) element`` (xs: int list) =
    match Stack.ofList xs with
    | None -> xs = []
    | Some s -> s.Focus = List.head xs && s.Up = []

[<Property>]
let ``insertUp inserts immediately above the old focus`` (s: Stack<int>) (x: int) =
    let k = focusIndex s
    let s' = Stack.insertUp x s
    let lst = Stack.toList s'
    s'.Focus = x                       // new element is focused
    && lst.[k] = x                     // ...at the old focus position
    && removeAt k lst = Stack.toList s // ...pushing everything else down unchanged

// =====================================================================
//  swapDown / swapUp — exact one-step reorder, inverse, and boundaries.
// =====================================================================

[<Property>]
let ``swapDown swaps the focus with the element below it`` (s: Stack<int>) =
    let k = focusIndex s
    let before = Stack.toList s
    let s' = Stack.swapDown s
    match s.Down with
    | [] -> s' = s                     // bottom boundary: no-op
    | _ ->
        let expected =
            before |> List.mapi (fun i v ->
                if i = k then before.[k + 1] elif i = k + 1 then before.[k] else v)
        Stack.toList s' = expected && s'.Focus = s.Focus

[<Property>]
let ``swapUp undoes swapDown when a Down element exists`` (s: Stack<int>) =
    match s.Down with
    | [] -> true
    | _ -> Stack.swapUp (Stack.swapDown s) = s

[<Property>]
let ``swap at the boundary is a no-op`` (s: Stack<int>) =
    let downOk = match s.Down with [] -> Stack.swapDown s = s | _ -> true
    let upOk = match s.Up with [] -> Stack.swapUp s = s | _ -> true
    downOk && upOk

// =====================================================================
//  delete — which element inherits focus, and exactly which value is lost.
// =====================================================================

[<Property>]
let ``delete focuses below, else above, else empties`` (s: Stack<int>) =
    match Stack.delete s, s.Down, s.Up with
    | Some s', y :: _, _ -> s'.Focus = y          // element below inherits focus
    | Some s', [], y :: _ -> s'.Focus = y         // else the element above
    | None, [], [] -> true                        // last element -> None
    | _ -> false

[<Property>]
let ``delete loses exactly the focused position`` (s: Stack<int>) =
    let k = focusIndex s
    let before = Stack.toList s
    match Stack.delete s with
    | None -> List.length before = 1
    | Some s' -> Stack.toList s' = removeAt k before
