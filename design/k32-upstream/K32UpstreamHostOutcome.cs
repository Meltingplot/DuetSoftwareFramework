using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Channel;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.SPI;

/// <summary>
/// K32-UPSTREAM: runs against upstream code only, see README.md next to this file. It is not part of the fork's unit tests. Same scenario as EmergencyStopCrossingMacroEnd.EmergencyStopWhileTheClosalOfAMacroIsPending,
/// but DCS runs in a real generic host built like upstream's Program.cs (Host.CreateDefaultBuilder, default HostOptions), so the test shows what
/// the host does once the transfer thread has ended: the reply of M112, whether the stop ever left DSF, the exception of the transfer thread, and
/// whether the host stops. PeriodicUpdateService is not started, as in FakeFirmwareHost
/// </summary>
[TestFixture]
public class K32UpstreamHostOutcome
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private static bool IsClosal(FakePacket packet) => packet.Request == FirmwareRequest.MacroFileClosed && packet.Channel == CodeChannel.HTTP;

    private static int StackDepth(Processor channel)
    {
        using (channel.Lock())
        {
            return channel.Stack.Count;
        }
    }

    private static void StepUntil(FakeFirmware.FakeFirmware firmware, Func<bool> condition, string what)
    {
        firmware.HoldTransfers();
        Assert.That(firmware.WaitUntil(f => f.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
        for (int i = 0; i < 200; i++)
        {
            int transfer = firmware.TransferCount;
            firmware.HoldTransfers(1);
            Assert.That(firmware.WaitUntil(f => f.TransferCount > transfer && f.IsTransferHeld, Timeout), Is.True, "The transfer was not held");
            if (condition())
            {
                return;
            }
        }
        Assert.Fail(what);
    }

    private sealed class NoHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider(Stopwatch clock) : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    provider.Lines.Enqueue($"+{provider._clock.ElapsedMilliseconds,5} ms [{logLevel}] {category}: {formatter(state, exception)}" + ((exception is not null) ? $" | {exception.GetType().FullName}: {exception.Message}" : string.Empty));
                }
            }
        }

        private readonly Stopwatch _clock = clock;
    }

    [Test]
    public async Task M112OutcomeInGenericHostWhenTheClosalOfAMacroIsPending()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-k32-upstream-" + Guid.NewGuid().ToString("N"));
        foreach (string directory in new[] { "sys", "macros", "gcodes", "filaments", "firmware", "menu", "www" })
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, directory));
        }
        Settings settings = new() { BaseDirectory = baseDirectory, ModelUpdateInterval = 20, SocketDirectory = Path.Combine(baseDirectory, "run") };
        FakeFirmware.FakeFirmware firmware = new(Options.Create(settings));
        Stopwatch clock = Stopwatch.StartNew();
        CapturingLoggerProvider logs = new(clock);

        // Like upstream Program.cs:121-190, without AddIPC (no socket needed) and with the fake firmware in place of AddLinkAdapter
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Information).AddProvider(logs))
            .ConfigureServices(services =>
            {
                services
                    .AddSingleton(Options.Create(settings))
                    .AddSingleton<ILinkAdapter>(firmware)
                    .AddSingleton<LinkInterface>()
                    .AddCodes()
                    .AddCommands()
                    .AddFiles()
                    .AddLink()
                    .AddModel()
                    .AddUtility()
                    .AddHostedService<LinkService>();

                // Do not start PeriodicUpdateService, it reads system information and sends codes on its own (as in FakeFirmwareHost)
                for (int i = 0; i < services.Count; i++)
                {
                    ServiceDescriptor descriptor = services[i];
                    if (descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationFactory is not null)
                    {
                        Func<IServiceProvider, object> factory = descriptor.ImplementationFactory;
                        services[i] = ServiceDescriptor.Singleton<IHostedService>(provider => factory(provider) is IHostedService service and not PeriodicUpdateService ? service : new NoHostedService());
                    }
                }
            })
            .Build();

        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        long? stoppingAt = null;
        lifetime.ApplicationStopping.Register(() => stoppingAt = clock.ElapsedMilliseconds);
        Task run = host.RunAsync();
        try
        {
            using (CancellationTokenSource cts = new(Timeout))
            {
                await host.Services.GetRequiredService<DuetControlServer.Model.ObjectModel>().WaitForFullUpdateAsync(cts.Token);
            }
            // RunAsync disposes the services when the host has stopped, so resolve them before
            DuetControlServer.Model.ObjectModel model = host.Services.GetRequiredService<DuetControlServer.Model.ObjectModel>();
            LinkService linkService = host.Services.GetServices<IHostedService>().OfType<LinkService>().Single();
            LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
            Processor http = host.Services.GetRequiredService<Manager>()[CodeChannel.HTTP];
            System.IO.File.WriteAllText(Path.Combine(baseDirectory, "sys", "short.g"), "G4 P1\n");

            Task<string> StartCode(string code)
            {
                SimpleCode simpleCode = host.Services.GetRequiredService<CommandFactory>().Create<SimpleCode>();
                simpleCode.Code = code;
                simpleCode.Channel = CodeChannel.HTTP;
                return simpleCode.ExecuteAsync();
            }

            // RRF has closed the macro and written the closal for the next transfer, DSF still has the macro on its stack
            Task<string> m98 = StartCode("M98 P\"short.g\"");
            StepUntil(firmware, () => firmware.GetPendingPackets().Any(IsClosal), "RRF did not close the macro");
            Assert.That(StackDepth(http), Is.EqualTo(2), "DSF does not have the macro on its stack");

            long? m112At = null, linkEndedAt = null;
            Task<string> m112 = StartCode("M112");
            _ = m112.ContinueWith(_ => m112At = clock.ElapsedMilliseconds, TaskContinuationOptions.ExecuteSynchronously);
            _ = linkService.ExecuteTask!.ContinueWith(_ => linkEndedAt = clock.ElapsedMilliseconds, TaskContinuationOptions.ExecuteSynchronously);
            Assert.That(SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null, Timeout), Is.True, "M112 did not request the emergency stop");
            long releasedAt = clock.ElapsedMilliseconds;
            firmware.ReleaseTransfers();

            // Wait for the host to stop on its own
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            long stoppedAt = clock.ElapsedMilliseconds;

            string m112Reply = m112.IsCompletedSuccessfully ? $"'{m112.Result.Replace("\n", "\\n")}'" : $"{m112.Status} {m112.Exception?.InnerException?.Message}";
            string m98Reply = m98.IsCompleted ? (m98.IsCompletedSuccessfully ? $"'{m98.Result.Replace("\n", "\\n")}'" : $"{m98.Status} {m98.Exception?.InnerException?.Message}") : "not completed";
            Exception? linkFault = linkService.ExecuteTask!.Exception?.InnerException;
            IReadOnlyList<FakeSbcRequest> pending = firmware.GetPendingRequests();
            TestContext.Out.WriteLine($"released transfers at +{releasedAt} ms");
            TestContext.Out.WriteLine($"M112 completed at +{m112At} ms, reply {m112Reply}");
            TestContext.Out.WriteLine($"transfer thread ended at +{linkEndedAt} ms, status {linkService.ExecuteTask!.Status}");
            TestContext.Out.WriteLine($"ApplicationStopping at +{stoppingAt} ms, host run task completed at +{stoppedAt} ms ({run.Status})");
            TestContext.Out.WriteLine($"M98 reply {m98Reply}");
            TestContext.Out.WriteLine($"state.status in DCS: {model.State.Status}");
            TestContext.Out.WriteLine($"RRF emergency stopped: {firmware.IsEmergencyStopped}, EmergencyStop requests RRF received: {firmware.GetReceivedRequests().Count(r => r.Request == SbcRequest.EmergencyStop)}");
            TestContext.Out.WriteLine($"requests DSF wrote that never went out: {string.Join(", ", pending.Select(r => $"#{r.Id} {r.Request}"))}");
            TestContext.Out.WriteLine($"transfer thread exception: {linkFault}");
            TestContext.Out.WriteLine("log (Information and above):");
            foreach (string line in logs.Lines)
            {
                TestContext.Out.WriteLine("  " + line);
            }

            // The facts that make up the report
            // M112 completes once the stop is written (LinkService.cs:269-273), before the packets are processed, so the reply does not depend on the
            // transfer thread. The timestamps above only show the order on this run
            Assert.That(m112.IsCompletedSuccessfully, Is.True, "M112 did not complete");
            Assert.That(m112.Result, Is.EqualTo("\n"), "M112 did not report success");
            Assert.That(model.State.Status, Is.EqualTo(MachineStatus.Halted), "DCS does not report the machine as halted");
            Assert.That(linkFault, Is.InstanceOf<InvalidOperationException>().With.Message.EqualTo("Stack underrun on channel HTTP"));
            Assert.That(firmware.IsEmergencyStopped, Is.False, "RRF got the stop");
            Assert.That(pending.Any(r => r.Request == SbcRequest.EmergencyStop), Is.True, "The stop is not left in the transmit buffer");
            Assert.That(stoppingAt, Is.Not.Null, "The host did not stop");
        }
        finally
        {
            if (!run.IsCompleted)
            {
                lifetime.StopApplication();
                await run.WaitAsync(Timeout).ContinueWith(_ => { });
            }
            host.Dispose();
            try
            {
                Directory.Delete(baseDirectory, true);
            }
            catch (IOException)
            {
                // Left behind
            }
        }
    }
}
