namespace WTF.Client

open System
open WTF.Client.DesktopEntry

/// PURE subsequence fuzzy matcher + ranker for the omnibox. Case-insensitive: a
/// query matches a candidate iff its letters appear in order (not necessarily
/// adjacent). The score rewards prefix matches, word-boundary / CamelCase starts,
/// consecutive runs, and early matches, so the "best" candidate sorts first.
module Fuzzy =

    let private isBoundary (prev: char) =
        prev = ' ' || prev = '-' || prev = '_' || prev = '.' || prev = '/'

    /// Score `query` against `candidate`. None when query is not a subsequence.
    /// Higher is better. An empty query scores 0 (every candidate "matches").
    let score (query: string) (candidate: string) : int option =
        if String.IsNullOrEmpty query then
            Some 0
        elif String.IsNullOrEmpty candidate then
            None
        else
            let q = query.ToLowerInvariant()
            let c = candidate.ToLowerInvariant()
            let mutable qi = 0
            let mutable total = 0
            let mutable consecutive = 0
            let mutable matchedFirst = false
            let mutable ci = 0
            while qi < q.Length && ci < c.Length do
                if q.[qi] = c.[ci] then
                    let mutable s = 10 // base per matched char
                    if ci = 0 then
                        s <- s + 15 // matches at the very start
                        if qi = 0 then matchedFirst <- true
                    elif isBoundary c.[ci - 1] then
                        s <- s + 10 // word boundary
                    elif Char.IsUpper candidate.[ci] && not (Char.IsUpper candidate.[ci - 1]) then
                        s <- s + 8 // CamelCase hump
                    // consecutive-run bonus (grows with the run length)
                    s <- s + consecutive * 5
                    // earliness bonus: earlier matches are slightly better
                    s <- s + max 0 (5 - ci / 4)
                    total <- total + s
                    consecutive <- consecutive + 1
                    qi <- qi + 1
                    ci <- ci + 1
                else
                    consecutive <- 0
                    ci <- ci + 1
            if qi = q.Length then
                // whole-string prefix bonus
                if c.StartsWith q then total <- total + 20
                if matchedFirst then total <- total + 5
                Some total
            else
                None

    /// Rank entries by `score` against the query (best first), dropping
    /// non-matches. An EMPTY query returns all entries in Name order (the launcher
    /// shows the full list until the user types). Ties break on Name (stable).
    let rank (query: string) (entries: Entry list) : Entry list =
        if String.IsNullOrWhiteSpace query then
            entries |> List.sortBy (fun e -> e.Name.ToLowerInvariant())
        else
            entries
            |> List.choose (fun e -> score query e.Name |> Option.map (fun s -> s, e))
            |> List.sortBy (fun (s, e) -> (-s, e.Name.ToLowerInvariant()))
            |> List.map snd
