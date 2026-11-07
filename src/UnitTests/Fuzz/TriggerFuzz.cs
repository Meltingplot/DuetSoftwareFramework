using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.Codes;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Fuzz;

/// <summary>
/// Random interleavings of the triggers of the CHX 350 with codes, macros, aborts and a job on the other channels, run against the fake firmware.
/// RRF requests sys/triggerN.g on the trigger channel whenever that channel is free, the lowest pending trigger first (GCodes::CheckTriggers,
/// GCodes.cpp:981-1062, emulated by <see cref="FakeFirmware.FakeFirmware.FireTrigger"/>). Properties:
/// <list type="bullet">
/// <item>every fired trigger runs its macro at least once and at most as often as it fired, and no trigger is left pending</item>
/// <item>DSF answers every trigger request by starting the file, never with an error, as all trigger files exist (Processor.DoMacroFile)</item>
/// <item>the codes of the trigger macros reach the firmware in the order of the files, one run after the other; a run ends early only at its
/// own abort (wiki, Gcode_meta_commands.md: abort "causes all nested macros and the current print file (if any) to be terminated")</item>
/// <item>no code of a macro reaches another channel than the one it runs on</item>
/// <item>no channel stays blocked: at the end every channel is idle in RRF and DSF, and a sentinel code on each channel is executed last</item>
/// </list>
/// Trigger macros do not contain M112, which would stop the machine until a reset (TriggerMacros.EmergencyStopTriggerWhileOtherChannelsAreBusy
/// covers it). Files are aborted by the abort lines of the macros and the job and by cancelling the job, but not by aborting a channel from
/// outside a file (LinkInterface.AbortAllAsync, as the IPC command InvalidateChannel does): that can cross a macro request of RRF and block the
/// channel, see <see cref="ChannelWorksAfterAbortWhileMacroRequestIsUnderway"/> and TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest
/// </summary>
[TestFixture]
public class TriggerFuzz
{
    /// <summary>
    /// Trigger numbers of the CHX 350 that run macros: T2 emergency stop input, T4 and T9 doors, T3, T5, T7, T8 and T10 expression triggers
    /// </summary>
    private static readonly int[] ChxTriggers = [2, 3, 4, 5, 7, 8, 9, 10];

    /// <summary>
    /// Channels that the input can keep busy with M400 (see <see cref="Kind.Hold"/>)
    /// </summary>
    private static readonly CodeChannel[] HoldChannels = [CodeChannel.Trigger, CodeChannel.HTTP, CodeChannel.SBC, CodeChannel.File, CodeChannel.Daemon, CodeChannel.Telnet];

    /// <summary>
    /// Channels that get a sentinel code at the end. The job channel is checked by the end of the job instead
    /// </summary>
    private static readonly CodeChannel[] SentinelChannels = [CodeChannel.HTTP, CodeChannel.SBC, CodeChannel.Telnet, CodeChannel.Daemon, CodeChannel.Trigger];

    /// <summary>
    /// Kind of an event
    /// </summary>
    public enum Kind
    {
        /// <summary>Trigger Arg fires</summary>
        Fire,
        /// <summary>M400 waits on channel HoldChannels[Arg] until it is released</summary>
        Hold,
        /// <summary>M400 finishes on channel HoldChannels[Arg]</summary>
        Release,
        /// <summary>A client runs a code on HTTP: a dwell (Arg 0), M400 (Arg 1) or the HTTP macro (Arg 2)</summary>
        HttpCode,
        /// <summary>A plugin starts the SBC macro on the SBC channel</summary>
        SbcMacro,
        /// <summary>M32 starts the job unless one is selected</summary>
        StartJob,
        /// <summary>The job is cancelled like DWC does: all files of the File channel are aborted and the job processor stops</summary>
        AbortJob,
        /// <summary>RRF requests daemon.g unless it runs already</summary>
        Daemon,
        /// <summary>Wait for Arg transfers</summary>
        Wait
    }

    /// <summary>
    /// Event of an input
    /// </summary>
    /// <param name="Kind">Kind of the event</param>
    /// <param name="Arg">Argument of the event</param>
    public sealed record Event(Kind Kind, int Arg)
    {
        /// <inheritdoc />
        public override string ToString() => $"{Kind} {Arg}";
    }

    /// <summary>
    /// Fuzzed input: the macros and the events
    /// </summary>
    /// <param name="Macros">Lines of the macro files by name, without line numbers. Trigger macros are written with line numbers and CRCs</param>
    /// <param name="Events">Events in order</param>
    public sealed record Input(IReadOnlyDictionary<string, string[]> Macros, IReadOnlyList<Event> Events)
    {
        /// <summary>
        /// Text that <see cref="Parse"/> reads, for regression tests
        /// </summary>
        public override string ToString() =>
            string.Join(" || ", Macros.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}: {string.Join(" | ", item.Value)}")) +
            " ## " + string.Join(", ", Events);

        /// <summary>
        /// Read an input from its text
        /// </summary>
        public static Input Parse(string text)
        {
            string[] parts = text.Split(" ## ");
            Dictionary<string, string[]> macros = parts[0].Split(" || ").Select(item => item.Split(": ", 2)).ToDictionary(item => item[0], item => item[1].Split(" | "));
            List<Event> events = [.. parts[1].Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split(' '))
                .Select(item => new Event(Enum.Parse<Kind>(item[0]), int.Parse(item[1], CultureInfo.InvariantCulture)))];
            return new Input(macros, events);
        }
    }

    #region Generator
    /// <summary>
    /// Marker codes: G4 with a number that tells the macro and the line, e.g. G4 P4002 is line 2 of trigger4.g
    /// </summary>
    private static string Marker(int macro, int line) => $"G4 P{macro * 1000 + line}";

    /// <summary>
    /// Numbers of the markers of the macros on the other channels
    /// </summary>
    private const int HttpMacro = 90, SbcMacro = 91, JobFile = 92, DaemonMacro = 93, TelnetMacro = 94, HttpCode = 95;

    private static string[] DrawLines(Random random, int macro, bool isTrigger)
    {
        List<string> lines = [];
        int count = 1 + random.Next(isTrigger ? 4 : 3);
        for (int i = 1; i <= count; i++)
        {
            int choice = random.Next(100);
            if (isTrigger && choice < 12)
            {
                lines.Add($"M98 P\"sub{macro}.g\"");
            }
            else if (isTrigger && choice < 20)
            {
                lines.Add("M98.4 P\"telnet-macro.g\"");
            }
            else if (choice >= 20 && choice < 24)
            {
                lines.Add("abort \"stop\"");
            }
            else if (choice < 40)
            {
                lines.Add("M400");
            }
            else
            {
                lines.Add(Marker(macro, i));
            }
        }
        return [.. lines];
    }

    private static Input Generate(Random random)
    {
        Dictionary<string, string[]> macros = [];
        foreach (int trigger in ChxTriggers)
        {
            macros[$"trigger{trigger}.g"] = DrawLines(random, trigger, true);

            // Called macro with an optional M99 (return from the macro, wiki M99)
            List<string> sub = [.. DrawLines(random, 100 + trigger, false)];
            if (random.Next(5) == 0)
            {
                sub.Insert(random.Next(sub.Count + 1), "M99");
            }
            macros[$"sub{trigger}.g"] = [.. sub];
        }
        macros["http-macro.g"] = DrawLines(random, HttpMacro, false);
        macros["sbc-macro.g"] = DrawLines(random, SbcMacro, false);
        macros["telnet-macro.g"] = DrawLines(random, TelnetMacro, false);
        macros["daemon.g"] = DrawLines(random, DaemonMacro, false);
        macros["job.g"] = DrawLines(random, JobFile, false);

        List<Event> events = [];
        for (int count = 3 + random.Next(10); count > 0; count--)
        {
            int choice = random.Next(100);
            events.Add(choice switch
            {
                < 30 => new Event(Kind.Fire, ChxTriggers[random.Next(ChxTriggers.Length)]),
                < 40 => new Event(Kind.Hold, random.Next(HoldChannels.Length)),
                < 52 => new Event(Kind.Release, random.Next(HoldChannels.Length)),
                < 60 => new Event(Kind.HttpCode, random.Next(3)),
                < 66 => new Event(Kind.SbcMacro, 0),
                < 72 => new Event(Kind.StartJob, 0),
                < 76 => new Event(Kind.AbortJob, 0),
                < 84 => new Event(Kind.Daemon, 0),
                _ => new Event(Kind.Wait, random.Next(1, 20))
            });
        }
        return new Input(macros, events);
    }

    /// <summary>
    /// Smaller inputs: one event less, or one line less in a macro of more than one line
    /// </summary>
    private static IEnumerable<Input> Shrink(Input input)
    {
        for (int i = 0; i < input.Events.Count; i++)
        {
            yield return input with { Events = [.. input.Events.Where((_, index) => index != i)] };
        }
        foreach ((string name, string[] lines) in input.Macros.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            for (int i = 0; i < lines.Length && lines.Length > 1; i++)
            {
                Dictionary<string, string[]> macros = new(input.Macros) { [name] = [.. lines.Where((_, index) => index != i)] };
                yield return input with { Macros = macros };
            }
        }
    }
    #endregion

    #region Expected codes
    /// <summary>
    /// Codes that a run of a trigger macro sends to the firmware on the trigger channel, in order. M98.4 starts the macro on Telnet and sends
    /// nothing on the trigger channel (MacrosOnOtherChannels), abort ends every macro of the channel (wiki, KeywordHandler.cs:168-171),
    /// M99 ends the called macro (wiki M99, GCodes::FileMacroCyclesReturn)
    /// </summary>
    private static List<string> ExpectedTriggerCodes(Input input, string fileName)
    {
        List<string> codes = [];
        foreach (string line in input.Macros[fileName])
        {
            if (line.StartsWith("abort"))
            {
                return codes;
            }
            if (line.StartsWith("M98.4"))
            {
                continue;
            }
            codes.Add(line);
            if (line.StartsWith("M98 "))
            {
                string sub = line[(line.IndexOf('"') + 1)..^1];
                foreach (string subLine in input.Macros[sub])
                {
                    if (subLine.StartsWith("abort"))
                    {
                        return codes;
                    }
                    codes.Add(subLine);
                    if (subLine == "M99")
                    {
                        break;
                    }
                }
            }
        }
        return codes;
    }

    /// <summary>
    /// Channel each marker belongs to
    /// </summary>
    private static Dictionary<string, CodeChannel> MarkerChannels(Input input)
    {
        Dictionary<string, CodeChannel> result = [];
        void add(string macro, CodeChannel channel)
        {
            foreach (string line in input.Macros[macro].Where(line => line.StartsWith("G4 ")))
            {
                result[line] = channel;
            }
        }
        foreach (int trigger in ChxTriggers)
        {
            add($"trigger{trigger}.g", CodeChannel.Trigger);
            add($"sub{trigger}.g", CodeChannel.Trigger);
        }
        add("http-macro.g", CodeChannel.HTTP);
        add("sbc-macro.g", CodeChannel.SBC);
        add("telnet-macro.g", CodeChannel.Telnet);
        add("daemon.g", CodeChannel.Daemon);
        add("job.g", CodeChannel.File);
        result[Marker(HttpCode, 1)] = CodeChannel.HTTP;
        return result;
    }
    #endregion

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private void Check(Input input) => CheckAsync(input).GetAwaiter().GetResult();

    private async Task CheckAsync(Input input)
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        HashSet<CodeChannel> held = [];
        host.Firmware.OnCode("M400", code =>
        {
            lock (held)
            {
                return held.Contains(code.Channel) ? FakeReply.NotFinished : FakeReply.Ok();
            }
        });
        foreach ((string name, string[] lines) in input.Macros)
        {
            string content = name.StartsWith("trigger") ? TriggerMacros.Numbered(lines) : string.Join('\n', lines) + "\n";
            System.IO.File.WriteAllText(Path.Combine(host.BaseDirectory, (name == "job.g") ? "gcodes" : "sys", name), content);
        }

        JobProcessor jobs = host.Services.GetRequiredService<JobProcessor>();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        List<(string code, Task task)> clientCodes = [];
        Dictionary<int, int> fired = [];
        int daemonRequests = 0;

        // Run the events
        foreach (Event item in input.Events)
        {
            switch (item.Kind)
            {
                case Kind.Fire:
                    host.Firmware.FireTrigger(item.Arg);
                    fired[item.Arg] = fired.GetValueOrDefault(item.Arg) + 1;
                    break;
                case Kind.Hold:
                    lock (held)
                    {
                        held.Add(HoldChannels[item.Arg]);
                    }
                    break;
                case Kind.Release:
                    lock (held)
                    {
                        held.Remove(HoldChannels[item.Arg]);
                    }
                    break;
                case Kind.HttpCode:
                    string httpCode = item.Arg switch { 0 => Marker(HttpCode, 1), 1 => "M400", _ => "M98 P\"http-macro.g\"" };
                    clientCodes.Add((httpCode, host.StartCode(httpCode)));
                    break;
                case Kind.SbcMacro:
                    clientCodes.Add(("M98 P\"sbc-macro.g\" on SBC", host.StartCode("M98 P\"sbc-macro.g\"", CodeChannel.SBC)));
                    break;
                case Kind.StartJob:
                    if (!jobs.IsFileSelected)
                    {
                        clientCodes.Add(("M32 \"job.g\"", host.StartCode("M32 \"job.g\"")));
                    }
                    break;
                case Kind.AbortJob:
                    // Like GCodeExecutionFuzz.ResetAsync and the cancel of a job
                    if (jobs.IsFileSelected)
                    {
                        await link.AbortAllAsync(CodeChannel.File);
                        using (await jobs.LockAsync())
                        {
                            jobs.Abort();
                        }
                    }
                    break;
                case Kind.Daemon:
                    // RRF starts daemon.g only when the daemon channel is not running it (GCodes.cpp:680-691). The fake firmware starts a requested
                    // system macro as soon as the channel has no code, so a second request waits until the first has gone out
                    if (host.Firmware.IsIdle(CodeChannel.Daemon) && DsfStackDepth(host, CodeChannel.Daemon) == 1 &&
                        host.Firmware.GetSentPackets().Count(packet => packet.Request == Request.ExecuteMacro && packet.Channel == CodeChannel.Daemon) == daemonRequests)
                    {
                        host.Firmware.RequestMacro(CodeChannel.Daemon, "daemon.g");
                        daemonRequests++;
                    }
                    break;
                case Kind.Wait:
                    int transfer = host.Firmware.TransferCount;
                    host.Firmware.WaitUntil(firmware => firmware.TransferCount >= transfer + item.Arg, Timeout);
                    break;
            }
        }

        // Let everything finish
        lock (held)
        {
            held.Clear();
        }
        foreach ((string code, Task task) in clientCodes)
        {
            try
            {
                await task.WaitAsync(Timeout);
            }
            catch (TimeoutException)
            {
                throw new FuzzFailure("hang", $"{code} of a client did not finish, stack depths in DSF: " +
                    string.Join(", ", HoldChannels.Select(channel => $"{channel} {DsfStackDepth(host, channel)}")) + ", idle in RRF: " +
                    string.Join(", ", HoldChannels.Where(host.Firmware.IsIdle)));
            }
            catch (OperationCanceledException)
            {
                // Aborted by the input
            }
        }
        if (!host.Firmware.WaitUntil(firmware => firmware.TriggersPending == 0 && HoldChannels.All(firmware.IsIdle), Timeout))
        {
            throw new FuzzFailure("blocked", $"Channels not idle in RRF: {string.Join(", ", HoldChannels.Where(channel => !host.Firmware.IsIdle(channel)))}, " +
                $"pending triggers 0x{host.Firmware.TriggersPending:x}");
        }
        DateTime end = DateTime.UtcNow + Timeout;
        while (jobs.IsFileSelected || HoldChannels.Any(channel => DsfStackDepth(host, channel) != 1))
        {
            if (DateTime.UtcNow > end)
            {
                throw new FuzzFailure("blocked in DSF", $"Job selected: {jobs.IsFileSelected}, stack depths: " +
                    string.Join(", ", HoldChannels.Select(channel => $"{channel} {DsfStackDepth(host, channel)}")));
            }
            await Task.Delay(5);
        }

        // Every fired trigger ran, none more often than it fired
        FakePacket[] requests = [.. host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.ExecuteMacro && packet.Channel == CodeChannel.Trigger && !packet.IsFromCode)];
        foreach (int trigger in ChxTriggers)
        {
            int runs = requests.Count(request => request.Text == $"trigger{trigger}.g");
            int fires = fired.GetValueOrDefault(trigger);
            if ((fires > 0 && runs == 0) || runs > fires)
            {
                throw new FuzzFailure("runs", $"Trigger {trigger} fired {fires} times and its macro ran {runs} times");
            }
        }
        if (requests.Any(request => !request.Text.StartsWith("trigger")))
        {
            throw new FuzzFailure("request", $"Unexpected macro requests: {string.Join(", ", requests.Select(request => request.Text))}");
        }

        // DSF started every requested file
        if (host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.MacroCompleted && request.Channel == CodeChannel.Trigger && request.Flag))
        {
            throw new FuzzFailure("macro error", "DSF reported a trigger macro as failed to start");
        }

        // The codes of the trigger channel are the runs one after the other
        string[] executed = [.. host.Firmware.GetExecutedCodes(CodeChannel.Trigger).Select(code => code.Text)];
        int position = 0;
        foreach (FakePacket request in requests)
        {
            List<string> expected = ExpectedTriggerCodes(input, request.Text);
            int matched = 0;
            while (matched < expected.Count && position < executed.Length && executed[position] == expected[matched])
            {
                matched++;
                position++;
            }
            if (matched < expected.Count)
            {
                throw new FuzzFailure("order", $"Run of {request.Text} ended after {matched} of its codes ({string.Join(", ", expected)}), " +
                    $"trigger channel executed: {string.Join(", ", executed)}");
            }
        }
        if (position != executed.Length)
        {
            throw new FuzzFailure("order", $"Codes on the trigger channel that no run explains: {string.Join(", ", executed[position..])} " +
                $"(all: {string.Join(", ", executed)}, runs: {string.Join(", ", requests.Select(request => request.Text))})");
        }

        // No code of a macro on another channel
        Dictionary<string, CodeChannel> markers = MarkerChannels(input);
        foreach (FakeCode code in host.Firmware.GetReceivedCodes())
        {
            if (markers.TryGetValue(code.Text, out CodeChannel channel) && channel != code.Channel)
            {
                throw new FuzzFailure("channel", $"{code.Text} of {channel} reached the firmware on {code.Channel}");
            }
        }

        // DCS stays responsive: a sentinel on each channel runs and is the last code there
        foreach (CodeChannel channel in SentinelChannels)
        {
            string sentinel = $"M117 \"sentinel {channel}\"";
            string reply;
            try
            {
                reply = await host.ExecuteAsync(sentinel, channel, Timeout);
            }
            catch (Exception e)
            {
                throw new FuzzFailure("sentinel", $"The sentinel on {channel} failed: {e.GetType().Name}: {e.Message}");
            }
            if (reply != "\n" || host.Firmware.GetExecutedCodes(channel).LastOrDefault()?.Text != sentinel)
            {
                throw new FuzzFailure("sentinel", $"The sentinel on {channel} replied '{reply}' or was not executed last");
            }
        }
    }

    /// <summary>
    /// Number of stack levels DSF has on a channel. The base level is one of them
    /// </summary>
    private static int DsfStackDepth(FakeFirmwareHost host, CodeChannel channel)
    {
        Processor processor = host.Services.GetRequiredService<Manager>()[channel];
        using (processor.Lock())
        {
            return processor.Stack.Count;
        }
    }

    [Test]
    public void TriggersWithOtherChannels() => Fuzzer.Run(Generate, Check, input => input.ToString(), Shrink, Fuzzer.Scale(25), TimeSpan.FromSeconds(30));

    /// <summary>
    /// Let the fake firmware perform one more transfer and wait until LinkService waits for the next one
    /// </summary>
    private static void Step(FakeFirmwareHost host)
    {
        int transfer = host.Firmware.TransferCount;
        host.Firmware.HoldTransfers(1);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "No transfer");
    }

    [Test, Explicit("DSF starts a macro that RRF requested for a code DSF has just cancelled (Processor.cs:1308-1325), RRF gives the request up when " +
        "the InvalidateChannel of the abort arrives (SbcInterface.cpp:880-883), so DSF waits for the end of that macro forever and the channel is blocked")]
    public async Task ChannelWorksAfterAbortWhileMacroRequestIsUnderway()
    {
        // Found by this fuzz test with an abort of HTTP while M98 waited for its macro (LinkInterface.AbortAllAsync, as the IPC command
        // InvalidateChannel and the abort keyword outside a file do)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-macro.g", "G4 P90001\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        // RRF executes M98 and asks for the macro in the transfer after that (FakeGCodes.DoFileMacro, SbcInterface.cpp:1492-1505)
        Task<string> m98 = host.StartCode("M98 P\"http-macro.g\"");
        for (int i = 0; i < 100 && !host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M98 P\"http-macro.g\""); i++)
        {
            Step(host);
        }
        Step(host);

        // DSF aborts the channel before it reads the request that is on its way
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }

        // Both sides must end up with a free channel
        Task<string> next = host.StartCode("G4 P2");
        bool finished = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2))) == next;
        Assert.That(finished, Is.True, $"The next code on HTTP did not finish, DSF has {DsfStackDepth(host, CodeChannel.HTTP)} stack levels on HTTP and RRF " +
            $"{(host.Firmware.IsIdle(CodeChannel.HTTP) ? "none" : "some")}");
    }

    /// <summary>
    /// Run a failing input again, 20 times because the interleavings depend on the timing. DSF_TRIGGER_FUZZ_INPUT names a file with the text of
    /// the input. With DSF_FAKE_FIRMWARE_LOG and DSF_FAKE_FIRMWARE_LOG_FILE (see <see cref="FakeFirmwareHost"/>) each run is marked in the log
    /// </summary>
    [Test, Explicit("Tool for failures of TriggersWithOtherChannels, needs DSF_TRIGGER_FUZZ_INPUT")]
    public void Replay([Range(1, 20)] int run)
    {
        string? log = Environment.GetEnvironmentVariable("DSF_FAKE_FIRMWARE_LOG_FILE");
        if (log is not null)
        {
            System.IO.File.AppendAllText(log, $"==== run {run}\n");
        }
        Check(Input.Parse(System.IO.File.ReadAllText(Environment.GetEnvironmentVariable("DSF_TRIGGER_FUZZ_INPUT")!).Trim()));
    }

    [Test]
    public void ParseFormattedInput()
    {
        // The text of a failing input can be pasted into a regression test
        Input input = Generate(new Random(1));
        Assert.That(Input.Parse(input.ToString()).ToString(), Is.EqualTo(input.ToString()));
    }
}
