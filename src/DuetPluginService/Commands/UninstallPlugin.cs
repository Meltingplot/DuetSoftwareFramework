using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using DuetPluginService.PermissionManagers;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.IPC;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UninstallPlugin"/> command
/// </summary>
/// <param name="permissionManager">Permission manager</param>
/// <param name="pluginStore">Plugin store</param>
/// <param name="privileges">Privileges of this service</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Application settings</param>
public sealed class UninstallPlugin(IPermissionManager permissionManager, PluginStore pluginStore, ServicePrivileges privileges, ILoggerFactory loggerFactory, IOptions<Settings> settings) : DuetAPI.Commands.UninstallPlugin
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Plugin is invalid</exception>
    [UnsupportedOSPlatform("windows")]
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ILogger logger = loggerFactory.CreateLogger($"Plugin {Plugin}");

        // Obtain virtual SD path
        string sdPath;
        using (CommandConnection connection = new())
        {
            await connection.ConnectAsync(_settings.SocketPath, cancellationToken);
            sdPath = await connection.ResolvePathAsync("0:/", cancellationToken);
        }

        // Get the plugin first
        Plugin? plugin = null;
        foreach (Plugin item in pluginStore.Plugins)
        {
            if (item.Id == Plugin)
            {
                plugin = item;
                break;
            }
        }

        if (plugin is null)
        {
            throw new ArgumentException($"Plugin {Plugin} not found by {(privileges.IsPrivileged ? "root service" : "service")}");
        }
        if (plugin.Pid > 0)
        {
            throw new ArgumentException("Plugin must be stopped before it can be uninstalled");
        }

        // Check if the manifest is writable. A manifest without write permission marks a plugin of a system package.
        // Both services check this before either of them deletes anything
        string manifestFile = Path.Combine(settings.Value.PluginDirectory, $"{Plugin}.json");
        if (File.Exists(manifestFile) && !File.GetUnixFileMode(manifestFile).HasFlag(UnixFileMode.UserWrite))
        {
            throw new ArgumentException("Plugin cannot be uninstalled via API");
        }

        // Each service removes what it has installed: the service that runs the plugin its manifest and its plugin directory,
        // the regular service the files on the virtual SD card and the links to the web files (see InstallPlugin)
        bool removesPluginDirectory = plugin.SbcPermissions.HasFlag(SbcPermissions.SuperUser) == privileges.IsPrivileged;
        bool removesSdFiles = !privileges.IsPrivileged;
        if (removesPluginDirectory || removesSdFiles)
        {
            if (ForUpgrade)
            {
                logger.LogInformation("Uninstalling plugin {Plugin} for upgrade", Plugin);
            }
            else
            {
                logger.LogInformation("Uninstalling plugin {Plugin}", Plugin);
            }

            // Remove the plugin manifest
            if (removesPluginDirectory && File.Exists(manifestFile))
            {
                logger.LogDebug("Removing plugin manifest");
                File.Delete(manifestFile);
            }

            // The file lists come from the manifest, so delete only files inside the directory that the installation put them into
            bool IsInside(string file, string directory)
            {
                if (Path.IsInside(file, directory))
                {
                    return true;
                }
                logger.LogWarning("Not deleting {File}, it is outside of {Directory}", file, directory);
                return false;
            }

            // Remove installed files and directories from the dwc and www directories
            string wwwDirectory = Path.Combine(sdPath, "www");
            foreach (string dwcFile in removesSdFiles ? plugin.DwcFiles : [])
            {
                string installWwwPath = Path.Combine(wwwDirectory, dwcFile);
                if (!IsInside(installWwwPath, wwwDirectory))
                {
                    continue;
                }

                if (Path.Exists(installWwwPath))
                {
                    logger.LogDebug("Removing {File}", installWwwPath);
                    File.Delete(installWwwPath);
                }

                string directory = Path.GetDirectoryName(installWwwPath)!;
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    logger.LogDebug("Removing {Directory}", directory);
                    Directory.Delete(directory);
                }
            }

            // Remove the files installed on the virtual SD card, sparing config files on an upgrade
            foreach (string sdFile in removesSdFiles ? plugin.SdFiles : [])
            {
                string fileName = Path.Combine(sdPath, sdFile);
                if (IsInside(fileName, sdPath) && File.Exists(fileName) && !(ForUpgrade && plugin.SbcConfigFiles.Any(file => fileName == Path.Combine(sdPath, "sys", file) || fileName == Path.Combine(sdPath, file))))
                {
                    if (Path.GetFileName(sdFile).Equals("daemon.g"))
                    {
                        // daemon.g may be still open at this time
                        logger.LogDebug("Renaming file {SourceFile} to {File}", fileName, fileName + ".bak");
                        File.Move(fileName, fileName + ".bak", true);
                    }
                    else
                    {
                        logger.LogDebug("Deleting file {File}", fileName);
                        File.Delete(fileName);
                    }
                }
            }

            string pluginBase = Path.Combine(settings.Value.PluginDirectory, Plugin);
            if (removesPluginDirectory && ForUpgrade)
            {
                // Remove only installed files so custom plugin files survive the upgrade
                foreach (string dsfFile in plugin.DsfFiles)
                {
                    string file = Path.Combine(pluginBase, "dsf", dsfFile);
                    if (IsInside(file, pluginBase) && File.Exists(file))
                    {
                        logger.LogDebug("Deleting file {File}", file);
                        File.Delete(file);
                    }
                }

                foreach (string dwcFile in plugin.DwcFiles)
                {
                    string file = Path.Combine(pluginBase, "dwc", dwcFile);
                    if (IsInside(file, pluginBase) && File.Exists(file))
                    {
                        logger.LogDebug("Deleting file {File}", file);
                        File.Delete(file);
                    }
                }
            }
            else if (removesPluginDirectory && Directory.Exists(pluginBase))
            {
                // Remove the full plugin directory
                logger.LogDebug("Removing plugin directory {Directory}", pluginBase);
                Directory.Delete(pluginBase, true);
            }
        }

        // Remove the security policy
        if (privileges.IsPrivileged && !_settings.DisableAppArmor)
        {
            await permissionManager.UninstallProfileAsync(plugin, cancellationToken);
        }

        // Plugin has been uninstalled
        using (await pluginStore.LockAsync(cancellationToken))
        {
            pluginStore.Plugins.Remove(plugin);
        }
        logger.LogInformation("Plugin uninstalled");
    }
}
