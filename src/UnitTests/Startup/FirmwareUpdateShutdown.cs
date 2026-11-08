using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using AsyncLock = DuetSharedLibrary.AsyncLock;

namespace UnitTests.Startup;

/// <summary>
/// A stop of DCS while the main firmware is updated through the IAP program. v3.6 waited for a running update before it shut down (SIGTERM handler,
/// SPI.Interface.WaitForUpdate, Program.cs:169-188 of upstream v3.6-dev); upstream c5e3214b dropped that with the generic host. A flash of the main board
/// took 11 s on the lab printer (journal 2026-11-02), longer than the shutdown timeout of 10 s, and systemd kills DCS after TimeoutStopSec=15.
/// LinkService.StopAsync now waits for the update first, ignores the shutdown timeout for it, and asks systemd for more time meanwhile
/// (EXTEND_TIMEOUT_USEC, sd_notify(3)).
/// The transfer thread holds the firmware update lock of LinkInterface while it updates the firmware (LinkService.cs:358-384), so the tests hold that
/// lock to stand for an update in progress: the transfer thread then waits at the lock as it would sit in the update, and no transfer takes place
/// </summary>
[TestFixture]
public class FirmwareUpdateShutdown
{
    private static readonly TimeSpan Timeout = DcsHost.Timeout;

    /// <summary>
    /// How long a test holds the update after the stop began: longer than the shutdown timeout of these tests and than the interval of the
    /// requests for more time (5 s)
    /// </summary>
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Shutdown timeout of the host in these tests, shorter than the update
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Notifier of systemd that records what DCS sends
    /// </summary>
    private sealed class RecordingNotifier : ISystemdNotifier
    {
        private readonly List<string> _states = [];

        public bool IsEnabled => true;

        public void Notify(ServiceState state)
        {
            lock (_states)
            {
                _states.Add(state.ToString());
            }
        }

        public string[] States
        {
            get
            {
                lock (_states)
                {
                    return [.. _states];
                }
            }
        }
    }

    /// <summary>
    /// Create DCS with the given notifier of systemd and the shutdown timeout of these tests
    /// </summary>
    private static DcsHost Create(RecordingNotifier notifier) => DcsHost.Create(configureServices: services =>
    {
        services.AddSingleton<ISystemdNotifier>(notifier);
        services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
    });

    /// <summary>
    /// Take the firmware update lock like the transfer thread does for an update
    /// </summary>
    private static Task<AsyncLock.Releaser> StartUpdateAsync(DcsHost host) => host.Services.GetRequiredService<LinkInterface>().FirmwareUpdateLock.LockAsync().AsTask().WaitAsync(Timeout);

    private static IEnumerable<string> Warnings(DcsHost host) => host.Log.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Message);

    /// <summary>
    /// The stop waits for the update, although it takes longer than the shutdown timeout, and asks systemd for 15 s more every 5 s. The other services
    /// keep running meanwhile, and DCS stops once the update has completed
    /// </summary>
    [Test]
    public async Task StopWaitsForTheFirmwareUpdate()
    {
        RecordingNotifier notifier = new();
        await using DcsHost host = Create(notifier);
        await host.StartAsync();
        AsyncLock.Releaser update = await StartUpdateAsync(host);

        Task<TimeSpan> stop = host.StopAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(HoldTime);
        Assert.Multiple(() =>
        {
            Assert.That(stop.IsCompleted, Is.False, "DCS stopped during the update");
            Assert.That(host.RunningServices, Does.Contain("LinkService").And.Contain("Server").And.Contain("CodeProcessorService"));
            Assert.That(notifier.States, Is.Not.Empty.And.All.EqualTo("EXTEND_TIMEOUT_USEC=15000000"));
            Assert.That(Warnings(host), Does.Contain("Waiting for the firmware update to finish before stopping"));
        });

        update.Dispose();
        await stop.WaitAsync(Timeout);
        Assert.That(Warnings(host).Where(message => message.Contains("did not finish")), Is.Empty);

        // The shutdown timeout has passed during the update, so the host gave the other services a cancelled token and did not wait for them to end
        // (BackgroundService.StopAsync); they end right after
        DateTime end = DateTime.UtcNow + Timeout;
        while (host.RunningServices.Count > 0)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(end), $"Still running: {string.Join(", ", host.RunningServices)}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Without an update the stop does not wait and does not ask systemd for more time
    /// </summary>
    [Test]
    public async Task StopWithoutAnUpdateDoesNotWait()
    {
        RecordingNotifier notifier = new();
        await using DcsHost host = Create(notifier);
        await host.StartAsync();

        TimeSpan elapsed = await host.StopAsync(Timeout);
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        Assert.That(notifier.States, Is.Empty);
        Assert.That(Warnings(host), Does.Not.Contain("Waiting for the firmware update to finish before stopping"));
    }

    /// <summary>
    /// An update that does not finish within LinkService.FirmwareUpdateStopTimeout (2 minutes) does not hold the stop for good: DCS logs it and stops
    /// anyway. The IAP program does not cause this, one that stops answering ends the update after Consts.IapTimeout (SPI.cs:1741, 1746)
    /// </summary>
    [Test]
    public async Task StopGivesUpOnAnUpdateThatDoesNotFinish()
    {
        RecordingNotifier notifier = new();
        await using DcsHost host = Create(notifier);
        LinkService linkService = host.HostedServices.OfType<LinkService>().Single();
        Assert.That(linkService.FirmwareUpdateStopTimeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
        linkService.FirmwareUpdateStopTimeout = TimeSpan.FromSeconds(2);
        await host.StartAsync();
        AsyncLock.Releaser update = await StartUpdateAsync(host);
        try
        {
            TimeSpan elapsed = await host.StopAsync(TimeSpan.FromSeconds(60)).WaitAsync(Timeout);
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(Warnings(host), Does.Contain("The firmware update did not finish within 00:00:02, stopping anyway"));
        }
        finally
        {
            // Let the transfer thread go before the host is disposed
            update.Dispose();
            await ((Task)linkService.ExecuteTask!).ContinueWith(_ => { }).WaitAsync(Timeout);
        }
    }
}
