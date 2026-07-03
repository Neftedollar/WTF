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

    // Named keysyms we care about (xkbcommon keysym values). The names follow the
    // xkb keysym names (as reported by `wev` / `xev`), so a config author can read
    // a key off `wev` and bind it directly: `bind "Print" screenshot`.
    let private named =
        dict
            [ 0xff0du, "Return"
              0xff8du, "Return" // KP_Enter
              0x20u, "space"
              0xff09u, "Tab"
              0xff1bu, "Escape"
              0xff08u, "BackSpace"
              0xffffu, "Delete"
              0xff63u, "Insert"
              0x2cu, "comma"
              0x2eu, "period"
              0x2du, "minus"
              0x3du, "equal"
              0x2bu, "plus"
              // navigation cluster
              0xff50u, "Home"
              0xff57u, "End"
              0xff55u, "PageUp"    // Prior
              0xff56u, "PageDown"  // Next
              0xff61u, "Print"
              0xff67u, "Menu"
              0xff51u, "Left"
              0xff52u, "Up"
              0xff53u, "Right"
              0xff54u, "Down"
              // common punctuation keys (US layout, unshifted keysym)
              0x2fu, "slash"
              0x5cu, "backslash"
              0x3bu, "semicolon"
              0x27u, "apostrophe"
              0x5bu, "bracketleft"
              0x5du, "bracketright"
              0x60u, "grave" ]

    /// Bare modifier keysyms (Shift_L .. Hyper_R): a modifier press on its own is
    /// never a chord, so it must stay unnameable (else "M-" alone would resolve).
    let private isModifier (sym: uint32) = sym >= 0xffe1u && sym <= 0xffeeu

    // Shifted US number-row symbols -> their base digit, so "M-S-2" resolves.
    let private shiftedDigit =
        dict
            [ 0x21u, "1"; 0x40u, "2"; 0x23u, "3"; 0x24u, "4"; 0x25u, "5"
              0x5eu, "6"; 0x26u, "7"; 0x2au, "8"; 0x28u, "9"; 0x29u, "0" ]

    let private keyName (sym: uint32) : string option =
        if sym = 0u then None                                          // NoSymbol
        elif named.ContainsKey sym then Some named[sym]
        elif shiftedDigit.ContainsKey sym then Some shiftedDigit[sym]
        elif sym >= 0x30u && sym <= 0x39u then Some(string (char sym)) // digits
        elif sym >= 0x61u && sym <= 0x7au then Some(string (char sym)) // a-z
        elif sym >= 0x41u && sym <= 0x5au then Some(string (char (sym + 0x20u))) // A-Z -> a-z
        elif sym >= 0xffbeu && sym <= 0xffd5u then Some(sprintf "F%d" (int (sym - 0xffbeu) + 1)) // F1-F24
        elif isModifier sym then None                                 // bare modifier: never a chord
        else
            // Universal fallback: ANY other key is bindable by its raw xkb keysym
            // in lowercase hex — `bind "M-0x1008ff14" (Spawn "playerctl play-pause")`.
            // (Find it with `wev`.) Unbound keys still fall through to the client,
            // because Keymap.lookup returns None for a chord no one bound.
            Some(sprintf "0x%x" sym)

    /// Build the chord, or None if the key isn't nameable. Modifier order: M C A S.
    let format (mods: uint32) (sym: uint32) : string option =
        keyName sym
        |> Option.map (fun name ->
            let p (bit: uint32) (s: string) = if mods &&& bit <> 0u then s else ""
            sprintf "%s%s%s%s%s" (p LOGO "M-") (p CTRL "C-") (p ALT "A-") (p SHIFT "S-") name)
