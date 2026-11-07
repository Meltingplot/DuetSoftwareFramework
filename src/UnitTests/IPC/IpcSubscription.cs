using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Tests of the Subscribe mode (DuetControlServer/IPC/Processors/ModelSubscription.cs)
/// </summary>
[TestFixture]
public class IpcSubscription
{
    private FakeFirmwareHost _host = null!;
    private IpcLog _log = null!;

    /// <summary>
    /// Time to wait for a patch that is not expected
    /// </summary>
    private static readonly TimeSpan NoPatchTimeout = TimeSpan.FromMilliseconds(300);

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _host = await FakeFirmwareHost.StartAsync(ipc: true);
        _log = IpcLog.Attach(_host);
    }

    [OneTimeTearDown]
    public async Task StopHost() => await _host.DisposeAsync();

    [SetUp]
    public void ClearLog() => _log.Clear();

    [TearDown]
    public void CheckLog() => Assert.That(_log.UnexpectedExceptions, Is.Empty, "The IPC server ran into an exception it did not expect");

    /// <summary>
    /// Serialize the object model of DCS
    /// </summary>
    public static async Task<JsonNode> GetDcsModelAsync(FakeFirmwareHost host)
    {
        using (await host.Model.AccessReadOnlyAsync())
        {
            return JsonSerializer.SerializeToNode(host.Model, ObjectModelContext.Default.ObjectModel)!;
        }
    }

    /// <summary>
    /// Serialize a client model like DCS serializes its own
    /// </summary>
    public static JsonNode Serialize(ObjectModel model) => JsonSerializer.SerializeToNode(model, ObjectModelContext.Default.ObjectModel)!;

    /// <summary>
    /// Remove the parts of a serialized model that are not kept in sync on purpose: messages are volatile, DCS clears them while
    /// clients are waiting for them (LockWrapper.Dispose)
    /// </summary>
    public static JsonNode WithoutVolatileParts(JsonNode model)
    {
        JsonNode copy = model.DeepClone();
        copy.AsObject().Remove("messages");
        return copy;
    }

    [Test]
    public async Task FullModeSendsTheModelAfterEachUpdate()
    {
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Full, socketPath: _host.SocketPath);
        Assert.That(subscription.Mode, Is.EqualTo(SubscriptionMode.Full));
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(model.Move.Axes, Has.Count.EqualTo(3));

        _host.Firmware.ObjectModel.Set("move.axes[0].homed", true);
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (!model.Move.Axes[0].Homed)
        {
            model = await subscription.GetObjectModelAsync(cts.Token);
        }
        _host.Firmware.ObjectModel.Set("move.axes[0].homed", false);

        // The serialized variant is the same JSON
        await using MemoryStream json = await subscription.GetSerializedObjectModelAsync(cts.Token);
        using JsonDocument document = await JsonDocument.ParseAsync(json);
        Assert.That(document.RootElement.GetProperty("move").GetProperty("axes").GetArrayLength(), Is.EqualTo(3));
    }

    [Test]
    public void FullModeSynchronously()
    {
        using SubscribeConnection subscription = new();
        subscription.Connect(SubscriptionMode.Full, socketPath: _host.SocketPath);
        ObjectModel model = subscription.GetObjectModel();
        Assert.That(model.Boards, Has.Count.EqualTo(1));
        using MemoryStream json = subscription.GetSerializedObjectModel();
        Assert.That(json.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task PatchesReproduceTheModel()
    {
        // In Patch mode the first message is the whole model and later ones carry only what changed, so a client that applies them keeps
        // a copy of the model of DCS (DSF wiki, API Overview: "only the changed object model fields are transferred when an update occurs")
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, socketPath: _host.SocketPath);
        ObjectModel model = await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        // Change RRF and SBC parts of the model, including list lengths
        _host.Firmware.ObjectModel.Set("move.axes[1].homed", true);
        _host.Firmware.ObjectModel.Set("state.status", "busy");
        _host.Firmware.ObjectModel.AutoStatus = false;
        _host.Firmware.ObjectModel.Set("global", JsonNode.Parse("{\"counter\":1,\"list\":[1,2,3]}"));
        using (CommandConnection command = new())
        {
            await command.ConnectAsync(_host.SocketPath);
            int session = await command.AddUserSessionAsync(AccessLevel.ReadWrite, SessionType.HTTP, "patch test");
            await command.SyncObjectModelAsync();
            await ExpectConsistentAsync(_host, subscription, model);

            await command.RemoveUserSessionAsync(session);
            _host.Firmware.ObjectModel.Set("global", JsonNode.Parse("{\"counter\":2,\"list\":[1]}"));
            _host.Firmware.ObjectModel.Set("move.axes[1].homed", false);
            _host.Firmware.ObjectModel.Set("state.status", "idle");
            await command.SyncObjectModelAsync();
            await ExpectConsistentAsync(_host, subscription, model);
        }
        _host.Firmware.ObjectModel.AutoStatus = true;
        _host.Firmware.ObjectModel.Set("global", new JsonObject());
    }

    /// <summary>
    /// Apply patches until the client model matches the model of DCS
    /// </summary>
    /// <param name="host">Host whose model the client copy must match</param>
    /// <param name="subscription">Subscription in Patch mode</param>
    /// <param name="model">Client copy of the model, built from the first message</param>
    /// <param name="patchTimeout">Time to wait for another patch while the models differ</param>
    public static async Task ExpectConsistentAsync(FakeFirmwareHost host, SubscribeConnection subscription, ObjectModel model, TimeSpan? patchTimeout = null)
    {
        while (true)
        {
            JsonNode expected = WithoutVolatileParts(await GetDcsModelAsync(host));
            JsonNode actual = WithoutVolatileParts(Serialize(model));
            if (JsonNode.DeepEquals(expected, actual))
            {
                return;
            }

            string difference = FirstDifference(expected, actual, string.Empty) ?? "(none)";
            JsonDocument patch;
            try
            {
                patch = await subscription.GetObjectModelPatchAsync().WaitAsync(patchTimeout ?? TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                throw new AssertionException($"No further patch arrived, but the client model differs from the model of DCS at {difference}");
            }
            using (patch)
            {
                model.UpdateFromJson(patch.RootElement, false);
            }
        }
    }

    /// <summary>
    /// Describe the first difference between two JSON values
    /// </summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (string key in expectedObject.Select(item => item.Key).Union(actualObject.Select(item => item.Key)))
            {
                string? difference = FirstDifference(expectedObject[key], actualObject[key], $"{path}/{key}");
                if (difference is not null)
                {
                    return difference;
                }
            }
            return null;
        }
        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                return $"{path}: {expectedArray.Count} items expected, got {actualArray.Count}";
            }
            for (int i = 0; i < expectedArray.Count; i++)
            {
                string? difference = FirstDifference(expectedArray[i], actualArray[i], $"{path}[{i}]");
                if (difference is not null)
                {
                    return difference;
                }
            }
            return null;
        }
        return JsonNode.DeepEquals(expected, actual) ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
    }

    [Test]
    public async Task FilteredPatchesContainOnlyMatchingPaths()
    {
        // ModelSubscription.CheckFilters drops changes outside the filters, the first message holds the filtered parts (Filter.GetFiltered)
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, ["move/axes[*]/homed", "global/**"], _host.SocketPath);
        Assert.That(subscription.Filters, Is.EqualTo(new[] { "move/axes[*]/homed", "global/**" }));
        await using (MemoryStream first = await subscription.GetSerializedObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout))
        {
            // GetSerializedObjectModelAsync acknowledges first, which is harmless here because the first message is already queued
            using JsonDocument document = await JsonDocument.ParseAsync(first);
            Assert.That(document.RootElement.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[] { "move", "global" }).Or.EquivalentTo(new[] { "move" }));
            JsonElement axes = document.RootElement.GetProperty("move").GetProperty("axes");
            Assert.That(axes.GetArrayLength(), Is.EqualTo(3));
            Assert.That(axes[0].EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "homed" }));
        }

        _host.Firmware.ObjectModel.Set("state.upTime", 1234);
        _host.Firmware.ObjectModel.Set("move.axes[2].homed", true);
        try
        {
            using JsonDocument patch = await subscription.GetObjectModelPatchAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
            Assert.That(patch.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "move" }), patch.RootElement.ToString());
            Assert.That(patch.RootElement.GetProperty("move").GetProperty("axes")[2].GetProperty("homed").GetBoolean(), Is.True);
        }
        finally
        {
            _host.Firmware.ObjectModel.Set("move.axes[2].homed", false);
        }
    }

    [Test]
    public async Task TargetedMessagesReachSubscribersOfTheChannel()
    {
        // LinkService passes messages for a single channel to ModelSubscription.RecordMessage, which adds them to the patch of
        // subscribers of that channel
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, CodeChannel.HTTP, ["messages"], _host.SocketPath);
        Assert.That(subscription.Channel, Is.EqualTo(CodeChannel.HTTP));
        await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        _host.Firmware.SendMessage(MessageTypeFlags.HttpMessage | MessageTypeFlags.ErrorMessageFlag, "only for HTTP");
        using JsonDocument patch = await subscription.GetObjectModelPatchAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        JsonElement message = patch.RootElement.GetProperty("messages")[0];
        Assert.That(message.GetProperty("content").GetString(), Is.EqualTo("only for HTTP"));
        Assert.That(message.GetProperty("type").GetInt32(), Is.EqualTo((int)MessageType.Error));
    }

    /// <summary>
    /// SubscribeInitMessage.Channel is the "optional code channel to receive messages from". ModelSubscription.RecordMessage hands messages
    /// for that channel to MachineModelPropertyChanged, which drops every message for a channel that is not one of the generic message
    /// channels ("This message isn't meant for this subscriber, skip it"), so subscribers of SBC, Daemon, Trigger etc. never get them
    /// </summary>
    [Test]
    [Explicit("Bug: messages for a subscriber's channel are dropped unless the channel is one of the generic message channels")]
    public async Task TargetedMessagesReachSubscribersOfOtherChannels()
    {
        using SubscribeConnection subscription = new();
        await subscription.ConnectAsync(SubscriptionMode.Patch, CodeChannel.SBC, ["messages"], _host.SocketPath);
        await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        _host.Firmware.SendMessage(MessageTypeFlags.SbcMessage | MessageTypeFlags.ErrorMessageFlag, "only for SBC");
        using JsonDocument patch = await subscription.GetObjectModelPatchAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        JsonElement message = patch.RootElement.GetProperty("messages")[0];
        Assert.That(message.GetProperty("content").GetString(), Is.EqualTo("only for SBC"));
        Assert.That(message.GetProperty("type").GetInt32(), Is.EqualTo((int)MessageType.Error));
    }

    [Test]
    public async Task GenericMessagesReachSubscribersOfGenericChannels()
    {
        // Generic messages go into the object model; a subscriber of a channel gets them only if that channel receives generic messages
        // (ModelSubscription.MachineModelPropertyChanged, MessageTypeFlags.GenericMessage)
        using SubscribeConnection generic = new();
        await generic.ConnectAsync(SubscriptionMode.Patch, CodeChannel.HTTP, ["messages"], _host.SocketPath);
        await generic.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        using SubscribeConnection sbc = new();
        await sbc.ConnectAsync(SubscriptionMode.Patch, CodeChannel.SBC, ["messages"], _host.SocketPath);
        await sbc.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(ModelSubscription.HasClientsWaitingForMessages, Is.True);

        _host.Firmware.SendMessage(MessageTypeFlags.GenericMessage, "for everyone");
        using JsonDocument patch = await generic.GetObjectModelPatchAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(patch.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), Is.EqualTo("for everyone"));

        // The SBC channel is not one of the generic ones, so it does not get the message
        await Assert.ThrowsAsync<TimeoutException>(async () => await sbc.GetObjectModelPatchAsync().WaitAsync(NoPatchTimeout));
    }

    [Test]
    public async Task VerboseAndObsoleteSubscribersAreCounted()
    {
        // UpdateService asks RRF for verbose and obsolete fields only while a subscriber needs them (ModelSubscription.VerboseSubscribers)
        Assert.That(ModelSubscription.VerboseSubscribers, Is.False);
        Assert.That(ModelSubscription.ObsoleteSubscribers, Is.False);
        using (SubscribeConnection subscription = new())
        {
            await subscription.ConnectAsync(SubscriptionMode.Patch, ["state/status"], _host.SocketPath, verbose: true, obsolete: true);
            Assert.That(subscription.Verbose, Is.True);
            Assert.That(subscription.Obsolete, Is.True);
            await subscription.GetObjectModelAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
            Assert.That(ModelSubscription.VerboseSubscribers, Is.True);
            Assert.That(ModelSubscription.ObsoleteSubscribers, Is.True);
        }

        // The counters go down when the processor ends, which it notices with the next acknowledgement or update
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (ModelSubscription.VerboseSubscribers || ModelSubscription.ObsoleteSubscribers)
        {
            _host.Firmware.ObjectModel.Set("state.upTime", Environment.TickCount);
            await Task.Delay(20, cts.Token);
        }
    }

    [Test]
    public async Task OtherCommandsEndTheSubscription()
    {
        // "Any other command will create an ArgumentException" (DSF wiki, API Overview). ModelSubscription.ProcessAsync does not catch it,
        // so the server closes the connection
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"filters\":[\"state/status\"]}");
        using (JsonDocument first = await client.ReceiveAsync() ?? throw new AssertionException("No model"))
        {
            Assert.That(first.RootElement.GetProperty("state").GetProperty("status").GetString(), Is.Not.Empty);
        }
        await client.SendAsync("{\"command\":\"GetObjectModel\"}");
        (var messages, bool closed) = await client.ReceiveUntilClosedAsync(TimeSpan.FromSeconds(5));
        Assert.That(messages, Is.Empty);
        Assert.That(closed, Is.True);
        Assert.That(_log.UnexpectedExceptions.Single().Exception, Is.TypeOf<ArgumentException>());
        _log.Clear();
    }

    [Test]
    public async Task ClientClosesWhileWaitingForUpdates()
    {
        // A closed connection is not an error (ModelSubscription.ProcessAsync swallows the SocketException)
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!, "{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Full\"}"))
        {
            using JsonDocument model = await client.ReceiveAsync() ?? throw new AssertionException("No model");
            await client.SendAsync("{\"command\":\"Acknowledge\"}");
        }
        _host.Firmware.ObjectModel.Set("state.upTime", 42);
        using CommandConnection command = new();
        await command.ConnectAsync(_host.SocketPath);
        await command.SyncObjectModelAsync();
    }

    [Test]
    public async Task InvalidFiltersAreRefused()
    {
        // Filter.ConvertFilter parses indices with int.Parse, which throws in the ModelSubscription constructor
        using RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!);
        await client.SendAsync("{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"filters\":[\"heat/heaters[1,2]\"]}");
        (var messages, bool closed) = await client.ReceiveUntilClosedAsync(TimeSpan.FromSeconds(5));
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(RawIpcClient.GetErrorType(messages[0].RootElement), Is.EqualTo(nameof(FormatException)));
        Assert.That(closed, Is.True);
    }

    [Test]
    [Explicit("Bug: a subscription stays registered if the success response cannot be sent, because only ProcessAsync unregisters it")]
    public async Task SubscriptionIsUnregisteredWhenTheClientIsGoneBeforeTheResponse()
    {
        // The ModelSubscription constructor registers the subscription and counts it as verbose; ProcessAsync undoes that when it ends.
        // Server.ProcessConnectionAsync sends the success response between the two, so if that fails, ProcessAsync never runs
        Assume.That(ModelSubscription.VerboseSubscribers, Is.False);
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(_host.SocketPath!))
        {
            // A peer that does not read any more makes the next send of the server fail with EPIPE
            client.Socket.Shutdown(SocketShutdown.Receive);
            await client.SendAsync("{\"mode\":\"Subscribe\",\"version\":13,\"subscriptionMode\":\"Patch\",\"verbose\":true}");

            // Wait for the server to close its end, our sends fail then
            using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
            try
            {
                while (true)
                {
                    await client.SendAsync(" ");
                    await Task.Delay(10, cts.Token);
                }
            }
            catch (SocketException)
            {
                // Server closed the connection
            }
        }
        Assert.That(ModelSubscription.VerboseSubscribers, Is.False);
    }
}
