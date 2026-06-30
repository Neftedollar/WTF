module WTF.TypeProviders.Tests

open Xunit
open WTF.TypeProviders

// THE PROOF: this type alias makes the F# compiler invoke the Type Provider's
// instantiation function with the literal XML path at COMPILE TIME. The provider
// reads /tests/WTF.TypeProviders.Tests/data/org.freedesktop.Accounts.xml,
// resolved against the project folder, and provides a nested type per interface.
// If this line compiles and the members below resolve, the TP worked.
type Accounts = DBusIntrospection<"data/org.freedesktop.Accounts.xml">

[<Fact>]
let ``interface name provided from xml`` () =
    Assert.Equal("org.freedesktop.Accounts", Accounts.OrgFreedesktopAccounts.Name)

[<Fact>]
let ``method names provided from xml`` () =
    Assert.Equal("CreateUser", Accounts.OrgFreedesktopAccounts.Methods.CreateUser.Name)
    Assert.Equal("FindUserById", Accounts.OrgFreedesktopAccounts.Methods.FindUserById.Name)

[<Fact>]
let ``method return types mapped from dbus signatures`` () =
    // FindUserById: in x (int64) -> out o (object path)
    Assert.Equal("ObjectPath", Accounts.OrgFreedesktopAccounts.Methods.FindUserById.DotNetType)
    // ListCachedUsers: out ao (object-path array)
    Assert.Equal("ObjectPath[]", Accounts.OrgFreedesktopAccounts.Methods.ListCachedUsers.DotNetType)

[<Fact>]
let ``property access and type provided`` () =
    Assert.Equal("read", Accounts.OrgFreedesktopAccounts.Properties.DaemonVersion.Access)
    Assert.Equal("System.String", Accounts.OrgFreedesktopAccounts.Properties.DaemonVersion.DotNetType)
    Assert.Equal("System.Boolean", Accounts.OrgFreedesktopAccounts.Properties.HasNoUsers.DotNetType)
    Assert.Equal("ObjectPath[]", Accounts.OrgFreedesktopAccounts.Properties.AutomaticLoginUsers.DotNetType)

[<Fact>]
let ``signal arg type provided`` () =
    Assert.Equal("UserAdded", Accounts.OrgFreedesktopAccounts.Signals.UserAdded.Name)
    Assert.Equal("ObjectPath", Accounts.OrgFreedesktopAccounts.Signals.UserAdded.DotNetType)
    Assert.Equal("ObjectPath", Accounts.OrgFreedesktopAccounts.Signals.UserDeleted.DotNetType)
