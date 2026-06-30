module WTF.Desktop.Tests.NotificationStoreTests

open Xunit
open FsCheck
open FsCheck.Xunit
open WTF.Desktop.Models

// Convenience: add with sensible defaults so tests stay readable.
let private addN now replaces summary timeout store =
    NotificationStore.add now 5000 replaces "app" "icon" summary "body" [] None timeout store

[<Fact>]
let ``empty store starts at id 1 with nothing active`` () =
    Assert.Equal(1u, NotificationStore.empty.NextId)
    Assert.Empty(NotificationStore.empty.Active)

[<Fact>]
let ``ids are monotonic across adds`` () =
    let s0 = NotificationStore.empty
    let s1, id1 = addN 0L 0u "a" 0 s0
    let s2, id2 = addN 0L 0u "b" 0 s1
    let _, id3 = addN 0L 0u "c" 0 s2
    Assert.Equal(1u, id1)
    Assert.Equal(2u, id2)
    Assert.Equal(3u, id3)

[<Fact>]
let ``NextId skips 0 on wrap`` () =
    let s = { NotificationStore.empty with NextId = System.UInt32.MaxValue }
    let s', id = addN 0L 0u "x" 0 s
    Assert.Equal(System.UInt32.MaxValue, id)
    Assert.Equal(1u, s'.NextId) // wrapped past 0 -> 1

[<Fact>]
let ``replacesId reuses the same id and overwrites content, keeping position`` () =
    let s0 = NotificationStore.empty
    let s1, id1 = addN 0L 0u "first" 0 s0
    let s2, _ = addN 0L 0u "second" 0 s1
    // Replace the first notification's content.
    let s3, idR = addN 0L id1 "first-edited" 0 s2
    Assert.Equal(id1, idR)
    // Same number of active items (no new allocation).
    Assert.Equal(2, List.length s3.Active)
    Assert.Equal(s2.NextId, s3.NextId) // NextId untouched on replace
    let edited = NotificationStore.tryFind id1 s3
    Assert.Equal(Some "first-edited", edited |> Option.map (fun n -> n.Summary))
    // Position preserved: ids in the same order as before the replace.
    let idsBefore = s2.Active |> List.map (fun n -> n.Id)
    let idsAfter = s3.Active |> List.map (fun n -> n.Id)
    Assert.Equal<uint32 list>(idsBefore, idsAfter)

[<Fact>]
let ``replacesId for an absent id allocates a fresh id`` () =
    let s0 = NotificationStore.empty
    // Nothing has id 99; replace should fall through to allocation.
    let s1, id = addN 0L 99u "x" 0 s0
    Assert.Equal(1u, id)
    Assert.Equal(1, List.length s1.Active)

[<Fact>]
let ``close present returns true and removes; absent returns false and is a no-op`` () =
    let s0 = NotificationStore.empty
    let s1, id = addN 0L 0u "x" 0 s0
    let s2, was = NotificationStore.close id s1
    Assert.True(was)
    Assert.Empty(s2.Active)
    let s3, was2 = NotificationStore.close 12345u s2
    Assert.False(was2)
    Assert.Equal(s2, s3)

[<Fact>]
let ``timeout semantics: -1 uses default, 0 never expires, >0 absolute`` () =
    let s0 = NotificationStore.empty
    // -1 -> default (5000) -> now + 5000
    let s1, id1 = addN 100L 0u "a" -1 s0
    Assert.Equal(Some 5100L, (NotificationStore.tryFind id1 s1).Value.ExpiresAtMs)
    // 0 -> never
    let s2, id2 = addN 100L 0u "b" 0 s1
    Assert.Equal(None, (NotificationStore.tryFind id2 s2).Value.ExpiresAtMs)
    // >0 -> now + t
    let s3, id3 = addN 100L 0u "c" 250 s2
    Assert.Equal(Some 350L, (NotificationStore.tryFind id3 s3).Value.ExpiresAtMs)

[<Fact>]
let ``timeout -1 with zero default never expires`` () =
    let s0 = NotificationStore.empty
    let s1, id = NotificationStore.add 0L 0 0u "app" "icon" "s" "b" [] None -1 s0
    Assert.Equal(None, (NotificationStore.tryFind id s1).Value.ExpiresAtMs)

[<Fact>]
let ``expire drops items at or before now and returns ids earliest-expiry-first`` () =
    let s0 = NotificationStore.empty
    let s1, idLate = addN 0L 0u "late" 300 s0   // expires at 300
    let s2, idEarly = addN 0L 0u "early" 100 s1  // expires at 100
    let s3, idNever = addN 0L 0u "never" 0 s2     // never
    // At now=300 both timed ones are due; earliest (100) reported before (300).
    let s4, removed = NotificationStore.expire 300L s3
    Assert.Equal<uint32 list>([ idEarly; idLate ], removed)
    Assert.Equal(1, List.length s4.Active)
    Assert.Equal(Some idNever, s4.Active |> List.tryHead |> Option.map (fun n -> n.Id))

[<Fact>]
let ``expire before due time removes nothing`` () =
    let s0 = NotificationStore.empty
    let s1, _ = addN 0L 0u "x" 500 s0
    let s2, removed = NotificationStore.expire 100L s1
    Assert.Empty(removed)
    Assert.Equal(1, List.length s2.Active)

[<Theory>]
[<InlineData(-2)>]
[<InlineData(-100)>]
[<InlineData(System.Int32.MinValue)>]
let ``timeout: negatives other than -1 never expire (catch-all arm)`` (timeout: int) =
    let s0 = NotificationStore.empty
    let s1, id = addN 100L 0u "x" timeout s0
    Assert.Equal(None, (NotificationStore.tryFind id s1).Value.ExpiresAtMs)

[<Fact>]
let ``NextId wrap collision: adding atop an active id at NextId produces a duplicate id (documented hole)`` () =
    // Pin the known invariant hole: if NextId lands on an id already held by an
    // active never-expiring notification, add() allocates it again -> two actives
    // share an Id. This is what close/tryFind/replace would then operate on
    // ambiguously. Documents current behavior so a future fix has a witness.
    let s0 = NotificationStore.empty
    let s1, id1 = addN 0L 0u "first" 0 s0 // id 1, never expires
    // Force NextId back onto the live id 1.
    let s2 = { s1 with NextId = id1 }
    let s3, id2 = addN 0L 0u "second" 0 s2
    Assert.Equal(id1, id2) // collision
    let dupes = s3.Active |> List.filter (fun n -> n.Id = id1)
    Assert.Equal(2, List.length dupes) // CURRENT behavior: duplicate active id
    // And the invariant-dependent ops are now ambiguous: close removes BOTH.
    let s4, was = NotificationStore.close id1 s3
    Assert.True(was)
    Assert.Empty(s4.Active)

[<Fact>]
let ``expire on an empty store returns empty and no removed ids`` () =
    let s', removed = NotificationStore.expire 1000L NotificationStore.empty
    Assert.Empty(removed)
    Assert.Empty(s'.Active)

[<Fact>]
let ``expire with equal expiry returns both ids and keeps no survivors`` () =
    let s0 = NotificationStore.empty
    let s1, id1 = addN 0L 0u "a" 100 s0 // expires at 100
    let s2, id2 = addN 0L 0u "b" 100 s1 // also expires at 100
    let s3, removed = NotificationStore.expire 100L s2
    // Both due; stable sort by equal key preserves their (newest-first) order.
    Assert.Equal(2, List.length removed)
    Assert.Equal<Set<uint32>>(Set.ofList [ id1; id2 ], Set.ofList removed)
    Assert.Empty(s3.Active)

[<Fact>]
let ``partial expire keeps survivors in newest-first order`` () =
    let s0 = NotificationStore.empty
    let s1, idOld = addN 0L 0u "old" 0 s0    // never
    let s2, idDue = addN 0L 0u "due" 50 s1   // expires at 50
    let s3, idNew = addN 0L 0u "new" 0 s2    // never
    let s4, removed = NotificationStore.expire 100L s3
    Assert.Equal<uint32 list>([ idDue ], removed)
    // Survivors retain newest-first (idNew before idOld), with the due one gone.
    Assert.Equal<uint32 list>([ idNew; idOld ], s4.Active |> List.map (fun n -> n.Id))

[<Fact>]
let ``close removes ALL matching ids when duplicates exist`` () =
    let s0 = NotificationStore.empty
    let s1, id1 = addN 0L 0u "first" 0 s0
    let s2 = { s1 with NextId = id1 } // force a collision
    let s3, _ = addN 0L 0u "second" 0 s2
    let s4, was = NotificationStore.close id1 s3
    Assert.True(was)
    Assert.Empty(s4.Active) // both copies gone

[<Fact>]
let ``replace resets CreatedMs to the new now and recomputes ExpiresAtMs`` () =
    let s0 = NotificationStore.empty
    let s1, id = addN 100L 0u "orig" 1000 s0 // created 100, expires 1100
    Assert.Equal(100L, (NotificationStore.tryFind id s1).Value.CreatedMs)
    // Replace at a later now with a new timeout.
    let s2, idR = addN 500L id "edited" 250 s1
    Assert.Equal(id, idR)
    let n = (NotificationStore.tryFind id s2).Value
    Assert.Equal(500L, n.CreatedMs)          // CreatedMs reset to new now
    Assert.Equal(Some 750L, n.ExpiresAtMs)   // 500 + 250 recomputed
    Assert.Equal("edited", n.Summary)

// --- Properties ---

[<Property>]
let ``allocated ids are never 0`` (count: byte) =
    let n = int count % 50
    let mutable s = NotificationStore.empty
    let mutable ok = true
    for i in 1..n do
        let s', id = addN (int64 i) 0u "x" 0 s
        s <- s'
        if id = 0u then ok <- false
    ok

[<Property>]
let ``close then tryFind is always None`` (idSeed: uint32) =
    let s0 = NotificationStore.empty
    let s1, id = addN 0L 0u "x" 0 s0
    let s2, _ = NotificationStore.close id s1
    (NotificationStore.tryFind id s2).IsNone

[<Property>]
let ``expire never returns an id that is still active`` (a: int) (b: int) =
    let ta = (abs a % 1000) + 1
    let tb = (abs b % 1000) + 1
    let s0 = NotificationStore.empty
    let s1, _ = addN 0L 0u "a" ta s0
    let s2, _ = addN 0L 0u "b" tb s1
    let s3, removed = NotificationStore.expire 500L s2
    let activeIds = s3.Active |> List.map (fun n -> n.Id) |> Set.ofList
    removed |> List.forall (fun id -> not (activeIds.Contains id))
