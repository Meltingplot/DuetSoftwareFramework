using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.IPC;

/// <summary>
/// Tests of <see cref="RequiredPermissionsAttribute"/>, which the IPC server uses to decide whether a connection may run a command
/// (DuetControlServer/IPC/Connection.cs, CheckPermissions and CheckCommandPermissions)
/// </summary>
[TestFixture]
public class RequiredPermissions
{
    [Test]
    public void GrantedByAnyRequiredFlag()
    {
        // The required flags are alternatives: one of them is enough (RequiredPermissionsAttribute.Check returns on the first match)
        RequiredPermissionsAttribute attribute = new(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite);
        Assert.That(attribute.Check(SbcPermissions.ObjectModelRead), Is.True);
        Assert.That(attribute.Check(SbcPermissions.ObjectModelReadWrite), Is.True);
        Assert.That(attribute.Check(SbcPermissions.ObjectModelRead | SbcPermissions.CommandExecution), Is.True);
    }

    /// <summary>
    /// A connection that has none of the required flags must be refused. Check used to iterate over Enum.GetValues, which starts with
    /// SbcPermissions.None, and Enum.HasFlag(0) is true for every value ("If the underlying value of flag is zero, the method returns true",
    /// .NET documentation of Enum.HasFlag), so the first iteration granted everything
    /// </summary>
    [Test]
    public void RefusedWithoutRequiredFlag()
    {
        Assert.That(new RequiredPermissionsAttribute(SbcPermissions.SuperUser).Check(SbcPermissions.None), Is.False);
        Assert.That(new RequiredPermissionsAttribute(SbcPermissions.ServicePlugins).Check(SbcPermissions.ObjectModelRead), Is.False);
        Assert.That(new RequiredPermissionsAttribute(SbcPermissions.CommandExecution).Check(SbcPermissions.ObjectModelRead | SbcPermissions.ManagePlugins), Is.False);
    }

    [Test]
    public void EveryClientCommandRequiresPermissions()
    {
        // The IPC server skips the check for a command without the attribute (Connection.CheckPermissions), so every command
        // a client can send must carry one
        Type[] commands = [.. typeof(BaseCommand).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(BaseCommand)) && !type.IsAbstract && type.Namespace == "DuetAPI.Commands")];
        Assert.That(commands, Is.Not.Empty);
        foreach (Type command in commands)
        {
            if (command == typeof(NotifyPluginStarted))
            {
                // Any plugin may report that it has started; DCS lets only the plugin itself or one with ManagePlugins do it
                // (DuetControlServer/Commands/Plugins/NotifyPluginStarted.cs)
                continue;
            }
            Assert.That(command.GetCustomAttribute<RequiredPermissionsAttribute>(), Is.Not.Null, $"{command.Name} has no RequiredPermissions attribute");
        }
    }

    /// <summary>
    /// Permissions of which a connection needs at least one to run each command (the RequiredPermissions attributes in DuetAPI/Commands,
    /// PLUGINS.md "Required Permissions for each Command"). A plugin gets exactly the permissions of its manifest, so this is what a manifest
    /// has to list. A command added or changed upstream makes this test fail, so that the table can be checked again
    /// </summary>
    private static readonly Dictionary<Type, SbcPermissions> _requiredPermissions = new()
    {
        // Code and CodeStream
        [typeof(Code)] = SbcPermissions.CommandExecution,

        // Intercept
        [typeof(Cancel)] = SbcPermissions.CodeInterceptionReadWrite,
        [typeof(Ignore)] = SbcPermissions.CodeInterceptionRead | SbcPermissions.CodeInterceptionReadWrite,
        [typeof(Resolve)] = SbcPermissions.CodeInterceptionReadWrite,
        [typeof(Rewrite)] = SbcPermissions.CodeInterceptionReadWrite,

        // Files
        [typeof(GetFileInfo)] = SbcPermissions.CommandExecution | SbcPermissions.FileSystemAccess | SbcPermissions.ReadGCodes,
        [typeof(ResolvePath)] = SbcPermissions.CommandExecution | SbcPermissions.FileSystemAccess,

        // Generic
        [typeof(CheckPassword)] = SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite,
        [typeof(EvaluateExpression)] = SbcPermissions.CommandExecution,
        [typeof(Flush)] = SbcPermissions.CommandExecution,
        [typeof(InvalidateChannel)] = SbcPermissions.CodeInterceptionReadWrite,
        [typeof(SetUpdateStatus)] = SbcPermissions.ObjectModelReadWrite,
        [typeof(SimpleCode)] = SbcPermissions.CommandExecution,
        [typeof(WriteMessage)] = SbcPermissions.CommandExecution | SbcPermissions.ObjectModelReadWrite,

        // HTTP endpoints
        [typeof(AddHttpEndpoint)] = SbcPermissions.RegisterHttpEndpoints,
        [typeof(RemoveHttpEndpoint)] = SbcPermissions.RegisterHttpEndpoints,

        // Subscribe
        [typeof(Acknowledge)] = SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite,

        // Object model
        [typeof(GetObjectModel)] = SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite,
        [typeof(PatchObjectModel)] = SbcPermissions.ObjectModelReadWrite,
        [typeof(QueryObjectModel)] = SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite,
        [typeof(SetNetworkProtocol)] = SbcPermissions.SuperUser,
        [typeof(SetWifiCountry)] = SbcPermissions.ObjectModelReadWrite,
        [typeof(SyncObjectModel)] = SbcPermissions.CommandExecution | SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite,

        // System packages. ServicePlugins lets the DSF services run them without SuperUser (see DsfServicesAreRefusedOnlySetNetworkProtocol)
        [typeof(InstallSystemPackage)] = SbcPermissions.SuperUser | SbcPermissions.ServicePlugins,
        [typeof(UninstallSystemPackage)] = SbcPermissions.SuperUser | SbcPermissions.ServicePlugins,

        // Plugins. NotifyPluginStarted has no attribute (see EveryClientCommandRequiresPermissions), SetPluginData needs ManagePlugins in
        // addition for the data of another plugin (DuetControlServer/Commands/Plugins/SetPluginData.cs)
        [typeof(InstallPlugin)] = SbcPermissions.ManagePlugins,
        [typeof(NotifyPluginStarted)] = SbcPermissions.None,
        [typeof(ReloadPlugin)] = SbcPermissions.ManagePlugins,
        [typeof(ResolvePluginProcess)] = SbcPermissions.ServicePlugins,
        [typeof(SetPluginData)] = SbcPermissions.ObjectModelReadWrite,
        [typeof(SetPluginProcess)] = SbcPermissions.ServicePlugins,
        [typeof(StartPlugin)] = SbcPermissions.ManagePlugins,
        [typeof(StartPlugins)] = SbcPermissions.ManagePlugins,
        [typeof(StopPlugin)] = SbcPermissions.ManagePlugins,
        [typeof(StopPlugins)] = SbcPermissions.ManagePlugins,
        [typeof(UninstallPlugin)] = SbcPermissions.ManagePlugins,

        // User sessions
        [typeof(AddUserSession)] = SbcPermissions.ManageUserSessions,
        [typeof(RemoveUserSession)] = SbcPermissions.ManageUserSessions
    };

    [Test]
    public void EveryCommandRequiresTheDocumentedPermissions()
    {
        // Each listed permission grants the command on its own, every other permission does not
        Type[] commands = [.. typeof(BaseCommand).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(BaseCommand)) && !type.IsAbstract && type.Namespace == "DuetAPI.Commands")];
        Assert.That(commands, Is.EquivalentTo(_requiredPermissions.Keys), "The table does not list the same commands as DuetAPI");
        foreach (Type command in commands)
        {
            RequiredPermissionsAttribute? attribute = command.GetCustomAttribute<RequiredPermissionsAttribute>();
            SbcPermissions granting = Enum.GetValues<SbcPermissions>().Where(permission => attribute?.Check(permission) == true).Aggregate(SbcPermissions.None, (granted, permission) => granted | permission);
            Assert.That(granting, Is.EqualTo(_requiredPermissions[command]), command.Name);
        }
    }

    [Test]
    public void ConnectionsWithEveryPermissionMayRunEveryCommand()
    {
        // Root processes (DuetControlServer/IPC/Connection.cs, AssignPermissionsAsync: SuperUser, ServicePlugins and GrantExternalPermissions)
        // hold every permission, so the check refuses them nothing. A connection without permissions is refused every command
        SbcPermissions all = Enum.GetValues<SbcPermissions>().Aggregate(SbcPermissions.None, (granted, permission) => granted | permission);
        foreach (Type command in _requiredPermissions.Keys)
        {
            RequiredPermissionsAttribute? attribute = command.GetCustomAttribute<RequiredPermissionsAttribute>();
            if (attribute is not null)
            {
                Assert.That(attribute.Check(all), Is.True, command.Name);
                Assert.That(attribute.Check(SbcPermissions.None), Is.False, command.Name);
            }
        }
    }

    [Test]
    public void DsfServicesAreRefusedOnlySetNetworkProtocol()
    {
        // The DSF services DWS, DPS and DCS get ServicePlugins and GrantExternalPermissions, but not SuperUser (DuetControlServer/IPC/Connection.cs,
        // AssignPermissionsAsync after IsDsfService). DWS still installs and uninstalls system packages for PUT and DELETE /machine/systemPackage,
        // which DWC uses for dsf-update*.zip. SetNetworkProtocol is sent only by DuetPiManagementPlugin, a superUser plugin that runs as root
        // (pkg/common/duetpimanagementplugin/opt/dsf/plugins/DuetPiManagementPlugin.json)
        SbcPermissions dsfService = Enum.GetValues<SbcPermissions>().Aggregate(SbcPermissions.None, (granted, permission) => granted | permission) & ~SbcPermissions.SuperUser;
        string[] refused = [.. _requiredPermissions.Keys
            .Where(command => command.GetCustomAttribute<RequiredPermissionsAttribute>()?.Check(dsfService) == false)
            .Select(command => command.Name).Order()];
        Assert.That(refused, Is.EqualTo(new[] { nameof(SetNetworkProtocol) }));
    }
}
