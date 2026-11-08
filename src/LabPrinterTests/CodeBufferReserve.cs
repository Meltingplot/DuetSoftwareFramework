using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Room in RRF's code buffer for a trigger macro while other channels read ahead
/// </summary>
/// <remarks>
/// RRF keeps one code buffer of 4096 bytes for all channels (SbcMessageFormats.h:47, SbcInterface.cpp:76) and frees a code's room only when the channel
/// takes the code (SbcInterface.cpp:1924-1948), so codes that DSF reads ahead behind a waiting code stay in it. Before #133 every channel could buffer
/// (4096 - 2 * 384) bytes (LinkInterface.cs:46), so two channels that wait filled the buffer and a trigger macro got no code out (T3). With the fix, codes
/// read ahead leave 2 * (4 + 384) bytes free, which only the next code of a channel without buffered codes may use (LinkInterface.SendCode).
/// Uses trigger 30 with sys/trigger30.g like TriggerMacros, and a macro that only waits on Telnet and on Aux2 (M98.4 and M98.5)
/// </remarks>
[TestFixture, Category("Trigger")]
public partial class CodeBufferReserve : LabTest
{
    private const int TriggerNumber = 30;
    private const string TriggerMacro = "0:/sys/trigger30.g";
    private const string Directory = $"{Lab.MacroDirectory}/code-buffer";
    private const string ResetMacro = $"{Directory}/reset.g";
    private const string LoadMacro = $"{Directory}/load.g";

    /// <summary>
    /// Channels that wait while the codes they have read ahead fill RRF's code buffer, with the number of M98.n (MCodeHandler.cs:603-610)
    /// </summary>
    private static readonly (CodeChannel Channel, int Number)[] LoadChannels = [(CodeChannel.Telnet, 4), (CodeChannel.Aux2, 5)];

    /// <summary>
    /// How long each load channel waits. Without the fix trigger macros cannot get a code to RRF for this long, which includes the macros
    /// of the door and E-stop triggers, so it is kept short
    /// </summary>
    private const int HoldSeconds = 12;

    /// <summary>
    /// How long the test counts trigger runs while the load channels wait
    /// </summary>
    private const int WindowSeconds = 5;

    /// <summary>
    /// A G4 P0 with this string parameter takes 340 bytes in RRF's buffer: 4 bytes buffered code header (Consts.BufferedCodeHeaderSize),
    /// 16 bytes code header, 4 bytes line number, 2 * 8 bytes parameters and the 300 bytes of the string (Writer.cs:95-166). RRF's G4 reads only
    /// S and P (GCodes.cpp:4025-4027) and does not wait for motion unless the channel commanded some (GCodes.cpp:4013-4023)
    /// </summary>
    private static readonly string Filler = $"G4 P0 Q\"{new string('x', 300)}\"";

    /// <summary>
    /// Bytes that one filler code takes in RRF's code buffer
    /// </summary>
    private const int FillerSize = 340;

    /// <summary>
    /// Fillers per load channel. DSF reads at most 16 codes of a macro ahead (Settings.BufferedMacroCodes, MacroFile.cs:394), the G4 S is one of them.
    /// Without the fix each channel may buffer up to 3328 bytes, so the two together send fillers until RRF reports less room than one filler needs
    /// (SendCode wants more room than the code, LinkInterface.cs:578; RRF reports the largest free block, SbcInterface.cpp:1376). With the fix they leave
    /// more than 2 * 388 bytes free
    /// </summary>
    private const int FillersPerChannel = 15;

    /// <summary>
    /// The load channels must have this much buffered, or the test shows nothing
    /// </summary>
    private const int MinBufferedBytes = 2000;

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        if ((await session.GetFileListAsync("0:/sys")).Any(item => item.Filename == "trigger30.g"))
        {
            Assert.Ignore($"{TriggerMacro} exists on this printer, so the tests do not touch trigger {TriggerNumber}");
        }

        await Lab.UploadAsync(session, ResetMacro, """
            if !exists(global.labTestReserveRuns)
              global labTestReserveRuns = 0
            else
              set global.labTestReserveRuns = 0

            """);
        await Lab.UploadAsync(session, LoadMacro, $"G4 S{HoldSeconds}\n" + string.Concat(Enumerable.Repeat(Filler + "\n", FillersPerChannel)));

        // The first code of the trigger macro has the size of a filler, so it does not fit into what the load leaves without the fix
        await Lab.UploadAsync(session, TriggerMacro, $"{Filler}\nset global.labTestReserveRuns = global.labTestReserveRuns + 1\n");
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, TriggerMacro);
            await Lab.DeleteAsync(session, LoadMacro);
            await Lab.DeleteAsync(session, ResetMacro);
            await Lab.DeleteAsync(session, Directory);
        }
    }

    [GeneratedRegex(@"^Code buffer space: (-?\d+)", RegexOptions.Multiline)]
    private static partial Regex BufferSpaceRegex();

    [GeneratedRegex(@"^Buffered codes: (\d+) bytes total")]
    private static partial Regex BufferedBytesRegex();

    /// <summary>
    /// Read the room DSF sees in RRF's code buffer (LinkInterface.cs:85-88) and the bytes each channel has buffered (Processor.cs:459-462, 489-493)
    /// from M122 "DSF", which DSF answers itself (MCodeHandler.cs:789-795)
    /// </summary>
    private async Task<(int Space, Dictionary<CodeChannel, int> Buffered)> ReadCodeBufferAsync()
    {
        string diagnostics = await SendCodeAsync("M122 \"DSF\"");
        Match space = BufferSpaceRegex().Match(diagnostics);
        Assert.That(space.Success, Is.True, $"M122 \"DSF\" shows no code buffer space: {diagnostics}");

        Dictionary<CodeChannel, int> buffered = [];
        CodeChannel? channel = null;
        foreach (string line in diagnostics.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.EndsWith(':') && Enum.TryParse(line[..^1], out CodeChannel header))
            {
                channel = header;
            }
            else if (channel is not null && BufferedBytesRegex().Match(line) is { Success: true } match)
            {
                buffered[channel.Value] = int.Parse(match.Groups[1].Value);
            }
        }
        return (int.Parse(space.Groups[1].Value), buffered);
    }

    private static int LoadBytes(Dictionary<CodeChannel, int> buffered) => LoadChannels.Sum(item => buffered.GetValueOrDefault(item.Channel));

    private static string Describe((int Space, Dictionary<CodeChannel, int>? Buffered) buffer) => (buffer.Buffered is null) ? "not read" :
        $"code buffer space {buffer.Space} bytes, buffered {string.Join(", ", buffer.Buffered.Select(item => $"{item.Key} {item.Value}"))}";

    /// <summary>
    /// The trigger fires twice per second. While Telnet and Aux2 wait in G4 S12 with the fillers behind it read ahead, its macro must still get its
    /// first code, which is as long as a filler, to RRF. Without the fix it waits until the load channels go on (TriggerMacros.TriggerMacroGetsSpareCodeBufferRoom
    /// in the unit tests)
    /// </summary>
    [Test]
    public async Task TriggerMacroGetsRoomWhileOtherChannelsReadAhead()
    {
        Assert.That(await SendCodeAsync($"M581 T{TriggerNumber}"), Does.Contain("is not configured"), $"Trigger {TriggerNumber} is in use");
        Assert.That(Lab.Read(Session, model => LoadChannels.All(item => model.Inputs[item.Channel] is not null)), Is.True, "Telnet or Aux2 is disabled");
        await SendCodeAsync($"M98 P\"{ResetMacro}\"");

        // Turns true twice per second. No string literals, they hang RRF 3.7 in M581.1 (TriggerMacros.StringLiteralInExpression)
        Assert.That(await SendCodeAsync($"M581.1 T{TriggerNumber} P\"mod(state.msUpTime, 500) < 250\" R0"), Does.Not.Contain("Error"));
        (int Space, Dictionary<CodeChannel, int> Buffered) loaded = default, stillLoaded = default;
        int runsBefore = 0, runsAfter = 0;
        bool loadInPlace = false, holdLasted = false;
        try
        {
            // M581.1 and M98 go through RRF's code buffer, so they are sent before the load. The macros that the M98.n start only wait
            foreach ((_, int number) in LoadChannels)
            {
                Assert.That(await SendCodeAsync($"M98.{number} P\"{LoadMacro}\""), Does.Not.Contain("Error"), $"M98.{number}");
            }

            for (int i = 0; i < 20 && !loadInPlace; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                loaded = await ReadCodeBufferAsync();
                loadInPlace = LoadBytes(loaded.Buffered) >= MinBufferedBytes;
            }

            if (loadInPlace)
            {
                // M122 "DSF" and M409 of a global do not use RRF's code buffer (MCodeHandler.cs:789-795, 808-843)
                runsBefore = (await QueryAsync("global.labTestReserveRuns")).GetInt32();
                await Task.Delay(TimeSpan.FromSeconds(WindowSeconds));
                runsAfter = (await QueryAsync("global.labTestReserveRuns")).GetInt32();
                stillLoaded = await ReadCodeBufferAsync();
                holdLasted = LoadBytes(stillLoaded.Buffered) >= MinBufferedBytes;
            }
        }
        finally
        {
            // The load channels go on after their G4, then the trigger can be deleted without waiting for room
            bool finished = await Lab.TryWaitForModelAsync(Session, model => LoadChannels.All(item => model.Inputs[item.Channel]?.StackDepth == 0),
                TimeSpan.FromSeconds(HoldSeconds + 20));
            if (finished)
            {
                await SendCodeAsync($"M581 T{TriggerNumber} P-1");

                // Let the last trigger macro finish
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            else
            {
                await Lab.RecoverAsync();
                await ReconnectSessionAsync();
                await SendCodeAsync($"M581 T{TriggerNumber} P-1");
                Assert.Fail("The load channels did not finish their macros");
            }
        }

        int runs = runsAfter - runsBefore;
        TestContext.Progress.WriteLine($"Load: {Describe(loaded)}. Trigger {TriggerNumber} ran {runs} times in {WindowSeconds} s while the load waited. " +
            $"At the end of the window: {Describe(stillLoaded)}");
        Assert.That(loadInPlace, Is.True, $"The load channels did not fill RRF's code buffer, so the test shows nothing: {Describe(loaded)}");
        Assert.That(holdLasted, Is.True, $"The load ended before the window, so the trigger runs may come after it: {Describe(stillLoaded)}");
        Assert.That(runs, Is.GreaterThanOrEqualTo(WindowSeconds), $"The trigger macro got no room in RRF's code buffer while the load waited (T3), {Describe(loaded)}");
    }
}
