using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;

namespace DuetSharedLibrary;

/// <summary>
/// Checks of a plugin manifest that DCS and DPS make wherever they take one in, from a plugin package or from the plugin directory
/// </summary>
public static class PluginManifestCheck
{
    /// <summary>
    /// Make sure a plugin manifest may be used
    /// </summary>
    /// <param name="manifest">Plugin manifest</param>
    /// <exception cref="ArgumentException">Manifest must not be used</exception>
    public static void Check(PluginManifest manifest)
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
    }
}
