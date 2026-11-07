using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Records the warnings and errors that the DCS services of a <see cref="FakeFirmwareHost"/> log, so that tests can tell an error
/// the IPC server reports to the client from an exception it did not expect (Server.ProcessConnectionAsync logs those as
/// "Terminating connection due to unexpected exception")
/// </summary>
public sealed class IpcLog : ILoggerProvider
{
    /// <summary>
    /// Logged entry
    /// </summary>
    /// <param name="Category">Logger category, i.e. the class name</param>
    /// <param name="Level">Log level</param>
    /// <param name="Message">Formatted message</param>
    /// <param name="Exception">Logged exception</param>
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception)
    {
        /// <inheritdoc />
        public override string ToString() => $"[{Level}] {Category}: {Message}" + ((Exception is not null) ? $" {Exception}" : string.Empty);
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>
    /// Attach a new log to the logger factory of a host. The logger factory applies its default minimum level, so only
    /// entries of level Information and above are recorded
    /// </summary>
    /// <param name="host">Host to attach to</param>
    /// <returns>Attached log</returns>
    public static IpcLog Attach(FakeFirmwareHost host)
    {
        IpcLog log = new();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(log);
        return log;
    }

    /// <summary>
    /// Recorded entries
    /// </summary>
    public IReadOnlyList<Entry> Entries => [.. _entries];

    /// <summary>
    /// Discard the recorded entries
    /// </summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Entries that report an exception the IPC server did not expect (Server.ProcessConnectionAsync, catch block)
    /// </summary>
    public IReadOnlyList<Entry> UnexpectedExceptions => [.. _entries.Where(entry => entry.Category == typeof(DuetControlServer.IPC.Server).FullName && entry.Message.Contains("unexpected exception"))];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class Logger(IpcLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                log._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
