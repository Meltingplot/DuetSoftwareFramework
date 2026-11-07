using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Fuzz;

/// <summary>
/// Fuzz tests that execute random and mutated codes through DCS and the fake firmware like clients do: single codes and snippets
/// via SimpleCode on the HTTP and SBC channels, as macro files started with M98 and as job files started with M32.
/// Each input must end with a reply or an error within a time limit, or, if it loops by design, end when it is aborted.
/// DCS must stay responsive afterwards, i.e. a sentinel code on the same channel is executed and reaches the firmware last,
/// and the codes of a direct input reach the firmware in the order of the input
/// </summary>
[TestFixture]
public class GCodeExecutionFuzz
{
    /// <summary>
    /// Mode of an input
    /// </summary>
    public enum Mode
    {
        /// <summary>Codes executed via SimpleCode on the HTTP channel</summary>
        Http,

        /// <summary>Codes executed via SimpleCode on the SBC channel</summary>
        Sbc,

        /// <summary>Codes written to a macro file that M98 starts on the HTTP channel</summary>
        Macro,

        /// <summary>Codes written to a job file that M32 starts</summary>
        Job
    }

    /// <summary>
    /// Fuzzed input
    /// </summary>
    /// <param name="Mode">How the input is executed</param>
    /// <param name="Text">Codes, one or more lines</param>
    public sealed record Input(Mode Mode, string Text);

    /// <summary>
    /// Time after which an input that loops by design is aborted
    /// </summary>
    private static readonly TimeSpan LoopBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Time an input may take before it counts as a hang
    /// </summary>
    private static readonly TimeSpan CodeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Codes that are left out because they leave the sandbox or block by design, with the reason from the source:
    /// <list type="bullet">
    /// <item>M112: emergency stop. Afterwards no code reaches the firmware until M999 (wiki M112, MCodeHandler.cs:748-772)</item>
    /// <item>M999: resets the firmware and stops DCS after its reply (MCodeHandler.cs:1341-1375)</item>
    /// <item>M997: updates the firmware, stops the plugins and DCS, or the DSF packages with S2 (MCodeHandler.cs:1202-1335)</item>
    /// <item>M291: a message box with S2 or S3 blocks the channel until it is acknowledged (wiki M291, FakeGCodes.DoMessageBox)</item>
    /// <item>M581/M582: SBC triggers configured by DSF start trigger macros later on their own (MCodeHandler.cs:1050-1061, SbcTriggerService)</item>
    /// <item>M586: P4 and M586.4 make DCS connect to an MQTT broker over the network, also if P is an expression (MCodeHandler.cs:1063-1097).
    /// Without that M118 P6 cannot publish anything (SystemCodes tests), so it stays in</item>
    /// <item>M111 P-1: changes the log level of DCS and O1 redirects its log into the object model for all later inputs (MCodeHandler.cs:687-746)</item>
    /// </list>
    /// </summary>
    private static bool IsExcluded(Code code) => code.Type == CodeType.MCode && code.MajorNumber is 112 or 999 or 997 or 291 or 581 or 582 or 586 or 111;

    /// <summary>
    /// Whether a code makes MCodeHandler.CodeExecutedAsync throw after a successful reply, which leaves the code unfinished
    /// (ExecutedHandlerErrors.DiagnosticsWithExpressionParameter). The accessors are the ones of MCodeHandler.cs:1432, 1443, 1465 and 1473.
    /// The fake firmware accepts any parameter, so inputs with such codes would hang for a reason that is already known
    /// </summary>
    private static bool ThrowsInReplyHandler(Code code)
    {
        if (code.Type != CodeType.MCode)
        {
            return false;
        }
        try
        {
            switch (code.MajorNumber)
            {
                case 122:
                    _ = code.GetInt('B', 0) == 0 && code.GetInt('P', 0) == 0;
                    break;
                case 260 or 261:
                    _ = code.TryGetString('V', out _);
                    break;
                case 596:
                    _ = code.TryGetInt('P', out int _);
                    break;
                case 606:
                    _ = code.TryGetInt('S', out int _);
                    break;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Codes that SimpleCode executes before the others (SimpleCode.cs:110-116), so they are left out of the order check
    /// </summary>
    private static bool IsPrioritized(CodeType type, int? majorNumber) => type == CodeType.MCode && majorNumber is 108 or 112 or 122 or 292 or 999;

    /// <summary>
    /// Codes on a single line that cover the handlers of DCS and the fake firmware
    /// </summary>
    private static readonly string[] Lines =
    [
        "G1 X10 Y20 F3000", "G0 X1", "G28", "G92 E0", "G53 G1 X0", "G4 P1", "G10 L2 P1 X0", "G29 S1", "G90", "G91", "G1 X{move.axes[0].userPosition + 1}",
        "T0", "T1 P0", "T-1", "T{1-1}",
        "M0", "M1", "M17", "M84", "M104 S200", "M106 P0 S0.5", "M115", "M114", "M117 \"msg\"", "M118 S\"hello\"", "M118 P1 S{1+1}", "M120", "M121",
        "M122", "M122 \"DSF\"", "M260 A1 B2", "M261 A1 B2 V\"x\"", "M292", "M400", "M408 S0", "M409 K\"move.axes[0].letter\"", "M409 K\"state\" F\"v\"",
        "M409 R1", "M500", "M503", "M550 P\"test\"", "M551 P\"pw\"", "M552", "M555 P2", "M563 P0 S\"Tool\" D0 H1", "M569 P0.1 S1", "M584 X0.1 Y0.2",
        "M596 P0", "M598", "M606", "M606 S1", "M906 X800", "M915 P0.1 S5", "M929 S1 P\"fuzz-log.txt\"", "M929", "M929 S0",
        "M98 P\"ok.g\"", "M98 P\"missing.g\"", "M98 R1", "M98.3 P\"ok.g\"", "M98.2 P\"ok.g\"", "M98.9 P\"ok.g\"",
        "M20", "M20 S2 P\"0:/gcodes\"", "M20 S3 P\"0:/macros\"", "M21", "M21 P1", "M22", "M23 \"small.g\"", "M24", "M25", "M26 S0", "M27",
        "M28 \"fuzz-written.g\"", "M29", "M30 \"fuzz-written.g\"", "M32 \"small.g\"", "M36", "M36 \"small.g\"", "M36.1 P\"small.g\" S0", "M36.2 P\"small.g\" S0",
        "M36.3 P\"small.g\"", "M37 P\"small.g\"", "M38 \"small.g\"", "M39", "M39 P0 S2", "M39 P5", "M470 P\"/sys/fuzzdir\"", "M471 S\"/sys/fuzzdir\" T\"/sys/fuzzdir2\"",
        "M472 P\"/sys/fuzzdir2\" R1", "M505", "M505 P\"sys\"", "M998",
        "echo 1+1, \"a\"", "echo directories.system", "echo >\"fuzz-out.txt\" \"x\"", "echo >>\"fuzz-out.txt\" 1", "echo >>>{\"fuzz-\" ^ \"out.txt\"} 'c'",
        "var a = 1", "global g = 1", "set global.g = 2", "set var.a = var.a + 1", "skip", "abort \"stop\"",
        "echo fileexists(\"0:/sys/ok.g\")", "echo #fileread(\"0:/sys/data.csv\", 0, 3, ',')", "echo exists(global.g)", "M117 {\"a\" ^ directories.system}",
        "; comment", "(comment)", "", "N1 G1 X1*80", "M117 \"a\" ; b"
    ];

    /// <summary>
    /// Lines that open blocks, valid in files only
    /// </summary>
    private static readonly string[] BlockLines = ["if true", "if false", "elif true", "else", "while iterations < 2", "while iterations < 3"];

    /// <summary>
    /// Lines that are valid in loops only
    /// </summary>
    private static readonly string[] LoopLines = ["break", "continue", "echo iterations"];

    /// <summary>
    /// Tokens inserted by mutations
    /// </summary>
    private static readonly string[] Tokens =
    [
        "G", "M", "T", " ", "\"", "'", "{", "}", "(", ")", "[", "]", ";", "*", "\n", "  ", ":", ".", "-", "#", "^", "=", "==", "&&", "||", "!",
        "var.a", "global.g", "iterations", "line", "directories.system", "move.axes[0]", "{1+1}", "\"\"", "0:/", "../", "1e9", "-1", "99999999999", "ä", "😀", "\0"
    ];

    private static readonly CodeChannel[] Channels = [CodeChannel.HTTP, CodeChannel.SBC];

    /// <summary>
    /// Draw a line and mutate it in a quarter of the cases
    /// </summary>
    private static string DrawLine(Random random, string[] pool)
    {
        string line = pool[random.Next(pool.Length)];
        if (random.Next(4) == 0)
        {
            StringBuilder builder = new(line);
            for (int mutations = 1 + random.Next(3); mutations > 0; mutations--)
            {
                int position = random.Next(builder.Length + 1);
                switch (random.Next(4))
                {
                    case 0:
                        builder.Insert(position, Tokens[random.Next(Tokens.Length)]);
                        break;
                    case 1:
                        if (builder.Length > 0)
                        {
                            int start = random.Next(builder.Length);
                            builder.Remove(start, Math.Min(1 + random.Next(4), builder.Length - start));
                        }
                        break;
                    case 2:
                        if (builder.Length > 0)
                        {
                            builder[random.Next(builder.Length)] = (char)random.Next(32, 127);
                        }
                        break;
                    default:
                        builder.Insert(position, random.Next(-1000, 100000).ToString());
                        break;
                }
            }
            line = builder.ToString();
        }
        return line;
    }

    /// <summary>
    /// Draw an input. Files get blocks with indented bodies
    /// </summary>
    private static Input Generate(Random random)
    {
        Mode mode = (Mode)random.Next(4);
        List<string> lines = [];
        int numLines = 1 + random.Next((mode is Mode.Http or Mode.Sbc) ? 3 : 6);
        for (int i = 0; i < numLines; i++)
        {
            if (mode is Mode.Macro or Mode.Job && random.Next(4) == 0)
            {
                string block = BlockLines[random.Next(BlockLines.Length)];
                lines.Add(block);
                for (int j = 1 + random.Next(2); j > 0; j--)
                {
                    lines.Add("  " + DrawLine(random, (block.StartsWith("while") && random.Next(3) == 0) ? LoopLines : Lines));
                }
            }
            else
            {
                lines.Add(DrawLine(random, (random.Next(20) == 0) ? BlockLines : Lines));
            }
        }
        return new Input(mode, string.Join('\n', lines) + ((mode is Mode.Macro or Mode.Job) ? "\n" : string.Empty));
    }

    private static string Format(Input input) => $"new Input(Mode.{input.Mode}, \"{input.Text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\0", "\\0")}\")";

    private static IEnumerable<Input> Shrink(Input input)
    {
        string[] lines = input.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                yield return input with { Text = string.Join('\n', lines.Where((_, index) => index != i)) };
            }
        }
        if (input.Mode is Mode.Macro or Mode.Job)
        {
            yield return input with { Mode = Mode.Http };
        }
    }

    /// <summary>
    /// Parse the codes of an input like SimpleCode (SimpleCode.cs:55-72) or CodeFile (CodeFile.cs:69-74) does
    /// </summary>
    /// <param name="text">Input</param>
    /// <param name="isFile">Whether the input is parsed as a file</param>
    /// <param name="codes">Codes parsed until the end or the first parse error, which a file executes before it is aborted</param>
    /// <returns>Whether the whole input could be parsed</returns>
    private static bool Parse(string text, bool isFile, out List<Code> codes)
    {
        codes = [];
        byte[] data = Encoding.UTF8.GetBytes(text);
        using MemoryStream stream = new(data);
        CodeParserBuffer buffer = new(isFile ? 32768 : Math.Max(data.Length, 1), isFile) { EnforceLineNumbers = isFile };
        try
        {
            while (buffer.GetPosition(stream) < stream.Length)
            {
                Code code = new();
                if (Code.ParseAsync(stream, code, buffer).AsTask().GetAwaiter().GetResult())
                {
                    codes.Add(code);
                }
            }
        }
        catch (Exception e) when (e is CodeParserException or ArgumentException)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether an exception that SimpleCode passed on is one that DCS throws on purpose for invalid codes.
    /// IPC sends it to the client as an error reply (IPC/Processors/Command.cs:131-152)
    /// </summary>
    private static bool IsExpectedException(Exception e) => e switch
    {
        // SimpleCode.ExecuteAsync documents it for cancelled codes
        OperationCanceledException => true,
        // M21 P1, M22, M36.3 and M998 are not supported in SBC mode (MCodeHandler.cs:218, 224, 471, 1339)
        NotSupportedException => true,
        // Conditional keywords outside a file (Code.cs:212-222)
        InvalidOperationException when e.Message == "Conditional codes must not be executed" => true,
        // A file that fileread or echo cannot open or write (Functions.cs:111, KeywordHandler.cs:149). The wiki: "If the file cannot be opened
        // and read ... the command containing the fileread call will be aborted"
        IOException or UnauthorizedAccessException => true,
        _ => false
    };

    private FakeFirmwareHost _host = null!;
    private int _sentinel;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _host = await FakeFirmwareHost.StartAsync();

        // RRF reports job.lastDuration while it is not printing (PrintMonitor.cpp:75, set by StopPrint in GCodes.cpp:4953-5002).
        // The fake firmware does not, and JobProcessor waits for it after a simulation with M37 (JobProcessor.cs:789-807)
        _host.Firmware.ObjectModel.Set("job.lastDuration", 1);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Write the files that the inputs refer to. A previous input may have deleted or moved them
    /// </summary>
    private void PrepareFiles()
    {
        foreach (string directory in new[] { "sys", "macros", "gcodes" })
        {
            Directory.CreateDirectory(Path.Combine(_host.BaseDirectory, directory));
        }
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "sys", "ok.g"), "G1 X1\nM400\n");
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "sys", "data.csv"), "1,2,3\n");
        System.IO.File.WriteAllText(Path.Combine(_host.BaseDirectory, "gcodes", "small.g"), "G1 X1\nG1 X2\n");
    }

    /// <summary>
    /// Undo the state that an input may leave behind and that is not part of the property: files being written by M28,
    /// a running job, the system directory changed by M505 and event logging
    /// </summary>
    private async Task ResetAsync()
    {
        // SimpleCode passes on the exception of a code without waiting for the following codes of the same request (SimpleCode.cs:173-201),
        // so wait for the channels to finish them before their state is reset
        CodeProcessor codeProcessor = _host.Services.GetRequiredService<CodeProcessor>();
        foreach (CodeChannel channel in Channels)
        {
            if (!await codeProcessor.FlushAsync(channel, true).AsTask().WaitAsync(CodeTimeout))
            {
                throw new FuzzFailure("flush", $"Could not flush the {channel} channel");
            }
        }

        for (int i = 0; i < codeProcessor.FilesBeingWritten.Length; i++)
        {
            // Same as M29 (MCodeHandler.cs:389-412)
            using (await codeProcessor.FileLocks[i].LockAsync())
            {
                StreamWriter? writer = codeProcessor.FilesBeingWritten[i];
                if (writer is not null)
                {
                    await writer.DisposeAsync();
                    codeProcessor.FilesBeingWritten[i] = null;
                }
            }
        }

        JobProcessor jobs = _host.Services.GetRequiredService<JobProcessor>();
        if (jobs.IsFileSelected)
        {
            // A file selected by M23 is not running, so the job is aborted as well (JobProcessor.Abort) and not only the File channel
            await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.File);
            using (await jobs.LockAsync())
            {
                jobs.Abort();
            }
            if (!await WaitForAsync(() => !jobs.IsFileSelected, CodeTimeout))
            {
                throw new FuzzFailure("job abort", "The job did not end after it was aborted");
            }
        }

        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Directories.System = "0:/sys/";
            _host.Model.Messages.Clear();
        }
        if (await _host.ExecuteAsync("M929 S0", CodeChannel.HTTP, CodeTimeout) != "\n")
        {
            throw new FuzzFailure("reset", "M929 S0 failed");
        }
        PrepareFiles();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                return false;
            }
            await Task.Delay(5);
        }
        return true;
    }

    /// <summary>
    /// Codes of a channel that the firmware executed after the given index, without those from macros.
    /// Executed and not received codes count, because the firmware discards the codes it has buffered when a macro starts on the channel
    /// and DSF sends them again once the macro has finished
    /// </summary>
    private List<FakeCode> ExecutedSince(CodeChannel channel, int index) =>
        [.. _host.Firmware.GetExecutedCodes(channel).Skip(index).Where(code => !code.IsFromMacro)];

    /// <summary>
    /// Run an input and check the properties
    /// </summary>
    private void Check(Input input) => CheckAsync(input).GetAwaiter().GetResult();

    private async Task CheckAsync(Input input)
    {
        bool isFile = input.Mode is Mode.Macro or Mode.Job;
        bool parsed = Parse(input.Text, isFile, out List<Code> codes);
        if (codes.Any(IsExcluded) || codes.Any(ThrowsInReplyHandler))
        {
            return;
        }
        if (input.Mode == Mode.Job && codes.Any(code => code.Type == CodeType.MCode && code.MajorNumber is 23 or 32 or 37))
        {
            // A job that selects a file replaces itself with that file, which waits for M24 if it was selected by M23 (JobProcessor.SelectFileAsync).
            // Then there is no end of the job to wait for. JobFiles.SelectFileFromJob covers this
            return;
        }

        await ResetAsync();
        CodeChannel channel = (input.Mode == Mode.Sbc) ? CodeChannel.SBC : (input.Mode == Mode.Job) ? CodeChannel.File : CodeChannel.HTTP;
        CodeChannel replyChannel = (input.Mode == Mode.Sbc) ? CodeChannel.SBC : CodeChannel.HTTP;
        int executedBefore = _host.Firmware.GetExecutedCodes(channel).Count;
        // A loop or M26 in a job file may make it run forever by design
        bool mayLoop = isFile && (input.Text.Contains("while") || input.Text.Contains("M26"));

        // Execute the input
        Task<string> execution;
        switch (input.Mode)
        {
            case Mode.Macro:
                // Lone surrogates become replacement characters like in SimpleCode.ParseAsync (SimpleCode.cs:57)
                System.IO.File.WriteAllBytes(Path.Combine(_host.BaseDirectory, "sys", "fuzz.g"), Encoding.UTF8.GetBytes(input.Text));
                execution = _host.StartCode("M98 P\"fuzz.g\"", CodeChannel.HTTP);
                break;
            case Mode.Job:
                System.IO.File.WriteAllBytes(Path.Combine(_host.BaseDirectory, "gcodes", "fuzz.g"), Encoding.UTF8.GetBytes(input.Text));
                execution = _host.StartCode("M32 \"fuzz.g\"", CodeChannel.HTTP);
                break;
            default:
                execution = _host.StartCode(input.Text, replyChannel);
                break;
        }

        string? reply = null;
        try
        {
            reply = await execution.WaitAsync(mayLoop ? LoopBudget : CodeTimeout);
        }
        catch (TimeoutException) when (mayLoop)
        {
            // A loop may run forever by design. Aborting it must end the code that started it
            await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
            try
            {
                reply = await execution.WaitAsync(CodeTimeout);
            }
            catch (TimeoutException)
            {
                throw new FuzzFailure("abort", "The macro did not end after it was aborted");
            }
            catch (Exception e) when (IsExpectedException(e))
            {
                // Aborted
            }
        }
        catch (TimeoutException)
        {
            throw new FuzzFailure("hang", $"No reply within {CodeTimeout.TotalSeconds} s");
        }
        catch (Exception e) when (IsExpectedException(e))
        {
            // Defined error for an invalid code
        }
        catch (Exception e)
        {
            throw new FuzzFailure("exception", $"{e.GetType().Name}: {e.Message}\n{e}");
        }

        // A job must end on its own, or when it is aborted if it may loop
        if (input.Mode == Mode.Job)
        {
            JobProcessor jobs = _host.Services.GetRequiredService<JobProcessor>();
            if (!await WaitForAsync(() => !jobs.IsFileSelected, mayLoop ? LoopBudget : CodeTimeout))
            {
                if (!mayLoop)
                {
                    throw new FuzzFailure("job hang", $"The job did not end within {CodeTimeout.TotalSeconds} s");
                }
                await _host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.File);
                if (!await WaitForAsync(() => !jobs.IsFileSelected, CodeTimeout))
                {
                    throw new FuzzFailure("job abort", "The job did not end after it was aborted");
                }
            }
        }

        // Direct codes reach the firmware in the order of the input. A parse error stops SimpleCode before any code is executed (SimpleCode.cs:128-140)
        if (input.Mode is Mode.Http or Mode.Sbc)
        {
            FakeCode[] received = [.. ExecutedSince(channel, executedBefore).Where(code => code.Type is CodeType.GCode or CodeType.MCode or CodeType.TCode && !IsPrioritized(code.Type, code.MajorNumber))];
            if (!parsed)
            {
                if (received.Length > 0)
                {
                    throw new FuzzFailure("parse error", $"Codes reached the firmware although the input does not parse: {string.Join(", ", received.Select(code => code.Text))}");
                }
            }
            else
            {
                Code[] sent = [.. codes.Where(code => code.Type is CodeType.GCode or CodeType.MCode or CodeType.TCode && !IsPrioritized(code.Type, code.MajorNumber))];
                int next = 0;
                foreach (FakeCode command in received)
                {
                    while (next < sent.Length && !IsSameCommand(sent[next], command))
                    {
                        next++;
                    }
                    if (next == sent.Length)
                    {
                        throw new FuzzFailure("order", $"Codes reached the firmware out of order: sent {string.Join(", ", sent.Select(code => code.ToString()))}, received {string.Join(", ", received.Select(code => code.Text))} (reply {reply})");
                    }
                    next++;
                }
            }
        }

        // DCS stays responsive: a sentinel on the same channel runs and reaches the firmware after everything else
        await ResetAsync();
        string sentinel = $"M117 \"sentinel {++_sentinel}\"";
        int beforeSentinel = _host.Firmware.GetExecutedCodes(replyChannel).Count;
        string sentinelReply;
        try
        {
            sentinelReply = await _host.ExecuteAsync(sentinel, replyChannel, CodeTimeout);
        }
        catch (Exception e)
        {
            throw new FuzzFailure("sentinel", $"The sentinel failed after the input: {e.GetType().Name}: {e.Message}");
        }
        if (sentinelReply != "\n")
        {
            throw new FuzzFailure("sentinel reply", $"Unexpected reply to the sentinel: '{sentinelReply}'");
        }
        List<FakeCode> afterSentinel = ExecutedSince(replyChannel, beforeSentinel);
        if (afterSentinel.Count == 0 || afterSentinel[^1].Text != sentinel)
        {
            throw new FuzzFailure("sentinel order", $"The sentinel was not executed last: {string.Join(", ", afterSentinel.Select(code => code.Text))}");
        }
        if (await _host.ExecuteAsync("echo \"ok\"", replyChannel, CodeTimeout) != "ok\n")
        {
            throw new FuzzFailure("sentinel echo", "echo failed after the input");
        }
    }

    /// <summary>
    /// Whether a received code is the parsed one. DSF evaluates the expression of a T-code number before it sends it
    /// </summary>
    private static bool IsSameCommand(Code sent, FakeCode received) =>
        sent.Type == received.Type && (sent.MajorNumber is null || (sent.MajorNumber == received.MajorNumber && ((sent.MinorNumber >= 0) ? sent.MinorNumber : null) == received.MinorNumber));

    [Test]
    public void ExecuteCodes() => Fuzzer.Run(Generate, Check, Format, Shrink, Fuzzer.Scale(300), TimeSpan.FromSeconds(30));
}
