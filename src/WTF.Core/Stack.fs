namespace WTF.Core

/// A "list with a hole punched in it" — the zipper at the heart of xMonad.
/// The focused element *is* the structural cursor, so focus tracking is free
/// and correct by construction (it can never desync from the window list).
///
/// `Up` is stored reversed: its head is the element immediately ABOVE the focus.
/// Visual order (top -> bottom) is therefore  `rev Up @ (Focus :: Down)`.
type Stack<'a> =
    { Focus: 'a
      Up: 'a list
      Down: 'a list }

module Stack =

    let singleton x = { Focus = x; Up = []; Down = [] }

    /// Flatten to visual order, top to bottom.
    let toList s = List.rev s.Up @ (s.Focus :: s.Down)

    /// Build from a visual-order list; focus lands on the first (top) element.
    let ofList =
        function
        | [] -> None
        | x :: xs -> Some { Focus = x; Up = []; Down = xs }

    let length s = List.length s.Up + 1 + List.length s.Down

    // --- focus movement (cursor moves, order is preserved) ---

    let private focusTopOf s =
        match toList s with
        | [] -> s
        | t :: rest -> { Focus = t; Up = []; Down = rest }

    let private focusBottomOf s =
        match List.rev (toList s) with
        | [] -> s
        | b :: rest -> { Focus = b; Up = rest; Down = [] }

    /// Move focus down one; wraps from bottom back to top.
    let focusDown s =
        match s.Down with
        | y :: ys -> { Focus = y; Up = s.Focus :: s.Up; Down = ys }
        | [] -> focusTopOf s

    /// Move focus up one; wraps from top back to bottom.
    let focusUp s =
        match s.Up with
        | y :: ys -> { Focus = y; Up = ys; Down = s.Focus :: s.Down }
        | [] -> focusBottomOf s

    // --- structural edits ---

    /// Re-point the focus onto a given value (by equality); no-op if absent.
    /// The Up/Down split is fully recoverable from (visual order, focus value),
    /// so this is the inverse used to rebuild a zipper from serialized state.
    let focus x s =
        let xs = toList s
        if List.contains x xs then
            let up = xs |> List.takeWhile ((<>) x) |> List.rev
            match xs |> List.skipWhile ((<>) x) with
            | f :: down -> { Focus = f; Up = up; Down = down }
            | [] -> s
        else s

    /// Insert above the focus and focus the new element (xMonad's insertUp).
    let insertUp x s = { Focus = x; Up = s.Up; Down = s.Focus :: s.Down }

    /// Remove the focused element; focus shifts down, else up, else empties.
    let delete s =
        match s.Down with
        | y :: ys -> Some { s with Focus = y; Down = ys }
        | [] ->
            match s.Up with
            | y :: ys -> Some { Focus = y; Up = ys; Down = [] }
            | [] -> None

    /// Swap the focused element with the one below it (keeps focus on it).
    let swapDown s =
        match s.Down with
        | y :: ys -> { s with Up = y :: s.Up; Down = ys }
        | [] -> s

    /// Swap the focused element with the one above it (keeps focus on it).
    let swapUp s =
        match s.Up with
        | y :: ys -> { s with Up = ys; Down = y :: s.Down }
        | [] -> s

    /// Swap the focused element with an ARBITRARY member `target` (by value),
    /// keeping focus on the (now relocated) focused element. No-op if `target`
    /// is the focus itself or not present. Generalises swapDown/swapUp to any
    /// slot — the primitive behind pick-a-window swapping (keyboard swap mode /
    /// mouse drag-to-rearrange).
    let swapWith target s =
        if target = s.Focus || not (List.contains target (toList s)) then s
        else
            toList s
            |> List.map (fun x ->
                if x = s.Focus then target
                elif x = target then s.Focus
                else x)
            |> ofList
            |> Option.map (focus s.Focus)
            |> Option.defaultValue s
