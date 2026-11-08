using System;
using System.Net.Http;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Macro paths that FilePathResolver refuses
/// </summary>
/// <remarks>
/// RRF passes the name of M98 P to DSF as it is (GCodes2.cpp:1713-1721, GCodes.cpp:3697-3699). DSF resolves it relative to 0:/sys and refuses a path
/// that leaves its volume (FilePathResolver.cs:63-72) or names an unknown drive (FilePathResolver.cs:80-95) with an ArgumentException. Before #132 that
/// exception escaped Processor.DoMacroFile and LinkService.HandleMacroRequest and stopped DCS: on 2026-11-07 (DSF 3.7.0+mp.13) M98 P"99:/macros/..." got
/// no reply, systemd killed DCS after its 15 s stop timeout and RRF stayed halted until M999 (MacroPaths.MacroPathRefusedByResolver in the unit tests).
/// Category Restart, because DCS without the fix stops and the test then restarts it and sends M999. Drive 9 exists on the lab printer, so the tests use 99
/// </remarks>
[TestFixture, Category("Restart")]
public class RefusedMacroPaths : LabTest
{
    private const string Directory = $"{Lab.MacroDirectory}/refused";
    private const string CallingMacro = $"{Directory}/calling.g";

    /// <summary>
    /// A macro that does not exist, for comparison
    /// </summary>
    private const string MissingMacro = $"{Directory}/missing.g";

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, CallingMacro);
            await Lab.DeleteAsync(session, Directory);
        }
    }

    /// <summary>
    /// Send a code that may stop DCS without the fix
    /// </summary>
    /// <returns>Reply of the code</returns>
    private async Task<string> SendOrRecoverAsync(string code, string pid)
    {
        try
        {
            return await SendCodeAsync(code, 20);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            string newPid = await Lab.GetControlServerPidAsync();
            await Lab.RecoverAsync();
            Assert.Fail($"{code} got no reply ({e.GetType().Name}: {e.Message}), DCS pid {pid} before and {newPid} after");
            throw;
        }
    }

    /// <summary>
    /// Check that DCS is still the same process, is not halted and passes codes on to RRF
    /// </summary>
    private async Task AssertDcsGoesOnAsync(string pid, string what)
    {
        // R1 makes DSF pass the query on to RRF (MCodeHandler.cs:808-813), as in SpiFaultInjection
        string firmwareReply = await SendOrRecoverAsync("M409 K\"state.upTime\" R1", pid);
        string newPid = await Lab.GetControlServerPidAsync();
        Assert.Multiple(() =>
        {
            Assert.That(newPid, Is.EqualTo(pid), $"DCS was restarted after {what}");
            Assert.That(firmwareReply, Does.Contain("\"result\":"), "RRF did not answer M409 R1");
            Assert.That(Lab.Read(Session, model => model.State.Status), Is.Not.EqualTo(MachineStatus.Halted), "RRF is halted");
        });
    }

    /// <summary>
    /// M98 with a refused path from a client gets the reply of a missing file (GCodes2.cpp:1720) and DCS goes on. The fix logs the reason as an error
    /// "Failed to start macro file" (Processor.DoMacroFile), which proves that the request went the new way
    /// </summary>
    [TestCase("99:/macros/lab-tests/refused/unknown-drive.g", TestName = "M98WithUnknownDriveIsNotFound")]
    [TestCase("0:/../lab-tests-refused-escape.g", TestName = "M98LeavingTheVolumeIsNotFound")]
    public async Task RefusedPathIsReportedLikeMissingFile(string fileName)
    {
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        string reply = await SendOrRecoverAsync($"M98 P\"{fileName}\"", pid);
        await AssertDcsGoesOnAsync(pid, $"M98 P\"{fileName}\"");

        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"M98 P\"{fileName}\": {reply.Trim()}");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain($"Macro file {fileName} not found"));
            Assert.That(journal, Does.Contain($"Failed to start macro file {fileName}"), "DCS did not log the refused path");
        });
    }

    /// <summary>
    /// A refused path in a macro ends like a missing file: the reply, the error in the log and what the calling macro does after it are the same
    /// (MacroPaths.RefusedPathInMacroEndsLikeMissingFile in the unit tests). The error of the inner M98 does not reach the HTTP reply of the
    /// outer one (both replies were empty on the lab printer on 2026-11-08), so the test reads it from the journal
    /// </summary>
    [Test]
    public async Task RefusedPathInMacroEndsLikeMissingFile()
    {
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        const string refusedMacro = "99:/macros/lab-tests/refused/missing.g";

        async Task<(string Reply, int Step)> RunCallingMacroAsync(string fileName)
        {
            await Lab.UploadAsync(Session, CallingMacro, $"""
                if !exists(global.labTestRefusedStep)
                  global labTestRefusedStep = 0
                set global.labTestRefusedStep = 1
                M98 P"{fileName}"
                set global.labTestRefusedStep = 2

                """);
            string reply = await SendOrRecoverAsync($"M98 P\"{CallingMacro}\"", pid);
            return (reply.Trim(), (await QueryAsync("global.labTestRefusedStep")).GetInt32());
        }

        (string missingReply, int missingStep) = await RunCallingMacroAsync(MissingMacro);
        (string refusedReply, int refusedStep) = await RunCallingMacroAsync(refusedMacro);
        await AssertDcsGoesOnAsync(pid, "the refused M98 in a macro");

        string journal = await Lab.ReadJournalAsync(cursor);
        TestContext.Progress.WriteLine($"Missing file: step {missingStep}, reply '{missingReply}'; refused path: step {refusedStep}, reply '{refusedReply}'");
        Assert.Multiple(() =>
        {
            Assert.That(journal, Does.Contain($"Macro file {MissingMacro} not found"), "The missing file was not reported, so the comparison shows nothing");
            Assert.That(journal, Does.Contain($"Macro file {refusedMacro} not found"), "The refused path was not reported like a missing file");
            Assert.That(refusedReply, Is.EqualTo(missingReply.Replace(MissingMacro, refusedMacro)));
            Assert.That(refusedStep, Is.EqualTo(missingStep), "The calling macro went on differently");
        });
    }
}
