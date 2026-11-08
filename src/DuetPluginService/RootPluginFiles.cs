using DuetAPI.ObjectModel;
using DuetSharedLibrary;
using System;
using System.Collections.Generic;
using System.IO;

namespace DuetPluginService;

/// <summary>
/// Checks of the files of a plugin with super-user permissions. The plugin directory belongs to the dsf user, so the root service loads such a
/// plugin and starts it only from files that nobody but itself may change (<see cref="FileExtensions.IsProtected"/>). The root service installs
/// these files itself (InstallPlugin), and the sticky bit of the plugin directory keeps the dsf user from renaming them
/// </summary>
public static class RootPluginFiles
{
    /// <summary>
    /// Make sure the manifest of a plugin with super-user permissions may be loaded by the root service
    /// </summary>
    /// <param name="manifestFile">Path of the manifest</param>
    /// <param name="privileges">Privileges of the root service</param>
    /// <exception cref="ArgumentException">Others may change the manifest</exception>
    public static void CheckManifest(string manifestFile, ServicePrivileges privileges)
    {
        if (!FileExtensions.IsProtected(manifestFile, privileges.UserId))
        {
            throw new ArgumentException($"{manifestFile} may be changed by others than the root service, the plugin must be installed again");
        }
    }

    /// <summary>
    /// Make sure the files a plugin with super-user permissions is started from may be changed by the root service only: its manifest, every directory
    /// from its plugin directory down to the executable, the executable itself and, for a Python plugin, the directories of its interpreter
    /// </summary>
    /// <param name="plugin">Plugin to start</param>
    /// <param name="pluginDirectory">Directory holding the plugins</param>
    /// <param name="executable">Executable to start, inside the directory of the plugin</param>
    /// <param name="privileges">Privileges of the root service</param>
    /// <exception cref="ArgumentException">Others may change one of the files</exception>
    public static void CheckStart(Plugin plugin, string pluginDirectory, string executable, ServicePrivileges privileges)
    {
        string pluginBase = Path.GetFullPath(Path.Combine(pluginDirectory, plugin.Id));
        if (!Path.GetFullPath(executable).StartsWith(pluginBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Refusing to start plugin {plugin.Id}: executable {executable} is outside of {pluginBase}");
        }

        // Each entry on the way is checked itself, so a symlink is refused wherever it is
        List<string> paths = [Path.Combine(pluginDirectory, plugin.Id + ".json"), pluginBase];
        for (string path = Path.GetFullPath(executable); path != pluginBase; path = Path.GetDirectoryName(path)!)
        {
            paths.Add(path);
        }
        if (plugin.SbcPythonDependencies.Count > 0)
        {
            paths.Add(Path.Combine(pluginBase, "venv"));
            paths.Add(Path.Combine(pluginBase, "venv", "bin"));
        }

        foreach (string path in paths)
        {
            if (!FileExtensions.IsProtected(path, privileges.UserId))
            {
                throw new ArgumentException($"Refusing to start plugin {plugin.Id}: {path} may be changed by others than the root service");
            }
        }
    }
}
