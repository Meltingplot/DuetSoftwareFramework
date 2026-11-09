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
/// outside a file (LinkInterface.AbortAllAsync, as the IPC command InvalidateChannel does): RRF still sends a macro request that the abort
/// reaches before it has gone out and blocks the channel, see TriggerMacros.TriggerChannelWorksAfterInvalidationDuringTriggerRequest.
/// A request that has gone out is covered by <see cref="ChannelWorksAfterAbortWhileMacroRequestIsUnderway"/> and <see cref="ChannelWorksAfterAbortCrossingMacroRequest"/>
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

    [Test]
    public async Task ChannelWorksAfterAbortWhileMacroRequestIsUnderway()
    {
        // Found by this fuzz test with an abort of HTTP while M98 waited for its macro (LinkInterface.AbortAllAsync, as the IPC command
        // InvalidateChannel and the abort keyword outside a file do). RRF gives the request up when the InvalidateChannel of the abort arrives
        // (SbcInterface.cpp:880-883), so DSF must not start the macro (Processor.DoMacroFile). Before, DSF started it without a start code and
        // waited for the end of that macro forever
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

        // The request arrived before InvalidateChannel went out, and DSF did not start the macro
        Assert.That(InvalidateChannelTransfer(host, CodeChannel.HTTP), Is.GreaterThan(MacroRequestTransfers(host, CodeChannel.HTTP).Single()));
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P90001"));
    }

    [Test]
    public async Task ChannelWorksAfterAbortCrossingMacroRequest()
    {
        // As ChannelWorksAfterAbortWhileMacroRequestIsUnderway, but DSF aborts HTTP before RRF sends the macro request, so InvalidateChannel goes
        // out with the transfer that brings the request. After each transfer RRF reads the packets of DSF before it writes its own for the next
        // transfer (SbcInterface.cpp:267-268, ExchangeData reads from :312 and writes macro requests at :1492-1506 and :1576-1590), so it sent this
        // request before it read InvalidateChannel and gives the request up as well (:880-883)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-macro.g", "G4 P90001\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        Task<string> m98 = host.StartCode("M98 P\"http-macro.g\"");
        for (int i = 0; i < 100 && !host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M98 P\"http-macro.g\""); i++)
        {
            Step(host);
        }
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
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel) &&
            firmware.IsIdle(CodeChannel.HTTP), Timeout), Is.True, "RRF did not get the abort");
        Assert.That(InvalidateChannelTransfer(host, CodeChannel.HTTP), Is.EqualTo(MacroRequestTransfers(host, CodeChannel.HTTP).Single()));

        // DSF did not start the macro of the request, the next macro on HTTP runs as usual
        Assert.That(await host.ExecuteAsync("M98 P\"http-macro.g\""), Is.EqualTo("\n"));
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Count(code => code.Text == "G4 P90001"), Is.EqualTo(1));
        Assert.That(host.Firmware.GetReceivedRequests().Count(request => request.Request == SbcRequest.MacroStarted && request.Channel == CodeChannel.HTTP), Is.EqualTo(1));
        Assert.That(MacroRequestTransfers(host, CodeChannel.HTTP), Has.Length.EqualTo(2));
    }

    /// <summary>
    /// Run M98 on HTTP with held transfers until the given condition holds
    /// </summary>
    private static Task<string> StartMacroUntil(FakeFirmwareHost host, Func<bool> condition, string description)
    {
        host.WriteMacro("http-close.g", "G4 P90003\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        Task<string> m98 = host.StartCode("M98 P\"http-close.g\"");
        for (int i = 0; i < 100 && !condition(); i++)
        {
            Step(host);
        }
        Assert.That(condition(), Is.True, description);
        Assert.That(DsfStackDepth(host, CodeChannel.HTTP), Is.EqualTo(2), "DSF got the closal already");
        return m98;
    }

    /// <summary>
    /// Whether RRF has closed the macro of <see cref="StartMacroUntil"/> (GCodes.cpp:770-776). It writes the closal with the next exchange (SbcInterface.cpp:1489-1493)
    /// </summary>
    private static bool RrfClosedTheMacro(FakeFirmwareHost host) =>
        host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P90003") && host.Firmware.GetStackDepth(CodeChannel.HTTP) == 0;

    /// <summary>
    /// Transfers in which RRF reported a closed macro file on a channel
    /// </summary>
    private static int[] MacroFileClosedTransfers(FakeFirmwareHost host, CodeChannel channel) =>
        [.. host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.MacroFileClosed && packet.Channel == channel).Select(packet => packet.Transfer)];

    /// <summary>
    /// Abort HTTP with the held transfers, let them go and check that DCS is still running and the channel works
    /// </summary>
    private static async Task AbortAndCheckChannelAsync(FakeFirmwareHost host, Task<string> m98)
    {
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Assert.That(DsfStackDepth(host, CodeChannel.HTTP), Is.EqualTo(1), "The abort left the macro on the stack of DSF");
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel) &&
            firmware.IsIdle(CodeChannel.HTTP), Timeout), Is.True, "RRF did not get the abort");

        // Before, DSF popped its stack a second time for the closal, Processor.Pop threw "Stack underrun on channel HTTP" and the exception stopped DCS
        int closals = MacroFileClosedTransfers(host, CodeChannel.HTTP).Length;
        await host.ExecuteAsync("M98 P\"http-close.g\"").WaitAsync(Timeout);
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Count(code => code.Text == "G4 P90003"), Is.EqualTo(2));
        Assert.That(MacroFileClosedTransfers(host, CodeChannel.HTTP), Has.Length.EqualTo(closals + 1));
        Assert.That(DsfStackDepth(host, CodeChannel.HTTP), Is.EqualTo(1));
    }

    [Test]
    public async Task DcsKeepsRunningWhenAMacroFileClosalArrivesBeforeTheAbortGoesOut()
    {
        // Found by the lab test AbortsCrossingMacroRequests once InvalidateChannel worked over IPC. RRF has written the closal, and DSF aborts
        // the channel before the transfer that brings it. The abort has removed the macro from the stack of DSF already (Processor.AbortAllFilesAsync),
        // and RRF does not report the files it closes for InvalidateChannel (SbcInterface.cpp:893-894), so this closal is void
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Task<string> m98 = StartMacroUntil(host, () => RrfClosedTheMacro(host), "RRF did not close the macro");
        Step(host);
        Assert.That(MacroFileClosedTransfers(host, CodeChannel.HTTP), Is.Empty, "RRF delivered the closal already");

        await AbortAndCheckChannelAsync(host, m98);
        Assert.That(InvalidateChannelTransfer(host, CodeChannel.HTTP), Is.EqualTo(MacroFileClosedTransfers(host, CodeChannel.HTTP)[0] + 1));
    }

    [Test]
    public async Task DcsKeepsRunningWhenAnAbortCrossesAMacroFileClosal()
    {
        // As DcsKeepsRunningWhenAMacroFileClosalArrivesBeforeTheAbortGoesOut, but InvalidateChannel goes out with the transfer that brings the closal.
        // RRF wrote the closal before it read InvalidateChannel (SbcInterface.cpp:310-313 and :1489), as for the macro requests of
        // ChannelWorksAfterAbortCrossingMacroRequest
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Task<string> m98 = StartMacroUntil(host, () => RrfClosedTheMacro(host), "RRF did not close the macro");

        await AbortAndCheckChannelAsync(host, m98);
        Assert.That(InvalidateChannelTransfer(host, CodeChannel.HTTP), Is.EqualTo(MacroFileClosedTransfers(host, CodeChannel.HTTP)[0]));
    }

    [Test]
    public async Task DcsKeepsRunningWhenAnAbortArrivesBeforeAMacroFileClosalIsSent()
    {
        // RRF closes the macro after it got the end of the file from DSF, and InvalidateChannel reaches it with the next transfer, before it
        // has written the closal. It reads the packets of DSF first (SbcInterface.cpp:310-313), so it must drop the closal then, as
        // GCodeBuffer::Reset does (GCodeBuffer.cpp:142). Otherwise DSF gets the closal one transfer after the abort went out, when the window
        // of the abort is over
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Task<string> m98 = StartMacroUntil(host, () => host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.MacroCompleted && request.Channel == CodeChannel.HTTP),
            "DSF did not end the macro");
        Assert.That(host.Firmware.GetStackDepth(CodeChannel.HTTP), Is.EqualTo(1), "RRF closed the macro already");

        await AbortAndCheckChannelAsync(host, m98);
        Assert.That(MacroFileClosedTransfers(host, CodeChannel.HTTP), Has.Length.EqualTo(1), "RRF reported the aborted macro as closed");
        Assert.That(MacroFileClosedTransfers(host, CodeChannel.HTTP)[0], Is.GreaterThan(InvalidateChannelTransfer(host, CodeChannel.HTTP)));
    }

    /// <summary>
    /// Run M98 on HTTP with held transfers until RRF waits for the macro and its request is on the way, then abort HTTP while the buffer
    /// mutex of RRF cannot be had, as on the lab printer on 2026-11-08 ("Resending packet #0 (request InvalidateChannel)"): while an M98 waits for
    /// its macro, the main task holds the mutex (GCodes.cpp:590), and InvalidateChannel waits only SbcYieldTimeout for it (SbcInterface.cpp:45, :888-900)
    /// </summary>
    private static async Task<Task<string>> AbortWhileRrfCannotLockAsync(FakeFirmwareHost host)
    {
        host.WriteMacro("http-macro.g", "G4 P90001\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        Task<string> m98 = host.StartCode("M98 P\"http-macro.g\"");
        for (int i = 0; i < 100 && !host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M98 P\"http-macro.g\""); i++)
        {
            Step(host);
        }
        Step(host);
        host.Firmware.FailBufferLocks(CodeChannel.HTTP, 1);
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        return m98;
    }

    /// <summary>
    /// Transfers in which RRF got an InvalidateChannel of a channel
    /// </summary>
    private static int[] InvalidateChannelTransfers(FakeFirmwareHost host, CodeChannel channel) =>
        [.. host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == channel).Select(request => request.Transfer)];

    [Test]
    public async Task CodesAfterAnAbortWaitUntilRrfHasProcessedIt()
    {
        // RRF asks for the InvalidateChannel again (SbcInterface.cpp:1338-1341) and processes the copy one transfer later. A code that DSF sent in
        // between reached RRF before the copy, which dropped it (InvalidateBufferedCodes), and the code never got a reply. On the printer the probe
        // G4 P0 of AbortsCrossingMacroRequests got none. DSF now sends nothing on the channel until RRF has processed the InvalidateChannel
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Task<string> m98 = await AbortWhileRrfCannotLockAsync(host);
        Task<string> next = host.StartCode("G4 P90002");
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }

        Assert.That(await next.WaitAsync(Timeout), Is.Not.Null, "The code after the abort got no reply");
        int[] invalidations = InvalidateChannelTransfers(host, CodeChannel.HTTP);
        Assert.That(invalidations, Has.Length.EqualTo(2), "RRF did not ask for the InvalidateChannel again");
        int codeTransfer = host.Firmware.GetReceivedRequests().First(request => request.Request == SbcRequest.Code && request.Code?.Text == "G4 P90002").Transfer;
        Assert.That(codeTransfer, Is.GreaterThan(invalidations[1]), "The code went out before RRF had processed the InvalidateChannel");
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.Member("G4 P90002"));
    }

    [Test]
    public async Task MacroRequestBetweenTwoCopiesOfAnAbortIsDiscarded()
    {
        // RRF asked for the InvalidateChannel again and requests a macro before it reads the copy. The copy ends that request with an error
        // (SbcInterface.cpp:880-886), so DSF must not start the macro, as for the requests before the first copy (#134). Before, DSF started it,
        // RRF warned "Macro file has been started on channel ... but none was requested" (lab printer, 2026-11-08) and the channel waited for it
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-other.g", "G4 P90004\n");
        Task<string> m98 = await AbortWhileRrfCannotLockAsync(host);

        // DSF writes the InvalidateChannel after the next transfer. RRF asks for it again in the transfer that brings it, and its main task asks for
        // the other macro in the same transfer, once the first copy has ended the wait of the M98
        Step(host);
        Assert.That(host.Firmware.GetPendingRequests().Any(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == CodeChannel.HTTP), Is.True,
            "DSF did not write the InvalidateChannel");
        host.Firmware.RequestMacro(CodeChannel.HTTP, "http-other.g");
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }
        Assert.That(host.Firmware.WaitUntil(firmware => InvalidateChannelTransfers(host, CodeChannel.HTTP).Length == 2 && firmware.IsIdle(CodeChannel.HTTP), Timeout), Is.True,
            "RRF did not process the copy of the abort");

        Assert.That(await host.ExecuteAsync("G4 P90005").WaitAsync(Timeout), Is.Not.Null);
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P90004"));
        Assert.That(DsfStackDepth(host, CodeChannel.HTTP), Is.EqualTo(1));
    }

    [Test]
    public async Task TriggerMacroAfterAnAcceptedAbortIsStarted()
    {
        // Guard for the two tests above: once RRF has processed an InvalidateChannel without asking for it again, its next requests are valid.
        // DSF must start a trigger macro that RRF requests then, or the trigger is lost (RRF gives the request up after SbcMaxRequestTime)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("trigger5.g", "G4 P90006\n");
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.Trigger);
        for (int i = 0; i < 10 && InvalidateChannelTransfers(host, CodeChannel.Trigger).Length == 0; i++)
        {
            Step(host);
        }
        Assert.That(InvalidateChannelTransfers(host, CodeChannel.Trigger), Has.Length.EqualTo(1));

        host.Firmware.RequestMacro(CodeChannel.Trigger, "trigger5.g");
        host.Firmware.ReleaseTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetExecutedCodes(CodeChannel.Trigger).Any(code => code.Text == "G4 P90006"), Timeout), Is.True,
            "DSF did not start the trigger macro");
        Assert.That(InvalidateChannelTransfers(host, CodeChannel.Trigger), Has.Length.EqualTo(1));
    }

    /// <summary>
    /// Code replies that RRF sent for the code with the given text
    /// </summary>
    private static FakePacket[] CodeReplies(FakeFirmwareHost host, string code) =>
        [.. host.Firmware.GetSentPackets().Where(packet => packet.IsCodeReply && packet.RelatedCode?.Text == code)];

    [Test]
    public async Task NextCodeGetsItsOwnReplyAfterAbortWhileM98WaitsForItsMacro()
    {
        // Lab printer 2026-11-08 (finding T10): after an IPC InvalidateChannel of Telnet while M98 waited for its macro, the next G4 P0 finished with
        // "Error: M98: Macro file ... not found". InvalidateChannel ends the wait with an error (SbcInterface.cpp:880-886), so RRF still finishes M98
        // and reports the macro as not found (GCodes2.cpp:1713-1722). DSF has dropped M98 for the abort (Processor.InvalidateRegular) and matches
        // replies by order (Processor.HandleReply), so that reply completed the next code and its own reply came out of order
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-macro.g", "G4 P90001\n");
        host.Firmware.OnCode("G4", code => (code.Text == "G4 P2") ? FakeReply.Ok("reply of G4 P2") : null);
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");

        // RRF executes M98 and waits for the macro, which it requests in the transfer after that (as in ChannelWorksAfterAbortWhileMacroRequestIsUnderway)
        Task<string> m98 = host.StartCode("M98 P\"http-macro.g\"");
        for (int i = 0; i < 100 && !host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Any(code => code.Text == "M98 P\"http-macro.g\""); i++)
        {
            Step(host);
        }
        Step(host);

        // DSF aborts HTTP, and the next code waits in DSF at least until InvalidateChannel has gone out (Processor.Spin)
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Task<string> next = host.StartCode("G4 P2");
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }

        // RRF drops the reply of M98, so the next code gets its own
        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("reply of G4 P2\n"));
        Assert.That(CodeReplies(host, "M98 P\"http-macro.g\""), Is.Empty, "RRF replied to M98, which DSF had dropped");
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P90001"));
    }

    [Test]
    public async Task NextCodeGetsItsOwnReplyWhenRrfAsksForTheAbortAgain()
    {
        // The case on the lab printer (2026-11-08 22:39 UTC): the M98 waits for its macro, so the main task holds the buffer mutex and RRF asks for
        // the InvalidateChannel again (FailBufferLocks). The first copy flags M98 before it ends the wait (SbcInterface.cpp:880-886), the copy flags nothing
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.Firmware.OnCode("G4", code => (code.Text == "G4 P2") ? FakeReply.Ok("reply of G4 P2") : null);
        Task<string> m98 = await AbortWhileRrfCannotLockAsync(host);
        Task<string> next = host.StartCode("G4 P2");
        host.Firmware.ReleaseTransfers();
        try
        {
            await m98.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }

        Assert.That(await next.WaitAsync(Timeout), Is.EqualTo("reply of G4 P2\n"));
        Assert.That(InvalidateChannelTransfers(host, CodeChannel.HTTP), Has.Length.EqualTo(2), "RRF did not ask for the InvalidateChannel again");
        Assert.That(CodeReplies(host, "M98 P\"http-macro.g\""), Is.Empty, "RRF replied to M98, which DSF had dropped");
        Assert.That(CodeReplies(host, "G4 P2"), Has.Length.EqualTo(1));
    }

    /// <summary>
    /// Let M400 run on HTTP until the returned event is set, then it finishes with the reply "late reply of M400"
    /// </summary>
    private static ManualResetEventSlim HoldM400(FakeFirmwareHost host)
    {
        ManualResetEventSlim release = new();
        host.Firmware.OnCode("M400", _ => release.IsSet ? FakeReply.Ok("late reply of M400") : FakeReply.NotFinished);
        host.Firmware.OnCode("G4", code => (code.Text == "G4 P2") ? FakeReply.Ok("reply of G4 P2") : null);
        return release;
    }

    /// <summary>
    /// Abort HTTP while M400 runs, queue G4 P2 behind M400 in RRF and let M400 finish. Returns the reply of G4 P2
    /// </summary>
    private static async Task<string> AbortWhileM400RunsAsync(FakeFirmwareHost host, ManualResetEventSlim release, Task<string> started)
    {
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetCurrentCode(CodeChannel.HTTP)?.Text == "M400", Timeout), Is.True, "M400 did not start");
        await host.Services.GetRequiredService<LinkInterface>().AbortAllAsync(CodeChannel.HTTP);
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.InvalidateChannel), Timeout), Is.True,
            "RRF did not get the abort");
        try
        {
            await started.WaitAsync(Timeout);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the abort
        }

        // RRF executes M400 on (InvalidateBufferedCodes drops only codes that wait in its code buffer), so the next code waits behind it
        Task<string> next = host.StartCode("G4 P2");
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.GetReceivedCodes(CodeChannel.HTTP).Any(code => code.Text == "G4 P2"), Timeout), Is.True, "G4 P2 did not reach RRF");
        Assert.That(host.Firmware.GetCurrentCode(CodeChannel.HTTP)?.Text, Is.EqualTo("M400"));
        release.Set();
        return await next.WaitAsync(Timeout);
    }

    [Test]
    public async Task NextCodeGetsItsOwnReplyAfterAbortWhileACodeRuns()
    {
        // As NextCodeGetsItsOwnReplyAfterAbortWhileM98WaitsForItsMacro, but for a code that RRF is still executing, e.g. M400 or G4 with a long delay.
        // RRF finishes it after InvalidateChannel and would send its final reply, which completed the next code
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        using ManualResetEventSlim release = HoldM400(host);

        Assert.That(await AbortWhileM400RunsAsync(host, release, host.StartCode("M400")), Is.EqualTo("reply of G4 P2\n"));
        Assert.That(CodeReplies(host, "M400"), Is.Empty, "RRF replied to M400, which DSF had dropped");
    }

    [Test]
    public async Task NextCodeGetsItsOwnReplyAfterAbortWhileACodeOfAMacroRuns()
    {
        // As NextCodeGetsItsOwnReplyAfterAbortWhileACodeRuns, but M400 runs in a macro. InvalidateChannel aborts the macro (SbcInterface.cpp:893-894),
        // M400 goes on and replies on the level of M98. DSF completes M98 itself (Processor.AbortAllFilesAsync), and RRF sends no reply for it:
        // its final reply comes only when the macro ends normally (GCodes.cpp:770-790)
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        host.WriteMacro("http-hold.g", "M400\nG4 P90004\n");
        using ManualResetEventSlim release = HoldM400(host);

        Assert.That(await AbortWhileM400RunsAsync(host, release, host.StartCode("M98 P\"http-hold.g\"")), Is.EqualTo("reply of G4 P2\n"));
        Assert.That(CodeReplies(host, "M400"), Is.Empty, "RRF replied to M400, which DSF had dropped");
        Assert.That(CodeReplies(host, "M98 P\"http-hold.g\""), Is.Empty, "RRF replied to M98, which DSF had completed");
        Assert.That(host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.None.EqualTo("G4 P90004"));
    }

    /// <summary>
    /// Transfers in which RRF asked for a macro on a channel
    /// </summary>
    private static int[] MacroRequestTransfers(FakeFirmwareHost host, CodeChannel channel) =>
        [.. host.Firmware.GetSentPackets().Where(packet => packet.Request == Request.ExecuteMacro && packet.Channel == channel).Select(packet => packet.Transfer)];

    /// <summary>
    /// Transfer that brought the only InvalidateChannel of a channel to RRF
    /// </summary>
    private static int InvalidateChannelTransfer(FakeFirmwareHost host, CodeChannel channel) =>
        host.Firmware.GetReceivedRequests().Single(request => request.Request == SbcRequest.InvalidateChannel && request.Channel == channel).Transfer;

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
