using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;

/// <summary>
/// Runs the real <see cref="DuetWebServer.Program.Main"/> in-process on its own thread, listening on 127.0.0.1 with a port the OS picks.
/// </summary>
/// <remarks>
/// Hosting choice: the real Main runs, not a copy of it. A test host that mirrors Program.Main line by line would drift from it unnoticed,
/// and Program.cs itself would stay untested. Main cannot be stopped on its own (app.Run() blocks), so this class does what
/// Microsoft.AspNetCore.Mvc.Testing does for top-level programs (HostFactoryResolver): it listens to the "Microsoft.Extensions.Hosting"
/// DiagnosticListener, which HostApplicationBuilder.Build writes to. On "HostBuilding" it gets the builder as IHostBuilder and adds a
/// logger provider (so tests can assert that no unhandled exception was logged), a short shutdown timeout and a data protection key directory in the
/// temporary directory (the authentication services pull in ASP.NET data protection, which would otherwise write a key to ~/.aspnet; DWS itself does
/// not use it, its session keys are GUIDs kept in SessionStorage); on "HostBuilt" it gets the
/// IHost, whose IHostApplicationLifetime stops app.Run() again. Everything else, including the configuration, the services, the middleware
/// order and the endpoint mapping, comes from Program.Main unchanged. Each instance gets its own configuration file, so every fixture can
/// have its own settings, and Main may run several times in one process.
/// The configuration file has the shape of pkg/common/duetwebserver/opt/dsf/conf/http.json, which Main reads via --config (Program.cs:36-45).
/// The content root is an empty temporary directory, so the appsettings.json that the DuetWebServer project copies into the test output
/// (Kestrel endpoint http://0.0.0.0:5000) is not read
/// </remarks>
public sealed class DwsHost : IAsyncDisposable
{
    /// <summary>
    /// Only one instance may build its host at a time, so that the diagnostic events can be told apart
    /// </summary>
    private static readonly SemaphoreSlim StartLock = new(1, 1);

    /// <summary>
    /// Maximum time to wait for the web server to start or stop
    /// </summary>
    private static readonly TimeSpan StartStopTimeout = TimeSpan.FromSeconds(20);

    private readonly string _directory;
    private readonly Thread _thread;
    private IHost? _host;
    private Exception? _mainException;

    private DwsHost(string directory, Thread thread)
    {
        _directory = directory;
        _thread = thread;
    }

    /// <summary>
    /// Base URI of the web server, e.g. http://127.0.0.1:41234/
    /// </summary>
    public Uri BaseUri { get; private set; } = null!;

    /// <summary>
    /// Services of the running web server
    /// </summary>
    public IServiceProvider Services => _host!.Services;

    /// <summary>
    /// Log entries written by the web server
    /// </summary>
    public LogCollector Log { get; } = new();

    /// <summary>
    /// Path of the configuration file passed via --config
    /// </summary>
    public string ConfigFile => Path.Combine(_directory, "http.json");

    /// <summary>
    /// Start DuetWebServer
    /// </summary>
    /// <param name="socketPath">IPC socket of DCS</param>
    /// <param name="webDirectory">Default web directory</param>
    /// <param name="configure">Optional callback to change the settings in the configuration file (keys as in Settings.cs)</param>
    /// <returns>Running web server</returns>
    public static async Task<DwsHost> StartAsync(string socketPath, string webDirectory, Action<JsonObject>? configure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dsf-dws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        JsonObject config = new()
        {
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Information" } },
            ["Kestrel"] = new JsonObject { ["Endpoints"] = new JsonObject { ["Http"] = new JsonObject { ["Url"] = "http://127.0.0.1:0" } } },
            ["KeepAliveInterval"] = 30,
            ["SessionTimeout"] = 8000,
            ["ModelRetryDelay"] = 200,
            ["UseStaticFiles"] = true,
            ["DefaultWebDirectory"] = webDirectory,
            ["MaxAge"] = 3600,
            ["SocketPath"] = socketPath,
            ["WebSocketBufferSize"] = 8192,
            // Must not exist, else a missing DCS is reported with its content (EndpointHelper.HandleDcsExceptionAsync)
            ["StartErrorFile"] = Path.Combine(directory, "dcs-start-error.txt")
        };
        configure?.Invoke(config);
        await File.WriteAllTextAsync(Path.Combine(directory, "http.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        string[] args = ["--config", Path.Combine(directory, "http.json"), "--contentRoot", directory, "--environment", "Production"];

        DwsHost? host = null;
        TaskCompletionSource<IHost> built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                DuetWebServer.Program.Main(args);
            }
            catch (Exception e)
            {
                host!._mainException = e;
                built.TrySetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "DuetWebServer.Program.Main"
        };
        host = new DwsHost(directory, thread);

        await StartLock.WaitAsync();
        try
        {
            using IDisposable subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(thread, directory, host.Log, built));
            thread.Start();
            host._host = await built.Task.WaitAsync(StartStopTimeout);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        finally
        {
            StartLock.Release();
        }

        try
        {
            // Wait for Kestrel to listen and get the port it picked
            IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            {
                Task finished = Task.Run(() => thread.Join());
                if (await Task.WhenAny(started.Task, finished).WaitAsync(StartStopTimeout) != started.Task)
                {
                    throw new InvalidOperationException("DuetWebServer terminated during start-up", host._mainException);
                }
            }
            string address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host.BaseUri = new Uri(address.EndsWith('/') ? address : address + "/");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        return host;
    }

    /// <summary>
    /// Assert that the web server logged no unhandled exception, i.e. no error that ASP.NET Core logged on behalf of an endpoint.
    /// Endpoints catch their own exceptions and log them via EndpointHelper, those are not counted here
    /// </summary>
    public void AssertNoUnhandledException()
    {
        LogEntry[] unhandled = Log.Entries.Where(entry => entry.IsUnhandledException).ToArray();
        Assert.That(unhandled, Is.Empty, () => "Unhandled exceptions in DuetWebServer:\n" + string.Join('\n', unhandled.Select(e => e.ToString())));
    }

    /// <summary>
    /// Stop DuetWebServer and delete the temporary directory
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try
            {
                _host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            }
            catch (ObjectDisposedException)
            {
                // Already stopped
            }
        }
        if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0 && !await Task.Run(() => _thread.Join(StartStopTimeout)))
        {
            Assert.Fail($"DuetWebServer did not stop within {StartStopTimeout.TotalSeconds} s");
        }
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Left behind in the temporary directory
        }
    }

    /// <summary>
    /// Observer that hooks into the host that is built on the given thread
    /// </summary>
    private sealed class ListenerObserver(Thread mainThread, string directory, LogCollector log, TaskCompletionSource<IHost> built) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Thread.CurrentThread != mainThread)
            {
                // Another host (none of ours)
                return;
            }

            if (value.Key == "HostBuilding" && value.Value is IHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(new CollectingLoggerProvider(log)));
                hostBuilder.ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
                    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
                });
            }
            else if (value.Key == "HostBuilt" && value.Value is IHost host)
            {
                built.TrySetResult(host);
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }

    /// <summary>
    /// Logger provider that keeps the entries in a <see cref="LogCollector"/>.
    /// Set DSF_DWS_LOG to any value to see the log in the test output as well
    /// </summary>
    private sealed class CollectingLoggerProvider(LogCollector log) : ILoggerProvider
    {
        private static readonly bool WriteToProgress = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DSF_DWS_LOG"));

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, log);

        public void Dispose() { }

        private sealed class CollectingLogger(string category, LogCollector log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                LogEntry entry = new(DateTime.Now, logLevel, category, eventId, formatter(state, exception), exception);
                log.Add(entry);
                if (WriteToProgress)
                {
                    TestContext.Progress.WriteLine(entry.ToString());
                }
            }
        }
    }
}

/// <summary>
/// Log entry of the web server
/// </summary>
/// <param name="Time">Time of the entry</param>
/// <param name="Level">Log level</param>
/// <param name="Category">Logger category</param>
/// <param name="EventId">Event ID</param>
/// <param name="Message">Formatted message</param>
/// <param name="Exception">Exception or null</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, EventId EventId, string Message, Exception? Exception)
{
    /// <summary>
    /// Whether ASP.NET Core logged an exception that an endpoint, a middleware or a hosted service did not handle.
    /// Kestrel logs it as "An unhandled exception was thrown by the application" (EventId 13, ApplicationError),
    /// the hosting layer and the WebSocket and routing middlewares in their own categories
    /// </summary>
    public bool IsUnhandledException => Level >= LogLevel.Error && (Category.StartsWith("Microsoft.AspNetCore") || Category.StartsWith("Microsoft.Extensions.Hosting") || Level == LogLevel.Critical);

    /// <inheritdoc />
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level}] {Category}: {Message}" + ((Exception is not null) ? $" {Exception}" : string.Empty);
}

/// <summary>
/// Thread-safe list of log entries
/// </summary>
public sealed class LogCollector
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>
    /// Add an entry
    /// </summary>
    /// <param name="entry">Entry to add</param>
    public void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Copy of the entries so far
    /// </summary>
    public LogEntry[] Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Number of entries so far, to look only at the ones that follow
    /// </summary>
    public int Count
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Entries from the given index on
    /// </summary>
    /// <param name="start">Index of the first entry</param>
    /// <returns>Entries</returns>
    public LogEntry[] Since(int start)
    {
        lock (_entries)
        {
            return [.. _entries.Skip(start)];
        }
    }
}
