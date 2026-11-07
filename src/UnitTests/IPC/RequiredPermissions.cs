using System;
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
    /// A connection that has none of the required flags must be refused. Check iterates over Enum.GetValues, which starts with
    /// SbcPermissions.None, and Enum.HasFlag(0) is true for every value ("If the underlying value of flag is zero, the method returns true",
    /// .NET documentation of Enum.HasFlag), so the first iteration grants everything
    /// </summary>
    [Test]
    [Explicit("Bug: RequiredPermissionsAttribute.Check grants every permission because Enum.HasFlag(SbcPermissions.None) is always true")]
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
}
