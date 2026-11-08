using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.ReloadPlugin"/> command
/// </summary>
/// <param name="pluginStore">Plugin store</param>
/// <param name="privileges">Privileges of this service</param>
/// <param name="settings">Application settings</param>
public sealed class ReloadPlugin(PluginStore pluginStore, ServicePrivileges privileges, IOptions<Settings> settings) : DuetAPI.Commands.ReloadPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Stop a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await pluginStore.LockAsync(cancellationToken))
        {
            // Try to find the plugin first
            Plugin? plugin = null;
            foreach (Plugin item in pluginStore.Plugins)
            {
                if (item.Id == Plugin)
                {
                    plugin = item;
                    break;
                }
            }

            // Update the plugin manifest
            string file = Path.Combine(settings.Value.PluginDirectory, Plugin + ".json");
            if (File.Exists(file))
            {
                await using FileStream manifestStream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using JsonDocument manifestJson = await JsonDocument.ParseAsync(manifestStream);

                // Check the manifest like an installation does before anything is changed
                Plugin manifest = new();
                manifest.UpdateFromJson(manifestJson.RootElement, false);
                PluginManifestCheck.Check(manifest, null);
                if (privileges.IsPrivileged && manifest.SbcPermissions.HasFlag(SbcPermissions.SuperUser))
                {
                    RootPluginFiles.CheckManifest(file, privileges);
                }

                if (plugin is null)
                {
                    plugin = new();
                    pluginStore.Plugins.Add(plugin);
                }
                plugin.UpdateFromJson(manifestJson.RootElement, false);
                plugin.Pid = -1;
            }
            else
            {
                if (plugin is null)
                {
                    // Don't attempt to remove a non-existent plugin
                    return;
                }
                pluginStore.Plugins.Remove(plugin);
            }
        }
    }
}
