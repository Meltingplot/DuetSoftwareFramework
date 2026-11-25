using System;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Replies on a channel that emulates Marlin. RRF creates Telnet in Marlin mode (RRF GCodes.cpp:130), DSF has no Telnet server, so a root IPC client
/// on the SBC sends the codes there (G3Helpers.RunCodeOnChannelAsync). DWC and the HMI use HTTP, which stays in RepRapFirmware mode
/// </summary>
[TestFixture]
public class TelnetCodes : LabTest
{
    private const CodeChannel Channel = CodeChannel.Telnet;
    private const string Directory = $"{Lab.MacroDirectory}/m20";
    private const string ListedFile = "a.g";

    /// <summary>
    /// Reply of M20 for a directory with one file in Marlin mode: DSF writes the header, one name per line and "End file list" (MCodeHandler.cs:190-228),
    /// and the Executed stage adds "ok" to the last code of a channel that emulates Marlin (Codes/Pipelines/Executed.cs:133-150). The same text as
    /// UnitTests/Codes/FileCodes.cs:104 (ListFilesInMarlinMode), the wiki (Gcodes.md:1666-1674) and RRF (GCodes.cpp:4661-4665). The older
    /// GcodeDictionaryFull.md:1196 of the wiki still shows "Begin file list:"
    /// </summary>
    private const string Expected = $"Begin file list\n{ListedFile}\nEnd file list\nok\n";

    [OneTimeTearDown]
    public async Task DeleteFilesAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, $"{Directory}/{ListedFile}");
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// M20 in Marlin mode starts with "Begin file list" without a colon. Without #73 DSF wrote "Begin file list:" (PR #73, MCodeHandler.cs:193 now),
    /// which upstream still does. DSF answers M20 itself and takes the mode from <c>inputs[channel].compatibility</c> (MCodeHandler.cs:179-183).
    /// If the channel does not emulate Marlin or nanoDLP, the test sets Marlin with M555 P2 on that channel (wiki Gcodes.md:5653-5677, the mode applies
    /// to the channel that sends it; DSF passes M555 to RRF, GCodes2.cpp:3807-3820) and sets the old mode back afterwards. Deterministic, one run catches it
    /// </summary>
    [Test]
    public async Task M20MarlinHeader()
    {
        Compatibility? before = Lab.Read(Session, model => model.Inputs[Channel]?.Compatibility);
        if (before is null)
        {
            Assert.Ignore($"{Channel} is disabled");
        }
        static bool EmulatesMarlin(ObjectModel model) => model.Inputs[Channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP;
        bool changeMode = !Lab.Read(Session, EmulatesMarlin);
        TestContext.Progress.WriteLine($"{Channel} compatibility: {before}{(changeMode ? ", setting Marlin for the test" : string.Empty)}");

        await Lab.UploadAsync(Session, $"{Directory}/{ListedFile}", "; lab test: listed by M20, never run\n");
        JsonElement reply;
        try
        {
            if (changeMode)
            {
                JsonElement setMode = await G3Helpers.RunCodeOnChannelAsync("M555 P2", Channel);
                Assert.That(G3Helpers.IsSuccess(setMode), Is.True, $"M555 P2 on {Channel}: {setMode}");
                await Lab.WaitForModelAsync(Session, EmulatesMarlin, TimeSpan.FromSeconds(10), $"{Channel} to emulate Marlin");
            }
            reply = await G3Helpers.RunCodeOnChannelAsync($"M20 P\"{Directory}\"", Channel);
        }
        finally
        {
            if (changeMode)
            {
                JsonElement restore = await G3Helpers.RunCodeOnChannelAsync($"M555 P{(int)before.Value}", Channel);
                bool restored = await Lab.TryWaitForModelAsync(Session, model => model.Inputs[Channel]?.Compatibility == before, TimeSpan.FromSeconds(10));
                TestContext.Progress.WriteLine($"M555 P{(int)before.Value} on {Channel}: {restore}, restored {restored}");
            }
        }
        if (changeMode)
        {
            Assert.That(Lab.Read(Session, model => model.Inputs[Channel]?.Compatibility), Is.EqualTo(before), $"The compatibility of {Channel} was not restored");
        }

        TestContext.Progress.WriteLine($"M20 on {Channel}: {reply}");
        Assert.That(G3Helpers.IsSuccess(reply), Is.True, $"M20 on {Channel}: {reply}");
        Assert.That(G3Helpers.GetString(reply, "result"), Is.EqualTo(Expected));
    }
}
