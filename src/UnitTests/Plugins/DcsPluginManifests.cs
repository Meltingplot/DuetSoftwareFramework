using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace UnitTests.Plugins;

using File = System.IO.File;

/// <summary>
/// Plugin manifests that DCS takes from the plugin directory: ReloadPlugin (DuetControlServer/Commands/Plugins/ReloadPlugin.cs) and the start of
/// PeriodicUpdateService (DuetControlServer/Model/PeriodicUpdateService.cs), which the fake firmware host does not start by itself. Both make the checks of
/// the installation (DuetSharedLibrary/PluginManifestCheck.cs)
/// </summary>
[TestFixture]
public class DcsPluginManifests : DpsTestBase
{
    private async Task ReloadAsync(string id)
    {
        DuetControlServer.Commands.ReloadPlugin command = Dcs.Services.GetRequiredService<DuetControlServer.Commands.CommandFactory>().Create<DuetControlServer.Commands.ReloadPlugin>();
        command.Plugin = id;
        await command.ExecuteAsync();
    }

    /// <summary>
    /// Load the manifests like DCS does at start, then stop the periodic updates again
    /// </summary>
    private async Task LoadAtStartAsync()
    {
        PeriodicUpdateService service = Dcs.Services.GetServices<IHostedService>().OfType<PeriodicUpdateService>().Single();
        using CancellationTokenSource cts = new(UnitTests.FakeFirmware.FakeFirmwareHost.DefaultTimeout);
        await service.StartAsync(cts.Token);
        await service.StopAsync(cts.Token);
    }

    private async Task<string[]> DcsPluginIdsAsync()
    {
        using (await Dcs.Model.AccessReadOnlyAsync())
        {
            return [.. Dcs.Model.Plugins.Keys.Order()];
        }
    }

    [TestCase("{\"name\":\"No id\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Invalid\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcPermissions\":[\"servicePlugins\"]}", "ServicePlugins permission is reserved for internal purposes")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcPermissions\":[\"superUser\"]}", "Plugins with super-user permissions are not allowed")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcExecutableArguments\":\"$(id)\"}", "SbcExecutableArguments must not contain shell metacharacters (\", $, `, \\, newline)")]
    public async Task ReloadRefusesInvalidManifests(string manifest, string error)
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Invalid.json"), manifest);
        ArgumentException e = (await Assert.ThrowsAsync<ArgumentException>(async () => await ReloadAsync("Invalid")))!;
        Assert.That(e.Message, Is.EqualTo(error));
        Assert.That(await DcsPluginIdsAsync(), Is.Empty, "An empty plugin was added to the object model");

        // A known plugin is left as it was
        await AddToDcsAsync(new Plugin { Id = "Invalid", Name = "Invalid", Version = "1.0", Pid = -1 });
        Assert.ThrowsAsync<ArgumentException>(async () => await ReloadAsync("Invalid"));
        Plugin plugin = (await GetFromDcsAsync("Invalid"))!;
        Assert.That((plugin.Name, plugin.Version), Is.EqualTo(("Invalid", "1.0")));
    }

    [TestCase("{\"name\":\"No id\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Invalid\"}", "Plugin manifest must provide an id and a name")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcPermissions\":[\"servicePlugins\"]}", "ServicePlugins permission is reserved for internal purposes")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcPermissions\":[\"superUser\"]}", "Plugins with super-user permissions are not allowed")]
    [TestCase("{\"id\":\"Invalid\",\"name\":\"Invalid\",\"sbcExecutableArguments\":\"$(id)\"}", "SbcExecutableArguments must not contain shell metacharacters (\", $, `, \\, newline)")]
    public async Task InvalidManifestsAreSkippedAtStart(string manifest, string error)
    {
        File.WriteAllText(Path.Combine(PluginDirectory, "Invalid.json"), manifest);
        File.WriteAllText(Path.Combine(PluginDirectory, "Valid.json"), "{\"id\":\"Valid\",\"name\":\"Valid\"}");
        await LoadAtStartAsync();
        Assert.That(await DcsPluginIdsAsync(), Is.EqualTo(new[] { "Valid" }));
        Assert.That(DcsLog.Entries.Any(entry => entry.Message == $"Skipping plugin manifest Invalid.json: {error}"), Is.True);
    }

    [Test]
    public async Task RootPluginsAreLoadedWithRootPluginSupport()
    {
        await EnableRootPluginSupportAsync();
        File.WriteAllText(Path.Combine(PluginDirectory, "Root.json"), "{\"id\":\"Root\",\"name\":\"Root\",\"sbcPermissions\":[\"superUser\"]}");
        await LoadAtStartAsync();
        Assert.That(await DcsPluginIdsAsync(), Is.EqualTo(new[] { "Root" }));

        // ReloadPlugin is forwarded to both plugin services
        File.WriteAllText(Path.Combine(PluginDirectory, "Reloaded.json"), "{\"id\":\"Reloaded\",\"name\":\"Reloaded\",\"sbcPermissions\":[\"superUser\"]}");
        await using (UnitTests.IPC.FakePluginService service = await UnitTests.IPC.FakePluginService.StartAsync(Dcs))
        await using (UnitTests.IPC.FakePluginService root = await UnitTests.IPC.FakePluginService.StartAsync(Dcs, asRoot: true))
        {
            await WaitForPluginsStartedAsync();
            await ReloadAsync("Reloaded");
        }
        Assert.That(await DcsPluginIdsAsync(), Is.EqualTo(new[] { "Reloaded", "Root" }));
    }
}
