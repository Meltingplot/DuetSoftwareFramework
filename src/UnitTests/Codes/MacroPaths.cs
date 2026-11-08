using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

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
        // A missing file is reported back to RRF, which replies "Macro file ... not found" (GCodes2.cpp:1718-1721, Processor.cs:1357-1361)
        Assert.That(await TryExecuteAsync("M98 P\"missing.g\""), Is.EqualTo("Error: M98: Macro file missing.g not found\n"));
        Assert.That(await TryExecuteAsync("M117 \"alive\""), Is.EqualTo("\n"));
    }

    [TestCase("../../escape.g")]
    [TestCase("0:/../escape.g")]
    [TestCase("9:/macros/escape.g")]
    public async Task MacroPathRefusedByResolver(string fileName)
    {
        // FilePathResolver throws for a path that escapes the volume (FilePathResolver.cs:63-72) or an unknown drive (FilePathResolver.cs:80-95).
        // The macro cannot be run, so it is reported as not found like a missing file (Processor.DoMacroFile), and DCS goes on.
        // Confirmed on the lab printer before the fix (DSF 3.7.0+mp.13, RRF 3.7.0+1.mp.8, 2026-11-07): M98 P"99:/macros/..." got no reply, DCS logged
        // "Invalid drive index" from GetDrivePath via DoMacroFile and HandleMacroRequest, the host stopped (BackgroundServiceExceptionBehavior StopHost),
        // systemd killed DCS after the 15 s stop timeout and restarted it, and RRF was halted until M999. Drive 9 exists there, so the test used 99.
        // RRF without an SBC cannot open these names either: FatFs refuses ".." (FF_FS_RPATH 0, ffconf.h:156, ff.c:3361-3367) and drives
        // from FF_VOLUMES (2, ffconf.h:169) on (ff.c:3628-3650), so f_open fails and M98 replies the same (GCodes.cpp:3729-3737, GCodes2.cpp:1718-1721)
        string? reply = await TryExecuteAsync($"M98 P\"{fileName}\"");
        string? firmwareCode = await TryExecuteAsync("M117 \"alive\"");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.EqualTo($"Error: M98: Macro file {fileName} not found\n"), "M98 got no reply");
            Assert.That(firmwareCode, Is.EqualTo("\n"), "the link to the firmware is gone");
        });
    }

    /// <summary>
    /// Run a macro that calls the given macro file and then a code of its own
    /// </summary>
    /// <param name="fileName">Macro file called by the macro</param>
    /// <returns>Reply, macro requests of DSF, messages of RRF and the codes RRF executed</returns>
    private async Task<string[]> RunCallingMacroAsync(string fileName)
    {
        int requestsBefore = _host.Firmware.GetReceivedRequests().Count, packetsBefore = _host.Firmware.GetSentPackets().Count;
        int codesBefore = _host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Count;
        _host.WriteMacro("calling.g", $"M98 P\"{fileName}\"\nG4 P1\n");
        string reply = await _host.ExecuteAsync("M98 P\"calling.g\"");
        return
        [
            reply,
            .. _host.Firmware.GetReceivedRequests().Skip(requestsBefore)
                .Where(request => request.Request is SbcRequest.MacroStarted or SbcRequest.MacroCompleted).Select(request => $"{request.Request} {request.Flag}"),
            .. _host.Firmware.GetSentPackets().Skip(packetsBefore)
                .Where(packet => packet.Request == FirmwareRequest.Message).Select(packet => $"{packet.MessageType}: {packet.Text}"),
            .. _host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Skip(codesBefore).Select(code => code.Text)
        ];
    }

    [TestCase("../../escape.g")]
    [TestCase("9:/macros/escape.g")]
    public async Task RefusedPathInMacroEndsLikeMissingFile(string fileName)
    {
        // DSF answers both with MacroCompleted and the error flag (Processor.cs:1357-1361, 910-935), so RRF, its replies and the calling macro go on the same way
        string[] missing = await RunCallingMacroAsync("missing.g");
        string[] refused = await RunCallingMacroAsync(fileName);
        Assert.That(refused, Is.EqualTo(missing.Select(text => text.Replace("missing.g", fileName))));
        Assert.That(await TryExecuteAsync("M117 \"alive\""), Is.EqualTo("\n"));
    }
}
