using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPIClient;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using UnitTests.SPI;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.Startup;

/// <summary>
/// The intermittent 15 s shutdown hang: systemd kills DCS when it has not exited 15 s after it began to stop (TimeoutStopSec=15,
/// duetcontrolserver.service:8). DCS tells systemd that it stops when ApplicationStopping fires (SystemdLifetime.cs:104-109 of
/// Microsoft.Extensions.Hosting.Systemd, the "Application is shutting down..." line), also when it stops on its own after M999. The host then waits up to
/// HostOptions.ShutdownTimeout, 30 s by default (HostOptions.cs:24), for each service, see <see cref="HostLifecycle.HostOptionsAreTheDefaults"/>.
/// So every service that does not stop until the shutdown timeout ends in SIGKILL. These tests show three ways to get there:
/// <list type="number">
/// <item>A code of a client that is still pending is never completed: the stages of the code pipeline stop reading their queues on ApplicationStopping
/// (PipelineStackItem.cs:63), and LinkService.StopAsync cancels the codes of the link into the Executed queue afterwards (LinkService.cs:241,
/// LinkInterface.cs:707-713, Channel/Processor.cs:1444-1484, CodeProcessor.cs:277-285, 318). The IPC connection waits for the code
/// (SimpleCode.ExecuteAsync awaits it without a token), and the IPC server waits for its connections until ApplicationStopped (IPC/Server.cs:123),
/// which the host signals only after all services have stopped (Host.cs:272 of Microsoft.Extensions.Hosting)</item>
/// <item>After M999 the transfer thread has ended (LinkService.cs:297), so a code that a client sends in the second before DCS stops
/// (MCodeHandler.cs:1363-1368) is never sent and hangs the same way. This fits "after the boot-time M999", when clients are busy</item>
/// <item>A transfer that does not complete keeps the transfer thread inside PerformFullTransfer until its token is cancelled (SPI.cs:324). It got
/// ApplicationStopped, which the host signals only after all services have stopped, so LinkService.StopAsync waited for the stop token (BackgroundService.cs:73
/// of Microsoft.Extensions.Hosting.Abstractions). This fits "systemctl restart while the SPI link is stuck", once DCS has reported the lost connection.
/// Fixed: the transfer gets the stop token of the LinkService (LinkService.cs:290, 542), like the loop around it, as before 2cbc04ae and in v3.6
/// (DataTransfer.cs:210 there)</item>
/// </list>
/// Each test stops with a limit of <see cref="StopLimit"/> instead of the 30 s of the host. Where DCS stopped in time, it would stop in well under a second
/// </summary>
[TestFixture]
public class ShutdownHang
{
    private static readonly TimeSpan Timeout = DcsHost.Timeout;

    /// <summary>
    /// Limit for stopping the host in these tests, below the 15 s of systemd
    /// </summary>
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Time within which DCS is expected to stop
    /// </summary>
    private static readonly TimeSpan ExpectedStopTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Stop the host and report which background services were still running after <see cref="ExpectedStopTime"/>, in the order the host stops them,
    /// so the first one is the service the host waits for
    /// </summary>
    private static async Task<(TimeSpan Elapsed, string Running)> StopAsync(DcsHost host)
    {
        Task<TimeSpan> stop = host.StopAsync(StopLimit);
        string[] running = await Task.WhenAny(stop, Task.Delay(ExpectedStopTime)) == stop ? [] : [.. host.RunningServices.Reverse()];
        string report = (running.Length > 0) ? $"After {ExpectedStopTime.TotalSeconds} s the host still waited for {running[0]}, not stopped yet: {string.Join(", ", running)}" : string.Empty;
        return (await stop, report);
    }

    private static void WaitForMessageBox(FakeFirmware.FakeFirmware firmware) =>
        Assert.That(firmware.WaitUntil(f => f.GetSentPackets().Any(packet => packet.Request == Request.WaitForAcknowledgement), Timeout), Is.True, "The message box did not open");

    /// <summary>
    /// The pipeline part of case 1 without the generic host: a blocking message box from a client is pending when DCS stops, as with a dialog that waits
    /// for the user (M291 S2). The fake firmware host stops the services like the generic host, ApplicationStopping first, then in reverse order
    /// </summary>
    [Test]
    [Explicit("Bug: a code that is still pending when DCS stops is never completed, because the pipeline stops reading its queues on ApplicationStopping")]
    public async Task PendingCodeIsCancelledWhenDcsStops()
    {
        FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Task<string> m291;
        try
        {
            m291 = host.StartCode("M291 P\"Continue?\" S2");
            WaitForMessageBox(host.Firmware);
        }
        finally
        {
            await host.DisposeAsync();
        }

        bool completed = await Task.WhenAny(m291, Task.Delay(ExpectedStopTime)) == m291;
        Assert.That(completed, Is.True, "M291 was neither finished nor cancelled");
        Assert.That(m291.IsCanceled || m291.IsFaulted, Is.True);
    }

    /// <summary>
    /// Case 1 with the generic host and a client on the IPC socket, as DWS is for a code from DWC
    /// </summary>
    [Test]
    [Explicit("Bug: DCS does not stop until the host's shutdown timeout (30 s, systemd kills it after 15 s) while an IPC client waits for a code")]
    public async Task StopsWhileAnIpcCodeIsPending()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();
        using CommandConnection client = new();
        await client.ConnectAsync(host.Settings.FullSocketPath);
        Task<string> m291 = client.PerformSimpleCodeAsync("M291 P\"Continue?\" S2", CodeChannel.HTTP);
        WaitForMessageBox(host.Firmware!);

        (TimeSpan elapsed, string running) = await StopAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.LessThan(ExpectedStopTime), running);
            Assert.That(m291.IsCompleted, Is.True, "The client got no answer");
        });
    }

    /// <summary>
    /// Case 2: M999 resets RRF and ends the transfer thread; a code that another client sends right after the reply is never sent to RRF, and DCS
    /// stops a second later. On a printer without other clients at that moment DCS stops at once, see <see cref="HostLifecycle.M999StopsDcsAfterItsReply"/>
    /// </summary>
    [Test]
    [Explicit("Bug: DCS does not stop until the host's shutdown timeout after M999 when a client has sent a code after the reset")]
    public async Task StopsAfterM999WhileAnotherCodeIsPending()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();
        using CommandConnection resetting = new(), other = new();
        await resetting.ConnectAsync(host.Settings.FullSocketPath);
        await other.ConnectAsync(host.Settings.FullSocketPath);

        await resetting.PerformSimpleCodeAsync("M999", CodeChannel.HTTP).WaitAsync(Timeout);
        Task<string> g4 = other.PerformSimpleCodeAsync("G4 P10", CodeChannel.SBC);
        Assert.That(await HostLifecycle.WaitAsync(host.Lifetime.ApplicationStopping, Timeout), Is.True, "M999 did not stop DCS");

        (TimeSpan elapsed, string running) = await StopAsync(host);
        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.LessThan(ExpectedStopTime), running);
            Assert.That(g4.IsCompleted, Is.True, "The client got no answer");
        });
    }

    /// <summary>
    /// Case 3 with the fake firmware: its transfer is held, and like PerformFullTransfer of the SPI adapter it ends the wait only for the cancellation
    /// token, which LinkService passes as its stop token (FakeFirmware.cs:566-577, LinkService.cs:542)
    /// </summary>
    [Test]
    public async Task StopsWhileTheLinkIsStuck()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();
        host.Firmware!.HoldTransfers();
        DateTime end = DateTime.UtcNow + Timeout;
        while (!host.Firmware.IsTransferHeld)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(end), "The transfer was not held");
            await Task.Delay(10);
        }

        (TimeSpan elapsed, string running) = await StopAsync(host);
        Assert.That(elapsed, Is.LessThan(ExpectedStopTime), running);
    }

    /// <summary>
    /// Case 3 on the SPI adapter of DCS: RRF stops answering after the connection is up, e.g. because it hangs, and DCS has reported the lost connection
    /// before it is stopped. TfrRdy keeps its level, so every wait for the pin ends with its timeout (IInputGpioPin.cs:23-29) and PerformFullTransfer starts
    /// over until SbcRestartTimeout (60 s, SPI.cs:324-331) or until its token is cancelled. That is the stop token of the LinkService (LinkService.cs:542),
    /// so the thread ends after the wait that is running when the service stops, at most SbcConnectionTimeout + SbcTransferTimeout (4.5 s, SPI.cs:1741)
    /// </summary>
    [Test]
    public async Task LinkServiceStopsWhileRrfDoesNotAnswer()
    {
        (TimeSpan elapsed, bool stopped) = await StopLinkServiceWithHungRrfAsync(lostBeforeStop: true);
        Assert.That(stopped, Is.True, $"The transfer thread was still running when StopAsync returned after {elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// The other order than <see cref="LinkServiceStopsWhileRrfDoesNotAnswer"/>: RRF hangs just before DCS is stopped, so the lost connection is reported
    /// after ApplicationStopping. The transfer thread ends with the stop token as well. Before that was passed, it ended here only by chance: the "Lost
    /// connection" message (SPI.cs:469-475) takes the lock of the event log with ApplicationStopping (Logger.cs:224), and the cancellation left the transfer loop
    /// </summary>
    [Test]
    public async Task LostConnectionAfterTheStopEndsTheThread()
    {
        (TimeSpan elapsed, bool stopped) = await StopLinkServiceWithHungRrfAsync(lostBeforeStop: false);
        Assert.That(stopped, Is.True, $"The transfer thread was still running when StopAsync returned after {elapsed.TotalSeconds:F1} s");
        Assert.That(elapsed, Is.LessThan(StopLimit));
    }

    /// <summary>
    /// Case 3 as seen on the lab printer (2026-11-08): with the link stuck in the activation transfer, DCS logged "Lost connection to Duet" with
    /// "Maximum number of SPI transfer retries exceeded" every 12 ms during the stop, and systemd killed it after 15 s. Here TfrRdy toggles at once, but RRF
    /// clocks out 0xFF, so every header has a bad format code (SPI.cs:1873-1878) and each full transfer fails after MaxSbcRetries without a wait (SPI.cs:336-339).
    /// PerformFullTransfer starts over at once (SPI.cs:442-477) and sees the stop token at its next round (SPI.cs:324)
    /// </summary>
    [Test]
    public async Task LinkServiceStopsWhileEveryTransferFails()
    {
        (TimeSpan elapsed, bool stopped) = await StopLinkServiceWithHungRrfAsync(lostBeforeStop: true, answerAtOnce: true);
        Assert.That(stopped, Is.True, $"The transfer thread was still running when StopAsync returned after {elapsed.TotalSeconds:F1} s");
        Assert.That(elapsed, Is.LessThan(ExpectedStopTime));
    }

    /// <summary>
    /// Start the LinkService on the SPI adapter, let RRF hang and stop the service like the host does
    /// </summary>
    /// <param name="lostBeforeStop">Whether to wait until DCS has reported the lost connection before it is stopped</param>
    /// <param name="answerAtOnce">Whether the hung RRF toggles TfrRdy at once, see <see cref="HungRrf"/></param>
    /// <returns>Time StopAsync took and whether the transfer thread had ended when it returned</returns>
    private static async Task<(TimeSpan Elapsed, bool Stopped)> StopLinkServiceWithHungRrfAsync(bool lostBeforeStop, bool answerAtOnce = false)
    {
        HungRrf? hung = null;
        await using LinkServiceOverSpi.Host host = await LinkServiceOverSpi.Host.StartAsync(createAdapter: (provider, rrf, settings) =>
        {
            hung = new HungRrf(rrf, answerAtOnce);
            return new SpiAdapter(provider.GetRequiredService<EventLogger>(), provider.GetRequiredService<DuetControlServer.Model.ObjectModel>(), NullLogger<SpiAdapter>.Instance,
                settings, hung, hung, null);
        });
        ApplicationLifetime lifetime = (ApplicationLifetime)host.Get<IHostApplicationLifetime>();

        hung!.Hang();
        if (lostBeforeStop)
        {
            // SPI.cs:469-475 counts the lost connection (PrintDiagnostics, "lost connections")
            DateTime end = DateTime.UtcNow + Timeout;
            while (!Diagnostics(host.Adapter).Contains("lost connections: 1"))
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(end), "DCS did not notice that RRF does not answer");
                await Task.Delay(50);
            }

            if (answerAtOnce)
            {
                // The lost connection is counted before it is reported (SPI.cs:471-474), and the transfers go on at once. Wait for the next failed round, so that
                // the report is done and the stop cannot end the thread through the event log, see LostConnectionAfterTheStopEndsTheThread
                int restarts = FullTransferRestarts(host.Adapter);
                while (FullTransferRestarts(host.Adapter) <= restarts + host.Get<IOptions<DuetControlServer.Settings>>().Value.MaxSbcRetries)
                {
                    Assert.That(DateTime.UtcNow, Is.LessThan(end), "The transfers did not go on after the lost connection");
                    await Task.Delay(10);
                }
            }
        }
        Assert.That(host.Service.ExecuteTask!.IsCompleted, Is.False, $"The transfer thread ended while RRF did not answer: {host.Service.ExecuteTask.Exception}");

        lifetime.StopApplication();
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (CancellationTokenSource cts = new(StopLimit))
        {
            await host.Service.StopAsync(cts.Token);
        }
        TimeSpan elapsed = stopwatch.Elapsed;
        bool stopped = host.Service.ExecuteTask.IsCompleted;

        // Let the transfer thread go before the host is disposed
        lifetime.NotifyStopped();
        hung.StopWaiting();
        await ((Task)host.Service.ExecuteTask).ContinueWith(_ => { }).WaitAsync(Timeout);
        return (elapsed, stopped);
    }

    /// <summary>
    /// Full transfers the SPI adapter has started over (SPI.cs:362, 370, PrintDiagnostics)
    /// </summary>
    private static int FullTransferRestarts(SpiAdapter adapter) => int.Parse(Regex.Match(Diagnostics(adapter), @"Full transfer restarts: (\d+)").Groups[1].Value);

    private static string Diagnostics(SpiAdapter adapter)
    {
        System.Text.StringBuilder builder = new();
        adapter.PrintDiagnostics(builder);
        return builder.ToString();
    }

    /// <summary>
    /// DCS has 30 s to stop its services (HostOptions.cs:24), but systemd kills it after TimeoutStopSec. With a shorter shutdown timeout the cases above would
    /// still be slow, but DCS would exit on its own before SIGKILL
    /// </summary>
    [Test]
    [Explicit("Bug: the shutdown timeout of the host (30 s) is longer than TimeoutStopSec of duetcontrolserver.service (15 s)")]
    public async Task ShutdownTimeoutIsShorterThanTheStopTimeoutOfSystemd()
    {
        string unitFile = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "pkg", "common", "duetcontrolserver", "usr", "lib", "systemd", "system", "duetcontrolserver.service");
        Match match = Regex.Match(System.IO.File.ReadAllText(unitFile), @"^TimeoutStopSec=(\d+)\s*$", RegexOptions.Multiline);
        Assert.That(match.Success, Is.True, "TimeoutStopSec not found");
        TimeSpan stopTimeout = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value));

        await using DcsHost host = DcsHost.Create();
        Assert.That(host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout, Is.LessThan(stopTimeout));
    }

    /// <summary>
    /// The SPI port of RRF until <see cref="Hang"/>, then a processor that no longer answers: TfrRdy keeps its level, so every wait for an edge ends with
    /// the timeout, and the SPI peripheral clocks out 0xFF as without a DMA set up (FakeRrfSpi.TransferFullDuplex).
    /// With <paramref name="answerAtOnce"/>, TfrRdy toggles at every wait instead, so the exchanges go on without a wait but carry only 0xFF
    /// </summary>
    private sealed class HungRrf(FakeRrfSpi rrf, bool answerAtOnce = false) : ISpiDevice, IInputGpioPin
    {
        private volatile bool _hung, _level;
        private readonly ManualResetEventSlim _stopWaiting = new();

        public void Hang() => _hung = true;

        /// <summary>
        /// End the waits at once, so that the transfer thread can see the end of the test
        /// </summary>
        public void StopWaiting() => _stopWaiting.Set();

        public int MissedEdges => _hung ? 0 : rrf.MissedEdges;

        public bool Value => _hung ? _level : rrf.Value;

        public void FlushEvents()
        {
            if (!_hung)
            {
                rrf.FlushEvents();
            }
        }

        public bool WaitForEvent(int timeout)
        {
            if (!_hung)
            {
                return rrf.WaitForEvent(timeout);
            }
            if (answerAtOnce)
            {
                _level = !_level;
                return _level;
            }
            _stopWaiting.Wait(timeout);
            throw new OperationCanceledException();
        }

        public void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            if (!_hung)
            {
                rrf.TransferFullDuplex(writeBuffer, readBuffer);
                return;
            }
            readBuffer.Fill(0xFF);
        }
    }
}
