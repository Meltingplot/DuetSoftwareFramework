using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Codes that change the state of the machine, which lie outside what the tests may do on their own. Tim decides on each run, see design/lab-test-plan.md
/// </summary>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class IntrusiveCodes : LabTest
{
    /// <summary>
    /// M112 completed before the emergency stop reached RRF, so an object model update could replace the halted status (#88). Only M999 ends the stop
    /// </summary>
    [Test]
    public async Task M112StaysHaltedUntilM999()
    {
        bool stayedHalted = true;
        try
        {
            string reply = string.Empty;
            try
            {
                reply = await SendCodeAsync("M112");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                Assert.Fail($"M112 got no reply: {e.Message}");
            }
            Assert.That(reply, Does.Not.Contain("Error"));

            await Lab.WaitForModelAsync(Session, model => model.State.Status == MachineStatus.Halted, TimeSpan.FromSeconds(5), "the printer to report that it halted");
            for (int i = 0; i < 20; i++)
            {
                stayedHalted &= Lab.Read(Session, model => model.State.Status) == MachineStatus.Halted;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        finally
        {
            await Lab.ResetHaltedFirmwareAsync();
            await ReconnectSessionAsync();
        }
        Assert.That(stayedHalted, Is.True, "The status changed after M112 before M999");
    }

    /// <summary>
    /// DCS crashed with a stack overflow while M111 P-1 O1 was active (#70): MessageLogger.Log called ObjectModel.OutputAsync, which logs the message again
    /// before its first await, so the two called each other at the first DCS log line at or above the log level. Fixed in DSF #70 (3.7.0+mp.14):
    /// MessageLogger adds the message to the object model directly (MessageLogTarget.cs:121-135). M111 O1 itself logs nothing and M115 makes DCS log nothing
    /// at Info (MCodeHandler.cs:737-790), so the test also starts a macro while O1 is active, which logs "Starting macro file ..." at Info (FileFactory.cs:58-61).
    /// Without the fix that line crashes DCS: it gets a new PID, the test recovers (DCS restart and M999, one E-stop) and fails. If DCS logs nothing at Info
    /// (log level above Info), the line is missing from the journal and the test is inconclusive. Deterministic
    /// </summary>
    [Test]
    public async Task M111OutputDoesNotCrashDcs()
    {
        const string macro = $"{Lab.MacroDirectory}/m111-info-line.g";
        await Lab.UploadAsync(Session, macro, "; lab printer test: starting this macro makes DCS log a line at Info\n");
        string cursor = await Lab.GetJournalCursorAsync(), pid = await Lab.GetControlServerPidAsync();
        string? failure = null;
        try
        {
            Assert.That(await SendCodeAsync("M111 P-1 O1"), Does.Not.Contain("Error"));
            foreach (string code in Enumerable.Repeat("M115", 5))
            {
                Assert.That(await SendCodeAsync(code), Does.Not.Contain("Error"));
            }
            Assert.That(await SendCodeAsync($"M98 P\"{macro}\""), Does.Not.Contain("Error"));
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            failure = e.Message;
        }
        finally
        {
            try
            {
                await SendCodeAsync("M111 P-1 O0");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                // DCS has been restarted, which does not keep O1
            }
        }

        string journal = await Lab.ReadJournalAsync(cursor);
        if (await Lab.GetControlServerPidAsync() != pid)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"DCS was restarted{(journal.Contains("Stack overflow") ? " after a stack overflow" : string.Empty)} ({failure ?? "codes were answered"})");
        }
        Assert.That(failure, Is.Null, "A code got no reply");
        Assert.That(journal, Does.Not.Contain("Stack overflow"));
        if (!journal.Split('\n').Any(line => line.Contains("Starting macro file") && line.Contains("m111-info-line.g")))
        {
            Assert.Inconclusive("DCS logged no line at Info while O1 was active, so nothing went through MessageLogger");
        }
    }

    /// <summary>
    /// M30 resolved a relative name against 0:/ (#74), and M30 and M472 reported success when the file did not exist (#80)
    /// </summary>
    [Test]
    public async Task M30AndM472Delete()
    {
        const string name = "lab-test-delete-me.g";
        await Lab.UploadAsync(Session, $"0:/gcodes/{name}", "; lab printer test, deleted again\n");
        try
        {
            Assert.That(await SendCodeAsync($"M30 \"{name}\""), Does.Not.Contain("Error"));
            Assert.That((await Session.GetFileListAsync("0:/gcodes")).Any(item => item.Filename == name), Is.False, "M30 did not delete the file from 0:/gcodes");
            Assert.That(await SendCodeAsync($"M30 \"{name}\""), Does.Contain($"Failed to delete file {name}: file not found"));
            Assert.That(await SendCodeAsync($"M472 P\"0:/gcodes/{name}\""), Does.Contain($"Failed to delete file or directory 0:/gcodes/{name}: file or directory not found"));
        }
        finally
        {
            await Lab.DeleteAsync(Session, $"0:/gcodes/{name}");
        }
    }

    /// <summary>
    /// M929 with an invalid log level stopped logging and reported success (#81). It stops logging as in RRF, but reports the error, so restore the state
    /// </summary>
    [Test]
    public async Task M929InvalidLevelReportsAnError()
    {
        string? level = (await QueryAsync("state.logLevel")).GetString();
        JsonElement logFile = await QueryAsync("state.logFile");
        try
        {
            Assert.That(await SendCodeAsync("M929 S4"), Does.Contain("parameter 'S' too high"));
            Assert.That(await SendCodeAsync("M929 S-1"), Does.Contain("expected non-negative integer after 'S'"));
        }
        finally
        {
            int? s = level switch { "warn" => 1, "info" => 2, "debug" => 3, _ => null };
            if (s is not null && logFile.ValueKind == JsonValueKind.String)
            {
                await SendCodeAsync($"M929 P\"{logFile.GetString()}\" S{s}");
            }
        }
        Assert.That((await QueryAsync("state.logLevel")).GetString(), Is.EqualTo(level), "The log level was not restored");
    }

    /// <summary>
    /// M606 S1 from a channel other than File forked the job reader of DCS, although RRF only warns (#76)
    /// </summary>
    [Test]
    public async Task M606FromHttpOnlyWarns()
    {
        InputChannelState? before = Lab.Read(Session, model => model.Inputs[CodeChannel.File2]?.State);
        Assert.That(await SendCodeAsync("M606 S1"), Does.Contain("this command is valid only when running a job from a stored file"));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.That(Lab.Read(Session, model => model.Inputs[CodeChannel.File2]?.State), Is.EqualTo(before), "File2 was started");
        Assert.That(Lab.Read(Session, model => model.Inputs[CodeChannel.File2]?.StackDepth), Is.EqualTo(0), "File2 got a stack");
    }
}
