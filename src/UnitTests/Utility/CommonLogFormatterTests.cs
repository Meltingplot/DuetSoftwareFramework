using System;
using System.IO;
using System.Linq;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// The console log formatter gives journald the syslog priority of every line, so that a flood of warnings uses up only the rate limit
/// of the warnings (journald-rate-limit.c, one pool per priority class). Before, every line of DCS was stored with priority 6
/// </summary>
[TestFixture]
public class CommonLogFormatterTests
{
    private static string Format(bool journal, LogLevel level, string category, string message, Exception? exception = null)
    {
        CommonLogFormatter formatter = new(journal);
        using StringWriter writer = new() { NewLine = "\n" };
        formatter.Write(new LogEntry<string>(level, category, new EventId(0), message, exception, (state, _) => state), null, writer);
        return writer.ToString();
    }

    [TestCase(LogLevel.Trace, "<7>[trace] ")]
    [TestCase(LogLevel.Debug, "<7>[debug] ")]
    [TestCase(LogLevel.Information, "<6>[info] ")]
    [TestCase(LogLevel.Warning, "<4>[warn] ")]
    [TestCase(LogLevel.Error, "<3>[error] ")]
    [TestCase(LogLevel.Critical, "<2>[fatal] ")]
    public void JournalLinesStartWithTheSyslogPriority(LogLevel level, string start)
    {
        Assert.That(Format(true, level, "DuetControlServer.Link.Adapter.SPI", "Bad header CRC16"), Is.EqualTo(start + "Bad header CRC16\n"));
    }

    /// <summary>
    /// The lines of an exception are journal entries of their own, so each of them needs the priority of the entry
    /// </summary>
    [Test]
    public void EveryLineOfAnEntryHasItsPriority()
    {
        Exception exception;
        try
        {
            throw new TimeoutException("No transfer with RepRapFirmware completed within 60000 ms");
        }
        catch (TimeoutException e)
        {
            exception = e;
        }

        string[] lines = Format(true, LogLevel.Error, "Microsoft.Extensions.Hosting.Internal.Host", "BackgroundService failed", exception).Split('\n');
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("<3>[error] BackgroundService failed"));
            Assert.That(lines[1], Does.StartWith("<3>   System.TimeoutException: No transfer with RepRapFirmware completed within 60000 ms"));
            Assert.That(lines.Length, Is.GreaterThan(3), "the stack trace has lines of its own");
            Assert.That(lines[..^1], Has.All.StartWith("<3>"));
            Assert.That(lines[^1], Is.Empty, "the entry ends with a newline");
            Assert.That(lines.Any(line => line.Contains('\x1b')), Is.False, "the journal gets no colors");
        });
    }

    [Test]
    public void MultiLineMessagesKeepTheirPriority()
    {
        Assert.That(Format(true, LogLevel.Warning, "DuetControlServer.Model.ObjectModel", "first\nsecond"), Is.EqualTo("<4>[warn] first\n<4>second\n"));
    }

    /// <summary>
    /// On a terminal, e.g. DuetControlServer -u started over SSH, the output stays as it was
    /// </summary>
    [Test]
    public void TerminalOutputIsUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Format(false, LogLevel.Warning, "DuetControlServer.Link.Adapter.SPI", "Bad header CRC16"), Is.EqualTo("[\x1b[33mwarn\x1b[0m] Bad header CRC16\n"));
            Assert.That(Format(false, LogLevel.Information, "IPC", "Connected"), Is.EqualTo("[\x1b[32minfo\x1b[0m] IPC: Connected\n"));
        });
    }
}
