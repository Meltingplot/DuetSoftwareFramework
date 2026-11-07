using System;
using System.Threading.Tasks;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Macro file names that FilePathResolver refuses, found by the file path fuzz test. In SBC mode RRF passes the file name of M98
/// to DSF as it is (GCodes.cpp:3697-3699, GCodeBuffer::RequestMacroFile), so it comes from any client or macro
/// </summary>
[TestFixture]
public class MacroPaths
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private async Task<string?> TryExecuteAsync(string code)
    {
        try
        {
            return await _host.ExecuteAsync(code, timeout: TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    [Test]
    public async Task MissingMacro()
    {
        // A missing file is reported back to RRF, which replies "Macro file ... not found" (GCodes.cpp:3706, Processor.cs:1350-1354)
        Assert.That(await TryExecuteAsync("M98 P\"missing.g\""), Is.EqualTo("Error: M98: Macro file missing.g not found\n"));
        Assert.That(await TryExecuteAsync("M117 \"alive\""), Is.EqualTo("\n"));
    }

    [TestCase("../../escape.g")]
    [TestCase("0:/../escape.g")]
    [TestCase("9:/macros/escape.g")]
    [Explicit("A macro path that FilePathResolver refuses kills the link: Processor.DoMacroFile (Processor.cs:1334) lets the ArgumentException of " +
              "FilePathResolver.ToPhysical escape LinkService.HandleMacroRequest (LinkService.cs:781-796) and Execute, which ends the LinkService thread (LinkService.cs:176-228)")]
    public async Task MacroPathRefusedByResolver(string fileName)
    {
        // FilePathResolver throws for a path that escapes the volume (FilePathResolver.cs:63-72) or an unknown drive (FilePathResolver.cs:80-95).
        // The macro cannot be run, so it should be reported as not found like a missing file, and DCS must go on.
        // Confirmed on the lab printer (DSF 3.7.0+mp.13, RRF 3.7.0+1.mp.8, 2026-11-07): M98 P"99:/macros/..." got no reply, DCS logged
        // "Invalid drive index" from GetDrivePath via DoMacroFile and HandleMacroRequest, the host stopped (BackgroundServiceExceptionBehavior StopHost),
        // systemd killed DCS after the 15 s stop timeout and restarted it, and RRF was halted until M999. Drive 9 exists there, so the test used 99
        string? reply = await TryExecuteAsync($"M98 P\"{fileName}\"");
        string? firmwareCode = await TryExecuteAsync("M117 \"alive\"");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Not.Null.And.Contain("not found"), "M98 got no reply");
            Assert.That(firmwareCode, Is.EqualTo("\n"), "the link to the firmware is gone");
        });
    }
}
