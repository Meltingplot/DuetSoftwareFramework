using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the M-codes that MCodeHandler handles inside macros: M98 R and the result variables of M260/M261
/// </summary>
[TestFixture]
public class MacroCodes
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private string[] Executed() => [.. _host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text)];

    [TestCase(1, ExpectedResult = true)]
    [TestCase(0, ExpectedResult = false)]
    public async Task<bool> PausableMacro(int rParam)
    {
        // Hold the macro after M98 R so that its state can be read
        using ManualResetEventSlim release = new();
        _host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok() : FakeReply.NotFinished);
        _host.WriteMacro("pausable.g", $"M98 R1\nM98 R{rParam}\nM400\n");

        Task<string> m98 = _host.StartCode("M98 P\"pausable.g\"");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes().Any(code => code.Text == "M400"), FakeFirmwareHost.DefaultTimeout), Is.True);

        // The wiki: R1 means the rest of the macro can be paused, R0 that it cannot. DCS flags its macro file, RRF gets the code as well
        bool isPausable = _host.Services.GetRequiredService<Manager>()[CodeChannel.HTTP].CurrentState.File is MacroFile macro && macro.IsPausable;

        release.Set();
        Assert.That(await m98.WaitAsync(FakeFirmwareHost.DefaultTimeout), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"pausable.g\"", "M98 R1", $"M98 R{rParam}", "M400" }));
        return isPausable;
    }

    /// <summary>
    /// Error replies that RRF sent for codes of the macro
    /// </summary>
    private string[] ErrorReplies() => [.. _host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply && packet.MessageType.HasFlag(MessageTypeFlags.ErrorMessageFlag)).Select(packet => packet.Text)];

    [Test]
    public async Task LocalVariablesInBlocks()
    {
        // DCS deletes the local variables of a block when it ends (DeleteLocalVariable), so a sibling block can declare the same name.
        // This is the path the result variables of M260/M261 are meant to follow, see ResultVariablesInBlocks
        _host.WriteMacro("vars.g", "if true\n  var x = 1\nif true\n  var x = 2\nG4 P1\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"vars.g\""), Is.EqualTo("\n"));
        Assert.That(ErrorReplies(), Is.Empty);
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"vars.g\"", "G4 P1" }));
    }

    [Test]
    public async Task ResultVariablesInBlocks()
    {
        // Platform.cpp:2516-2521 (GetResultVariable) creates the variable of V and fails if a variable of that name exists.
        // MCodeHandler.CodeExecutedAsync registers it with the block that is current when RRF has replied. CodeFile marks the block when it
        // reads the code, like var, so the block end waits for the reply. Before, the reader could end the block first, the variable went to
        // another block, and the second code failed with "variable 'x' already exists"
        _host.WriteMacro("i2c.g", "if true\n  M261 A1 B2 V\"x\"\nif true\n  M260 A1 B2 V\"x\"\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"i2c.g\""), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"i2c.g\"", "M261 A1 B2 V\"x\"", "M260 A1 B2 V\"x\"" }));
        Assert.That(ErrorReplies(), Is.Empty);
        Assert.That(_host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.DeleteLocalVariable && request.Text == "x"), Is.True);
    }

    [Test]
    public async Task ResultVariableInLoop()
    {
        // Each iteration declares the result variable again, so the end of every iteration must delete it
        _host.WriteMacro("i2c.g", "var i = 0\nwhile var.i < 2\n  M261 A1 B2 V\"x\"\n  set var.i = var.i + 1\nG4 P1\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"i2c.g\""), Is.EqualTo("\n"));
        Assert.That(ErrorReplies(), Is.Empty);
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"i2c.g\"", "M261 A1 B2 V\"x\"", "M261 A1 B2 V\"x\"", "G4 P1" }));
    }

    [Test]
    public async Task ResultVariableAtFileLevel()
    {
        // A result variable declared outside a block lives until the macro ends
        _host.WriteMacro("i2c.g", "M261 A1 B2 V\"x\"\nG4 P1\n");

        Assert.That(await _host.ExecuteAsync("M98 P\"i2c.g\""), Is.EqualTo("\n"));
        Assert.That(ErrorReplies(), Is.Empty);
        Assert.That(Executed(), Is.EqualTo(new[] { "M98 P\"i2c.g\"", "M261 A1 B2 V\"x\"", "G4 P1" }));
    }
}
