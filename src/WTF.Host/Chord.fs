namespace WTF.Host

/// Translate a wlroots (modifier-mask, xkb-keysym) pair into a chord string like
/// "M-S-j", so it can be looked up in the F# keymap. US layout for the beta.
module Chord =

    // wlr_keyboard modifier bits (from wlr_keyboard.h).
    [<Literal>]
    let SHIFT = 1u
    [<Literal>]
    let CTRL = 4u
    [<Literal>]
    let ALT = 8u
    [<Literal>]
    let LOGO = 64u

    // Named keysyms we care about (xkbcommon keysym values).
    let private named =
        dict
            [ 0xff0du, "Return"
              0xff8du, "Return" // KP_Enter
              0x20u, "space"
              0xff09u, "Tab"
              0xff1bu, "Escape"
              0x2cu, "comma"
              0x2eu, "period"
              0x2du, "minus"
              0x3du, "equal"
              0x2bu, "plus"
              0xff51u, "Left"
              0xff52u, "Up"
              0xff53u, "Right"
              0xff54u, "Down" ]

    // Shifted US number-row symbols -> their base digit, so "M-S-2" resolves.
    let private shiftedDigit =
        dict
            [ 0x21u, "1"; 0x40u, "2"; 0x23u, "3"; 0x24u, "4"; 0x25u, "5"
              0x5eu, "6"; 0x26u, "7"; 0x2au, "8"; 0x28u, "9"; 0x29u, "0" ]

    let private keyName (sym: uint32) : string option =
        if named.ContainsKey sym then Some named[sym]
        elif shiftedDigit.ContainsKey sym then Some shiftedDigit[sym]
        elif sym >= 0x30u && sym <= 0x39u then Some(string (char sym)) // digits
        elif sym >= 0x61u && sym <= 0x7au then Some(string (char sym)) // a-z
        elif sym >= 0x41u && sym <= 0x5au then Some(string (char (sym + 0x20u))) // A-Z -> a-z
        else None

    /// Build the chord, or None if the key isn't nameable. Modifier order: M C A S.
    let format (mods: uint32) (sym: uint32) : string option =
        keyName sym
        |> Option.map (fun name ->
            let p (bit: uint32) (s: string) = if mods &&& bit <> 0u then s else ""
            sprintf "%s%s%s%s%s" (p LOGO "M-") (p CTRL "C-") (p ALT "A-") (p SHIFT "S-") name)
