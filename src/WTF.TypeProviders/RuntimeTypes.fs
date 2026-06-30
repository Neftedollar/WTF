namespace WTF.TypeProviders.Runtime

/// Runtime erasure target for the D-Bus introspection Type Provider.
///
/// The provided (design-time) types erase to these ordinary F# records. At
/// runtime there is NO type-provider code left — a value typed as a provided
/// interface/member type is really one of these records, and the provided
/// static literal properties (Name/Signature/DotNetType/Access) erase to baked
/// in string constants at the call site.
///
/// Because erasing Type Providers emit ordinary compiled IL at build time, the
/// whole feature is fully compatible with NativeAOT (unlike the JIT-based FCS /
/// reflective-plugin features in #11/#13): the provided members are constants
/// and these records are normal types in a normally-compiled assembly.

/// One member (method / property / signal) of a D-Bus interface.
type DBusMemberInfo =
    { /// Member name, e.g. "CreateUser".
      Name: string
      /// "method" | "property" | "signal".
      Kind: string
      /// Raw D-Bus type signature(s), e.g. "x" or "ssi -> o".
      Signature: string
      /// Mapped .NET type name as a string, e.g. "ObjectPath" or "System.UInt32".
      DotNetType: string
      /// Property access ("read"|"write"|"readwrite"); "" for methods/signals.
      Access: string }

/// One D-Bus interface and its members.
type DBusInterfaceInfo =
    { /// Full interface name, e.g. "org.freedesktop.Accounts".
      Name: string
      Members: DBusMemberInfo[] }

/// Runtime erasure target for the Apps config Type Provider. A value typed as a
/// provided app type (e.g. `Apps.Firefox`) IS one of these records at runtime;
/// the provided literal members (AppId/Exec/Name) erase to baked constants.
type AppInfo =
    { /// Window app-id used by rules: StartupWMClass if present, else the
      /// desktop-file id (basename minus ".desktop").
      AppId: string
      /// The Exec command with FreeDesktop field codes (%u %F …) stripped.
      Exec: string
      /// Human-readable application name (Name= from [Desktop Entry]).
      Name: string }

/// Runtime erasure target for one XKB entry (a layout or an option) parsed from
/// evdev.lst. The provided literal members (Code/Description) erase to constants.
type XkbEntry =
    { /// The xkb token, e.g. "ru" (layout) or "grp:alt_shift_toggle" (option).
      Code: string
      /// The human-readable description column.
      Description: string }
