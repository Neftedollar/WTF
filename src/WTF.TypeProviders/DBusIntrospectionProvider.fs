namespace WTF.TypeProviders

open System
open System.IO
open System.Xml.Linq
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open WTF.TypeProviders.Runtime

/// Maps D-Bus type signatures to .NET type-name strings.
///
/// The TP records the mapped type as a STRING literal (e.g. "System.UInt32",
/// "System.String[]", "ObjectPath", "IDictionary<string,obj>") in each provided
/// member's DotNetType property — it never loads Tmds.DBus or builds a real
/// System.Type at design time, so the provider stays dependency-free.
module internal DBusSig =

    /// Map a single complete D-Bus signature to a .NET type-name string.
    /// Returns the mapped name and the count of signature chars consumed.
    let rec private mapOne (sig_: string) (i: int) : string * int =
        if i >= sig_.Length then "System.Object", 0
        else
            match sig_.[i] with
            | 'y' -> "System.Byte", 1
            | 'b' -> "System.Boolean", 1
            | 'n' -> "System.Int16", 1
            | 'q' -> "System.UInt16", 1
            | 'i' -> "System.Int32", 1
            | 'u' -> "System.UInt32", 1
            | 'x' -> "System.Int64", 1
            | 't' -> "System.UInt64", 1
            | 'd' -> "System.Double", 1
            | 's' -> "System.String", 1
            | 'o' -> "ObjectPath", 1
            | 'g' -> "System.String", 1
            | 'h' -> "CloseSafeHandle", 1
            | 'v' -> "System.Object", 1
            | 'a' ->
                // Array. a{KV} -> IDictionary<K,V>; aT -> T[].
                if i + 1 < sig_.Length && sig_.[i + 1] = '{' then
                    // dict entry: a{KV} — K is a single basic type, V is one complete type.
                    let kName, kLen = mapOne sig_ (i + 2)
                    let vName, vLen = mapOne sig_ (i + 2 + kLen)
                    // expect closing '}'
                    let closeAt = i + 2 + kLen + vLen
                    let consumed = (closeAt - i) + 1 // include '}'
                    sprintf "IDictionary<%s,%s>" (shortName kName) (shortName vName), consumed
                else
                    let elemName, elemLen = mapOne sig_ (i + 1)
                    elemName + "[]", elemLen + 1
            | '(' ->
                // struct: ( ... ) -> ValueTuple<...>
                let mutable j = i + 1
                let parts = ResizeArray<string>()
                while j < sig_.Length && sig_.[j] <> ')' do
                    let n, l = mapOne sig_ j
                    parts.Add(shortName n)
                    j <- j + l
                let consumed = (j - i) + 1 // include ')'
                sprintf "ValueTuple<%s>" (String.Join(",", parts)), consumed
            | _ -> "System.Object", 1

    /// Friendly short name for nested generics (string instead of System.String etc.).
    and private shortName (n: string) =
        match n with
        | "System.String" -> "string"
        | "System.Object" -> "obj"
        | "System.Boolean" -> "bool"
        | "System.Int32" -> "int32"
        | "System.UInt32" -> "uint32"
        | "System.Int64" -> "int64"
        | "System.UInt64" -> "uint64"
        | "System.Byte" -> "byte"
        | "System.Double" -> "double"
        | "System.Int16" -> "int16"
        | "System.UInt16" -> "uint16"
        | other -> other

    /// Map a complete (possibly compound) D-Bus signature string to a .NET name.
    let map (sig_: string) : string =
        if String.IsNullOrEmpty sig_ then "System.Void"
        else
            let name, _ = mapOne sig_ 0
            name

    /// Map a sequence of arg signatures (the OUT/return side of a method) to a
    /// .NET return-type string: 0 -> void, 1 -> that type, n -> a value tuple.
    let mapReturn (sigs: string list) : string =
        match sigs with
        | [] -> "System.Void"
        | [ one ] -> map one
        | many -> sprintf "ValueTuple<%s>" (String.Join(",", many |> List.map (map >> (fun n -> if n.StartsWith "System." then n else n))))

/// Design-time XML model.
module internal Introspection =

    type Arg = { Name: string; Direction: string; Sig: string }
    type Member = { Name: string; Kind: string; Args: Arg list; Access: string; PropSig: string }
    type Interface = { Name: string; Members: Member list }

    let private localName (e: XElement) = e.Name.LocalName

    let parseFile (path: string) : Interface list =
        let doc = XDocument.Load(path)
        let node = doc.Root
        [ for iface in node.Elements() do
            if localName iface = "interface" then
                let ifaceName = iface.Attribute(XName.Get "name").Value
                let members =
                    [ for m in iface.Elements() do
                        match localName m with
                        | "method" ->
                            let args =
                                [ for a in m.Elements() do
                                    if localName a = "arg" then
                                        let nm = a.Attribute(XName.Get "name")
                                        let dir = a.Attribute(XName.Get "direction")
                                        let ty = a.Attribute(XName.Get "type")
                                        yield { Name = (if isNull nm then "" else nm.Value)
                                                Direction = (if isNull dir then "in" else dir.Value)
                                                Sig = (if isNull ty then "" else ty.Value) } ]
                            yield { Name = m.Attribute(XName.Get "name").Value
                                    Kind = "method"; Args = args; Access = ""; PropSig = "" }
                        | "signal" ->
                            let args =
                                [ for a in m.Elements() do
                                    if localName a = "arg" then
                                        let nm = a.Attribute(XName.Get "name")
                                        let ty = a.Attribute(XName.Get "type")
                                        yield { Name = (if isNull nm then "" else nm.Value)
                                                Direction = "out"
                                                Sig = (if isNull ty then "" else ty.Value) } ]
                            yield { Name = m.Attribute(XName.Get "name").Value
                                    Kind = "signal"; Args = args; Access = ""; PropSig = "" }
                        | "property" ->
                            let ty = m.Attribute(XName.Get "type")
                            let acc = m.Attribute(XName.Get "access")
                            yield { Name = m.Attribute(XName.Get "name").Value
                                    Kind = "property"; Args = []
                                    Access = (if isNull acc then "" else acc.Value)
                                    PropSig = (if isNull ty then "" else ty.Value) }
                        | _ -> () ]
                yield { Name = ifaceName; Members = members } ]

    /// Sanitize a D-Bus interface name (org.freedesktop.Accounts) into a valid
    /// .NET identifier (OrgFreedesktopAccounts).
    let sanitize (name: string) : string =
        name.Split([| '.'; '-' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun part ->
            if part.Length = 0 then part
            else string (Char.ToUpperInvariant part.[0]) + part.Substring(1))
        |> String.concat ""

[<TypeProvider>]
type DBusIntrospectionProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let ns = "WTF.TypeProviders"
    let asm = System.Reflection.Assembly.GetExecutingAssembly()

    // The runtime erasure targets live in this same assembly.
    let interfaceInfoTy = typeof<DBusInterfaceInfo>
    let memberInfoTy = typeof<DBusMemberInfo>

    /// A static literal string property: erases to the constant at the call site.
    let literalProp (name: string) (value: string) =
        ProvidedProperty(name, typeof<string>, isStatic = true,
                         getterCode = fun _ -> <@@ value @@>)

    let buildMemberType (m: Introspection.Member) =
        let inSigs  = m.Args |> List.filter (fun a -> a.Direction = "in")  |> List.map (fun a -> a.Sig)
        let outSigs = m.Args |> List.filter (fun a -> a.Direction = "out") |> List.map (fun a -> a.Sig)

        let signature, dotNet, access =
            match m.Kind with
            | "method" ->
                let inSig  = String.concat "" inSigs
                let outSig = String.concat "" outSigs
                let readable =
                    let lhs = m.Args |> List.filter (fun a -> a.Direction = "in")
                                     |> List.map (fun a -> sprintf "%s:%s" a.Name a.Sig)
                                     |> String.concat ", "
                    let rhs = m.Args |> List.filter (fun a -> a.Direction = "out")
                                     |> List.map (fun a -> sprintf "%s:%s" a.Name a.Sig)
                                     |> String.concat ", "
                    if rhs = "" then lhs else sprintf "%s -> %s" lhs rhs
                let sigStr = sprintf "(%s)%s | %s" inSig outSig readable
                sigStr, DBusSig.mapReturn outSigs, ""
            | "property" ->
                m.PropSig, DBusSig.map m.PropSig, m.Access
            | "signal" ->
                let s = String.concat "" outSigs
                s, DBusSig.mapReturn outSigs, ""
            | _ -> "", "System.Object", ""

        let t = ProvidedTypeDefinition(m.Name, baseType = Some memberInfoTy, hideObjectMethods = true)
        t.AddMember(literalProp "Name" m.Name)
        t.AddMember(literalProp "Kind" m.Kind)
        t.AddMember(literalProp "Signature" signature)
        t.AddMember(literalProp "DotNetType" dotNet)
        t.AddMember(literalProp "Access" access)
        t

    let buildInterfaceType (iface: Introspection.Interface) =
        let typeName = Introspection.sanitize iface.Name
        let t = ProvidedTypeDefinition(typeName, baseType = Some interfaceInfoTy, hideObjectMethods = true)
        t.AddMember(literalProp "Name" iface.Name)
        t.AddMember(literalProp "FullName" iface.Name)

        let container (cname: string) (members: Introspection.Member list) =
            let c = ProvidedTypeDefinition(cname, baseType = Some typeof<obj>, hideObjectMethods = true)
            for m in members do
                c.AddMember(buildMemberType m)
            c

        let methods    = iface.Members |> List.filter (fun m -> m.Kind = "method")
        let properties = iface.Members |> List.filter (fun m -> m.Kind = "property")
        let signals    = iface.Members |> List.filter (fun m -> m.Kind = "signal")

        t.AddMember(container "Methods" methods)
        t.AddMember(container "Properties" properties)
        t.AddMember(container "Signals" signals)
        t

    let resolvePath (xmlPath: string) =
        if Path.IsPathRooted xmlPath then xmlPath
        else Path.Combine(config.ResolutionFolder, xmlPath)

    let rootType = ProvidedTypeDefinition(asm, ns, "DBusIntrospection",
                                          baseType = Some typeof<obj>, hideObjectMethods = true)

    let buildInstance (typeName: string) (xmlPath: string) =
        let resolved = resolvePath xmlPath
        let provided = ProvidedTypeDefinition(asm, ns, typeName,
                                              baseType = Some typeof<obj>, hideObjectMethods = true)
        let ifaces = Introspection.parseFile resolved
        for iface in ifaces do
            provided.AddMember(buildInterfaceType iface)
        // Also expose the source path as a literal for diagnostics.
        provided.AddMember(literalProp "XmlPath" resolved)
        provided

    do
        rootType.DefineStaticParameters(
            parameters = [ ProvidedStaticParameter("XmlPath", typeof<string>) ],
            instantiationFunction =
                (fun typeName args ->
                    let xmlPath = args.[0] :?> string
                    buildInstance typeName xmlPath))

        this.AddNamespace(ns, [ rootType ])

[<assembly: TypeProviderAssembly>]
do ()
