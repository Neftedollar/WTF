namespace WTF.Core

/// Pure, immutable undo/redo history — a zipper over snapshots. It lives in the
/// brain as plain data so it is unit-testable, but the *cell* that holds it is a
/// mutable in the host: Core owns the logic, Host owns the state, exactly the
/// same division of labour as `World` itself. The reducer never sees this type,
/// so the brain stays pure and total.
type History<'a> =
    { Past: 'a list      // most-recent first (head = the state before Present)
      Present: 'a
      Future: 'a list    // next-to-redo first
      Limit: int }       // cap on Past length; the oldest entries fall off

module History =

    let create limit present : History<'a> =
        { Past = []; Present = present; Future = []; Limit = max 0 limit }

    /// Cap a most-recent-first list at n, dropping the oldest (the tail).
    let private cap n xs = if List.length xs > n then List.truncate n xs else xs

    /// Record a new present: the old present moves onto Past, Future is cleared
    /// (a fresh edit invalidates any redo branch), and Past is bounded by Limit.
    let push present h =
        { h with
            Past = cap h.Limit (h.Present :: h.Past)
            Present = present
            Future = [] }

    let canUndo h = not (List.isEmpty h.Past)
    let canRedo h = not (List.isEmpty h.Future)

    /// Step back: pop Past into Present, push the old Present onto Future.
    /// Returns the new history and the restored value, or None at the start.
    let undo h : (History<'a> * 'a) option =
        match h.Past with
        | p :: rest -> Some({ h with Past = rest; Present = p; Future = h.Present :: h.Future }, p)
        | [] -> None

    /// Step forward: the inverse of undo. None at the end of the timeline.
    let redo h : (History<'a> * 'a) option =
        match h.Future with
        | f :: rest -> Some({ h with Past = h.Present :: h.Past; Present = f; Future = rest }, f)
        | [] -> None
