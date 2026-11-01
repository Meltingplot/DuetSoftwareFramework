using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the M-codes that MCodeHandler handles for DSF itself or changes before RRF: M111, M118, M122, M550-M552, M586, M929 and M998.
/// Only paths that stay inside the test host are used: no network connection, hostname change or plugin
/// </summary>
[TestFixture]
public class SystemCodes
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Codes that reached the firmware, including those it discarded
    /// </summary>
    private string[] Received() => [.. _host.Firmware.GetReceivedCodes().Select(code => code.Text)];

    private Settings Settings => _host.Services.GetRequiredService<IOptions<Settings>>().Value;

    /// <summary>
    /// Wait for generic messages that contain the given text. The message logger adds them without waiting for the object model lock
    /// </summary>
    private async Task<string[]> WaitForMessagesAsync(string text)
    {
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (true)
        {
            using (await _host.Model.AccessReadOnlyAsync(cts.Token))
            {
                string[] messages = [.. _host.Model.Messages.Select(message => message.Content).Where(content => content.Contains(text))];
                if (messages.Length > 0)
                {
                    return messages;
                }
            }
            await Task.Delay(10, cts.Token);
        }
    }

    [Test]
    public async Task ReportLogLevel()
    {
        Settings.LogLevel = LogLevel.Warning;

        Assert.That(await _host.ExecuteAsync("M111 P-1"), Is.EqualTo("Current DCS log level: Warning\n"));
        Assert.That(Received(), Is.Empty);
    }

    // Levels as listed in the wiki for M111 P-1 in SBC mode
    [TestCase("trace", LogLevel.Trace)]
    [TestCase("debug", LogLevel.Debug)]
    [TestCase("info", LogLevel.Information)]
    [TestCase("warning", LogLevel.Warning)]
    [TestCase("error", LogLevel.Error)]
    [TestCase("fatal", LogLevel.Critical)]
    public async Task SetLogLevel(string level, LogLevel expected)
    {
        Assert.That(await _host.ExecuteAsync($"M111 P-1 S\"{level}\""), Is.EqualTo("\n"));
        Assert.That(Settings.LogLevel, Is.EqualTo(expected));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetInvalidLogLevel()
    {
        LogLevel before = Settings.LogLevel;

        Assert.That(await _host.ExecuteAsync("M111 P-1 S\"verbose\""), Does.StartWith("Error: M111: Invalid log level 'verbose'"));
        Assert.That(Settings.LogLevel, Is.EqualTo(before));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DisableLogMessagesWithoutEnabling()
    {
        Assert.That(await _host.ExecuteAsync("M111 P-1 O0"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task LogMessagesToClients()
    {
        // The wiki: O1 outputs DCS log messages as generic messages via DWC. This crashed DCS with a stack overflow when
        // MessageLogger.Log used ObjectModel.OutputAsync, which logs the message again and so called MessageLogger.Log again
        Assert.That(await _host.ExecuteAsync("M111 P-1 O1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M111 P-1 S\"info\""), Is.EqualTo("\n"));
        Assert.That(await WaitForMessagesAsync("Log level changed to Information"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task OutputMessageWhileLoggingToClients()
    {
        // ObjectModel.OutputAsync logs the message before it adds it, and that log message goes to the message logger as well
        Assert.That(await _host.ExecuteAsync("M111 P-1 O1"), Is.EqualTo("\n"));
        await _host.Model.OutputAsync(new Message(MessageType.Warning, "Output while logging to clients")).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(await WaitForMessagesAsync("Output while logging to clients"), Is.Not.Empty);
    }

    [Test]
    public async Task DebugLevelOfFirmwareModule()
    {
        // Only P-1 is meant for DCS, the other modules belong to RRF
        Assert.That(await _host.ExecuteAsync("M111 P4 S1"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M111 P4 S1" }));
    }

    [Test]
    public async Task PublishMqttMessageWithoutClient()
    {
        // P6 is the MQTT client, which DCS provides in SBC mode. It has not been enabled with M586 P4 S1
        Assert.That(await _host.ExecuteAsync("M118 P6 S\"message\" T\"topic\""), Is.EqualTo("Error: M118: MQTT client is not connected\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SendMessageToOtherTarget()
    {
        Assert.That(await _host.ExecuteAsync("M118 S\"Hello Duet\""), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M118 S\"Hello Duet\"" }));
    }

    [Test]
    public async Task DsfDiagnostics()
    {
        // The wiki: M122 "DSF" reports immediate DSF diagnostics
        Assert.That(await _host.ExecuteAsync("M122 \"DSF\""), Does.StartWith("=== Duet Control Server ===\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DiagnosticsOfFirmwareAndDsf()
    {
        _host.Firmware.OnCode("M122", _ => FakeReply.Ok("=== Diagnostics ===\n"));

        // DCS appends its own diagnostics to the main board report of RRF
        string reply = await _host.ExecuteAsync("M122");
        Assert.That(reply, Does.StartWith("=== Diagnostics ===\n=== Duet Control Server ===\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M122" }));
    }

    [TestCase("M122 P1")]
    [TestCase("M122 B1")]
    public async Task DiagnosticsOfFirmwareOnly(string code)
    {
        // Reports with P or of other boards are left as they are
        _host.Firmware.OnCode("M122", _ => FakeReply.Ok("report\n"));

        Assert.That(await _host.ExecuteAsync(code), Is.EqualTo("report\n"));
        Assert.That(Received(), Is.EqualTo(new[] { code }));
    }

    [Test]
    public async Task ReportName()
    {
        Assert.That(await _host.ExecuteAsync("M550"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M550" }));
    }

    [Test]
    public async Task SetNameOfHost()
    {
        // A name with the letters and digits of the hostname is passed on to RRF, which updates its name too
        string name = $"{Environment.MachineName}-";
        Assume.That(name.Length, Is.LessThanOrEqualTo(40));

        Assert.That(await _host.ExecuteAsync($"M550 P\"{name}\""), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { $"M550 P\"{name}\"" }));
    }

    [Test]
    public async Task SetNameOtherThanHost()
    {
        // The name of an SBC follows its hostname, which only DuetPiManagementPlugin changes
        Assert.That(await _host.ExecuteAsync($"M550 P\"{Environment.MachineName}x9\""), Is.EqualTo("Error: M550: Machine name must consist of the same letters and digits as configured by the Linux hostname\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetNameTooLong()
    {
        Assert.That(await _host.ExecuteAsync($"M550 P\"{new string('a', 41)}\""), Is.EqualTo("Error: M550: Machine name is too long\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetPassword()
    {
        // DWS checks the password from the object model, RRF gets the code as well
        Assert.That(await _host.ExecuteAsync("M551 P\"my-very-secret-word\""), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Password, Is.EqualTo("my-very-secret-word"));
        }
        Assert.That(Received(), Is.EqualTo(new[] { "M551 P\"my-very-secret-word\"" }));
    }

    [Test]
    public async Task NetworkWithoutManagementPlugin()
    {
        // The wiki: in SBC mode DuetPiManagementPlugin handles M552. DCS answers if the plugin has not intercepted it
        Assert.That(await _host.ExecuteAsync("M552 S1"), Is.EqualTo("Error: M552: M552 is reserved for SBC mode\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetCorsSite()
    {
        Assert.That(await _host.ExecuteAsync("M586 C\"http://example.com\""), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Network.CorsSite, Is.EqualTo("http://example.com"));
        }

        // An empty site resets it
        Assert.That(await _host.ExecuteAsync("M586 C\"\""), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Network.CorsSite, Is.Null);
        }
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task EnableMqttWithoutConfiguration()
    {
        // The wiki: M586.4 configures the client before M586 P4 S1 enables it. Without a configuration DCS does not connect
        Assert.That(await _host.ExecuteAsync("M586 P4 S1 H\"127.0.0.1\""), Is.EqualTo("Error: M586: Use M586.4 to configure MQTT client options first\n"));
        Assert.That(await _host.ExecuteAsync("M586 P4 S0"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ConfigureMqtt()
    {
        // M586.4 only stores the client options, the connection is made by M586 P4 S1, which is not sent here
        Assert.That(await _host.ExecuteAsync("M586.4 C\"duet\" U\"username\" K\"password\" S\"subscription\" O2"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.Empty);
    }

    // The wiki: S1 = WARN, S2 = INFO, S3 = DEBUG
    [TestCase(1, EventLogLevel.Warn, "warn")]
    [TestCase(2, EventLogLevel.Info, "info")]
    [TestCase(3, EventLogLevel.Debug, "debug")]
    public async Task EventLogging(int sParam, EventLogLevel level, string levelName)
    {
        // Same wording as Platform::ConfigureLogging
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is disabled\n"));

        // Like Logger::Start (OpenSysFile) the file is in the system directory
        Assert.That(await _host.ExecuteAsync($"M929 P\"events.txt\" S{sParam}"), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.LogLevel, Is.EqualTo(level));
            Assert.That(_host.Model.State.LogFile, Is.EqualTo("events.txt"));
        }
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo($"Event logging is enabled at log level {levelName}\n"));
        Assert.That(System.IO.File.Exists(Path.Combine(_host.BaseDirectory, "sys", "events.txt")), Is.True);

        Assert.That(await _host.ExecuteAsync("M929 S0"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is disabled\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.LogLevel, Is.EqualTo(EventLogLevel.Off));
        }
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task EventLoggingToDefaultFile()
    {
        // The wiki: a default file name is used if P is missing, DEFAULT_LOG_FILE is "eventlog.txt" in RRF's Configuration.h
        Assert.That(await _host.ExecuteAsync("M929 S1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is enabled at log level warn\n"));
        Assert.That(System.IO.File.Exists(Path.Combine(_host.BaseDirectory, "sys", "eventlog.txt")), Is.True);
        Assert.That(await _host.ExecuteAsync("M929 S0"), Is.EqualTo("\n"));
    }

    [TestCase(4, "parameter 'S' too high")]
    [TestCase(-1, "expected non-negative integer after 'S'")]
    public async Task EventLoggingWithInvalidLevel(int sParam, string error)
    {
        // Platform.cpp:3387-3388 (ConfigureLogging) stops logging before GetLimitedUIValue('S', 0, 4) checks the value.
        // GCodeBuffer.cpp:623-626 reports a value that is too high, BinaryParser.cpp:272-275 a negative one
        Assert.That(await _host.ExecuteAsync("M929 S1"), Is.EqualTo("\n"));

        Assert.That(await _host.ExecuteAsync($"M929 S{sParam}"), Is.EqualTo($"Error: M929: {error}\n"));
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is disabled\n"));
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M998")]
    [TestCase("M998 P34")]
    public void ResendRequest(string code)
    {
        // DCS does not support resend requests and throws NotSupportedException, which the IPC server reports to the client.
        // RRF replies "Checksum error on line 34" instead (the M998 case in GCodes2.cpp)
        Assert.That(async () => await _host.ExecuteAsync(code), Throws.TypeOf<NotSupportedException>());
        Assert.That(Received(), Is.Empty);
    }
}
