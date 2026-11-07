using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Codes.Meta.Parsing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Fuzz;

/// <summary>
/// Fuzz tests for the meta G-code expressions that DSF evaluates itself: the SBC-side evaluator MetaExpressionParser, the
/// substitution of SBC fields and custom functions in Expressions, and a comparison with the expression evaluator of the fake firmware
/// on the part of the grammar that both take from RRF's ExpressionParser
/// </summary>
[TestFixture]
public class ExpressionFuzz
{
    /// <summary>
    /// Context with a few variables of each type. Functions that are not built into the parser are forwarded
    /// </summary>
    private sealed class Context : IExpressionEvaluationContext
    {
        private static readonly Dictionary<string, object?> Values = new()
        {
            ["var.x"] = 5,
            ["var.f"] = 1.5f,
            ["var.u"] = 3000000000u,
            ["var.s"] = "text",
            ["var.c"] = 'c',
            ["var.b"] = true,
            ["var.n"] = null,
            ["var.arr"] = new object?[] { 1, 2.5f, "a", new object?[] { true } },
            ["var.dt"] = new DateTime(2026, 11, 7, 12, 0, 0),
            ["var.d"] = new DriverId(1, 2),
            ["global.l"] = 5000000000L
        };

        public int? Iterations => 2;

        public int LineNumber => 7;

        public bool TryResolveIdentifier(string path, bool wantExists, bool wantArrayLength, out object? value)
        {
            if (wantExists)
            {
                value = Values.ContainsKey(path);
                return !path.StartsWith("move", StringComparison.Ordinal);
            }
            if (Values.TryGetValue(path, out value))
            {
                if (wantArrayLength)
                {
                    value = value switch { string s => s.Length, object?[] a => a.Length, _ => null };
                    return value is not null;
                }
                return true;
            }
            return false;
        }

        public bool TryCallFunction(string name, object?[] arguments, bool wantArrayLength, out object? value)
        {
            value = null;
            return false;
        }
    }

    private static readonly string[] SeedExpressions =
    [
        "1 + 2 * 3", "(1 + 2) * 3", "-var.x / 2", "var.x > 3 ? \"a\" : 'b'", "#var.arr", "#var.s", "var.arr[3][0]", "var.s[1]", "exists(var.x)", "exists(var.nothing)",
        "max(1, 2.5, var.x)", "min(var.u, 2)", "abs(-3)", "sqrt(2)", "sin(pi / 2) + cos(0) - tan(1)", "asin(1) + acos(0) + atan(1) + atan2(1, 1)", "degrees(pi) + radians(180)",
        "square(var.f)", "exp(1) * log(10)", "isnan(sqrt(-1))", "floor(2.5) + ceil(2.5) + round(2.5)", "mod(7, 3) + mod(7.5, 2)", "pow(2, 10)", "vector(3, var.s)",
        "take(\"abc\", 2) ^ drop(\"abc\", 1)", "take(var.arr, 2)", "find(\"abc\", 'b') + find(var.s, \"xt\")", "datetime(0) + 5", "var.dt - datetime(0)", "+var.dt",
        "\"a\" ^ var.f ^ var.c ^ var.b ^ var.n", "iterations + line", "null == var.n", "true && false || !var.b", "var.x = 5 && var.f != 1.5 || var.s == \"text\"",
        "var.x <= 5 && var.x >= 5 && var.x < 6 && var.x > 4", "{1, 2, 3}", "{var.x, {1}}[1][0]", "1.5e3 / 2", "3000000000 + 1", "var.u * 2", "global.l + 1", "0x1F + 1",
        "var.d == \"1.2\"", "'a' == \"a\"", "#{1, 2}", "move.axes[0].letter", "random(10) * 0", "fileexists(\"x\")", "{var.x}", "(((1)))", "1 ? 2 : 3", "\"\"\"\" ^ \"x\""
    ];

    private static readonly string[] Tokens =
    [
        "+", "-", "*", "/", "^", "&", "&&", "|", "||", "!", "=", "==", "!=", "<", "<=", ">", ">=", "?", ":", "#", "(", ")", "[", "]", "{", "}", ",", "\"", "'", ".",
        "var.x", "var.arr", "var.s", "var.dt", "var.u", "var.n", "null", "true", "pi", "iterations", "line", "exists(", "max(", "take(", "datetime(", "vector(",
        "1e40", "-0", "0x", "2147483647", "4294967296", "99999999999999999999", "1.5", " ", "ä"
    ];

    /// <summary>
    /// Whether an exception is the known one of ParseNumber for an exponent without digits or too many hex digits (MetaExpressionParserErrors)
    /// </summary>
    private static bool IsKnownNumberError(Exception e) => e is FormatException or OverflowException && (e.StackTrace?.Contains("MetaExpressionParser.ParseNumber") ?? false);

    private static IReadOnlyList<byte[]> Corpus => [.. SeedExpressions.Select(Encoding.UTF8.GetBytes)];

    private static IReadOnlyList<byte[]> Dictionary => [.. Tokens.Select(Encoding.UTF8.GetBytes)];

    /// <summary>
    /// Describe the result of an evaluation for the determinism check
    /// </summary>
    private static string Describe(bool resolved, object? value) => resolved ? Describe(value) : "(forwarded)";

    private static string Describe(object? value) => value switch
    {
        null => "null",
        object?[] array => "{" + string.Join(",", array.Select(Describe)) + "}",
        float f when float.IsNaN(f) => "NaN",
        _ => $"{value.GetType().Name}:{value}"
    };

    [Test]
    public void EvaluateOnSbc() => Fuzzer.Run(Corpus, input =>
    {
        // MetaExpressionParser.TryEvaluate returns a value, asks to forward the expression or throws a CodeParserException for syntax errors (MetaExpressionParser.cs:11-22, 56)
        string expression = Encoding.UTF8.GetString(input);
        string first;
        try
        {
            first = Describe(MetaExpressionParser.TryEvaluate(expression, new Context(), out object? value), value);
        }
        catch (Exception e) when (e is CodeParserException || IsKnownNumberError(e))
        {
            first = "(error)";
        }

        // The evaluation has no side effects, so it gives the same result again unless it draws a random number
        if (!expression.Contains("random"))
        {
            string second;
            try
            {
                second = Describe(MetaExpressionParser.TryEvaluate(expression, new Context(), out object? value), value);
            }
            catch (Exception e) when (e is CodeParserException || IsKnownNumberError(e))
            {
                second = "(error)";
            }
            if (first != second)
            {
                throw new FuzzFailure("determinism", $"First {first}, then {second}");
            }
        }
    }, Dictionary);

    #region Through DCS
    private FakeFirmwareHost? _host;

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    /// <summary>
    /// Expressions with SBC fields and the custom functions of DSF (Functions.cs:24-30) mixed with firmware values
    /// </summary>
    private static readonly string[] DsfExpressions =
    [
        "directories.system", "#directories.system", "directories.system ^ move.axes[0].letter", "volumes[0].freeSpace", "#volumes", "state.logLevel",
        "network.corsSite", "job.lastFileAborted && true", "fileexists(\"0:/sys/a.g\")", "fileexists(directories.system ^ \"a.g\")",
        "fileread(\"0:/sys/data.csv\", 0, 3, ',')", "#fileread(\"0:/sys/data.csv\", 1, 2, ',')", "exists(directories.web)", "exists(global.nothing)",
        "max(1, #directories.system)", "{directories.web}", "(directories.system)", "move.axes[0].homed ? directories.system : directories.web", "iterations",
        "line", "1 + 2", "\"a\" ^ 'b'", "state.status", "move.axes[1 + 0].letter", "volumes[1 - 1].path"
    ];

    private static readonly string[] DsfTokens = ["(", ")", "[", "]", "{", "}", "\"", "'", ",", "#", "^", " ", "fileread(", "fileexists(", "exists(", "directories.", "0", "-1", ".."];

    private static string MutateExpression(Random random, string expression)
    {
        StringBuilder builder = new(expression);
        for (int i = random.Next(4); i > 0; i--)
        {
            int position = random.Next(builder.Length + 1);
            switch (random.Next(3))
            {
                case 0:
                    builder.Insert(position, DsfTokens[random.Next(DsfTokens.Length)]);
                    break;
                case 1:
                    if (builder.Length > 0)
                    {
                        builder.Remove(random.Next(builder.Length), 1);
                    }
                    break;
                default:
                    builder.Insert(position, DsfExpressions[random.Next(DsfExpressions.Length)]);
                    break;
            }
        }
        return builder.ToString();
    }

    public sealed record DsfInput(string Expression, bool OnlySbcFields);

    [Test]
    public async Task EvaluateThroughDcs()
    {
        _host = await FakeFirmwareHost.StartAsync();
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "sys", "a.g"), "G1 X1\n");
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "sys", "data.csv"), "1, 2.5, \"a\"\n");
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Volumes.Clear();
            _host.Model.Volumes.Add(new Volume { Path = _host.BaseDirectory, FreeSpace = 1234 });
        }
        Expressions expressions = _host.Services.GetRequiredService<Expressions>();
        CodeFactory codeFactory = _host.Services.GetRequiredService<CodeFactory>();

        Fuzzer.Run(random =>
        {
            string expression = DsfExpressions[random.Next(DsfExpressions.Length)];
            return new DsfInput((random.Next(4) == 0) ? expression : MutateExpression(random, expression), random.Next(2) == 0);
        }, input =>
        {
            Code code = codeFactory.Create();
            code.Channel = CodeChannel.HTTP;
            try
            {
                // EvaluateExpressionToStringAsync documents CodeParserException (Expressions.cs:1053-1055). The custom functions throw an ArgumentException
                // for invalid arguments (Functions.cs:55, 72, 84-107) and fileread passes on the exceptions of File.OpenText (Functions.cs:111)
                Task<string> evaluation = expressions.EvaluateExpressionToStringAsync(code, input.Expression, input.OnlySbcFields, false);
                if (!evaluation.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new FuzzFailure("hang", "No result within 5 s");
                }
            }
            catch (AggregateException ae) when (ae.InnerException is CodeParserException or ArgumentException or IOException || IsKnownNumberError(ae.InnerException!))
            {
                // Defined error
            }
            catch (AggregateException ae)
            {
                throw new FuzzFailure("exception", $"{ae.InnerException!.GetType().Name}: {ae.InnerException.Message}\n{ae.InnerException}");
            }
        }, input => $"new DsfInput(\"{input.Expression.Replace("\\", "\\\\").Replace("\"", "\\\"")}\", {input.OnlySbcFields.ToString().ToLowerInvariant()})",
        input => Enumerable.Range(0, input.Expression.Length).Select(i => input with { Expression = input.Expression.Remove(i, 1) }), Fuzzer.Scale(1000));
    }
    #endregion

    #region Comparison with the fake firmware
    /// <summary>
    /// Draw a well-typed integer expression from literals, + - * and unary minus, and conditionals. The values stay small so that nothing overflows
    /// </summary>
    private static string IntExpression(Random random, int depth)
    {
        if (depth <= 0 || random.Next(3) == 0)
        {
            return random.Next(0, 20).ToString();
        }
        string expression = random.Next(5) switch
        {
            0 => $"-{IntExpression(random, depth - 1)}",
            1 => $"{BoolExpression(random, depth - 1)} ? {IntExpression(random, depth - 1)} : {IntExpression(random, depth - 1)}",
            _ => $"{IntExpression(random, depth - 1)} {"+-*"[random.Next(3)]} {IntExpression(random, depth - 1)}"
        };
        return (random.Next(2) == 0) ? $"({expression})" : expression;
    }

    /// <summary>
    /// Draw a well-typed Boolean expression from literals, comparisons, &amp;&amp; || and !
    /// </summary>
    private static string BoolExpression(Random random, int depth)
    {
        if (depth <= 0 || random.Next(4) == 0)
        {
            return (random.Next(2) == 0) ? "true" : "false";
        }
        string[] comparisons = ["<", "<=", ">", ">=", "==", "!=", "="];
        string expression = random.Next(4) switch
        {
            0 => $"!{BoolExpression(random, depth - 1)}",
            1 => $"{BoolExpression(random, depth - 1)} {((random.Next(2) == 0) ? "&&" : "||")} {BoolExpression(random, depth - 1)}",
            _ => $"{IntExpression(random, depth - 1)} {comparisons[random.Next(comparisons.Length)]} {IntExpression(random, depth - 1)}"
        };
        return (random.Next(2) == 0) ? $"({expression})" : expression;
    }

    private static string Evaluate(Func<object?> evaluate)
    {
        try
        {
            return Describe(evaluate());
        }
        catch (Exception e) when (e is CodeParserException or FakeExpressionException)
        {
            return "(error)";
        }
    }

    [Test]
    public void CompareWithFakeFirmware()
    {
        // Both evaluators follow the operator priorities, short-circuit evaluation and type rules of RRF's ExpressionParser::ParseInternal for this
        // part of the grammar (MetaExpressionParser.cs:10-13, FakeExpressionEvaluator.cs:13-25). Integer + - * stay integers in both
        Fuzzer.Run(random => (random.Next(2) == 0) ? IntExpression(random, 4) : BoolExpression(random, 4), expression =>
        {
            string dsf = Evaluate(() => MetaExpressionParser.TryEvaluate(expression, null, out object? value) ? value : throw new FuzzFailure("forwarded", "Nothing to forward in a literal expression"));
            string fake = Evaluate(() => new FakeExpressionEvaluator(id => throw new FakeExpressionException($"unknown value '{id}'"), _ => false).Evaluate(expression));
            if (dsf != fake)
            {
                throw new FuzzFailure("difference", $"MetaExpressionParser gave {dsf}, the fake firmware {fake}");
            }
        }, expression => $"\"{expression}\"", expression => [], Fuzzer.Iterations);
    }
    #endregion
}
