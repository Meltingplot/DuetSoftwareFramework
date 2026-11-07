using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// A client in Patch mode keeps a copy of the object model of DCS by applying every patch to the first message
/// (Documentation/articles/ipc.md, "Subscribe"). ModelSubscription collects the changes in a patch while the client has not acknowledged the
/// last message yet (ModelSubscription.MachineModelPropertyChanged), so a slow client gets one patch with everything that changed meanwhile.
/// <see cref="IpcSubscription"/> covers a client that acknowledges every message right away
/// </summary>
[TestFixture]
public class IpcSubscriptionConsistency
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;
    private CommandConnection _command = null!;

    private const string ThreeAxes = """[{ "letter": "X", "homed": false, "machinePosition": 0, "userPosition": 0 }, { "letter": "Y", "homed": false, "machinePosition": 0, "userPosition": 0 }, { "letter": "Z", "homed": false, "machinePosition": 0, "userPosition": 0 }]""";

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(ipc: true);
        _log = IpcLog.Attach(_host);
        _command = new CommandConnection();
        await _command.ConnectAsync(_host.SocketPath);
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _command.Dispose();
        await _host.DisposeAsync();
    }

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public async Task RestoreModel()
    {
        _host.Firmware.ObjectModel.Set("move.axes", JsonNode.Parse(ThreeAxes));
        _host.Firmware.ObjectModel.Set("global", new JsonObject());
        _host.Firmware.ObjectModel.Set("fans", new JsonArray());
        _host.Firmware.ObjectModel.Set("tools", new JsonArray());
        _host.Firmware.ObjectModel.Set("state.status", "idle");
        _host.Firmware.ObjectModel.AutoStatus = true;
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Plugins.Clear();
            _host.Model.Job.Layers.Clear();
        }
        await _command.SyncObjectModelAsync();
        Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");
    }

    private static JsonArray Axes(params (char letter, bool homed)[] axes) =>
        [.. axes.Select(axis => (JsonNode)new JsonObject { ["letter"] = axis.letter.ToString(), ["homed"] = axis.homed, ["machinePosition"] = 0, ["userPosition"] = 0 })];

    /// <summary>
    /// Change RRF and SBC parts of the model, including the length of lists and the keys of dictionaries, and wait until DCS has them
    /// </summary>
    private async Task ChangeModelAsync(int round)
    {
        _host.Firmware.ObjectModel.AutoStatus = false;
        if (round == 0)
        {
            _host.Firmware.ObjectModel.Set("move.axes", Axes(('X', true), ('Y', false), ('Z', true), ('U', false), ('V', true)));
            _host.Firmware.ObjectModel.Set("global", JsonNode.Parse("{\"counter\":1,\"list\":[1,2,3],\"text\":\"a\"}"));
            _host.Firmware.ObjectModel.Set("state.status", "busy");
            using (await _host.Model.AccessReadWriteAsync())
            {
                Plugin plugin = new() { Id = "Consistency", Name = "Consistency", Pid = 1234, SbcPermissions = SbcPermissions.ObjectModelRead };
                plugin.Data["value"] = JsonSerializer.SerializeToElement(1);
                _host.Model.Plugins.Add(plugin.Id, plugin);
                _host.Model.Job.Layers.Add(new Layer { Duration = 10, Height = 0.2F });
                _host.Model.Job.Layers.Add(new Layer { Duration = 12, Height = 0.4F });
            }
        }
        else
        {
            _host.Firmware.ObjectModel.Set("move.axes", Axes(('X', false), ('Y', true)));
            _host.Firmware.ObjectModel.Set("global", JsonNode.Parse("{\"counter\":2,\"list\":[4]}"));
            _host.Firmware.ObjectModel.Set("state.status", "paused");
            using (await _host.Model.AccessReadWriteAsync())
            {
                _host.Model.Plugins["Consistency"].Data["value"] = JsonSerializer.SerializeToElement("two");
                _host.Model.Plugins["Consistency"].Pid = -1;
                _host.Model.Job.Layers.RemoveAt(0);
            }
        }
        await _command.SyncObjectModelAsync();
    }

    [Test]
    public async Task SlowClientGetsEverythingThatChangedMeanwhile()
    {
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, socketPath: _host.SocketPath);
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        // Nothing is acknowledged while the model changes twice, so the changes pile up in one patch
        await ChangeModelAsync(0);
        await ChangeModelAsync(1);
        await IpcSubscription.ExpectConsistentAsync(_host, subscription, model);
        Assert.That(model.Move.Axes.Select(axis => axis.Letter), Is.EqualTo(new[] { 'X', 'Y' }));
        Assert.That(model.Job.Layers, Has.Count.EqualTo(1));
        Assert.That(model.Plugins["Consistency"].Data["value"]!.Value.GetString(), Is.EqualTo("two"));
    }

    [Test]
    public async Task ClientThatKeepsUpStaysConsistent()
    {
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, socketPath: _host.SocketPath);
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await ChangeModelAsync(0);
        await IpcSubscription.ExpectConsistentAsync(_host, subscription, model);
        await ChangeModelAsync(1);
        await IpcSubscription.ExpectConsistentAsync(_host, subscription, model);
    }

    [Test]
    public async Task ListsWithGapsAndValueItemsStayConsistent()
    {
        // RRF reports unconfigured fans as null and tools carry lists of numbers. Changes of such list items are recorded with the whole list
        // shape (ModelSubscription.GetPathNode and MachineModelPropertyChanged add null, dictionaries or the values themselves for the other items)
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, socketPath: _host.SocketPath);
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        _host.Firmware.ObjectModel.Set("fans", JsonNode.Parse("""[{ "actualValue": 0.5, "name": "A", "requestedValue": 0.5, "rpm": -1 }, null, { "actualValue": 1, "name": "C", "requestedValue": 1, "rpm": 1200 }]"""));
        _host.Firmware.ObjectModel.Set("tools", JsonNode.Parse("""[{ "number": 0, "name": "T0", "active": [200], "standby": [150], "offsets": [0, 0, 0], "heaters": [1] }]"""));
        await _command.SyncObjectModelAsync();
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Fans, Has.Count.EqualTo(3), "DCS did not take the fans from the firmware");
        }
        await IpcSubscription.ExpectConsistentAsync(_host, subscription, model);

        // Several changes pile up before the client acknowledges again
        _host.Firmware.ObjectModel.Set("fans[2].actualValue", 0.25);
        _host.Firmware.ObjectModel.Set("tools[0].active", JsonNode.Parse("[210, 215]"));
        _host.Firmware.ObjectModel.Set("tools[0].offsets", JsonNode.Parse("[1, 2, 3]"));
        await _command.SyncObjectModelAsync();
        _host.Firmware.ObjectModel.Set("fans", JsonNode.Parse("""[null, { "actualValue": 0, "name": "B", "requestedValue": 0, "rpm": -1 }]"""));
        await _command.SyncObjectModelAsync();
        await IpcSubscription.ExpectConsistentAsync(_host, subscription, model);
        Assert.That(model.Fans[0], Is.Null);
        Assert.That(model.Tools[0]!.Active, Is.EqualTo(new[] { 210F, 215F }));
    }

    [Test]
    public async Task FilteredClientKeepsTheFilteredParts()
    {
        // The first message holds the filtered parts (Filter.GetFiltered) and changes outside the filters are dropped (ModelSubscription.CheckFilters).
        // A change of a whole list item matches a filter that goes deeper (Filter.PathMatches accepts a path that is a prefix of the filter)
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, ["move/axes[*]/homed", "global/**", "state/status"], _host.SocketPath);
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await ChangeModelAsync(0);
        await ChangeModelAsync(1);

        string Filtered(ObjectModel source) => JsonSerializer.Serialize(new
        {
            homed = source.Move.Axes.Select(axis => axis.Homed).ToArray(),
            global = source.Global.ToDictionary(item => item.Key, item => item.Value?.ToString()),
            status = source.State.Status
        });

        string expected;
        using (await _host.Model.AccessReadOnlyAsync())
        {
            expected = Filtered(_host.Model);
        }
        while (Filtered(model) != expected)
        {
            using JsonDocument patch = await subscription.GetObjectModelPatchAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(patch.RootElement.EnumerateObject().Select(property => property.Name), Is.SubsetOf(new[] { "move", "global", "state" }), patch.RootElement.ToString());
            model.UpdateFromJson(patch.RootElement, false);
        }

        // Parts outside the filters were never sent
        Assert.That(model.Plugins, Is.Empty);
        Assert.That(model.Job.Layers, Is.Empty);
    }

    [Test]
    public async Task JobLayersAreSentToCurrentClients()
    {
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, $"{{\"mode\":\"Subscribe\",\"version\":{Defaults.ProtocolVersion},\"subscriptionMode\":\"Patch\"}}");
        (await client.ReceiveAsync())!.Dispose();
        await AddLayerAsync();
        using JsonDocument patch = await ReceivePatchWithStatusAsync(client);
        Assert.That(patch.RootElement.TryGetProperty("job", out JsonElement job) && job.TryGetProperty("layers", out _), Is.True, patch.RootElement.ToString());
    }

    /// <summary>
    /// ModelSubscription leaves job.layers out of the patches for clients with API version 10 and older, because "the data type has changed and
    /// sending it to outdated clients would result in memory leaks" (ModelSubscription.cs:495-498, upstream since ea7e9939 "Towards 3.3-b2").
    /// It compares the name of the collection with nameof(Job.Layers), "Layers", but the observer names collections after their JSON name,
    /// "layers" (Observer/ModelObjectObserver.cs uses property.JsonName), and string.Equals is case-sensitive, so the check never applies
    /// </summary>
    [Test]
    [Explicit("Bug: job.layers is sent to subscribers with API version 10 and older because the collection name is compared case-sensitively")]
    public async Task JobLayersAreNotSentToOutdatedClients()
    {
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Subscribe\",\"version\":10,\"subscriptionMode\":\"Patch\"}");
        (await client.ReceiveAsync())!.Dispose();
        await AddLayerAsync();
        using JsonDocument patch = await ReceivePatchWithStatusAsync(client);
        Assert.That(patch.RootElement.TryGetProperty("job", out JsonElement job) && job.TryGetProperty("layers", out _), Is.False, patch.RootElement.ToString());
    }

    /// <summary>
    /// Add a job layer and change the status in the same update
    /// </summary>
    private async Task AddLayerAsync()
    {
        _host.Firmware.ObjectModel.AutoStatus = false;
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Job.Layers.Add(new Layer { Duration = 10, Height = 0.2F });
        }
        _host.Firmware.ObjectModel.Set("state.status", "busy");
        await _command.SyncObjectModelAsync();
    }

    /// <summary>
    /// Acknowledge and receive patches until one carries the status change of <see cref="AddLayerAsync"/>, which came after the layer
    /// </summary>
    private static async Task<JsonDocument> ReceivePatchWithStatusAsync(RawIpcClient client)
    {
        while (true)
        {
            await client.SendAsync("{\"command\":\"Acknowledge\"}");
            JsonDocument patch = await client.ReceiveAsync() ?? throw new AssertionException("Subscription closed");
            if (patch.RootElement.TryGetProperty("state", out JsonElement state) && state.TryGetProperty("status", out _))
            {
                return patch;
            }
            patch.Dispose();
        }
    }
}
