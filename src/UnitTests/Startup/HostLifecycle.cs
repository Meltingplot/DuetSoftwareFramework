using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPIClient;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Startup;

/// <summary>
/// Start and stop of DCS under the generic host, with the services registered as in Program.cs (see <see cref="DcsHost"/>)
/// </summary>
[TestFixture]
public class HostLifecycle
{
    /// <summary>
    /// The host starts the hosted services in the order of their registration and stops them in reverse (Host.cs of Microsoft.Extensions.Hosting,
    /// StartAsync and StopAsync, "Ensure hosted services are stopped in LIFO order"). Program.cs registers the services with AddCodes, AddFiles, AddIPC,
    /// AddModel and AddLinkAdapter in this order (Program.cs:179-188), so the IPC server listens before the LinkService connects to RRF, and the
    /// LinkService is the first to stop while the IPC server still accepts connections
    /// </summary>
    [Test]
    public async Task HostedServicesStartInRegistrationOrder()
    {
        await using DcsHost host = DcsHost.Create();
        Assert.That(host.HostedServices.Select(service => service.GetType().Name), Is.EqualTo(new[]
        {
            "Functions",                // Codes/Extensions.cs:20
            "CodeProcessorService",     // Codes/Extensions.cs:24
            "JobProcessor",             // Files/Extensions.cs:30
            "Server",                   // IPC/Extensions.cs:28
            "Observer",                 // Model/Extensions.cs:25
            "PeriodicUpdateService",    // Model/Extensions.cs:26
            "SbcTriggerService",        // Model/Extensions.cs:27
            "UpdateService",            // Model/Extensions.cs:28
            "LinkService"               // Link/Extensions.cs:53
        }));
    }

    /// <summary>
    /// Program.cs sets the shutdown timeout to 10 s instead of the 30 s of HostOptions.cs of Microsoft.Extensions.Hosting (Program.cs:177), see
    /// <see cref="ShutdownHang.ShutdownTimeoutIsShorterThanTheStopTimeoutOfSystemd"/>. The other options are the defaults, so the host stops when a background
    /// service fails (BackgroundServiceExceptionBehavior.StopHost)
    /// </summary>
    [Test]
    public async Task HostOptionsAreTheDefaultsExceptTheShutdownTimeout()
    {
        await using DcsHost host = DcsHost.Create();
        HostOptions options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        Assert.That(options.ShutdownTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(options.BackgroundServiceExceptionBehavior, Is.EqualTo(BackgroundServiceExceptionBehavior.StopHost));
        Assert.That(options.ServicesStartConcurrently, Is.False);
        Assert.That(options.ServicesStopConcurrently, Is.False);
    }

    [Test]
    public async Task StartsAndStops()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();

        Assert.That(host.Lifetime.ApplicationStarted.IsCancellationRequested, Is.True);
        Assert.That(System.IO.File.Exists(host.Settings.FullSocketPath), Is.True, "IPC socket");
        Assert.That(host.Firmware!.TransferCount, Is.GreaterThan(0));
        using (host.Model.AccessReadOnly())
        {
            Assert.That(host.Model.Boards.Select(board => board.Name), Is.EqualTo(new[] { "Duet 3 MB6HC (fake)" }));
        }
        Assert.That(host.RunningServices, Is.EquivalentTo(new[] { "CodeProcessorService", "JobProcessor", "Server", "PeriodicUpdateService", "SbcTriggerService", "UpdateService", "LinkService" }));

        TimeSpan elapsed = await host.StopAsync(DcsHost.Timeout);
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(host.RunningServices, Is.Empty);
        Assert.That(host.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        Assert.That(System.IO.File.Exists(host.Settings.FullSocketPath), Is.False, "The IPC server removes its socket (IPC/Server.cs:130-139)");
        Assert.That(host.Log.Entries.Where(entry => entry.Level >= LogLevel.Error), Is.Empty);
    }

    [Test]
    public async Task IpcClientRunsCodes()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();

        using CommandConnection connection = new();
        await connection.ConnectAsync(host.Settings.FullSocketPath);
        string reply = await connection.PerformSimpleCodeAsync("echo 1 + 2", CodeChannel.SBC).WaitAsync(DcsHost.Timeout);
        Assert.That(reply, Is.EqualTo("3\n"));
    }

    /// <summary>
    /// M999 resets RRF and stops DCS a second after its reply (MCodeHandler.cs:1342-1373). The transfer thread ends right after the reset
    /// (LinkService.cs:338-352), and systemd starts a new DCS (Restart=always, duetcontrolserver.service:9). Nothing else is going on here, so DCS stops at once
    /// </summary>
    [Test]
    public async Task M999StopsDcsAfterItsReply()
    {
        await using DcsHost host = DcsHost.Create();
        await host.StartAsync();

        using CommandConnection connection = new();
        await connection.ConnectAsync(host.Settings.FullSocketPath);
        string reply = await connection.PerformSimpleCodeAsync("M999", CodeChannel.HTTP).WaitAsync(DcsHost.Timeout);
        Assert.That(reply, Does.Not.Contain("Error"));
        Assert.That(host.Firmware!.GetReceivedRequests().Count(request => request.Request == SbcRequest.Reset), Is.EqualTo(1));

        LinkService linkService = host.HostedServices.OfType<LinkService>().Single();
        await linkService.ExecuteTask!.WaitAsync(DcsHost.Timeout);
        Assert.That(await WaitAsync(host.Lifetime.ApplicationStopping, DcsHost.Timeout), Is.True, "M999 did not stop DCS");

        TimeSpan elapsed = await host.StopAsync(DcsHost.Timeout);
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        Assert.That(host.RunningServices, Is.Empty);
    }

    /// <summary>
    /// The PeriodicUpdateService reads the plugin manifests from the plugin directory when it starts, if PluginSupport is on (PeriodicUpdateService.cs:85-88).
    /// Settings.cs:127-129 says the directory is provided by DPS, not by the DCS package. DCS does not depend on DPS (pkg/deb/duetcontrolserver/DEBIAN/control:6),
    /// the meta package only recommends it (pkg/deb/duetsoftwareframework/DEBIAN/control:7), and only the DuetPiManagementPlugin package ships /opt/dsf/plugins.
    /// Directory.GetFiles threw for a missing directory outside the try block of the loop, so the start of the host failed. Now a missing directory means
    /// that no plugin is installed
    /// </summary>
    [Test]
    public async Task StartsWithoutPluginDirectory()
    {
        await using DcsHost host = DcsHost.Create(configure: config => config["PluginDirectory"] = (string)config["PluginDirectory"]! + "-missing");
        await host.StartAsync();
        Assert.That(host.RunningServices, Does.Contain("PeriodicUpdateService"));
        using (host.Model.AccessReadOnly())
        {
            Assert.That(host.Model.Plugins, Is.Empty);
        }
    }

    /// <summary>
    /// Without plugin support the plugin directory is not read
    /// </summary>
    [Test]
    public async Task StartsWithoutPluginDirectoryIfPluginsAreOff()
    {
        await using DcsHost host = DcsHost.Create(configure: config =>
        {
            config["PluginDirectory"] = (string)config["PluginDirectory"]! + "-missing";
            config["PluginSupport"] = false;
        });
        await host.StartAsync();
        Assert.That(host.RunningServices, Does.Contain("PeriodicUpdateService"));
    }

    /// <summary>
    /// Wait until a token is cancelled
    /// </summary>
    /// <returns>Whether it was cancelled in time</returns>
    internal static async Task<bool> WaitAsync(CancellationToken token, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
