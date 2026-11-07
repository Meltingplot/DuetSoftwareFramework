using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// With the trace log level (DuetControlServer -l trace) the IPC server logs every message it receives and sends (Connection.cs, the
/// LogTrace calls behind logger.IsEnabled(LogLevel.Trace)), which is how the IPC traffic of a printer is debugged. The traffic must
/// still work and be logged as JSON
/// </summary>
[TestFixture]
public class IpcTraceLog
{
    [Test]
    public async Task TrafficIsLoggedAtTraceLevel()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(firmware => firmware.OnCode("M7801", _ => FakeReply.Ok("traced")), settings =>
        {
            settings.PluginDirectory = System.IO.Path.Combine(settings.BaseDirectory, "plugins");
            settings.PluginsFilename = System.IO.Path.Combine(settings.BaseDirectory, "plugins.txt");
        }, ipc: true, minimumLogLevel: LogLevel.Trace);
        IpcLog log = IpcLog.Attach(host, LogLevel.Trace);
        string[] Traces(int id) => [.. log.Entries.Where(entry => entry.Level == LogLevel.Trace && entry.Message.StartsWith($"IPC#{id}: ")).Select(entry => entry.Message)];

        // Init messages, commands, raw JSON results and errors
        using (RawIpcClient client = await RawIpcClient.ConnectAsync(host.SocketPath!, "{\"mode\":\"Command\",\"version\":13}"))
        {
            (await client.RequestAsync("{\"command\":\"GetObjectModel\",\"filters\":[\"state/status\"]}")).Dispose();
            (await client.RequestAsync("{\"command\":\"NoSuchCommand\"}")).Dispose();
            string[] traces = Traces(client.Id);
            Assert.That(traces, Has.Some.StartsWith($"IPC#{client.Id}: Sending {{\"version\":"), "Server init message");
            Assert.That(traces, Has.Some.EqualTo($"IPC#{client.Id}: Received {{\"mode\":\"Command\",\"version\":13}}"), "Client init message");
            Assert.That(traces, Has.Some.EqualTo($"IPC#{client.Id}: Received {{\"command\":\"GetObjectModel\",\"filters\":[\"state/status\"]}}"), "Command");
            // A filtered GetObjectModel returns a whole ObjectModel that holds only the filtered values (GetObjectModel.ExecuteRawJsonAsync)
            Assert.That(traces, Has.Some.StartsWith($"IPC#{client.Id}: Sending {{\"success\":true,\"result\":{{").And.Contains("\"status\":\"idle\""), "Raw JSON result");
            Assert.That(traces, Has.Some.StartsWith($"IPC#{client.Id}: Sending {{\"success\":false,\"errorType\":\"ArgumentException\""), "Error response");
        }

        // Codes passed to an interceptor
        using (InterceptConnection interceptor = new())
        {
            await interceptor.ConnectAsync(InterceptionMode.Pre, [CodeChannel.SBC], ["M7801"], false, host.SocketPath);
            Task<string> reply = host.StartCode("M7801", CodeChannel.SBC);
            await interceptor.ReceiveCodeAsync().AsTask().WaitAsync(FakeFirmwareHost.DefaultTimeout);
            await interceptor.IgnoreCodeAsync();
            Assert.That(await reply.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("traced\n"));
            Assert.That(Traces(interceptor.Id), Has.Some.StartsWith($"IPC#{interceptor.Id}: Sending {{\"command\":\"Code\""));
        }

        // Responses of the plugin service
        using CommandConnection external = new();
        await external.ConnectAsync(host.SocketPath);
        using (await host.Model.AccessReadWriteAsync())
        {
            host.Model.Plugins.Add("Traced", new Plugin { Id = "Traced", Name = "Traced", SbcExecutable = "plugin" });
        }
        await using FakePluginService service = await FakePluginService.StartAsync(host);
        service.Responder = command => Task.FromResult(command.GetProperty("command").GetString() == "ResolvePluginProcess" ? "{\"success\":true,\"result\":\"Traced\"}" : "{\"success\":true}");
        await external.StartPluginAsync("Traced");
        await external.NotifyPluginStartedAsync();
        Assert.That(Traces(service.Connection.Id), Has.Some.StartsWith($"IPC#{service.Connection.Id}: Sending {{\"command\":\"StartPlugin\""));
        Assert.That(Traces(service.Connection.Id), Has.Some.EqualTo($"IPC#{service.Connection.Id}: Received {{\"success\":true}}"));
        Assert.That(Traces(service.Connection.Id), Has.Some.EqualTo($"IPC#{service.Connection.Id}: Received {{\"success\":true,\"result\":\"Traced\"}}"));
        Assert.That(log.UnexpectedExceptions, Is.Empty);
    }
}
