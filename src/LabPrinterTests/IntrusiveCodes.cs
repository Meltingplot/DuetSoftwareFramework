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

    /// <summary>
    /// After <c>M505.1 P"dir"</c>, a relative <c>M505.1 P"sub"</c> looked in 0:/www/sub instead of the current web folder and failed with "Directory not
    /// found" (#75). Fixed in DSF #75 (3.7.0+mp.14): M505 and M505.1 resolve a relative folder against directories.system or directories.web
    /// (MCodeHandler.cs:1004, FileDirectory.Web), as the wiki says: "default path is the existing HTTP root folder if a relative path is given"
    /// (Gcodes.md:5450). DSF handles the code itself (MCodeHandler.cs:997-1023). Without the fix the second M505.1 replies "Directory not found".
    /// Only M505.1 is tested: M505 moves the sys folder, which every macro request of RRF uses (Processor.cs:1707), the door and emergency stop triggers
    /// included. DWS serves the folder of the test for a few seconds, so a reload of the panel or DWC fails meanwhile. Deterministic
    /// </summary>
    [Test]
    public async Task M505Point1IsRelativeToTheCurrentFolder()
    {
        const string folder = $"{Lab.MacroDirectory}/web/a";
        await Lab.UploadAsync(Session, $"{folder}/sub/lab-test.txt", "lab printer test: folder of M505.1\n");
        string? web = (await QueryAsync("directories.web")).GetString();
        Assert.That(web, Is.Not.Null.And.Not.Empty, "directories.web is not set");

        string first, second;
        string? result;
        try
        {
            first = await SendCodeAsync($"M505.1 P\"{folder}\"");
            second = await SendCodeAsync("M505.1 P\"sub\"");
            result = (await QueryAsync("directories.web")).GetString();
        }
        finally
        {
            await SendCodeAsync($"M505.1 P\"{web}\"");
        }

        Assert.That((await QueryAsync("directories.web")).GetString(), Is.EqualTo(web), "The web folder was not set back");
        Assert.That(first, Does.Not.Contain("Error"));
        Assert.That(second, Does.Not.Contain("Error"), "M505.1 did not resolve the relative folder against the current one");
        Assert.That(result?.TrimEnd('/'), Is.EqualTo($"{folder}/sub"));
    }

    /// <summary>
    /// M997 of an invalid UF2 file stopped the plugins, failed the update, wrote the start error file and restarted DCS, because upstream 61df011ec unpacked
    /// the file inside the update (UP4). Fixed in DSF #197 (3.7.0+mp.15): M997 unpacks a UF2 file before it stops anything and refuses an invalid one with
    /// "Firmware file &lt;name&gt; is invalid: &lt;reason&gt;" (MCodeHandler.cs:1321-1335). DSF 3.7.0+mp.14 refused it at the same point as "Failed to read firmware
    /// file &lt;name&gt;: &lt;reason&gt;" (#148). Upstream 61df011ec without #197 was in no fork release; such a DCS restarts, the test then recovers (one E-stop)
    /// and fails.
    /// Nothing is flashed: DCS handles M997 S0 itself and does not pass it to RRF. The module check (MCodeHandler.cs:1256-1260), the lookups of the IAP and
    /// the firmware file (:1263-1312), the empty file check (:1316-1320) and the UF2 check (:1322-1335) all return before the plugins are stopped (:1338-1339)
    /// and before UpdateFirmware (:1346), the only step that sends anything to the board. The file is 100 bytes, no multiple of 512, so UnpackUF2Async throws
    /// before it reads a block (Utility/Firmware.cs:43-46). The test runs only on DCS 3.7.0+mp.14 or newer, where this order is checked; a plugin that
    /// intercepts M997 S0 would change it (the CHX350 plugin was not read). Deterministic
    /// </summary>
    [Test]
    public async Task M997RefusesAnInvalidUf2File()
    {
        ApprovalHelpers.RequireForkRelease(Session, 14, "older builds were not checked to refuse an invalid UF2 file before anything is flashed");
        const string firmware = $"{Lab.MacroDirectory}/firmware/labtest.uf2";
        await Lab.UploadAsync(Session, firmware, "lab printer test: this is no UF2 file, its size is no multiple of 512 bytes " + new string('x', 24));
        string pid = await Lab.GetControlServerPidAsync();
        var plugins = Lab.Read(Session, model => model.Plugins.Values.Select(plugin => (plugin.Id, plugin.Pid)).OrderBy(plugin => plugin.Id).ToList());

        string reply;
        try
        {
            reply = await SendCodeAsync($"M997 S0 P\"{firmware}\"", 30);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            reply = $"no reply: {e.Message}";
        }
        if (await Lab.GetControlServerPidAsync() != pid)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
            Assert.Fail($"DCS was restarted after M997 of an invalid UF2 file ({reply})");
        }
        if (reply.Contains("Failed to find IAP file"))
        {
            Assert.Inconclusive($"The IAP file is missing, so M997 stopped before the UF2 check: {reply}");
        }

        Assert.That(reply, Does.Contain("Error").And.Contain("UF2 file size must be a multiple of 512 bytes"));
        Assert.That(Lab.Read(Session, model => model.Plugins.Values.Select(plugin => (plugin.Id, plugin.Pid)).OrderBy(plugin => plugin.Id).ToList()), Is.EqualTo(plugins),
            "M997 stopped or restarted plugins");
    }

    /// <summary>
    /// M997 S0:4 updated module 0 and dropped module 4: DCS updates the main board itself and the update restarts RRF and DCS. Fixed in DSF #148 (fix g,
    /// 3.7.0+mp.14): such a mix is refused before anything is updated (MCodeHandler.cs:1254-1260), as RRF refuses a module it cannot update. The P file does
    /// not exist, so even a DCS without the check stops at "Failed to find firmware file" (:1309) and flashes nothing. Deterministic
    /// </summary>
    [Test]
    public async Task M997RefusesModule0WithOtherModules()
    {
        ApprovalHelpers.RequireForkRelease(Session, 14, "older builds were not checked to refuse the module mix before anything is flashed");
        string reply = await SendCodeAsync($"M997 S0:4 P\"{Lab.MacroDirectory}/firmware/lab-test-missing.bin\"", 30);
        Assert.That(reply, Does.Contain("Module 0 cannot be updated together with other modules in SBC mode, update module 4 separately"));
    }

    /// <summary>
    /// runonce.g was never run in SBC mode on 3.7: RRF leaves it to the SBC (RepRap.cpp:639 of RRF), DSF 3.6 ran it in Program.cs, and upstream c5e3214be
    /// dropped that when it moved the start code. The wiki: "If this file is present at startup, it is run after running config.g and activating the
    /// network, and then deleted" (Tuning/Macros.md:203). Fixed in DSF #224 (not in 3.7.0+mp.15): once the model leaves "starting", DCS runs runonce.g with
    /// M98 on the Trigger channel after config.g and deletes it (PeriodicUpdateService.cs:279-333). It runs when DCS starts, so the test restarts DCS and RRF
    /// with M999 (one E-stop, on 3.7 DCS exits after sending the reset). Without the fix runonce.g neither runs nor is deleted, the test deletes it.
    /// Deterministic
    /// </summary>
    [Test, Category("Restart")]
    public async Task RunOnceRunsAfterConfig()
    {
        await G3Helpers.RequireControlServerWithNewFixesAsync("#224");
        const string runOnce = "0:/sys/runonce.g";
        if ((await Session.GetFileListAsync("0:/sys")).Any(item => item.Filename == "runonce.g"))
        {
            Assert.Ignore($"{runOnce} exists on this printer, the test does not replace it");
        }
        await Lab.UploadAsync(Session, runOnce, "; lab printer test: DSF runs this once after config.g and deletes it\n" + ApprovalHelpers.Global("labTestRunOnce", "true"));
        try
        {
            string pid = await Lab.GetControlServerPidAsync();
            try
            {
                await SendCodeAsync("M999");
            }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
            {
                // DCS exits after sending the reset
            }
            await Lab.WaitForControlServerRestartAsync(pid);
            await Lab.ResetHaltedFirmwareAsync();
            await ReconnectSessionAsync();

            // The reset cleared the globals, so the global exists only if runonce.g ran
            bool ran = false;
            for (int i = 0; i < 60 && !ran; i++)
            {
                ran = (await QueryAsync("global.labTestRunOnce")).ValueKind == JsonValueKind.True;
                if (!ran)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            bool kept = (await Session.GetFileListAsync("0:/sys")).Any(item => item.Filename == "runonce.g");

            Assert.That(ran, Is.True, "runonce.g did not run after the restart");
            Assert.That(kept, Is.False, "runonce.g was not deleted after it had run");
        }
        finally
        {
            await Lab.DeleteAsync(Session, runOnce);
        }
    }
}
