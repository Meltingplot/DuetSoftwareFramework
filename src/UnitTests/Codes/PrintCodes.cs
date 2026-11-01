using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the job and motion system codes that MCodeHandler handles before RRF, in the cases that need no job file:
/// M0/M1, M23/M32, M24, M26, M27, M37, M598 and M606. The fake firmware does not emulate job files
/// </summary>
[TestFixture]
public class PrintCodes
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

    [TestCase("M0")]
    [TestCase("M1")]
    public async Task StopWithoutJob(string code)
    {
        // Without a selected file DCS leaves M0/M1 to RRF, which refuses to cancel a print that is not paused (the M0 case in GCodes2.cpp and the wiki)
        _host.Firmware.OnCode(code, _ => FakeReply.Error("Pause the print before attempting to cancel it"));

        Assert.That(await _host.ExecuteAsync(code), Is.EqualTo($"Error: {code}: Pause the print before attempting to cancel it\n"));
        Assert.That(Received(), Is.EqualTo(new[] { code }));
    }

    [Test]
    public async Task SelectFileWithoutName()
    {
        Assert.That(await _host.ExecuteAsync("M23"), Is.EqualTo("Error: M23: Filename expected\n"));
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M23")]
    [TestCase("M32")]
    public async Task SelectMissingFile(string code)
    {
        Assert.That(await _host.ExecuteAsync($"{code} \"missing.g\""), Is.EqualTo($"Error: {code}: Could not find file missing.g\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ResumeWithoutFile()
    {
        // Same text as the M24 case in GCodes2.cpp
        Assert.That(await _host.ExecuteAsync("M24"), Is.EqualTo("Error: M24: Cannot print, because no file is selected!\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetFilePositionWithoutFile()
    {
        Assert.That(await _host.ExecuteAsync("M26 S5"), Is.EqualTo("Error: M26: Not printing a file\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task PrintStatusWithoutFile()
    {
        // The wiki and the M27 case in GCodes2.cpp: "Not SD printing." if no file is being printed
        Assert.That(await _host.ExecuteAsync("M27"), Is.EqualTo("Not SD printing.\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SimulateMissingFile()
    {
        Assert.That(await _host.ExecuteAsync("M37 P\"missing.g\""), Is.EqualTo("Error: M37: GCode file \"missing.g\" not found\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SynchroniseFromOtherChannel()
    {
        // DCS only makes its own file readers meet at M598, RRF synchronises the motion systems
        Assert.That(await _host.ExecuteAsync("M598"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M598" }));
    }

    [Test]
    public async Task PushAndPop()
    {
        // RRF keeps the stack, DCS only resynchronises its object model after M121 when there are multiple motion systems
        Assert.That(await _host.ExecuteAsync("M120"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M121"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M120", "M121" }));
    }

    [Test]
    public async Task ReportForkState()
    {
        // The wiki: without S RRF reports whether the input stream has been forked
        Assert.That(await _host.ExecuteAsync("M606"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M606" }));
    }

    [Test]
    public async Task ForkWithoutSecondFileChannel()
    {
        // Without a second file channel (boards without multiple motion systems) DCS leaves M606 S1 to RRF
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[(int)CodeChannel.File2] = null;
        }

        Assert.That(await _host.ExecuteAsync("M606 S1"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M606 S1" }));
    }

    /// <summary>
    /// Reply to M606 S from a channel other than File like GCodes5.cpp:213-217 (ForkInputReader)
    /// </summary>
    private void WarnAboutForkFromOtherChannel() =>
        _host.Firmware.OnCode("M606", code => (code.HasParameter('S') && code.Channel != CodeChannel.File) ? FakeReply.Warning("this command is valid only when running a job from a stored file") : null);

    [Test]
    public async Task ForkFromOtherChannel()
    {
        // GCodes5.cpp:213-217 and the wiki (Gcodes.md:8108): from a channel other than File "a warning is issued but it is otherwise ignored".
        // MCodeHandler used to call JobProcessor.ForkAsync for any channel and replied "No file is selected" as an error
        WarnAboutForkFromOtherChannel();

        Assert.That(await _host.ExecuteAsync("M606 S1"), Is.EqualTo("Warning: M606: this command is valid only when running a job from a stored file\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M606 S1" }));
    }

    [Test]
    public async Task ForkFromOtherChannelWithFileSelected()
    {
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "a.g"), "G1 X1\n");
        Assert.That(await _host.ExecuteAsync("M23 \"a.g\""), Is.EqualTo("\n"));
        WarnAboutForkFromOtherChannel();

        // With a job file, MCodeHandler used to fork it although RRF ignores the code. JobProcessor then reported two job streams
        // and started reading the second one together with the job (JobProcessor.cs:719-724)
        Assert.That(await _host.ExecuteAsync("M606 S1"), Is.EqualTo("Warning: M606: this command is valid only when running a job from a stored file\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M23 \"a.g\"", "M606 S1" }));
        Assert.That(_host.Services.GetRequiredService<JobProcessor>().NumJobStreams, Is.EqualTo(1));
    }
}
