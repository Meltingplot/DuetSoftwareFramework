using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// The object model of DSF, and so of DWC, while other output keeps the output buffers of RRF busy
/// </summary>
/// <remarks>
/// RRF builds every object model response for DSF in its pool of 40 output buffers of 256 bytes (Configuration.h). Since RRF #113 the SBC task builds one
/// only while it holds the object model report mutex and at least 36 buffers are free (MinimumBuffersForSbcJson in SbcInterface.cpp). With that many free,
/// a response that does not fit is longer than a transfer and is discarded with "Cannot store excessively long object model response". Otherwise RRF leaves
/// the request unacknowledged and DSF sends it again, with the warning "Resending packet #… (request GetObjectModel)" (LinkService.cs). So the model waits
/// while other output holds more than 4 buffers.
///
/// The test keeps the pool busy from three channels: M409 … R1, which RRF answers itself under the same mutex with 18 to 19 buffers per reply, on Telnet and
/// Aux2, and M122 on Aux. Meanwhile it watches the model the way DWC does, through the WebSocket of DWS, and changes a global variable from HTTP once per
/// tick. A global of the test brings the response for the key "global" close to a full transfer, so that each tick needs nearly every free buffer
/// </remarks>
[TestFixture]
public partial class OutputBufferPressure : LabTest
{
    /// <summary>
    /// How long the other channels keep the buffers busy, LAB_PRESSURE_SECONDS overrides it
    /// </summary>
    private static readonly int PressureSeconds = int.TryParse(Environment.GetEnvironmentVariable("LAB_PRESSURE_SECONDS"), out int seconds) ? seconds : 60;

    /// <summary>
    /// Longest time the model may lag behind, as a user of DWC would notice it
    /// </summary>
    private const double MaxStaleSeconds = 5;

    /// <summary>
    /// Size the response for the key "global" is brought to. One transfer holds 8180 bytes of it
    /// (SbcTransferBufferSize - sizeof(PacketHeader) - sizeof(StringHeader), SbcMessageFormats.h)
    /// </summary>
    private const int TargetGlobalResponseBytes = 7000;
    private const int MaxGlobalResponseBytes = 8180;

    private const string TickVariable = "labTestPressureTick";
    private const string LoadVariable = "labTestPressureLoad";

    /// <summary>
    /// Codes that keep the buffers busy, per channel. Every channel runs its code again as soon as it has the reply
    /// </summary>
    private static readonly (CodeChannel Channel, string Code)[] Pressure =
    [
        (CodeChannel.Telnet, "M409 K\"move\" F\"v\" R1"),
        (CodeChannel.Aux2, "M409 K\"boards\" F\"v\" R1"),
        (CodeChannel.Aux, "M122")
    ];

    /// <summary>
    /// IPC client on the SBC with one connection per channel, as in AbortsCrossingMacroRequests. It runs as root, which DCS grants every command
    /// (Connection.cs), sends the code of its channel again as soon as the reply is there, and prints one JSON object with the counts per channel.
    /// coreutils timeout ends it if the local side goes away, plink does not stop a remote script
    /// </summary>
    private const string Script = """
        import json, socket, statistics, sys, threading, time

        duration = float(sys.argv[1])
        jobs = [arg.split("=", 1) for arg in sys.argv[2:]]

        class Ipc:
            def __init__(self):
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect("/run/dsf/dcs.sock")
                self.data = b""
                init = self.receive(5)
                self.send({"mode": "Command", "version": init["version"]})
                if not self.receive(5).get("success"):
                    raise RuntimeError("DCS refused the command mode")

            def send(self, message):
                self.sock.sendall(json.dumps(message).encode())

            def receive(self, timeout):
                deadline = time.monotonic() + timeout
                while True:
                    try:
                        text = self.data.decode().lstrip()
                        message, end = json.JSONDecoder().raw_decode(text)
                        self.data = text[end:].encode()
                        return message
                    except ValueError:
                        pass
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        return None
                    self.sock.settimeout(remaining)
                    try:
                        chunk = self.sock.recv(65536)
                    except socket.timeout:
                        return None
                    if not chunk:
                        raise ConnectionError("DCS closed the connection")
                    self.data += chunk

        results = {}

        def run(channel, code):
            times, errors, first_error = [], 0, None
            try:
                ipc = Ipc()
                deadline = time.monotonic() + duration
                while time.monotonic() < deadline:
                    start = time.monotonic()
                    ipc.send({"command": "SimpleCode", "code": code, "channel": channel})
                    reply = ipc.receive(30)
                    if reply is None:
                        errors += 1
                        first_error = first_error or "no reply within 30 s"
                        break
                    result = reply.get("result") or ""
                    if not reply.get("success") or result.lstrip().startswith("Error"):
                        errors += 1
                        first_error = first_error or (reply.get("errorMessage") or result)[:160]
                    times.append(time.monotonic() - start)
            except Exception as e:
                errors += 1
                first_error = first_error or "%s: %s" % (type(e).__name__, e)
            results[channel] = {"code": code, "count": len(times), "errors": errors, "firstError": first_error,
                                "medianMs": round(statistics.median(times) * 1000, 1) if times else None,
                                "maxMs": round(max(times) * 1000, 1) if times else None}

        threads = [threading.Thread(target=run, args=(channel, code)) for channel, code in jobs]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        print(json.dumps(results))
        """;

    [GeneratedRegex(@"Used output buffers: (\d+) of (\d+) \((\d+) max\)")]
    private static partial Regex OutputBuffersRegex();

    [GeneratedRegex(@"Resending packet #\d+ \(request GetObjectModel\)")]
    private static partial Regex ObjectModelResendRegex();

    private static readonly string[] LinkErrors = ["Connection timeout", "Lost connection", "Connection to controller has been reset", "TfrRdy", "Out-of-order"];

    private const string DiscardMessage = "Cannot store excessively long object model response";

    /// <summary>
    /// The object model keeps up while three channels keep RRF's output buffers busy: every change of a global variable reaches the model of DWS within
    /// <see cref="MaxStaleSeconds"/>, state.upTime keeps counting, no response that fits a transfer is discarded, the link stays up, and every code of
    /// the other channels gets its reply without an error
    /// </summary>
    [Test]
    public async Task ObjectModelKeepsUpdatingUnderOutputBufferPressure()
    {
        foreach ((CodeChannel channel, _) in Pressure)
        {
            Assert.That(Lab.Read(Session, model => model.Inputs[channel] is not null), Is.True, $"{channel} is disabled");
        }

        await SetGlobalAsync(TickVariable, "0");
        await SetGlobalAsync(LoadVariable, "0");
        try
        {
            // Bring the response for "global" close to a full transfer. RRF refuses an array larger than a block of its heap ("Array too large",
            // ArrayHandle::Allocate), so the load is an array of arrays; vector() shares the inner one, the response lists every element.
            // Each inner array adds 402 bytes ("[1234567,…,1234567],") to the response that RRF builds
            int baseBytes = await GetGlobalResponseBytesAsync();
            int elements = Math.Max(1, (TargetGlobalResponseBytes - baseBytes) / 402);
            await SetGlobalAsync(LoadVariable, $"vector({elements}, vector(50, 1234567))");
            await Lab.WaitForModelAsync(Session, model => model.Global.TryGetValue(LoadVariable, out JsonElement? value) &&
                                                          value?.ValueKind == JsonValueKind.Array && value.Value.GetArrayLength() == elements,
                                        TimeSpan.FromSeconds(30), $"{LoadVariable} with {elements} arrays in the model");
            int loadedBytes = await GetGlobalResponseBytesAsync();
            TestContext.Progress.WriteLine($"Response for \"global\": {baseBytes} bytes, {loadedBytes} with {elements} arrays of 50 elements in {LoadVariable} (one transfer holds {MaxGlobalResponseBytes})");
            Assert.That(loadedBytes, Is.InRange(TargetGlobalResponseBytes - 500, MaxGlobalResponseBytes - 500), "the response for \"global\" did not get the intended size");

            string buffersBefore = OutputBuffersRegex().Match(await SendCodeAsync("M122", 30)).Value;
            int upTimeBefore = Lab.Read(Session, model => model.State.UpTime);
            string cursor = await Lab.GetJournalCursorAsync();

            // Start the pressure and watch the model meanwhile
            Task<string> pressure = Lab.RunOnSbcAsync($$"""
                command -v python3 >/dev/null || { echo '{"error": "python3 is missing"}'; exit 0; }
                sudo -n timeout {{PressureSeconds + 60}} python3 - {{PressureSeconds}} {{string.Join(' ', Pressure.Select(job => $"'{job.Channel}={job.Code}'"))}} <<'EOF'
                {{Script}}
                EOF
                """);

            using CancellationTokenSource stopSampling = new();
            Task<double> upTimeGap = SampleUpTimeGapAsync(stopSampling.Token);

            List<double> codeMs = [], modelMs = [], m409Ms = [];
            List<int> notSeen = [];
            List<string> codeErrors = [];
            await Task.Delay(TimeSpan.FromSeconds(2));
            Stopwatch elapsed = Stopwatch.StartNew();
            for (int tick = 1; elapsed.Elapsed < TimeSpan.FromSeconds(PressureSeconds - 5); tick++)
            {
                // A change from HTTP, like a user of DWC: the code, then the change in the model
                Stopwatch watch = Stopwatch.StartNew();
                string reply = await SendCodeAsync($"set global.{TickVariable} = {tick}", 30);
                codeMs.Add(watch.Elapsed.TotalMilliseconds);
                if (reply.Contains("Error"))
                {
                    codeErrors.Add($"tick {tick}: {reply.Trim()}");
                }
                int expected = tick;
                if (!await Lab.TryWaitForModelAsync(Session, model => GetTick(model) >= expected, TimeSpan.FromSeconds(15)))
                {
                    notSeen.Add(tick);
                }
                modelMs.Add(watch.Elapsed.TotalMilliseconds);

                if (tick % 5 == 0)
                {
                    // DSF answers this M409 from its own model once a full update has come in (MCodeHandler.cs), so it waits for the object model of RRF
                    watch.Restart();
                    await SendCodeAsync("M409 K\"state.upTime\"", 30);
                    m409Ms.Add(watch.Elapsed.TotalMilliseconds);
                }
                await Task.Delay(500);
            }

            stopSampling.Cancel();
            double maxUpTimeGap = await upTimeGap;
            string output = await pressure;

            string buffersAfter = OutputBuffersRegex().Match(await SendCodeAsync("M122", 30)).Value;
            int upTimeAfter = Lab.Read(Session, model => model.State.UpTime);
            string[] journal = (await Lab.ReadJournalAsync(cursor)).Split('\n');
            int resends = journal.Count(line => ObjectModelResendRegex().IsMatch(line));
            string[] discards = [.. journal.Where(line => line.Contains(DiscardMessage))];
            string[] linkErrors = [.. journal.Where(line => LinkErrors.Any(line.Contains))];
            string[] suppressed = [.. (await Lab.ReadJournalAsync(cursor, "systemd-journald")).Split('\n').Where(line => line.Contains("Suppressed") && line.Contains("duetcontrolserver"))];

            TestContext.Progress.WriteLine($"Pressure for {PressureSeconds} s: {output.Trim()}");
            TestContext.Progress.WriteLine($"RRF output buffers before: {buffersBefore}, after: {buffersAfter}");
            TestContext.Progress.WriteLine($"Ticks: {modelMs.Count}, code {Describe(codeMs)}, in the model after {Describe(modelMs)}; M409 from DSF {Describe(m409Ms)}; longest time without a new state.upTime {maxUpTimeGap:0.0} s");
            TestContext.Progress.WriteLine($"Journal: {resends} GetObjectModel resends ({resends / (double)PressureSeconds:0.0}/s), {discards.Length} discarded responses, {linkErrors.Length} link errors, {suppressed.Length} suppression notes of journald");
            foreach (string line in suppressed.Concat(discards.Take(3)).Concat(linkErrors.Take(5)))
            {
                TestContext.Progress.WriteLine($"  {line}");
            }

            using JsonDocument result = JsonDocument.Parse(output.Trim().Split('\n').Last());
            if (result.RootElement.TryGetProperty("error", out JsonElement scriptError))
            {
                Assert.Ignore($"No pressure: {scriptError.GetString()}");
            }

            Assert.Multiple(() =>
            {
                foreach ((CodeChannel channel, string code) in Pressure)
                {
                    JsonElement counts = result.RootElement.GetProperty(channel.ToString());
                    Assert.That(counts.GetProperty("count").GetInt32(), Is.Positive, $"{channel} ran no {code}");
                    Assert.That(counts.GetProperty("errors").GetInt32(), Is.Zero, $"{channel}: {code} failed: {counts.GetProperty("firstError")}");
                }
                Assert.That(codeErrors, Is.Empty, "set global from HTTP failed");
                Assert.That(notSeen, Is.Empty, "ticks that did not reach the model of DWS within 15 s");
                Assert.That(modelMs.Max() / 1000, Is.LessThanOrEqualTo(MaxStaleSeconds), "longest time until a tick reached the model of DWS, in s");
                Assert.That(maxUpTimeGap, Is.LessThanOrEqualTo(MaxStaleSeconds), "longest time without a new state.upTime in the model of DWS, in s");
                Assert.That(discards, Is.Empty, "object model responses that fit a transfer were discarded");
                Assert.That(linkErrors, Is.Empty, "the link reported errors");
                Assert.That(upTimeAfter, Is.GreaterThanOrEqualTo(upTimeBefore), "RRF restarted");
            });

            if (resends == 0)
            {
                Assert.Inconclusive("RRF never deferred a GetObjectModel request, so the pressure did not reach the wait of RRF #113");
            }
        }
        finally
        {
            // The globals stay until the next reset, so take the load out of every later response for "global"
            await SendCodeAsync($"set global.{LoadVariable} = 0", 30);
        }
    }

    private static int GetTick(ObjectModel model) =>
        model.Global.TryGetValue(TickVariable, out JsonElement? value) && value?.ValueKind == JsonValueKind.Number ? value.Value.GetInt32() : -1;

    private static string Describe(List<double> milliseconds) =>
        (milliseconds.Count == 0) ? "-" : $"median {milliseconds.Order().ElementAt(milliseconds.Count / 2):0} ms, max {milliseconds.Max():0} ms";

    /// <summary>
    /// Create a global variable of the test or set it. A new one is awaited in the model, so that the next call sees it and uses set
    /// </summary>
    private async Task SetGlobalAsync(string name, string expression)
    {
        bool exists = Lab.Read(Session, model => model.Global.ContainsKey(name));
        string reply = await SendCodeAsync(exists ? $"set global.{name} = {expression}" : $"global {name} = {expression}", 30);
        Assert.That(reply, Does.Not.Contain("Error"), $"Could not set global.{name}");
        if (!exists)
        {
            await Lab.WaitForModelAsync(Session, model => model.Global.ContainsKey(name), TimeSpan.FromSeconds(30), $"global.{name} in the model");
        }
    }

    /// <summary>
    /// Length of the response that RRF builds for the key "global" with the flags DSF uses for it. M409 with R1 is answered by RRF (MCodeHandler.cs),
    /// with the same function that builds the response for DSF (RepRap::GetModelResponse)
    /// </summary>
    private async Task<int> GetGlobalResponseBytesAsync() =>
        Encoding.UTF8.GetByteCount((await SendCodeAsync("M409 K\"global\" F\"d99v\" R1", 30)).TrimEnd());

    /// <summary>
    /// Longest time in seconds in which the model of DWS showed no new state.upTime, which RRF counts once a second
    /// </summary>
    private async Task<double> SampleUpTimeGapAsync(CancellationToken cancellationToken)
    {
        int lastUpTime = Lab.Read(Session, model => model.State.UpTime);
        Stopwatch sinceChange = Stopwatch.StartNew();
        double maxGap = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            int upTime = Lab.Read(Session, model => model.State.UpTime);
            if (upTime != lastUpTime)
            {
                maxGap = Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
                lastUpTime = upTime;
                sinceChange.Restart();
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
    }
}
