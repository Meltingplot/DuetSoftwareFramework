using System;
using System.Collections.Generic;
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
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.FakeFirmware;

/// <summary>
/// DCS services wired like in DCS, but with the <see cref="FakeFirmware"/> as link adapter and the real LinkService running.
/// Tests send codes the way clients do and the fake firmware answers like RRF.
/// Set the environment variable DSF_FAKE_FIRMWARE_LOG to a log level (e.g. Debug) to see the DCS log in the test output,
/// and DSF_FAKE_FIRMWARE_LOG_FILE to a file path to append it to that file as well
/// </summary>
public sealed class FakeFirmwareHost : IAsyncDisposable
{
    /// <summary>
    /// Default timeout for codes and waits
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hosted services that are not started because they read system information and send codes on their own
    /// </summary>
    private static readonly Type[] ServicesNotStarted = [typeof(PeriodicUpdateService)];

    private readonly ServiceProvider _provider;
    private readonly ApplicationLifetime _lifetime;
    private readonly List<IHostedService> _startedServices = [];

    private FakeFirmwareHost(ServiceProvider provider, FakeFirmware firmware, string baseDirectory)
    {
        _provider = provider;
        _lifetime = (ApplicationLifetime)provider.GetRequiredService<IHostApplicationLifetime>();
        Firmware = firmware;
        BaseDirectory = baseDirectory;
    }

    /// <summary>
    /// Fake firmware
    /// </summary>
    public FakeFirmware Firmware { get; }

    /// <summary>
    /// DCS services
    /// </summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// Object model of DCS
    /// </summary>
    public DuetControlServer.Model.ObjectModel Model => _provider.GetRequiredService<DuetControlServer.Model.ObjectModel>();

    /// <summary>
    /// Temporary base directory of the virtual SD card
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// Build the services, start them and wait for the first object model update
    /// </summary>
    /// <param name="configureFirmware">Optional callback to set up the fake firmware before DCS connects</param>
    /// <param name="configureSettings">Optional callback to change the settings</param>
    /// <returns>Started host</returns>
    public static async Task<FakeFirmwareHost> StartAsync(Action<FakeFirmware>? configureFirmware = null, Action<Settings>? configureSettings = null)
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "dsf-fake-firmware-" + Guid.NewGuid().ToString("N"));
        foreach (string directory in new[] { "sys", "macros", "gcodes", "filaments", "firmware", "menu", "www" })
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, directory));
        }

        Settings settings = new()
        {
            BaseDirectory = baseDirectory,
            ModelUpdateInterval = 20
        };
        configureSettings?.Invoke(settings);

        FakeFirmware firmware = new(Options.Create(settings));
        configureFirmware?.Invoke(firmware);

        ServiceCollection services = new();
        services
            .AddLogging(builder =>
            {
                if (Enum.TryParse(Environment.GetEnvironmentVariable("DSF_FAKE_FIRMWARE_LOG"), true, out LogLevel logLevel))
                {
                    builder.SetMinimumLevel(logLevel).AddProvider(new TestContextLoggerProvider(Environment.GetEnvironmentVariable("DSF_FAKE_FIRMWARE_LOG_FILE")));
                }
            })
            .AddSingleton(Options.Create(settings))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(firmware)
            .AddSingleton<ILinkAdapter>(firmware)
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility()
            .AddHostedService<LinkService>();

        FakeFirmwareHost host = new(services.BuildServiceProvider(), firmware, baseDirectory);
        firmware.Logger = host._provider.GetRequiredService<ILoggerFactory>().CreateLogger<FakeFirmware>();
        try
        {
            await host.StartServicesAsync();
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        return host;
    }

    /// <summary>
    /// Start the hosted services in the order the generic host starts them
    /// </summary>
    private async Task StartServicesAsync()
    {
        using CancellationTokenSource cts = new(DefaultTimeout);
        foreach (IHostedService service in _provider.GetServices<IHostedService>())
        {
            if (!ServicesNotStarted.Contains(service.GetType()))
            {
                await service.StartAsync(cts.Token);
                _startedServices.Add(service);
            }
        }
        _lifetime.NotifyStarted();

        // Wait for the object model to be read from the firmware
        await Model.WaitForFullUpdateAsync(cts.Token);
    }

    /// <summary>
    /// Write a macro file to the sys directory
    /// </summary>
    /// <param name="fileName">Name of the macro file</param>
    /// <param name="content">Content of the macro</param>
    /// <returns>Name of the macro file</returns>
    public string WriteMacro(string fileName, string content)
    {
        System.IO.File.WriteAllText(Path.Combine(BaseDirectory, "sys", fileName), content);
        return fileName;
    }

    /// <summary>
    /// Start a code like a client does via the SimpleCode command
    /// </summary>
    /// <param name="code">Code to execute</param>
    /// <param name="channel">Code channel</param>
    /// <returns>Task returning the code reply</returns>
    public Task<string> StartCode(string code, CodeChannel channel = CodeChannel.HTTP)
    {
        DuetControlServer.Commands.SimpleCode simpleCode = _provider.GetRequiredService<CommandFactory>().Create<DuetControlServer.Commands.SimpleCode>();
        simpleCode.Code = code;
        simpleCode.Channel = channel;
        return simpleCode.ExecuteAsync();
    }

    /// <summary>
    /// Execute a code like a client does via the SimpleCode command and wait for the reply
    /// </summary>
    /// <param name="code">Code to execute</param>
    /// <param name="channel">Code channel</param>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>Code reply</returns>
    public Task<string> ExecuteAsync(string code, CodeChannel channel = CodeChannel.HTTP, TimeSpan? timeout = null) => StartCode(code, channel).WaitAsync(timeout ?? DefaultTimeout);

    /// <summary>
    /// Execute a single code and get its result as a message
    /// </summary>
    /// <param name="code">Code to execute</param>
    /// <param name="channel">Code channel</param>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>Code result</returns>
    public async Task<Message?> ExecuteCodeAsync(string code, CodeChannel channel = CodeChannel.HTTP, TimeSpan? timeout = null)
    {
        DuetControlServer.Commands.SimpleCode simpleCode = _provider.GetRequiredService<CommandFactory>().Create<DuetControlServer.Commands.SimpleCode>();
        simpleCode.Code = code;
        simpleCode.Channel = channel;

        Code? parsedCode = null;
        await foreach (Code item in simpleCode.ParseAsync())
        {
            parsedCode = (parsedCode is null) ? item : throw new ArgumentException("Expected a single code", nameof(code));
        }
        return await (parsedCode ?? throw new ArgumentException("Expected a code", nameof(code))).ExecuteAsync().WaitAsync(timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// Stop the services and delete the temporary directory
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async ValueTask DisposeAsync()
    {
        // Stop the services in reverse order like the generic host does
        _lifetime.StopApplication();
        using (CancellationTokenSource cts = new(DefaultTimeout))
        {
            for (int i = _startedServices.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _startedServices[i].StopAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopping took too long, carry on with the next one
                }
            }
        }
        _lifetime.NotifyStopped();
        await _provider.DisposeAsync();

        try
        {
            Directory.Delete(BaseDirectory, true);
        }
        catch (IOException)
        {
            // A macro may still be open, the temporary directory is left behind then
        }
    }

    /// <summary>
    /// Logger provider that writes to the NUnit progress output and optionally to a file
    /// </summary>
    /// <param name="logFile">Optional file to append the log to</param>
    private sealed class TestContextLoggerProvider(string? logFile) : ILoggerProvider
    {
        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new TestContextLogger(categoryName, logFile);

        /// <inheritdoc />
        public void Dispose() { }

        /// <summary>
        /// Logger that writes to the NUnit progress output and optionally to a file
        /// </summary>
        /// <param name="categoryName">Category of the logger</param>
        /// <param name="logFile">Optional file to append the log to</param>
        private sealed class TestContextLogger(string categoryName, string? logFile) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {categoryName}: {formatter(state, exception)}" + ((exception is not null) ? $" {exception}" : string.Empty);
                TestContext.Progress.WriteLine(line);
                if (!string.IsNullOrEmpty(logFile))
                {
                    lock (typeof(TestContextLogger))
                    {
                        System.IO.File.AppendAllText(logFile, line + Environment.NewLine);
                    }
                }
            }
        }
    }
}
