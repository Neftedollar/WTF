namespace WTF.Core

// =============================================================================
// The plugin ABI — a single tiny, declarative interface.
//
// This is the ".NET as a platform" extension point (#13): any compiled .NET
// assembly (F#, C#, VB, ...) that references WTF.Core can ship custom layouts
// that become available by name, exactly like the built-ins. A user drops the
// .dll into ~/.config/wtf/plugins/ and the loader (in WTF.Plugins) discovers it.
//
// WTF.Core stays PURE: `IWtfLayoutPlugin` is JUST A TYPE — zero IO, zero
// reflection, zero AssemblyLoadContext. All of that lives in the loader. The
// brain is untouched.
//
// DECLARATIVE BY DESIGN: a plugin does NOT register anything itself — it simply
// HANDS BACK the (name, factory) pairs it wants registered, and the loader owns
// the actual `Registry.register`. That keeps the contract minimal and means the
// loader controls collisions, logging and lifetime.
//
// STABILITY: this interface IS the plugin ABI. Keep it FROZEN — adding a member
// is a breaking change that invalidates every shipped plugin (an
// already-compiled plugin would no longer satisfy the interface). New
// capabilities should arrive as NEW optional interfaces a plugin MAY also
// implement, never as new members here.
//
// `LayoutFactory = int -> float -> Layout<WindowId>` is defined in World.fs, so
// this file is Compile-included AFTER World.fs.
// =============================================================================

/// A layout/extension plugin: a parameterless-ctor class in an external
/// assembly that exposes one or more named layouts for the loader to register.
type IWtfLayoutPlugin =
    /// Human-readable plugin name, for logging / diagnostics only.
    abstract member Name: string
    /// The (name, factory) pairs the loader will `Registry.register`. Each
    /// factory is `nmaster -> ratio -> Layout<WindowId>`; gaps are applied by
    /// `World.arrange` (Layout.withGaps), so plugins need no gap handling.
    abstract member Layouts: (string * LayoutFactory) list

/// A WORKSPACE-TYPE plugin (#5): one level up from a layout — it contributes named
/// workspace TYPES (models of how a workspace organises windows over time). Frozen
/// ABI, sibling of `IWtfLayoutPlugin`; discovered by the SAME loader scan and fed
/// into `WorkspaceRegistry`. `WorkspaceArranger = WorkspaceView -> placements` is
/// defined in World.fs, so this file stays Compile-included AFTER World.fs.
type IWtfWorkspacePlugin =
    /// Human-readable plugin name, for logging / diagnostics only.
    abstract member Name: string
    /// The (name, arranger) pairs the loader will `WorkspaceRegistry.register`.
    abstract member WorkspaceTypes: (string * WorkspaceArranger) list
