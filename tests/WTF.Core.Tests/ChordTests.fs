module WTF.Core.Tests.ChordTests

// The "host" review section had ZERO Chord coverage and the test project did not
// even reference WTF.Host. Chord.format is the sole translation from a wlroots
// (modifier-mask, keysym) pair into the chord strings the keymap is keyed by, so
// a one-char drift in any of its tables silently kills real keybindings. These
// tests pin every documented mapping AND round-trip every default bind through
// Keymap.lookup so the convention-only coupling becomes machine-checked.

open Xunit
open WTF.Host
open WTF.Core

// wlr_keyboard modifier bits (mirror of Chord's literals).
let private SHIFT = Chord.SHIFT
let private CTRL = Chord.CTRL
let private ALT = Chord.ALT
let private LOGO = Chord.LOGO

// ---------------------------------------------------------------------------
//  Modifier rendering + ordering (M C A S).
// ---------------------------------------------------------------------------

[<Fact>]
let ``no modifiers renders the bare key`` () =
    Assert.Equal(Some "j", Chord.format 0u 0x6au)

[<Fact>]
let ``each single modifier in isolation`` () =
    Assert.Equal(Some "M-j", Chord.format LOGO 0x6au)
    Assert.Equal(Some "C-j", Chord.format CTRL 0x6au)
    Assert.Equal(Some "A-j", Chord.format ALT 0x6au)
    Assert.Equal(Some "S-j", Chord.format SHIFT 0x6au)

[<Fact>]
let ``all four modifiers render in the exact M C A S order`` () =
    Assert.Equal(Some "M-C-A-S-j", Chord.format (LOGO ||| CTRL ||| ALT ||| SHIFT) 0x6au)

[<Fact>]
let ``representative modifier subsets keep canonical order`` () =
    Assert.Equal(Some "M-S-j", Chord.format (LOGO ||| SHIFT) 0x6au)
    Assert.Equal(Some "M-C-j", Chord.format (LOGO ||| CTRL) 0x6au)
    Assert.Equal(Some "C-A-j", Chord.format (CTRL ||| ALT) 0x6au)
    // Order is fixed by the formatter, not by the order bits are OR'd.
    Assert.Equal(Some "M-C-A-S-j", Chord.format (SHIFT ||| ALT ||| CTRL ||| LOGO) 0x6au)

// ---------------------------------------------------------------------------
//  Irrelevant modifier bits (CapsLock, NumLock/Mod2, high bits) are ignored.
// ---------------------------------------------------------------------------

[<Fact>]
let ``capslock bit alone does not appear in the chord`` () =
    Assert.Equal(Some "j", Chord.format 2u 0x6au)

[<Fact>]
let ``capslock shifts nothing - logo plus J stays M-j without S`` () =
    // 'J' (0x4a) folds to 'j'; the lock bit (2) must not synthesize an S- prefix.
    Assert.Equal(Some "M-j", Chord.format (LOGO ||| 2u) 0x4au)

[<Fact>]
let ``only the four real modifier bits survive an all-ones mask`` () =
    Assert.Equal(Some "M-C-A-S-j", Chord.format 0xffffffffu 0x6au)

// ---------------------------------------------------------------------------
//  Shifted-digit path: the sole mechanism behind MoveToWorkspace 1..9.
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData(0x21u, "M-S-1")>]
[<InlineData(0x40u, "M-S-2")>]
[<InlineData(0x23u, "M-S-3")>]
[<InlineData(0x24u, "M-S-4")>]
[<InlineData(0x25u, "M-S-5")>]
[<InlineData(0x5eu, "M-S-6")>]
[<InlineData(0x26u, "M-S-7")>]
[<InlineData(0x2au, "M-S-8")>]
[<InlineData(0x28u, "M-S-9")>]
[<InlineData(0x29u, "M-S-0")>]
let ``shifted number-row symbols map to M-S-digit`` (sym: uint32) (expected: string) =
    Assert.Equal(Some expected, Chord.format (LOGO ||| SHIFT) sym)

[<Theory>]
[<InlineData(0x31u, "M-1")>]
[<InlineData(0x32u, "M-2")>]
[<InlineData(0x39u, "M-9")>]
[<InlineData(0x30u, "M-0")>]
let ``plain digits map to M-digit`` (sym: uint32) (expected: string) =
    Assert.Equal(Some expected, Chord.format LOGO sym)

// ---------------------------------------------------------------------------
//  Named-keys table — every entry must render, incl. KP_Enter folding.
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData(0xff0du, "Return")>]   // Return
[<InlineData(0xff8du, "Return")>]   // KP_Enter folds to Return
[<InlineData(0x20u, "space")>]
[<InlineData(0xff09u, "Tab")>]
[<InlineData(0xff1bu, "Escape")>]
[<InlineData(0x2cu, "comma")>]
[<InlineData(0x2eu, "period")>]
[<InlineData(0x2du, "minus")>]
[<InlineData(0x3du, "equal")>]
[<InlineData(0x2bu, "plus")>]
[<InlineData(0xff51u, "Left")>]
[<InlineData(0xff52u, "Up")>]
[<InlineData(0xff53u, "Right")>]
[<InlineData(0xff54u, "Down")>]
let ``named keysyms render their canonical name`` (sym: uint32) (expected: string) =
    Assert.Equal(Some expected, Chord.format 0u sym)

[<Fact>]
let ``both Return keysyms collapse to the same chord`` () =
    Assert.Equal(Chord.format SHIFT 0xff0du, Chord.format SHIFT 0xff8du)

// ---------------------------------------------------------------------------
//  Case folding + boundary keysyms (off-by-one around the three ASCII ranges).
// ---------------------------------------------------------------------------

[<Fact>]
let ``uppercase letters fold to lowercase`` () =
    Assert.Equal(Some "q", Chord.format 0u 0x51u)            // 'Q' -> q
    Assert.Equal(Some "M-S-q", Chord.format (LOGO ||| SHIFT) 0x51u)
    Assert.Equal(Some "z", Chord.format 0u 0x5au)            // 'Z' -> z
    Assert.Equal(Some "a", Chord.format 0u 0x41u)            // 'A' -> a

[<Fact>]
let ``lowercase letters pass through`` () =
    Assert.Equal(Some "a", Chord.format 0u 0x61u)
    Assert.Equal(Some "z", Chord.format 0u 0x7au)

[<Theory>]
[<InlineData(0x2fu)>]   // '/' just below '0'
[<InlineData(0x3au)>]   // ':' just above '9'
let ``digit-range boundaries are unmappable`` (sym: uint32) =
    // '/' and ':' bracket the digit range and are not nameable -> None.
    Assert.Equal(None, Chord.format 0u sym)

// ---------------------------------------------------------------------------
//  None / graceful path — unmappable keysyms must return None so onKey -> 0.
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData(0xffebu)>]   // Super_L (bare modifier)
[<InlineData(0xffbeu)>]   // F1
[<InlineData(0x7eu)>]     // '~'
[<InlineData(0x60u)>]     // '`'
[<InlineData(0x5fu)>]     // '_'
[<InlineData(0x3cu)>]     // '<'
[<InlineData(0x5bu)>]     // '['  (just above 'Z'+? boundary)
[<InlineData(0x7bu)>]     // '{'  (just above 'z')
[<InlineData(0x0u)>]
[<InlineData(0xffffffffu)>]
let ``unmappable keysyms return None`` (sym: uint32) =
    Assert.Equal(None, Chord.format 0u sym)

[<Fact>]
let ``0x40 is the shifted-2, not None, not a boundary miss`` () =
    // 0x40 ('@') sits just above the digit range but IS in shiftedDigit -> "2".
    Assert.Equal(Some "2", Chord.format 0u 0x40u)

// ---------------------------------------------------------------------------
//  Round-trip: every default bind in baseKeys/workspaceKeys is producible from
//  a real US-keyboard (mods, keysym) AND resolves through Keymap.lookup.
// ---------------------------------------------------------------------------

// (modifier mask, keysym, expected chord) for each default bind onKey can fire.
let defaultBindCases : obj[] list =
    [ LOGO, 0xff0du, "M-Return"          // M-Return  Spawn kitty
      LOGO, 0x70u, "M-p"                 // M-p
      LOGO, 0x6au, "M-j"                 // M-j
      LOGO, 0x6bu, "M-k"                 // M-k
      LOGO, 0x6du, "M-m"                 // M-m
      LOGO ||| SHIFT, 0xff0du, "M-S-Return"  // M-S-Return SwapMaster
      LOGO ||| SHIFT, 0x4au, "M-S-j"     // Shift+j arrives as 'J' 0x4a
      LOGO ||| SHIFT, 0x4bu, "M-S-k"
      LOGO ||| SHIFT, 0x43u, "M-S-c"
      LOGO, 0x20u, "M-space"             // M-space NextLayout
      LOGO, 0x74u, "M-t"
      LOGO, 0x77u, "M-w"
      LOGO, 0x62u, "M-b"
      LOGO, 0x67u, "M-g"
      LOGO, 0x66u, "M-f"
      LOGO ||| SHIFT, 0x20u, "M-S-space" // ToggleFloat
      LOGO ||| SHIFT, 0x46u, "M-S-f"     // ToggleFullscreen ('F')
      LOGO, 0x68u, "M-h"
      LOGO, 0x6cu, "M-l"
      LOGO, 0x2eu, "M-period"            // IncMaster
      LOGO, 0x2cu, "M-comma"             // DecMaster
      LOGO, 0x3du, "M-equal"             // IncGaps
      LOGO, 0x2du, "M-minus"             // DecGaps
      LOGO, 0xff09u, "M-Tab"             // NextWorkspace
      LOGO, 0x7au, "M-z"                 // Undo
      LOGO ||| SHIFT, 0x5au, "M-S-z"     // Redo ('Z')
      LOGO ||| SHIFT, 0x53u, "M-S-s" ]   // SaveSession ('S')
    |> List.map (fun (m, s, c) -> [| box (m: uint32); box (s: uint32); box (c: string) |])

[<Theory>]
[<MemberData(nameof defaultBindCases)>]
let ``each default base bind is producible and resolves`` (mods: uint32) (sym: uint32) (chord: string) =
    Assert.Equal(Some chord, Chord.format mods sym)
    Assert.True(
        (Keymap.lookup Program.defaultConfig chord).IsSome,
        sprintf "chord %s not bound in defaultConfig" chord)

// Workspace switch (M-1..M-9) and move (M-S-1..M-S-9) binds.
[<Theory>]
[<InlineData(0x31u, "M-1")>]
[<InlineData(0x35u, "M-5")>]
[<InlineData(0x39u, "M-9")>]
let ``workspace switch binds are producible and resolve`` (sym: uint32) (chord: string) =
    Assert.Equal(Some chord, Chord.format LOGO sym)
    Assert.True((Keymap.lookup Program.defaultConfig chord).IsSome)

[<Theory>]
[<InlineData(0x21u, "M-S-1")>]
[<InlineData(0x25u, "M-S-5")>]
[<InlineData(0x28u, "M-S-9")>]
let ``workspace move binds are producible and resolve`` (sym: uint32) (chord: string) =
    Assert.Equal(Some chord, Chord.format (LOGO ||| SHIFT) sym)
    Assert.True((Keymap.lookup Program.defaultConfig chord).IsSome)

[<Fact>]
let ``the quit chord M-S-q is exactly what onKey special-cases`` () =
    // onKey matches the literal "M-S-q"; 'Q' is 0x51 and folds with the S- prefix.
    Assert.Equal(Some "M-S-q", Chord.format (LOGO ||| SHIFT) 0x51u)
