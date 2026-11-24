using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Codes.Meta.Parsing;
using DuetControlServer.Commands;
using Code = DuetControlServer.Commands.Code;
using DuetControlServer.Files.Parser;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Machine;

/// <summary>
/// Both expression parsers of DSF descend recursively per nesting level (MetaExpressionParser.ParseInternal, eatExpression in
/// Expressions.EvaluateExpressionAsync). A deep enough nesting overflowed the stack, which ends the process because a stack overflow cannot be
/// caught. An expression reaches them from any code, from a ";customInfo" line in the header of a G-code file (FileInfoParser.cs:544-558), which
/// every client listing the file asks for, and from M581.1 conditions. RRF refuses such an expression with "Expression nesting too deep"
/// (ExpressionParser::CheckStack in RRF)
/// </summary>
[TestFixture]
public class ExpressionNesting
{
    private const int DeepNesting = 100_000;

    private static string Nested(int depth) => new string('(', depth) + "1" + new string(')', depth);

    /// <summary>
    /// Run an action on a thread with a known stack size, so that the depth at which the stack runs out does not depend on the test runner
    /// </summary>
    private static void RunWithStack(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    [Test]
    public void ParserRefusesDeepNesting()
    {
        Assert.That(() => RunWithStack(() => MetaExpressionParser.TryEvaluate(Nested(DeepNesting), null, out _)),
            Throws.InstanceOf<CodeParserException>().With.Message.StartsWith("Expression nesting too deep"));
    }

    [Test]
    public void ParserEvaluatesModerateNesting()
    {
        object? value = null;
        RunWithStack(() => Assert.That(MetaExpressionParser.TryEvaluate(Nested(500), null, out value), Is.True));
        Assert.That(value, Is.EqualTo(1));
    }

    /// <summary>
    /// FakeFirmwareHost for the tests that need DCS services
    /// </summary>
    private FakeFirmwareHost? _host;

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    /// <summary>
    /// A G-code file with a deeply nested ";customInfo" value. Its key is left out like any other key that fails to evaluate
    /// (FileInfoParser.cs:96-107), and the other keys are still read
    /// </summary>
    [Test]
    public async Task CustomInfoWithDeepNestingIsSkipped()
    {
        _host = await FakeFirmwareHost.StartAsync();
        string path = Path.Combine(_host.BaseDirectory, "gcodes", "deep.gcode");
        await System.IO.File.WriteAllTextAsync(path, $";customInfo deep = {{{Nested(DeepNesting)}}}\n;customInfo ok = 1\nG1 X1\n");

        GCodeFileInfo info = await _host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);

        Assert.That(info.CustomInfo.Keys, Is.EquivalentTo(new[] { "ok" }));
    }

    /// <summary>
    /// Expressions of which only the SBC fields are replaced, for example the conditions of SBC triggers, are taken apart by eatExpression
    /// </summary>
    [Test]
    public async Task SbcFieldReplacementRefusesDeepNesting()
    {
        _host = await FakeFirmwareHost.StartAsync();
        Expressions expressions = _host.Services.GetRequiredService<Expressions>();
        Code code = _host.Services.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;

        Task<string>? evaluation = null;
        RunWithStack(() => evaluation = expressions.EvaluateExpressionToStringAsync(code, Nested(DeepNesting), true, false));

        Assert.That(async () => await evaluation!, Throws.InstanceOf<CodeParserException>().With.Message.StartsWith("Expression nesting too deep"));
    }
}
