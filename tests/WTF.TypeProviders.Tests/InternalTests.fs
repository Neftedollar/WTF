module WTF.TypeProviders.InternalTests

open System
open System.IO
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open WTF.TypeProviders

// ---------------------------------------------------------------------------
// White-box tests of the internal DBusSig / Introspection modules. These are
// reachable because WTF.TypeProviders grants InternalsVisibleTo to this project.
// The previous suite only exercised s/b/o/i/x indirectly via one Accounts XML.
// ---------------------------------------------------------------------------

// A synthetic fixture surfacing TP paths the real Accounts XML never reaches
// (tuple returns, default arg direction, struct/dict mapping, multi-arg signals,
// write/readwrite/missing property access, empty interface).
type Fixtures = DBusIntrospection<"data/org.wtf.Fixtures.xml">

// === DBusSig.map — full single-char primitive table =========================

[<Theory>]
[<InlineData("y", "System.Byte")>]
[<InlineData("b", "System.Boolean")>]
[<InlineData("n", "System.Int16")>]
[<InlineData("q", "System.UInt16")>]
[<InlineData("i", "System.Int32")>]
[<InlineData("u", "System.UInt32")>]
[<InlineData("x", "System.Int64")>]
[<InlineData("t", "System.UInt64")>]
[<InlineData("d", "System.Double")>]
[<InlineData("s", "System.String")>]
[<InlineData("o", "ObjectPath")>]
[<InlineData("g", "System.String")>]
[<InlineData("h", "CloseSafeHandle")>]
[<InlineData("v", "System.Object")>]
let ``map: every primitive signature char`` (sg: string) (expected: string) =
    Assert.Equal(expected, DBusSig.map sg)

[<Theory>]
[<InlineData("y")>]
[<InlineData("b")>]
[<InlineData("i")>]
[<InlineData("x")>]
[<InlineData("s")>]
[<InlineData("o")>]
[<InlineData("v")>]
[<InlineData("h")>]
let ``mapOne: a primitive consumes exactly one char`` (sg: string) =
    let _, consumed = DBusSig.mapOne sg 0
    Assert.Equal(1, consumed)

// === DBusSig.map — array / dict / struct compounds ==========================

[<Theory>]
[<InlineData("as", "System.String[]")>]
[<InlineData("ai", "System.Int32[]")>]
[<InlineData("aai", "System.Int32[][]")>]
[<InlineData("ao", "ObjectPath[]")>]
[<InlineData("a{sv}", "IDictionary<string,obj>")>]
[<InlineData("a{si}", "IDictionary<string,int32>")>]
[<InlineData("(si)", "ValueTuple<string,int32>")>]
[<InlineData("((i)s)", "ValueTuple<ValueTuple<int32>,string>")>]
let ``map: compound signatures`` (sg: string) (expected: string) =
    Assert.Equal(expected, DBusSig.map sg)

[<Fact>]
let ``map: nested dict a{oa{sv}}`` () =
    Assert.Equal("IDictionary<ObjectPath,IDictionary<string,obj>>", DBusSig.map "a{oa{sv}}")

[<Fact>]
let ``map: array of struct a(si)`` () =
    Assert.Equal("ValueTuple<string,int32>[]", DBusSig.map "a(si)")

// Pin the recursion offsets: mapOne must report the exact chars consumed so
// that array/dict element lengths advance correctly.
[<Theory>]
[<InlineData("ai", 2)>]
[<InlineData("aai", 3)>]
[<InlineData("a{sv}", 5)>]
[<InlineData("(si)", 4)>]
[<InlineData("((i)s)", 6)>]
let ``mapOne: consumed-char count is exact`` (sg: string) (expected: int) =
    let _, consumed = DBusSig.mapOne sg 0
    Assert.Equal(expected, consumed)

// === DBusSig.map — unknown / reserved / empty (graceful fallback) ===========

[<Theory>]
[<InlineData("")>]
let ``map: empty signature is void`` (sg: string) =
    Assert.Equal("System.Void", DBusSig.map sg)

[<Fact>]
let ``map: null signature is void`` () =
    Assert.Equal("System.Void", DBusSig.map null)

[<Theory>]
[<InlineData("r")>]
[<InlineData("e")>]
[<InlineData("*")>]
[<InlineData("?")>]
let ``map: unknown signature char falls back to System.Object`` (sg: string) =
    Assert.Equal("System.Object", DBusSig.map sg)

[<Fact>]
let ``map: multi-type signature returns only the first complete type`` () =
    // "ss" — map only decodes the first complete type.
    Assert.Equal("System.String", DBusSig.map "ss")

// === DBusSig.mapReturn — void / single / tuple ==============================

[<Fact>]
let ``mapReturn: no out args is void`` () =
    Assert.Equal("System.Void", DBusSig.mapReturn [])

[<Fact>]
let ``mapReturn: single out arg is that type`` () =
    Assert.Equal("ObjectPath", DBusSig.mapReturn [ "o" ])

[<Fact>]
let ``mapReturn: multiple out args form a short-named value tuple`` () =
    // Regression: the old no-op lambda left these System.-prefixed, inconsistent
    // with a struct of the same types. They must now be short-named.
    Assert.Equal("ValueTuple<int32,string>", DBusSig.mapReturn [ "i"; "s" ])
    Assert.Equal(DBusSig.map "(is)", DBusSig.mapReturn [ "i"; "s" ])

// === Introspection.sanitize ================================================

[<Theory>]
[<InlineData("org.freedesktop.Accounts", "OrgFreedesktopAccounts")>]
[<InlineData("org.foo-bar", "OrgFooBar")>]      // hyphen splits like a dot
[<InlineData("org.foo_bar", "OrgFoo_bar")>]     // underscore preserved
[<InlineData("foo", "Foo")>]                    // single segment
[<InlineData("", "")>]                          // empty input
[<InlineData("org.0foo", "Org0foo")>]           // non-leading digit segment ok
let ``sanitize: name mapping`` (input: string) (expected: string) =
    Assert.Equal(expected, Introspection.sanitize input)

[<Fact>]
let ``sanitize: leading-digit first segment is prefixed to stay a valid identifier`` () =
    // Regression: "0rg.foo" previously yielded "0rgFoo" (invalid .NET identifier).
    Assert.Equal("_0rgFoo", Introspection.sanitize "0rg.foo")

[<Fact>]
let ``sanitize: documents the . vs - boundary collision`` () =
    // Known limitation: '.' and '-' both split, so these distinct interfaces
    // collide to the same identifier. Pinned so a future fix is a conscious change.
    Assert.Equal(Introspection.sanitize "org.foo.Bar", Introspection.sanitize "org.foo-Bar")

[<Property>]
let ``sanitize: result never starts with a digit`` () =
    let alnum = Gen.elements (['a'..'z'] @ ['A'..'Z'] @ ['0'..'9'] @ [ '.'; '-'; '_' ])
    let nameGen = Gen.nonEmptyListOf alnum |> Gen.map (List.toArray >> String)
    Prop.forAll (Arb.fromGen nameGen) (fun name ->
        let s = Introspection.sanitize name
        s = "" || not (Char.IsDigit s.[0]))

// === Introspection.parseFile — happy + graceful paths =======================

let private withTempXml (contents: string) (f: string -> 'a) : 'a =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml")
    File.WriteAllText(path, contents)
    try f path
    finally (try File.Delete path with _ -> ())

[<Fact>]
let ``parseFile: well-formed node yields interfaces and members`` () =
    let xml =
        "<node><interface name='a.b.C'>" +
        "<method name='M'><arg name='p' direction='in' type='s'/></method>" +
        "<property name='P' type='b' access='read'/>" +
        "<signal name='S'><arg name='q' type='o'/></signal>" +
        "</interface></node>"
    withTempXml xml (fun p ->
        let ifaces = Introspection.parseFile p
        Assert.Equal(1, ifaces.Length)
        Assert.Equal("a.b.C", ifaces.[0].Name)
        Assert.Equal(3, ifaces.[0].Members.Length))

[<Fact>]
let ``parseFile: syntactically broken xml degrades to empty list`` () =
    withTempXml "<node><interface name='x'></node" (fun p ->
        Assert.Empty(Introspection.parseFile p))

[<Fact>]
let ``parseFile: empty / whitespace file degrades to empty list`` () =
    withTempXml "   " (fun p ->
        Assert.Empty(Introspection.parseFile p))

[<Fact>]
let ``parseFile: root that is not <node> is iterated without crashing`` () =
    // Root element other than <node>: no child <interface> elements -> empty.
    withTempXml "<root><thing/></root>" (fun p ->
        Assert.Empty(Introspection.parseFile p))

[<Fact>]
let ``parseFile: interface missing name attribute does not NRE`` () =
    withTempXml "<node><interface><method name='M'/></interface></node>" (fun p ->
        let ifaces = Introspection.parseFile p
        Assert.Equal(1, ifaces.Length)
        Assert.Equal("", ifaces.[0].Name))

[<Fact>]
let ``parseFile: member missing name attribute does not NRE`` () =
    withTempXml "<node><interface name='i'><method><arg type='s'/></method><property type='b'/></interface></node>" (fun p ->
        let ifaces = Introspection.parseFile p
        let names = ifaces.[0].Members |> List.map (fun m -> m.Name)
        Assert.Equal<string list>([ ""; "" ], names))

[<Fact>]
let ``parseFile: arg with no direction defaults to in`` () =
    withTempXml "<node><interface name='i'><method name='M'><arg name='x' type='i'/></method></interface></node>" (fun p ->
        let m = (Introspection.parseFile p).[0].Members.[0]
        Assert.Equal("in", m.Args.[0].Direction))

// ---------------------------------------------------------------------------
// Consumer-level (real Type Provider) assertions over the synthetic fixture.
// These exercise buildMemberType — the Signature string, multi-out tuples,
// property access, and empty interfaces — which is only reachable via the TP.
// ---------------------------------------------------------------------------

[<Fact>]
let ``TP: multi-out method maps to a value tuple`` () =
    Assert.Equal("ValueTuple<int32,string>", Fixtures.OrgWtfFixtures.Methods.TwoOut.DotNetType)

[<Fact>]
let ``TP: method with only in args returns void`` () =
    Assert.Equal("System.Void", Fixtures.OrgWtfFixtures.Methods.DefaultDir.DotNetType)

[<Fact>]
let ``TP: struct out arg maps to a value tuple`` () =
    Assert.Equal("ValueTuple<string,int32>", Fixtures.OrgWtfFixtures.Methods.StructReturn.DotNetType)

[<Fact>]
let ``TP: dict out arg maps to IDictionary`` () =
    Assert.Equal("IDictionary<string,obj>", Fixtures.OrgWtfFixtures.Methods.DictReturn.DotNetType)

[<Fact>]
let ``TP: multi-arg signal maps to a value tuple`` () =
    Assert.Equal("ValueTuple<int32,string>", Fixtures.OrgWtfFixtures.Signals.MultiSig.DotNetType)

[<Fact>]
let ``TP: property access write / readwrite / missing`` () =
    Assert.Equal("write", Fixtures.OrgWtfFixtures.Properties.Writable.Access)
    Assert.Equal("readwrite", Fixtures.OrgWtfFixtures.Properties.ReadWritable.Access)
    Assert.Equal("", Fixtures.OrgWtfFixtures.Properties.NoAccess.Access)

[<Fact>]
let ``TP: empty interface still produces a usable provided type`` () =
    Assert.Equal("org.wtf.Empty", Fixtures.OrgWtfEmpty.Name)
    Assert.Equal("org.wtf.Empty", Fixtures.OrgWtfEmpty.FullName)

// === Signature string format (buildMemberType) over the Accounts fixture ====

type Accts = DBusIntrospection<"data/org.freedesktop.Accounts.xml">

[<Fact>]
let ``TP Signature: method with in and out args`` () =
    Assert.Equal("(x)o | id:x -> user:o", Accts.OrgFreedesktopAccounts.Methods.FindUserById.Signature)

[<Fact>]
let ``TP Signature: multi-in single-out method`` () =
    Assert.Equal("(ssi)o | name:s, fullname:s, accountType:i -> user:o",
                 Accts.OrgFreedesktopAccounts.Methods.CreateUser.Signature)

[<Fact>]
let ``TP Signature: void method (only in args)`` () =
    Assert.Equal("(s) | name:s", Accts.OrgFreedesktopAccounts.Methods.UncacheUser.Signature)
    Assert.Equal("System.Void", Accts.OrgFreedesktopAccounts.Methods.UncacheUser.DotNetType)
    Assert.Equal("System.Void", Accts.OrgFreedesktopAccounts.Methods.DeleteUser.DotNetType)

[<Fact>]
let ``TP: array return type (as)`` () =
    Assert.Equal("System.String[]", Accts.OrgFreedesktopAccounts.Methods.GetUsersLanguages.DotNetType)

// === RuntimeTypes erasure-shape contract ====================================
// The provided literal properties erase to baked-in constants typed as plain
// strings; this confirms they round-trip through the runtime record fields used
// at the erasure target.

[<Fact>]
let ``RuntimeTypes: provided literals erase to baked string constants`` () =
    let m: WTF.TypeProviders.Runtime.DBusMemberInfo =
        { Name = Accts.OrgFreedesktopAccounts.Methods.FindUserById.Name
          Kind = Accts.OrgFreedesktopAccounts.Methods.FindUserById.Kind
          Signature = Accts.OrgFreedesktopAccounts.Methods.FindUserById.Signature
          DotNetType = Accts.OrgFreedesktopAccounts.Methods.FindUserById.DotNetType
          Access = Accts.OrgFreedesktopAccounts.Methods.FindUserById.Access }
    Assert.Equal("FindUserById", m.Name)
    Assert.Equal("method", m.Kind)
    Assert.Equal("ObjectPath", m.DotNetType)
    Assert.Equal("", m.Access)
    let iface: WTF.TypeProviders.Runtime.DBusInterfaceInfo =
        { Name = Accts.OrgFreedesktopAccounts.Name; Members = [| m |] }
    Assert.Equal("org.freedesktop.Accounts", iface.Name)
    Assert.Single(iface.Members) |> ignore
