using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;

namespace DuetSharedLibrary;

/// <summary>
/// Checks of a plugin manifest that DCS and DPS make wherever they take one in, from a plugin package or from the plugin directory,
/// and again before a plugin is started
/// </summary>
public static class PluginManifestCheck
{
    /// <summary>
    /// User ID of root, which owns the files of plugins that system packages install
    /// </summary>
    public const int RootUserId = 0;

    /// <summary>
    /// Check if a plugin manifest was installed by a system package: only its owner can change it (<see cref="FileExtensions.IsProtected"/>)
    /// and it has no write permission at all, which marks a plugin that cannot be uninstalled via API (DuetPluginService UninstallPlugin)
    /// </summary>
    /// <param name="manifestFile">Path of the manifest</param>
    /// <param name="owner">User ID that installs system packages, i.e. root</param>
    /// <returns>Whether the manifest is the one of a system package</returns>
    /// <remarks>
    /// A plugin with super-user permissions from a system package may run without root plugin support, because the package was installed by root
    /// </remarks>
    public static bool IsInstalledByPackage(string manifestFile, int owner) =>
        FileExtensions.IsProtected(manifestFile, owner) && (File.GetUnixFileMode(manifestFile) & UnixFileMode.UserWrite) == 0;

    /// <summary>
    /// Make sure a plugin manifest may be used
    /// </summary>
    /// <param name="manifest">Plugin manifest</param>
    /// <param name="rootPluginSupport">
    /// Whether plugins with super-user permissions are allowed (RootPluginSupport of DCS or a manifest of a system package, see <see cref="IsInstalledByPackage"/>)
    /// or null if the caller does not know it. DPS acts on a plugin only when DCS tells it to, and the root service checks it again before it starts a plugin
    /// </param>
    /// <exception cref="ArgumentException">Manifest must not be used</exception>
    public static void Check(PluginManifest manifest, bool? rootPluginSupport)
    {
        // Make sure the required fields are present. The property setters only validate the content,
        // a missing manifest key leaves the default value behind
        if (string.IsNullOrEmpty(manifest.Id) || string.IsNullOrEmpty(manifest.Name))
        {
            throw new ArgumentException("Plugin manifest must provide an id and a name");
        }

        // Check for reserved permissions
        if (manifest.SbcPermissions.HasFlag(SbcPermissions.ServicePlugins))
        {
            throw new ArgumentException("ServicePlugins permission is reserved for internal purposes");
        }

        // Permit root plugins only if they're enabled
        if (manifest.SbcPermissions.HasFlag(SbcPermissions.SuperUser) && rootPluginSupport == false)
        {
            throw new ArgumentException("Plugins with super-user permissions are not allowed");
        }

        // DPS passes the arguments in a command line (Arguments or PythonLaunchArguments), which may be configured to go through a shell.
        // Reject any char that could break out of its quoting
        if (!string.IsNullOrEmpty(manifest.SbcExecutableArguments) &&
            manifest.SbcExecutableArguments.AsSpan().IndexOfAny(['"', '$', '`', '\\', '\n', '\r']) >= 0)
        {
            throw new ArgumentException("SbcExecutableArguments must not contain shell metacharacters (\", $, `, \\, newline)");
        }
    }
}
