using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests.IPC;

namespace UnitTests.Startup;

/// <summary>
/// DCS as Program.cs builds it: the generic host of Host.CreateDefaultBuilder with UseSystemd, the config file as JSON source, the shutdown timeout of
/// Program.cs:188 and the same service registrations in the same order (Program.cs:129-201), so that all hosted services run and the host starts and stops
/// them. Differences to Program.cs:
/// <list type="bullet">
/// <item>The link adapter is the <see cref="FakeFirmware.FakeFirmware"/> or an SPI adapter on a fake device instead of the hardware</item>
/// <item>The log goes to an <see cref="IpcLog"/> instead of the console, without the dynamic level filter of Program.cs:153-159</item>
/// <item>The host lifetime does nothing, so the test process keeps its own signal handlers (Program.cs gets the console or systemd lifetime)</item>
/// <item>The content root is the temporary directory, so no appsettings.json of the test directory is read</item>
/// <item>Program.cs's handlers around the host (start error file, Terminate, update-only mode) are not run, see <see cref="DcsProcess"/> for those</item>
/// </list>
/// </summary>
internal sealed class DcsHost : IAsyncDisposable
{
    /// <summary>
    /// Default timeout for starting and stopping
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private bool _stopCalled, _disposed;

    private DcsHost(IHost host, string directory, FakeFirmware.FakeFirmware? firmware, IpcLog log)
    {
        _host = host;
        Directory = directory;
        Firmware = firmware;
        Log = log;
    }

    /// <summary>
    /// Temporary directory holding the config file, the virtual SD card and the IPC socket
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Fake firmware, or null if the SPI adapter runs on a fake device
    /// </summary>
    public FakeFirmware.FakeFirmware? Firmware { get; }

    /// <summary>
    /// Warnings and errors logged by DCS
    /// </summary>
    public IpcLog Log { get; }

    /// <summary>
    /// Services of the host
    /// </summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Lifetime of the host
    /// </summary>
    public IHostApplicationLifetime Lifetime => Services.GetRequiredService<IHostApplicationLifetime>();

    /// <summary>
    /// Settings of DCS
    /// </summary>
    public Settings Settings => Services.GetRequiredService<IOptions<Settings>>().Value;

    /// <summary>
    /// Object model of DCS
    /// </summary>
    public ObjectModel Model => Services.GetRequiredService<ObjectModel>();

    /// <summary>
    /// Hosted services in the order the host starts them
    /// </summary>
    public IReadOnlyList<IHostedService> HostedServices => [.. Services.GetServices<IHostedService>()];

    /// <summary>
    /// Background services whose ExecuteAsync has not finished
    /// </summary>
    public IReadOnlyList<string> RunningServices => [.. HostedServices.OfType<BackgroundService>().Where(service => service.ExecuteTask is { IsCompleted: false }).Select(service => service.GetType().Name)];

    /// <summary>
    /// Build the host
    /// </summary>
    /// <param name="configureFirmware">Set up the fake firmware before DCS connects</param>
    /// <param name="configure">Change the config file</param>
    /// <param name="createSpi">Create the SPI adapter on a fake device instead of using the fake firmware</param>
    /// <returns>Host that is not started yet</returns>
    public static DcsHost Create(Action<FakeFirmware.FakeFirmware>? configureFirmware = null, Action<JsonObject>? configure = null, Func<IServiceProvider, SpiAdapter>? createSpi = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dsf-host-" + Guid.NewGuid().ToString("N"));
        foreach (string subDirectory in new[] { "sd/sys", "sd/macros", "sd/gcodes", "sd/filaments", "sd/firmware", "sd/menu", "sd/www", "plugins", "run" })
        {
            System.IO.Directory.CreateDirectory(Path.Combine(directory, subDirectory));
        }

        // The device paths do not exist, so a real adapter would fail instead of reaching hardware
        JsonObject config = new()
        {
            [nameof(Settings.BaseDirectory)] = Path.Combine(directory, "sd"),
            [nameof(Settings.SocketDirectory)] = Path.Combine(directory, "run"),
            [nameof(Settings.StartErrorFile)] = Path.Combine(directory, "run", "dcs.err"),
            [nameof(Settings.PluginDirectory)] = Path.Combine(directory, "plugins"),
            [nameof(Settings.PluginsFilename)] = Path.Combine(directory, "plugins.txt"),
            [nameof(Settings.SpiDevice)] = Path.Combine(directory, "no-spidev"),
            [nameof(Settings.GpioChipDevice)] = Path.Combine(directory, "no-gpiochip"),
            [nameof(Settings.UsbDevice)] = Path.Combine(directory, "no-tty"),
            [nameof(Settings.ModelUpdateInterval)] = 20
        };
        configure?.Invoke(config);
        string configFile = Path.Combine(directory, "config.json");
        System.IO.File.WriteAllText(configFile, config.ToJsonString());
        FileInfo configFileValue = new(configFile);

        FakeFirmware.FakeFirmware? firmware = null;
        if (createSpi is null)
        {
            firmware = new FakeFirmware.FakeFirmware(Options.Create(new Settings()));
            configureFirmware?.Invoke(firmware);
        }

        IpcLog log = new();
        IHost host = Host.CreateDefaultBuilder()
            .UseContentRoot(directory)
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddProvider(log);
            })
            .UseSystemd()
            .ConfigureAppConfiguration((hostingContext, config) => config.AddJsonFile(configFileValue.FullName, optional: true).AddCommandLine([]))
            .ConfigureServices((context, services) =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
                services
                    .AddSettings(context.Configuration, false, null, configFileValue, null, null, null, out _)
                    .AddCodes()
                    .AddCommands()
                    .AddFiles()
                    .AddIPC()
                    .AddLink()
                    .AddModel();
                int adapterIndex = services.Count;
                services.AddLinkAdapter();
                ReplaceLinkAdapter(services, adapterIndex, firmware, createSpi);
                services.AddUtility();
                services.AddSingleton<IHostLifetime, TestLifetime>();
            })
            .Build();
        if (firmware is not null)
        {
            firmware.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<FakeFirmware.FakeFirmware>();
        }
        return new DcsHost(host, directory, firmware, log);
    }

    /// <summary>
    /// AddLinkAdapter registers the SPI adapter, the link adapter and diagnostics for it, the link interface and the LinkService (Link/Extensions.cs:47-53).
    /// Only the adapter is replaced, so the LinkService and LinkInterface stay as DCS registers them
    /// </summary>
    private static void ReplaceLinkAdapter(IServiceCollection services, int index, FakeFirmware.FakeFirmware? firmware, Func<IServiceProvider, SpiAdapter>? createSpi)
    {
        if (services[index].ServiceType != typeof(SpiAdapter) || services[index + 1].ServiceType != typeof(ILinkAdapter) || services[index + 2].ServiceType != typeof(IDiagnostics))
        {
            throw new InvalidOperationException("AddLinkAdapter registers other services than expected, the test host must be adapted");
        }

        if (createSpi is not null)
        {
            services[index] = ServiceDescriptor.Singleton(createSpi);
        }
        else
        {
            services.RemoveAt(index + 2);
            services[index + 1] = ServiceDescriptor.Singleton<ILinkAdapter>(firmware!);
            services.RemoveAt(index);
        }
    }

    /// <summary>
    /// Start the host and wait for the first object model update
    /// </summary>
    public async Task StartAsync()
    {
        using CancellationTokenSource cts = new(Timeout);
        await _host.StartAsync(cts.Token);
        await Model.WaitForFullUpdateAsync(cts.Token);
    }

    /// <summary>
    /// Stop the host like host.Run() does once ApplicationStopping fires (HostingAbstractionsHostExtensions.WaitForShutdownAsync), but give up after the
    /// given time instead of the shutdown timeout of the host
    /// </summary>
    /// <param name="limit">Time after which the host stops waiting for its services</param>
    /// <returns>Time the host took to stop</returns>
    public async Task<TimeSpan> StopAsync(TimeSpan limit)
    {
        _stopCalled = true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource cts = new(limit);
        await _host.StopAsync(cts.Token);
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Write a macro file to the sys directory
    /// </summary>
    public void WriteMacro(string fileName, string content) => System.IO.File.WriteAllText(Path.Combine(Directory, "sd", "sys", fileName), content);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Firmware?.ReleaseTransfers();
        if (!_stopCalled)
        {
            try
            {
                await StopAsync(Timeout);
            }
            catch (Exception e) when (e is OperationCanceledException or AggregateException)
            {
                // Stopping failed, the services are disposed anyway
            }
        }
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }

        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
            // A file may still be open, the temporary directory is left behind then
        }
    }

    /// <summary>
    /// Host lifetime that neither waits for a start signal nor registers signal handlers
    /// </summary>
    private sealed class TestLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
