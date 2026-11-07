using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Files;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the macros that RRF starts itself: config.g on the trigger channel (GCodes::RunConfigFile, GCodes.cpp:412-420) and daemon.g
/// on the daemon channel (GCodes.cpp:680-691), and for the copies of code blocks that forked files use
/// </summary>
[TestFixture]
public class SystemMacros
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private string[] Executed(CodeChannel channel) => [.. _host.Firmware.GetExecutedCodes(channel).Select(code => code.Text)];

    private void RunSystemMacro(CodeChannel channel, string fileName, string lastCode)
    {
        _host.Firmware.RequestMacro(channel, fileName);
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(channel).Any(code => code.Text == lastCode) && firmware.GetStackDepth(channel) == 0,
                    FakeFirmwareHost.DefaultTimeout), Is.True, $"{fileName} did not finish");
    }

    [Test]
    public async Task ConfigFile()
    {
        // Before config.g DSF sends the host name and the date and time (MacroFile.cs:299-331)
        _host.WriteMacro("config.g", "G1 X1\nM400\n");
        RunSystemMacro(CodeChannel.Trigger, "config.g", "M400");

        string[] codes = Executed(CodeChannel.Trigger);
        Assert.That(codes, Has.Length.EqualTo(4));
        Assert.That(codes[0], Is.EqualTo($"M550 P\"{Environment.MachineName}\""));
        Assert.That(codes[1], Does.Match("^M905 P\"[0-9]{4}-[0-9]{2}-[0-9]{2}\" S\"[0-9]{2}:[0-9]{2}:[0-9]{2}\"$"));
        Assert.That(codes[2..], Is.EqualTo(new[] { "G1 X1", "M400" }));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.StartupError, Is.Null);
        }
    }

    [Test]
    public async Task ErrorInConfigFile()
    {
        // An error while config.g runs becomes the startup error (MacroFile.cs:417, ObjectModel.HandleMacroErrorAsync)
        _host.WriteMacro("config.g", "M400\nelse\nG1 X1\n");
        RunSystemMacro(CodeChannel.Trigger, "config.g", "M400");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Trigger) == 0, FakeFirmwareHost.DefaultTimeout), Is.True);

        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.State.StartupError?.File, Is.EqualTo("config.g"));
            Assert.That(_host.Model.State.StartupError?.Message, Is.EqualTo("unexpected else"));
        }
        Assert.That(Executed(CodeChannel.Trigger), Does.Not.Contain("G1 X1"));
    }

    [Test]
    public void Daemon()
    {
        // daemon.g runs like any other macro, DSF only logs its start and end at a lower level (MacroFile.cs:239-246, 485-492)
        _host.WriteMacro("daemon.g", "G1 X1\nM400\n");
        RunSystemMacro(CodeChannel.Daemon, "daemon.g", "M400");
        Assert.That(Executed(CodeChannel.Daemon), Is.EqualTo(new[] { "G1 X1", "M400" }));
    }

    [Test]
    public void AbortDaemon()
    {
        // abort terminates the macro (wiki, Abort command)
        _host.WriteMacro("daemon.g", "M400\nabort \"stop\"\nG1 X1\n");
        RunSystemMacro(CodeChannel.Daemon, "daemon.g", "M400");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetStackDepth(CodeChannel.Daemon) == 0, FakeFirmwareHost.DefaultTimeout), Is.True);
        Assert.That(Executed(CodeChannel.Daemon), Is.EqualTo(new[] { "M400" }));
    }

    [Test]
    public void CopyCodeBlock()
    {
        // A forked file copies its blocks with a with-expression; the local variables must be a separate list (CodeBlock.cs:26-47)
        CodeBlock original = new(new Code("  while true") { FilePosition = 12, LineNumber = 3 }, true)
        {
            LineNumbersStarted = true,
            SeenCodes = true,
            ExpectingElse = true,
            ContinueLoop = true,
            Iterations = 4,
            HasLocalVariables = true,
            EndPosition = 99,
            AllowVariableRedeclaration = true
        };
        original.LocalVariables.Add("x");

        CodeBlock copy = original with { };
        Assert.That(copy, Is.Not.SameAs(original));
        Assert.That((copy.Indent, copy.FilePosition, copy.LineNumber, copy.Keyword, copy.ProcessBlock), Is.EqualTo((2, (long?)12, (long?)3, KeywordType.While, true)));
        Assert.That((copy.LineNumbersStarted, copy.SeenCodes, copy.ExpectingElse, copy.ContinueLoop, copy.Iterations), Is.EqualTo((true, true, true, true, 4)));
        Assert.That((copy.HasLocalVariables, copy.EndPosition, copy.AllowVariableRedeclaration), Is.EqualTo((true, 99L, true)));
        Assert.That(copy.LocalVariables, Is.EqualTo(new[] { "x" }));

        copy.LocalVariables.Clear();
        Assert.That(original.LocalVariables, Is.EqualTo(new[] { "x" }));

        // A while block ends at the same indentation, a var block only at a smaller one (CodeBlock.cs:79)
        Assert.That(original.IsFinished(2), Is.True);
        Assert.That(new CodeBlock(new Code("  var x = 1"), true, KeywordType.Var).IsFinished(2), Is.False);
    }
}
