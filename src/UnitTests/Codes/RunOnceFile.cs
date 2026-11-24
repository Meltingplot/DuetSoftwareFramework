using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for runonce.g. In SBC mode RRF leaves it to the SBC, which runs it once RRF has started config.g (RepRap.cpp:639 of RRF 3.7), and
/// deletes it afterwards, as RRF does when it runs from SD card (RepRap.cpp:647-651) and DSF 3.6 did (Program.cs:252-286 of upstream v3.6-dev)
/// </summary>
[TestFixture]
public class RunOnceFile
{
    private FakeFirmwareHost _host = null!;
    private IHostedService? _periodicUpdateService;

    /// <summary>
    /// RRF reports "starting" until it has started config.g
    /// </summary>
    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync(firmware => firmware.ProcessingConfig = true);

    [TearDown]
    public async Task TearDown()
    {
        if (_periodicUpdateService is not null)
        {
            await _periodicUpdateService.StopAsync(CancellationToken.None);
        }
        await _host.DisposeAsync();
    }

    /// <summary>
    /// Start the service that runs runonce.g. The fake host leaves it out because it reads system information (FakeFirmwareHost.cs:37-40)
    /// </summary>
    private async Task StartPeriodicUpdateServiceAsync()
    {
        _periodicUpdateService = _host.Services.GetServices<IHostedService>().OfType<PeriodicUpdateService>().Single();
        await _periodicUpdateService.StartAsync(CancellationToken.None);
    }

    private string RunOnceFilePath => Path.Combine(_host.BaseDirectory, "sys", "runonce.g");

    private static bool IsRunOnceRequest(FakeCode code) => code.Type == DuetAPI.Commands.CodeType.MCode && code.MajorNumber == 98 && code.Text.Contains("runonce.g");

    private async Task<bool> WaitForAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + FakeFirmwareHost.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                return false;
            }
            await Task.Delay(20);
        }
        return true;
    }

    [Test]
    public async Task RunsAfterConfigFileAndIsDeleted()
    {
        _host.WriteMacro("config.g", "G1 X1\nM400\n");
        _host.WriteMacro("runonce.g", "G1 X5\n");
        await StartPeriodicUpdateServiceAsync();

        // Nothing happens while RRF has yet to start config.g
        await Task.Delay(1000);
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Trigger).Any(IsRunOnceRequest), Is.False, "runonce.g was requested before config.g");
        Assert.That(System.IO.File.Exists(RunOnceFilePath), Is.True);

        // RRF starts config.g and then clears processingConfig (RepRap.cpp:634-654)
        _host.Firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        _host.Firmware.ProcessingConfig = false;

        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G1 X5") && firmware.GetStackDepth(CodeChannel.Trigger) == 0,
                    FakeFirmwareHost.DefaultTimeout), Is.True, "runonce.g did not run");

        // It runs on the trigger channel after config.g. The periodic update may send M552 to that channel at any point (PeriodicUpdateService.cs)
        string[] codes = [.. _host.Firmware.GetExecutedCodes(CodeChannel.Trigger).Where(code => code.Text is "G1 X1" or "M400" or "G1 X5" || IsRunOnceRequest(code)).Select(code => code.Text)];
        Assert.That(codes, Has.Length.EqualTo(4));
        Assert.That(codes[0..2], Is.EqualTo(new[] { "G1 X1", "M400" }));
        Assert.That(codes[2], Does.StartWith("M98 "));
        Assert.That(codes[3], Is.EqualTo("G1 X5"));

        // Then it is deleted, so it runs only once
        Assert.That(await WaitForAsync(() => !System.IO.File.Exists(RunOnceFilePath)), Is.True, "runonce.g was not deleted");
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Trigger).Count(IsRunOnceRequest), Is.EqualTo(1));
    }

    [Test]
    public async Task NothingIsRequestedWithoutRunOnceFile()
    {
        _host.WriteMacro("config.g", "M400\n");
        await StartPeriodicUpdateServiceAsync();

        _host.Firmware.RequestMacro(CodeChannel.Trigger, "config.g");
        _host.Firmware.ProcessingConfig = false;
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "M400") && firmware.GetStackDepth(CodeChannel.Trigger) == 0,
                    FakeFirmwareHost.DefaultTimeout), Is.True, "config.g did not run");

        await Task.Delay(500);
        Assert.That(_host.Firmware.GetReceivedCodes(CodeChannel.Trigger).Any(IsRunOnceRequest), Is.False);
    }
}
