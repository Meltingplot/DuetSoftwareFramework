using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Text codes with fileexists() or fileread() that RRF passes on to DSF as a DoCode request. In SBC mode RRF does this for the G/M/T-codes and the
/// global and set global commands it gets on USB, PanelDue (Aux) or from a trigger action, so that it never waits for the SBC while it holds a write lock.
/// The other meta commands are covered as well because DWC can send them in the same way.
/// DSF runs such a code as a prioritized code of its channel (Processor.DoFirmwareCode, Processor.cs:1253-1264; SimpleCode.cs:111-115) and
/// evaluates the functions itself, they are custom functions of DSF (Functions.cs:26-28):
/// <list type="bullet">
/// <item>a G/M/T-code with them goes through Code.ProcessInternally, which substitutes only the SBC fields and custom functions
/// (Code.cs:238 -> CodeProcessor.cs:168 -> Expressions.EvaluateAsync with evaluateAll = false, Expressions.cs:459-475, :884-906, :1028-1031)
/// and then goes to RRF as a binary code of the same channel</item>
/// <item>global, var and set substitute the functions and send RRF a SetVariable request (KeywordHandler.cs:261, :273)</item>
/// <item>echo evaluates everything (KeywordHandler.cs:166), so RRF gets EvaluateExpression requests with the substituted rest</item>
/// </list>
/// The result goes back to RRF as a message to the channel alone, which finishes the DoCode (Executed.cs, SbcInterface.cpp:774-793 of RRF).
/// A code that fails in DSF is answered with its error, else RRF's channel stays busy until it is aborted or the connection ends
/// </summary>
[TestFixture]
public class FirmwareCodeFileFunctions
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();
        _host.WriteMacro("present.g", "G4 P1\n");
        _host.WriteMacro("data.csv", "1,2,3\n");
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Message flags of a message to a single channel, as DSF sends the result of a code from the firmware (Executed.cs:179)
    /// </summary>
    private static MessageTypeFlags Destination(CodeChannel channel) => (MessageTypeFlags)(1 << (int)channel);

    /// <summary>
    /// Let the firmware pass a text code on to DSF and get the number of requests DSF had sent before
    /// </summary>
    private int PassCodeToSbc(CodeChannel channel, string code)
    {
        int requestsBefore = _host.Firmware.GetReceivedRequests().Count;
        _host.Firmware.PassCodeToSbc(channel, code);
        return requestsBefore;
    }

    /// <summary>
    /// Requests that DSF sent since the code was passed on, without the object model polls
    /// </summary>
    private IReadOnlyList<FakeSbcRequest> RequestsSince(int requestsBefore) =>
        [.. _host.Firmware.GetReceivedRequests().Skip(requestsBefore).Where(request => request.Request != SbcRequest.GetObjectModel)];

    /// <summary>
    /// Check that no request DSF sent asks RRF to evaluate one of the file functions
    /// </summary>
    private void AssertNoFileFunctionSent(int requestsBefore)
    {
        foreach (FakeSbcRequest request in RequestsSince(requestsBefore))
        {
            string content = request.Text + " " + request.Argument + " " + request.Code?.Text;
            Assert.That(content, Does.Not.Contain("fileexists").And.Not.Contain("fileread"), $"DSF passed a file function on to RRF: {request}");
        }
    }

    /// <summary>
    /// Wait until DSF has finished the DoCode with a message to its channel alone and the channel is free again
    /// </summary>
    private void AssertFinished(CodeChannel channel, string code, string result)
    {
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(channel), Timeout), Is.True,
            "DSF did not finish the code that RRF passed on");
        Assert.That(_host.Firmware.GetFinishedSbcCodes()[0], Is.EqualTo((code, result)));
        Assert.That(_host.Firmware.GetReceivedMessages().Select(message => message.MessageFlags), Has.Some.EqualTo(Destination(channel)));
        Assert.That(_host.Firmware.GetUnemulated(), Is.Empty);
    }

    #region G/M-codes that go on to RRF
    [TestCase(CodeChannel.USB, "present.g", "{true}")]
    [TestCase(CodeChannel.USB, "missing.g", "{false}")]
    [TestCase(CodeChannel.Aux, "present.g", "{true}")]
    [TestCase(CodeChannel.Aux, "missing.g", "{false}")]
    [TestCase(CodeChannel.Trigger, "present.g", "{true}")]
    public void CodeCarriesFileExistsResult(CodeChannel channel, string file, string value)
    {
        // DSF replaces the call by its value and keeps the curly braces, so RRF still sees an expression (Expressions.cs:459-475, :906).
        // M118 is not handled by DSF without P6 (MCodeHandler.cs:819-829), so it goes on to RRF
        int requestsBefore = PassCodeToSbc(channel, $"M118 S{{fileexists(\"0:/sys/{file}\")}}");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(channel).Count == 1, Timeout), Is.True, "DSF did not send the code on to RRF");

        FakeCode code = _host.Firmware.GetReceivedCodes(channel)[0];
        Assert.That(code.Command, Is.EqualTo("M118"));
        Assert.That(code.GetParameter('S')!.IsExpression, Is.True);
        Assert.That((string)code.GetParameter('S')!, Is.EqualTo(value));
        Assert.That(code.Flags.HasFlag(CodeFlags.IsFromFirmware), Is.True);

        // DSF asked RRF nothing before it sent the code
        AssertNoFileFunctionSent(requestsBefore);
        Assert.That(RequestsSince(requestsBefore).Select(request => request.Request), Is.EqualTo(new[] { SbcRequest.Code }));
    }

    [Test]
    public void CodeKeepsTheRestOfTheExpressionForRrf()
    {
        // Only the custom function is replaced, the operators around it are left for RRF to evaluate (Expressions.cs:1028-1031)
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, "G4 P{fileexists(\"0:/sys/present.g\") ? 1 : 0}");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.USB).Count == 1, Timeout), Is.True, "DSF did not send the code on to RRF");

        FakeCode code = _host.Firmware.GetReceivedCodes(CodeChannel.USB)[0];
        Assert.That(code.Command, Is.EqualTo("G4"));
        Assert.That((string)code.GetParameter('P')!, Is.EqualTo("{true ? 1 : 0}"));
        AssertNoFileFunctionSent(requestsBefore);
    }

    [TestCase(CodeChannel.USB)]
    [TestCase(CodeChannel.Aux)]
    public void CodeCarriesFileReadResult(CodeChannel channel)
    {
        // fileread takes the file, the number of elements to skip, the number to read and a delimiter character (Functions.cs:82-106,
        // wiki Gcode_meta_commands.md:468). # gives the number of elements DSF read (Functions.cs:230, Expressions.cs:564-569)
        int requestsBefore = PassCodeToSbc(channel, "M118 S{#fileread(\"0:/sys/data.csv\", 0, 3, ',')}");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(channel).Count == 1, Timeout), Is.True, "DSF did not send the code on to RRF");

        FakeCode code = _host.Firmware.GetReceivedCodes(channel)[0];
        Assert.That((string)code.GetParameter('S')!, Is.EqualTo("{3}"));
        AssertNoFileFunctionSent(requestsBefore);
        Assert.That(RequestsSince(requestsBefore).Select(request => request.Request), Is.EqualTo(new[] { SbcRequest.Code }));
    }

    [Test]
    public void CodeIsSentOnTheChannelOfTheDoCode()
    {
        // RRF takes a binary code of a channel whose text code is with DSF (GCodes.cpp:721-762 -> SbcInterface::FillBuffer, RRF fork 3d4f542c3),
        // which ends the executingOnSbc state of its buffer (BinaryParser::Put). That only works if DSF sends the code on the channel of the DoCode:
        // codes from the firmware do not move to another channel (CodeProcessor.cs:215-221)
        PassCodeToSbc(CodeChannel.Aux, "M118 S{fileexists(\"0:/sys/present.g\")}");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes().Count == 1, Timeout), Is.True, "DSF did not send the code on to RRF");
        Assert.That(_host.Firmware.GetReceivedCodes()[0].Channel, Is.EqualTo(CodeChannel.Aux));
    }

    [TestCase(CodeChannel.USB)]
    [TestCase(CodeChannel.Aux)]
    [Explicit("The fake firmware holds a binary code of a channel while that channel's text code is with DSF (FakeGCodes.cs:596-600), " +
              "RRF takes it (GCodes.cpp:721-762 -> SbcInterface::FillBuffer). Run this once the fake does the same")]
    public void CodeForRrfFinishesTheDoCode(CodeChannel channel)
    {
        PassCodeToSbc(channel, "M118 S{fileexists(\"0:/sys/present.g\")}");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(channel).Count == 1 && firmware.IsIdle(channel), Timeout), Is.True,
            "The code DSF sent on to RRF did not free the channel");
        Assert.That(_host.Firmware.GetExecutedCodes(channel)[0].Text, Is.EqualTo("M118 S{true}"));
    }
    #endregion

    #region Meta commands
    [TestCase(CodeChannel.USB, "global x = fileexists(\"0:/sys/present.g\")", "global.x", "true", true)]
    [TestCase(CodeChannel.Aux, "global x = fileexists(\"0:/sys/present.g\")", "global.x", "true", true)]
    [TestCase(CodeChannel.Trigger, "global x = fileexists(\"0:/sys/present.g\")", "global.x", "true", true)]
    [TestCase(CodeChannel.USB, "global x = fileexists(\"0:/sys/missing.g\")", "global.x", "false", false)]
    [TestCase(CodeChannel.USB, "global x = #fileread(\"0:/sys/data.csv\", 0, 3, ',')", "global.x", "3", 3)]
    [TestCase(CodeChannel.USB, "var x = fileexists(\"0:/sys/present.g\")", "var.x", "true", true)]
    public void NewVariableGetsTheSubstitutedValue(CodeChannel channel, string code, string varName, string expression, object value)
    {
        // KeywordHandler replaces the SBC fields and functions and lets RRF create the variable (KeywordHandler.cs:261, :269, :273)
        int requestsBefore = PassCodeToSbc(channel, code);
        AssertFinished(channel, code, "\n");

        FakeSbcRequest setVariable = RequestsSince(requestsBefore).Single(request => request.Request == SbcRequest.SetVariable);
        Assert.That(setVariable.Channel, Is.EqualTo(channel));
        Assert.That(setVariable.Flag, Is.True, "create");
        Assert.That(setVariable.Text, Is.EqualTo(varName));
        Assert.That(setVariable.Argument, Is.EqualTo(expression));
        AssertNoFileFunctionSent(requestsBefore);

        object? actual = null;
        bool exists = varName.StartsWith("global.") ? _host.Firmware.ObjectModel.TryGetGlobal("x", out actual) : _host.Firmware.TryGetLocalVariable(channel, "x", out actual);
        Assert.That(exists, Is.True);
        Assert.That(actual, Is.EqualTo(value));
    }

    [TestCase("set global.x = fileexists(\"0:/sys/config.g\")", "false", false)]
    [TestCase("set global.x = fileexists(\"0:/sys/present.g\")", "true", true)]
    [TestCase("set global.x = {fileexists(\"missing.g\")}", "{false}", false)]
    public async Task SetGetsTheSubstitutedValue(string code, string expression, bool value)
    {
        // There is no config.g in the test setup. A call inside { } keeps the braces (Expressions.cs:941-947)
        Assert.That(await _host.ExecuteAsync("global x = 5"), Is.EqualTo("\n"));
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, code);
        AssertFinished(CodeChannel.USB, code, "\n");

        FakeSbcRequest setVariable = RequestsSince(requestsBefore).Single(request => request.Request == SbcRequest.SetVariable);
        Assert.That(setVariable.Channel, Is.EqualTo(CodeChannel.USB));
        Assert.That(setVariable.Flag, Is.False, "create");
        Assert.That(setVariable.Text, Is.EqualTo("global.x"));
        Assert.That(setVariable.Argument, Is.EqualTo(expression));
        AssertNoFileFunctionSent(requestsBefore);
        Assert.That(_host.Firmware.ObjectModel.TryGetGlobal("x", out object? actual), Is.True);
        Assert.That(actual, Is.EqualTo(value));
    }

    [Test]
    public void GlobalGetsTheArrayOfFileRead()
    {
        // DSF writes the elements as an array expression in { } (Expressions.cs:607-610), which RRF still accepts (wiki Gcode_meta_commands.md:393-403).
        // The fake firmware does not evaluate array literals (FakeExpressionEvaluator.cs:23), so only the request is checked here
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, "global y = fileread(\"0:/sys/data.csv\", 0, 3, ',')");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Skip(requestsBefore).Any(request => request.Request == SbcRequest.SetVariable), Timeout), Is.True,
            "DSF did not send the variable to RRF");

        FakeSbcRequest setVariable = RequestsSince(requestsBefore).Single(request => request.Request == SbcRequest.SetVariable);
        Assert.That(setVariable.Text, Is.EqualTo("global.y"));
        Assert.That(setVariable.Argument, Is.EqualTo("{1,2,3}"));
        AssertNoFileFunctionSent(requestsBefore);
    }

    [TestCase(CodeChannel.USB)]
    [TestCase(CodeChannel.Aux)]
    public void EchoGetsTheSubstitutedValues(CodeChannel channel)
    {
        // echo evaluates all of its expressions (KeywordHandler.cs:166). The parser of DSF does not evaluate custom functions
        // (Machine/MetaExpressionParser.cs:379), so DSF substitutes them and asks RRF to evaluate the rest (Expressions.cs:1009-1037)
        const string code = "echo fileexists(\"0:/sys/present.g\"), fileexists(\"0:/sys/missing.g\")";
        int requestsBefore = PassCodeToSbc(channel, code);
        AssertFinished(channel, code, "true false\n");

        Assert.That(RequestsSince(requestsBefore).Where(request => request.Request == SbcRequest.EvaluateExpression).Select(request => (request.Channel, request.Text)),
            Is.EqualTo(new[] { (channel, "true"), (channel, "false") }));
        AssertNoFileFunctionSent(requestsBefore);
    }

    [Test]
    public void AbortGetsTheSubstitutedValue()
    {
        // abort evaluates its message like echo before it aborts the files of the channel (KeywordHandler.cs:166-171). How the DoCode ends after the
        // abort is not checked here
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, "abort fileexists(\"0:/sys/present.g\")");
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Skip(requestsBefore).Any(request => request.Request == SbcRequest.InvalidateChannel), Timeout), Is.True,
            "DSF did not abort the channel");

        Assert.That(RequestsSince(requestsBefore).Where(request => request.Request == SbcRequest.EvaluateExpression).Select(request => request.Text), Is.EqualTo(new[] { "true" }));
        AssertNoFileFunctionSent(requestsBefore);
    }

    [Test]
    public void IfIsAnsweredWithAnError()
    {
        // Code.ProcessInternally throws for if/elif/else/while outside a file (Code.cs:208-218). RRF does not pass them on, but DWC can send them
        const string code = "if fileexists(\"0:/sys/present.g\")";
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, code);
        AssertFinishedWithError(CodeChannel.USB, code, "Conditional codes must not be executed");
        Assert.That(RequestsSince(requestsBefore).Where(request => request.Request != SbcRequest.Message), Is.Empty);
    }
    #endregion

    #region Errors
    [TestCase("M118 S{fileexists(1)}", "fileexists requires a string argument")]
    [TestCase("M118 S{#fileread(\"0:/sys/missing.csv\", 0, 1, ',')}", "Could not find file")]
    [TestCase("global y = fileread(\"0:/sys/data.csv\", 0, \",\")", "fileread requires 4 arguments")]
    [TestCase("global y = fileread(\"0:/sys/data.csv\", 0, 3, \",\")", "fileread requires a delimiter character")]
    public void ErrorOfFileFunctionFinishesTheDoCode(string code, string message)
    {
        // RRF aborts a command whose fileread call fails (wiki Gcode_meta_commands.md:499), i.e. it finishes it with an error. In DSF the functions
        // throw (Functions.cs:72, :86, :105, :111) and the ProcessInternally stage cancels the code with the exception (ProcessInternally.cs:32-39,
        // CodeProcessor.CancelCode). The exception does not reach SimpleCode: codes from the firmware are asynchronous (Code.cs:136-141) and
        // Processor.DoFirmwareCode drops the task (Processor.cs:1263). So the Executed stage answers RRF with the error
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, code);
        AssertFinishedWithError(CodeChannel.USB, code, message);
        Assert.That(RequestsSince(requestsBefore).Where(request => request.Request != SbcRequest.Message), Is.Empty, "DSF asked RRF something about the code");
    }

    [Test]
    public async Task ErrorOfRrfFinishesTheDoCode()
    {
        // The SetVariable request fails in RRF because the variable exists. LinkInterface.SetVariableAsync throws the error of RRF, and the code ends
        // as in ErrorOfFileFunctionFinishesTheDoCode
        Assert.That(await _host.ExecuteAsync("global x = 5"), Is.EqualTo("\n"));
        const string code = "global x = fileexists(\"0:/sys/present.g\")";
        int requestsBefore = PassCodeToSbc(CodeChannel.USB, code);
        AssertFinishedWithError(CodeChannel.USB, code, "variable 'global.x' already exists");

        FakeSbcRequest setVariable = RequestsSince(requestsBefore).First(request => request.Request != SbcRequest.Message);
        Assert.That(setVariable.Request, Is.EqualTo(SbcRequest.SetVariable));
        Assert.That(setVariable.Argument, Is.EqualTo("true"));
    }

    /// <summary>
    /// Wait until DSF has finished the DoCode with an error message to its channel alone and the channel is free again
    /// </summary>
    private void AssertFinishedWithError(CodeChannel channel, string code, string message)
    {
        Assert.That(_host.Firmware.WaitUntil(firmware => firmware.GetFinishedSbcCodes().Count == 1 && firmware.IsIdle(channel), Timeout), Is.True,
            "DSF did not finish the code that RRF passed on");
        Assert.That(_host.Firmware.GetFinishedSbcCodes()[0].Code, Is.EqualTo(code));
        Assert.That(_host.Firmware.GetFinishedSbcCodes()[0].Result, Does.Contain(message));
        Assert.That(_host.Firmware.GetReceivedMessages().Select(item => item.MessageFlags), Has.Some.EqualTo(Destination(channel) | MessageTypeFlags.ErrorMessageFlag));
    }
    #endregion
}
