using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the flow control of meta G-code in macro files, which DSF performs itself while it reads the file
/// (CodeFile.ReadCodeAsync) and which RRF never sees: if/elif/else, while with iterations, break and continue, skip and abort.
/// The macros are started with M98 like a client does, the fake firmware executes the codes that DSF sends
/// </summary>
[TestFixture]
public class MetaCommandFiles
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
    /// Run a macro with M98 and return the reply
    /// </summary>
    private async Task<string> RunMacroAsync(string content)
    {
        _host.WriteMacro("test.g", content);
        return await _host.ExecuteAsync("M98 P\"test.g\"");
    }

    /// <summary>
    /// Error messages that DSF output to the object model, e.g. for an error while reading a macro (MacroFile.cs:417-418)
    /// </summary>
    private async Task<string[]> ErrorMessagesAsync()
    {
        using (await _host.Model.AccessReadOnlyAsync())
        {
            return [.. _host.Model.Messages.Where(message => message.Type == MessageType.Error).Select(message => message.Content)];
        }
    }

    #region Conditional construct
    [TestCase("true", "false", "false", "G1 X1")]
    [TestCase("false", "true", "false", "G1 X2")]
    [TestCase("false", "false", "true", "G1 X3")]
    [TestCase("false", "false", "false", "G1 X4")]
    [TestCase("true", "true", "true", "G1 X1")]
    [TestCase("false", "true", "true", "G1 X2")]
    public async Task IfElifElse(string condition1, string condition2, string condition3, string expected)
    {
        // The wiki (Gcode_meta_commands.md, Conditional construct): the first block whose condition is true runs, else the else block.
        // A true condition makes CodeFile skip the following elif blocks without evaluating them (CodeFile.cs:479-488)
        Assert.That(await RunMacroAsync($"if {condition1}\n  G1 X1\nelif {condition2}\n  G1 X2\nelif {condition3}\n  G1 X3\nelse\n  G1 X4\nG1 X5\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { expected, "G1 X5" }));
    }

    [Test]
    public async Task NestedConditions()
    {
        Assert.That(await RunMacroAsync("if true\n  if false\n    G1 X1\n  else\n    G1 X2\n  G1 X3\nelse\n  G1 X4\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X2", "G1 X3" }));
    }

    [Test]
    public async Task ConditionFromFirmware()
    {
        // move.axes[0].homed is owned by the firmware, so DSF forwards the condition and evaluates the result (CodeFile.cs:508-518)
        Assert.That(await RunMacroAsync("if move.axes[0].homed\n  G1 X1\nelif move.axes[0].letter == \"X\"\n  G1 X2\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X2" }));
    }

    [TestCase("if 1\n  G1 X1\nG1 X2\n", "invalid conditional result '1', must be either true or false")]
    [TestCase("elif true\n  G1 X1\nG1 X2\n", "unexpected elif condition")]
    [TestCase("else\n  G1 X1\nG1 X2\n", "unexpected else")]
    [TestCase("if true\n  G1 X0\n    else\n  G1 X1\nG1 X2\n", "unexpected else")]
    [TestCase("G1 X0\nbreak\nG1 X2\n", "break or continue cannot be called outside while loop")]
    [TestCase("if true\n  continue\nG1 X2\n", "break or continue cannot be called outside while loop")]
    [TestCase("while true\nG1 X2\n", "empty while loop detected")]
    public async Task InvalidFlowControl(string content, string error)
    {
        // CodeFile throws a CodeParserException while reading, MacroFile reports it and aborts the macro (MacroFile.cs:408-426),
        // so the lines after the error are not executed
        string reply = await RunMacroAsync(content);
        Assert.That(Executed(), Does.Not.Contain("G1 X2"), $"reply: {reply}");
        Assert.That(await ErrorMessagesAsync(), Has.Some.Contains(error));
    }
    #endregion

    #region Loops
    [Test]
    public async Task WhileWithIterations()
    {
        // The wiki (Loop): 'iterations' is the number of completed iterations of the innermost loop. DSF substitutes it before it sends the code
        Assert.That(await RunMacroAsync("while iterations < 3\n  G1 X{iterations}\nG1 Y0\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X{0}", "G1 X{1}", "G1 X{2}", "G1 Y0" }));
    }

    [Test]
    public async Task BreakAndContinue()
    {
        // break ends the innermost loop, continue starts its next iteration (wiki, Loop; CodeFile.cs:536-558)
        Assert.That(await RunMacroAsync("while true\n  if iterations == 1\n    continue\n  if iterations == 3\n    break\n  G1 X{iterations}\nG1 Y0\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X{0}", "G1 X{2}", "G1 Y0" }));
    }

    [Test]
    public async Task NestedLoops()
    {
        // Each loop counts its own iterations (wiki, Nested loops)
        Assert.That(await RunMacroAsync("while iterations < 2\n  G1 X{iterations}\n  while iterations < 2\n    G1 Y{iterations}\n  G1 Z0\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X{0}", "G1 Y{0}", "G1 Y{1}", "G1 Z0", "G1 X{1}", "G1 Y{0}", "G1 Y{1}", "G1 Z0" }));
    }

    [Test]
    public async Task LoopWithLocalVariable()
    {
        // The end of each iteration deletes the local variables of the loop body, so the next iteration can declare them again (CodeFile.cs:398-421)
        Assert.That(await RunMacroAsync("while iterations < 2\n  var x = iterations * 10\n  G1 X{var.x}\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X{var.x}", "G1 X{var.x}" }));
        Assert.That(await ErrorMessagesAsync(), Is.Empty);
    }

    [Test]
    public async Task IterationsOutsideLoop()
    {
        // CodeFile.GetIterations throws "'iterations' used when not inside a loop" (CodeFile.cs:220) while DSF substitutes the SBC fields,
        // and MacroFile reports the exception with the file and line (MacroFile.cs:441-452)
        await RunMacroAsync("G1 X{iterations}\n");
        Assert.That(Executed(), Is.Empty);
        Assert.That(await ErrorMessagesAsync(), Has.Some.EqualTo("in file test.g line 1: 'iterations' used when not inside a loop"));
    }

    #endregion

    #region Other keywords
    [Test]
    public async Task Skip()
    {
        // skip does nothing (wiki, Skip command; CodeFile.cs:569-571)
        Assert.That(await RunMacroAsync("skip\nif true\n  skip\nG1 X1\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X1" }));
    }

    [Test]
    public async Task Abort()
    {
        // The wiki: abort terminates all nested macros, the message is presented to the user. CodeFile closes the file (CodeFile.cs:560-563)
        await RunMacroAsync("G1 X1\nabort \"stopped \" ^ 1\nG1 X2\n");
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X1" }));
    }

    [Test]
    public async Task EchoInMacro()
    {
        // echo writes the expressions separated by spaces (wiki, Echo command). Replies to codes of a macro are asynchronous,
        // so DSF outputs them as generic messages instead of returning them with the reply to M98 (Executed.cs:212-215)
        Assert.That(await RunMacroAsync("var x = 5\necho \"x is\", var.x, var.x * 2\n"), Is.EqualTo("\n"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Messages.Select(message => message.ToString()), Has.Some.EqualTo("x is 5 10"));
        }
    }

    [Test]
    public async Task LineNumber()
    {
        // 'line' is the line number of the code in the file, which DSF substitutes (Expressions.cs:704-710)
        Assert.That(await RunMacroAsync("G1 X1\n\nG1 X{line}\n"), Is.EqualTo("\n"));
        Assert.That(Executed(), Is.EqualTo(new[] { "G1 X1", "G1 X{3}" }));
    }
    #endregion
}
