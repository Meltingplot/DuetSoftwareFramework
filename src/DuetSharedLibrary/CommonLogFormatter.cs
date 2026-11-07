using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace DuetSharedLibrary;

/// <summary>
/// Custom console formatter that provides a common log output format
/// </summary>
public sealed class CommonLogFormatter : ConsoleFormatter
{
    /// <summary>
    /// Whether the output goes to the systemd journal, see <see cref="CommonLogFormatter(bool)"/>
    /// </summary>
    private readonly bool _journal;

    /// <summary>
    /// Constructor. systemd sets JOURNAL_STREAM for a service whose output goes to the journal
    /// </summary>
    public CommonLogFormatter() : this(Environment.GetEnvironmentVariable("JOURNAL_STREAM") is not null) { }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="journal">Whether the output goes to the systemd journal. Every line then starts with the syslog priority of its entry,
    /// so that journald stores it with that priority and rate-limits each priority on its own, and colors are left out</param>
    public CommonLogFormatter(bool journal) : base(nameof(CommonLogFormatter))
    {
        _journal = journal;

        // Disable console output buffering to ensure journalctl -f works correctly
        // When stdout is not a terminal (e.g., systemd service), it's fully buffered by default
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
    }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string? message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (logEntry.Exception == null && message == null)
        {
            return;
        }

        // Get log level name and color
        string levelName = GetLogLevelString(logEntry.LogLevel);
        string colorCode = _journal ? string.Empty : GetColorCode(logEntry.LogLevel);
        string resetCode = _journal ? string.Empty : "\x1b[0m";

        // Get category name (logger name)
        string categoryName = logEntry.Category;

        // Format: [level] [logger:] message
        StringBuilder entry = new();
        entry.Append('[').Append(colorCode).Append(levelName).Append(resetCode).Append("] ");

        // Only include category if it doesn't contain '.' and doesn't end with '.g'
        bool includeCategory = !categoryName.Contains('.') && !categoryName.EndsWith(".g");
        if (includeCategory)
        {
            entry.Append(categoryName).Append(": ");
        }

        // Write message
        if (!string.IsNullOrEmpty(message))
        {
            entry.Append(message);
        }

        // Write exception if present and different from message
        if (logEntry.Exception != null)
        {
            string exceptionString = logEntry.Exception.ToString();
            if (message != exceptionString)
            {
                entry.Append('\n');

                // Color the exception based on log level
                string exceptionColor = logEntry.LogLevel >= LogLevel.Error ? colorCode : string.Empty;
                entry.Append(exceptionColor).Append("   ").Append(exceptionString);
                if (!string.IsNullOrEmpty(exceptionColor))
                {
                    entry.Append(resetCode);
                }
            }
        }

        if (_journal)
        {
            // journald reads the priority from the start of each line, so the lines of an exception keep the priority of their entry
            string priority = $"<{GetSyslogPriority(logEntry.LogLevel)}>";
            textWriter.Write(priority);
            textWriter.Write(entry.ToString().ReplaceLineEndings("\n" + priority));
            textWriter.Write('\n');
        }
        else
        {
            textWriter.Write(entry.ToString().ReplaceLineEndings(textWriter.NewLine));
            textWriter.WriteLine();
        }
    }

    /// <summary>
    /// Get the syslog priority of the log level (sd-daemon.h)
    /// </summary>
    private static int GetSyslogPriority(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace or LogLevel.Debug => 7,  // SD_DEBUG
            LogLevel.Information => 6,              // SD_INFO
            LogLevel.Warning => 4,                  // SD_WARNING
            LogLevel.Error => 3,                    // SD_ERR
            LogLevel.Critical => 2,                 // SD_CRIT
            _ => 6
        };
    }

    /// <summary>
    /// Get the ANSI color code for the log level
    /// </summary>
    private static string GetColorCode(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "\x1b[90m",           // Gray
            LogLevel.Debug => "\x1b[2;37m",         // Dim Gray
            LogLevel.Information => "\x1b[32m",     // Green
            LogLevel.Warning => "\x1b[33m",         // Yellow
            LogLevel.Error => "\x1b[31m",           // Red
            LogLevel.Critical => "\x1b[1m\x1b[31m", // Bold Red
            _ => "\x1b[0m"                          // Default/Reset
        };
    }

    /// <summary>
    /// Get the string representation of the log level
    /// </summary>
    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => logLevel.ToString().ToLowerInvariant()
        };
    }
}

/// <summary>
/// Options for the common log formatter
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed class CommonLogFormatterOptions : ConsoleFormatterOptions
{
    // No additional options needed currently, but can be extended if needed
}

/// <summary>
/// Logging builder extensions for the common log formatter
/// </summary>
public static class CommonLogFormatterExtensions
{
    /// <summary>
    /// Register the common console log formatter
    /// </summary>
    /// <param name="builder">Logging builder</param>
    /// <returns>Logging builder</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "CommonLogFormatterOptions is annotated with DynamicallyAccessedMemberTypes.All so its members survive trimming for configuration binding")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "CommonLogFormatterOptions adds no members of its own, so binding it needs no runtime generic instantiation")]
    public static ILoggingBuilder AddCommonLogFormatter(this ILoggingBuilder builder) => builder.AddConsoleFormatter<CommonLogFormatter, CommonLogFormatterOptions>();
}
