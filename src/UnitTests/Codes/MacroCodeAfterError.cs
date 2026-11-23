using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests that no code of a macro runs after a code of it has failed, and that the codes before it still run (design/open-findings.md, L7).
/// MacroFile.RunAsync starts up to BufferedMacroCodes codes before it waits for the first one. The codes it had read ahead carried the token
/// of the channel instead of the macro's, so aborting the macro did not cancel them, and it aborted the macro only when it got to the failed
/// code, after the pipeline had gone on with the next ones
/// </summary>
[TestFixture]
public class MacroCodeAfterError
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Codes the firmware executed on the HTTP channel, without the M98 that started the macro
    /// </summary>
    private string[] Executed() => [.. _host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text).Where(text => !text.StartsWith("M98 P\"test.g\""))];

    /// <summary>
    /// Error messages that DSF output to the object model
    /// </summary>
    private async Task<string[]> ErrorMessagesAsync()
    {
        using (await _host.Model.AccessReadOnlyAsync())
        {
            return [.. _host.Model.Messages.Where(message => message.Type == MessageType.Error).Select(message => message.Content)];
        }
    }

    // RRF refuses to set an unknown variable (SbcInterface.cpp:1137-1143), so SetVariableAsync throws in KeywordHandler
    private const string RefusedByFirmware = "set var.missing = 1";

    // DSF throws while it substitutes the SBC fields of a code outside a loop (CodeFile.GetIterations)
    private const string FailingInDsf = "G1 X{iterations}";

    [TestCase(RefusedByFirmware, "global after = true\nG4 P1\n")]
    [TestCase(RefusedByFirmware, "G4 P1\nglobal after = true\n")]
    [TestCase(FailingInDsf, "global after = true\nG4 P1\n")]
    [TestCase(FailingInDsf, "G4 P1\nglobal after = true\n")]
    public async Task NoCodeRunsAfterFailingCode(string failingLine, string linesAfter)
    {
        // The line before the failing one has run, the lines after it must not: neither a meta command that DSF handles nor a code for RRF
        _host.WriteMacro("test.g", $"G4 P0\n{failingLine}\n{linesAfter}");
        string reply = await _host.ExecuteAsync("M98 P\"test.g\"");

        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P0" }), $"reply: {reply}");
        Assert.That(_host.Firmware.ObjectModel.TryGetGlobal("after", out _), Is.False, "the global of the line after the failing one was created");
        Assert.That((await ErrorMessagesAsync()).Count(message => message.StartsWith("in file test.g line 2: ")), Is.EqualTo(1));
    }

    [Test]
    public async Task NoCodeRunsAfterFailingCodeInLongMacro()
    {
        // More lines after the failing one than DSF reads ahead (BufferedMacroCodes, 16 by default)
        string linesAfter = string.Concat(Enumerable.Range(1, 40).Select(i => $"G4 P{i}\n"));
        _host.WriteMacro("test.g", $"G4 P0\n{RefusedByFirmware}\nglobal after = true\n{linesAfter}");
        string reply = await _host.ExecuteAsync("M98 P\"test.g\"");

        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P0" }), $"reply: {reply}");
        Assert.That(_host.Firmware.ObjectModel.TryGetGlobal("after", out _), Is.False, "the global of the line after the failing one was created");
    }

    [Test]
    public async Task CodesBeforeLineThatCannotBeReadStillRun()
    {
        // CodeFile throws for an unexpected else while it reads the line (CodeFile.cs, "unexpected else"). RRF has executed the lines before it by then,
        // so DSF must not cancel those that it has read ahead and not finished yet. The lines after it are not read.
        // RRF holds M400 until the error is reported, so echo waits in its flush and the G-codes after it wait behind echo while DSF reads the else
        using ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        _host.WriteMacro("test.g", "M400\necho \"before\"\nG4 P1\nG4 P2\nelse\nG4 P99\n");
        Task<string> m98 = _host.StartCode("M98 P\"test.g\"");

        Stopwatch stopwatch = Stopwatch.StartNew();
        while ((await ErrorMessagesAsync()).Length == 0 && stopwatch.Elapsed < FakeFirmwareHost.DefaultTimeout)
        {
            await Task.Delay(10);
        }
        release.Set();
        string reply = await m98.WaitAsync(FakeFirmwareHost.DefaultTimeout);

        Assert.That(Executed(), Is.EqualTo(new[] { "M400", "G4 P1", "G4 P2" }), $"reply: {reply}");

        // The message can name the line after the else (line 12 for an else in line 11, also without this fix), so the line is not checked here
        string[] errors = await ErrorMessagesAsync();
        Assert.That(errors, Has.Length.EqualTo(1));
        Assert.That(errors[0], Does.StartWith("in file test.g line ").And.EndWith(": unexpected else"));
    }

    [Test]
    public async Task ErrorResultDoesNotAbortMacro()
    {
        // An error result is not a failed code. RRF goes on with the macro after a code that returns an error: ActOnCode only reports a
        // GCodeException (GCodes2.cpp:155-162). DSF does the same: MacroFile.RunAsync only reports an error result
        _host.Firmware.OnCode("M42", _ => FakeReply.Error("M42: test error"));
        _host.WriteMacro("test.g", "G4 P0\nM42 P0 S1\nglobal after = true\nG4 P1\n");
        string reply = await _host.ExecuteAsync("M98 P\"test.g\"");

        Assert.That(Executed(), Is.EqualTo(new[] { "G4 P0", "M42 P0 S1", "G4 P1" }), $"reply: {reply}");
        Assert.That(_host.Firmware.ObjectModel.TryGetGlobal("after", out _), Is.True);
    }
}
