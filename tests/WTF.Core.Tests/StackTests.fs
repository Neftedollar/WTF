module WTF.Core.Tests.StackTests

open FsCheck.Xunit
open WTF.Core

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
