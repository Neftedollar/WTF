namespace WTF.Client

// The PURE state model of the launcher/omnibox: query text, the ranked entry
// list, and the selection index — plus the transitions a keypress makes. No
// Wayland, no rendering, no IO (entries are passed IN), so every transition is
// unit-testable. Shared by the standalone `wtf-omnibox` exe AND the in-process
// overlay the compositor host drives; both feed keys in and render the result.

open WTF.Client.DesktopEntry

module OmniboxModel =

    type Model =
        { Query: string
          All: Entry list          // the full app set (immutable for the session)
          Ranked: Entry list        // `All` fuzzy-ranked against `Query`
          Selected: int }           // index into `Ranked` (0 when empty)

    /// A fresh model over `entries` (empty query ranks everything).
    let init (entries: Entry list) : Model =
        { Query = ""
          All = entries
          Ranked = Fuzzy.rank "" entries
          Selected = 0 }

    /// Re-rank against the current query and clamp the selection into range.
    let private reRank (m: Model) : Model =
        let ranked = Fuzzy.rank m.Query m.All
        { m with
            Ranked = ranked
            Selected = if ranked.IsEmpty then 0 else min m.Selected (ranked.Length - 1) }

    /// Append a typed character (a full string so a surrogate pair stays intact).
    let typeText (s: string) (m: Model) : Model = reRank { m with Query = m.Query + s }

    /// Delete the last character (no-op on an empty query).
    let backspace (m: Model) : Model =
        if m.Query.Length > 0 then reRank { m with Query = m.Query.Substring(0, m.Query.Length - 1) }
        else m

    /// Move the selection up/down, clamped to the ranked list.
    let up (m: Model) : Model = { m with Selected = max 0 (m.Selected - 1) }
    let down (m: Model) : Model =
        { m with Selected = min (max 0 (m.Ranked.Length - 1)) (m.Selected + 1) }

    /// The currently selected entry, if any (None when the list is empty).
    let selected (m: Model) : Entry option =
        if m.Selected >= 0 && m.Selected < m.Ranked.Length then Some m.Ranked.[m.Selected]
        else None
