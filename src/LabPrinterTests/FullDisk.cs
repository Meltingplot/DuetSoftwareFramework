using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Files that DSF writes on a full disk. The disk is full only for the file under test: config-override.g is replaced for one M500 by a symlink to
/// /dev/full, on which every write fails with ENOSPC (null(4)), and the event log is pointed at a tmpfs of four pages that the test fills
/// </summary>
/// <remarks>
/// config-override.g holds the Z height of the CHX (set-new-height.g:22, M500 P10:31) and is never deleted or written by the test: it is copied to
/// <see cref="ApprovalHelpers.SbcDirectory"/> and checked by SHA-256 first, then renamed next to the symlink in the same directory, and renamed back by a
/// trap of the same SBC script, even if the script is stopped. Afterwards the test checks the file against the checksum and copies the backup back if it
/// differs. DCS runs as user dsf without AppArmor confinement in the DSF packages (pkg/common/duetcontrolserver/.../duetcontrolserver.service; the AppArmor
/// profiles of pkg/common/duetpluginservice/opt/dsf/conf/apparmor.conf are for plugins); an image may differ, so a refused open of /dev/full or of the
/// tmpfs is inconclusive and the test prints the permissions and the AppArmor label of DCS
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class FullDisk : LabTest
{
    private const string Backup = $"{ApprovalHelpers.SbcDirectory}/config-override.g";
    private const string LogDirectory = $"{Lab.MacroDirectory}/full-disk";
    private const string LogFile = $"{LogDirectory}/event.log";
    private const string Tmpfs = $"{ApprovalHelpers.SbcDirectory}/full";

    [SetUp]
    public Task RequirePython3Async() => ApprovalHelpers.RequirePython3Async();

    /// <summary>
    /// RRF writes config-override.g on the SBC with OpenFile, WriteFile and CloseFile requests. DSF opened it with a FileStream buffer, so every WriteFile was
    /// answered as written although nothing reached the disk; the error came at the close, which has no answer in the protocol. So M500 reported success on a
    /// full disk and left an empty file (FileMode.Create had truncated it). Fixed in DSF #226: a file RRF writes is opened without a buffer
    /// (LinkService.cs:1490-1494), so the failed write is answered as failed, and RRF replies "Failed to write file config-override.g" and deletes the file
    /// (GCodes.cpp:5418-5428 of RRF), which removes the symlink here. Without the fix M500 replies without an error. A refused open gives
    /// "Failed to create file config-override.g" (GCodes.cpp:5336-5340, LinkService.cs:1513-1517), which is inconclusive. Deterministic
    /// </summary>
    [Test]
    public async Task M500ReportsAFullDisk()
    {
        string? systemDirectory = (await QueryAsync("directories.system")).GetString()?.TrimEnd('/');
        if (systemDirectory != "0:/sys")
        {
            Assert.Ignore($"directories.system is {systemDirectory}, the test only handles 0:/sys");
        }
        string target = $"{await ApprovalHelpers.GetSdDirectoryAsync()}/sys/config-override.g";

        // Back it up and record its checksum before anything is changed
        string state = (await Lab.RunOnSbcAsync($"""
            set -e
            target='{target}'
            if [ -L "$target" ]; then echo symlink; exit 0; fi
            if [ -e "$target.lab-original" ]; then echo leftover; exit 0; fi
            sudo -n install -d -m 0700 '{ApprovalHelpers.SbcDirectory}'
            if [ -e "$target" ]; then
              sudo -n cp -p "$target" '{Backup}'
              sum=$(sudo -n sha256sum "$target" | cut -d' ' -f1)
              [ "$(sudo -n sha256sum '{Backup}' | cut -d' ' -f1)" = "$sum" ]
              echo "file $sum"
            else
              echo none
            fi
            """)).Trim();
        if (state == "symlink")
        {
            Assert.Ignore($"{target} is a symlink, the test does not touch it");
        }
        Assert.That(state, Is.Not.EqualTo("leftover"), $"{target}.lab-original is left from an earlier run: put it back by hand first");
        TestContext.Progress.WriteLine($"config-override.g before the test: {state}");

        string? result = null, failure = null;
        try
        {
            string output = await Lab.RunOnSbcAsync($$"""
                set -e
                target='{{target}}'
                restore() {
                  sudo -n rm -f "$target.lab-link"
                  if [ -e "$target.lab-original" ]; then
                    sudo -n mv -fT "$target.lab-original" "$target"
                  elif [ -L "$target" ]; then
                    sudo -n rm -f "$target"
                  fi
                }
                trap restore EXIT
                trap 'exit 1' HUP INT TERM
                if [ -e "$target" ]; then sudo -n mv -T "$target" "$target.lab-original"; fi
                sudo -n ln -sfn /dev/full "$target.lab-link"
                sudo -n mv -T "$target.lab-link" "$target"
                sudo -n timeout 60 python3 - M500 SBC <<'EOF'
                {{ApprovalHelpers.IpcCodeScript}}
                EOF
                """);
            using JsonDocument reply = JsonDocument.Parse(output.Trim().Split('\n').Last());
            result = reply.RootElement.TryGetProperty("result", out JsonElement text) ? text.GetString() ?? string.Empty : reply.RootElement.ToString();
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            failure = e.Message;
        }
        finally
        {
            string restored = await RestoreConfigOverrideAsync(target, state);
            Assert.That(restored, Is.EqualTo(state), $"config-override.g was not restored, the backup is at {Backup} on the SBC");
        }

        Assert.That(failure, Is.Null, "The SBC script failed");
        TestContext.Progress.WriteLine($"M500: {result}");
        if (result!.Contains("Failed to create file"))
        {
            Assert.Inconclusive($"DCS could not open /dev/full for writing: {await DescribeAccessAsync("/dev/full")}");
        }
        Assert.That(result, Does.Contain("Failed to write file config-override.g"), "M500 did not report that nothing could be written");
    }

    /// <summary>
    /// With event logging on and a full disk, the next logged message stopped DCS: EventLogger.LogAsync caught the failed write and stopped logging, but the
    /// stop wrote "Event logging stopped" through the same writer, which failed again and left the catch; the writer stayed set, so every later message threw
    /// as well. On the code pipeline the exception became the result of the code (Executed.cs:110-120), on the link task it stopped DCS. Fixed in DSF #227:
    /// a failed write stops logging without writing to the file again (Logger.cs:272-276, :306-310, StopInternalAsync(true) :201-245), state.logFile is
    /// null and state.logLevel off, as in RRF. The trigger here is a DSF error reply, M472 of a missing file (MCodeHandler.cs:951), which Executed.cs:119
    /// logs at warn level. Without the fix the reply of M472 is the IOException and state.logFile stays set. Deterministic once the start line has filled
    /// the page of the log file exactly; else inconclusive.
    /// The log is set to warn level for the test, so few other messages are written meanwhile. An RRF warning logged while the disk is full stops an unfixed
    /// DCS (LinkService.HandleMessage on the link task), the test then recovers it (DCS restart and M999)
    /// </summary>
    [Test]
    public async Task M929StopsLoggingOnAFullDisk()
    {
        // needs a DCS with #227
        string? level = (await QueryAsync("state.logLevel")).GetString();
        JsonElement oldFile = await QueryAsync("state.logFile");
        string sdDirectory = await ApprovalHelpers.GetSdDirectoryAsync();
        await Lab.UploadAsync(Session, $"{LogDirectory}/keep.txt", "lab printer test: folder of the event log link\n");

        string pid = await Lab.GetControlServerPidAsync(), cursor = await Lab.GetJournalCursorAsync();
        string? reply = null, inconclusive = null, logFileAfter = null, levelAfter = null, pidAfter = null;
        try
        {
            // tmpfs counts whole pages. The log file gets the page size minus the 42 bytes of "yyyy-MM-dd HH:mm:ss Event logging started\n" (Logger.cs:105),
            // which M929 then writes, so that the next byte needs a new page
            string setup = await Lab.RunOnSbcAsync($$"""
                set -e
                page=$(getconf PAGESIZE)
                sudo -n install -d -m 0755 '{{ApprovalHelpers.SbcDirectory}}' '{{Tmpfs}}'
                if mountpoint -q '{{Tmpfs}}'; then echo leftover; exit 0; fi
                sudo -n mount -t tmpfs -o size=$((4 * page)),mode=0777 lab-full-disk '{{Tmpfs}}'
                { head -c $((page - 43)) /dev/zero | tr '\0' '#'; echo; } | sudo -n tee '{{Tmpfs}}/event.log' >/dev/null
                sudo -n chmod 0666 '{{Tmpfs}}/event.log'
                sudo -n ln -sfn '{{Tmpfs}}/event.log' '{{sdDirectory}}{{LogFile["0:".Length..]}}'
                echo "page $page"
                """);
            Assert.That(setup, Does.Not.StartWith("leftover"), $"{Tmpfs} was still mounted from an earlier run, it is unmounted now");
            int page = int.Parse(setup.Trim()["page ".Length..]);

            string start = await SendCodeAsync($"M929 P\"{LogFile}\" S1");
            if (start.Contains("Error"))
            {
                inconclusive = $"M929 could not start logging to the tmpfs: {start.Trim()}; {await DescribeAccessAsync($"{Tmpfs}/event.log")}";
            }
            else
            {
                string fill = await Lab.RunOnSbcAsync($"""
                    size=$(stat -c %s '{Tmpfs}/event.log')
                    sudo -n dd if=/dev/zero of='{Tmpfs}/filler' bs={page} 2>/dev/null || true
                    echo "$size $(stat -f -c %a '{Tmpfs}') $(stat -c %s '{Tmpfs}/event.log')"
                    """);
                int[] numbers = [.. fill.Trim().Split(' ').Select(int.Parse)];
                if (numbers[0] != page || numbers[1] != 0 || numbers[2] != page)
                {
                    inconclusive = $"The log file did not end at a page boundary on a full disk (size {numbers[0]} after M929, {numbers[1]} free blocks, size {numbers[2]} after the fill, page {page})";
                }
                else
                {
                    reply = await SendCodeAsync($"M472 P\"{LogDirectory}/missing.g\"");
                    logFileAfter = (await QueryAsync("state.logFile")).ToString();
                    levelAfter = (await QueryAsync("state.logLevel")).GetString();
                    pidAfter = await Lab.GetControlServerPidAsync();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            // DCS stopped
            reply = $"no reply: {e.Message}";
        }
        finally
        {
            await RestoreLoggingAsync(level, oldFile, pid, sdDirectory);
        }

        if (inconclusive is not null)
        {
            Assert.Inconclusive(inconclusive);
        }
        string journal = await Lab.ReadJournalAsync(cursor);
        Assert.That(pidAfter, Is.EqualTo(pid), "DCS stopped on the full disk");
        Assert.That(reply, Does.Contain($"Failed to delete file or directory {LogDirectory}/missing.g: file or directory not found"), "The failed log write became the reply of the code");
        Assert.That((logFileAfter, levelAfter), Is.EqualTo((string.Empty, "off")), "Logging was not stopped after the failed write (state.logFile, state.logLevel)");
        Assert.That(journal, Does.Contain("Failed to write to log file"));
    }

    /// <summary>
    /// Free the tmpfs, set the event log back to its file and level, verify it, then unmount the tmpfs. A DCS that cannot start the log again is restarted
    /// (one E-stop): config.g starts the log again when RRF restarts (chx350-config sys/config.g:190)
    /// </summary>
    private async Task RestoreLoggingAsync(string? level, JsonElement oldFile, string pid, string sdDirectory)
    {
        await Lab.RunOnSbcAsync($"sudo -n rm -f '{Tmpfs}/filler'");
        bool restored;
        try
        {
            if (await Lab.GetControlServerPidAsync() != pid)
            {
                throw new InvalidOperationException("DCS was restarted");
            }
            int? s = level switch { "warn" => 1, "info" => 2, "debug" => 3, _ => null };
            await SendCodeAsync(s is not null && oldFile.ValueKind == JsonValueKind.String ? $"M929 P\"{oldFile.GetString()}\" S{s}" : "M929 S0");
            restored = (await QueryAsync("state.logLevel")).GetString() == level && (await QueryAsync("state.logFile")).ToString() == oldFile.ToString();
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            TestContext.Progress.WriteLine($"Could not set the event log back: {e.Message}");
            restored = false;
        }
        if (!restored)
        {
            await Lab.RecoverAsync();
            await ReconnectSessionAsync();
        }

        await Lab.RunOnSbcAsync($"""
            sudo -n rm -f '{sdDirectory}{LogFile["0:".Length..]}'
            sudo -n umount '{Tmpfs}' 2>/dev/null || sudo -n umount -l '{Tmpfs}' || true
            sudo -n rmdir '{Tmpfs}' 2>/dev/null || true
            """);
        await Lab.DeleteAsync(Session, $"{LogDirectory}/keep.txt");
        await Lab.DeleteAsync(Session, LogDirectory);
    }

    /// <summary>
    /// Check config-override.g against the state before the test and copy the backup back if it differs
    /// </summary>
    /// <returns>State after the test, in the format of the state before it</returns>
    private static async Task<string> RestoreConfigOverrideAsync(string target, string expected)
    {
        string Check() => $"""
            if [ -L '{target}' ]; then echo symlink
            elif [ -f '{target}' ]; then echo "file $(sudo -n sha256sum '{target}' | cut -d' ' -f1)"
            elif [ -e '{target}' ]; then echo other
            else echo none
            fi
            """;
        string state = (await Lab.RunOnSbcAsync(Check())).Trim();
        if (state != expected && expected.StartsWith("file "))
        {
            TestContext.Progress.WriteLine($"config-override.g is {state} after the test, copying the backup back");
            state = (await Lab.RunOnSbcAsync($"""
                set -e
                [ ! -L '{target}' ] || sudo -n rm -f '{target}'
                sudo -n cp -p '{Backup}' '{target}'
                {Check()}
                """)).Trim();
        }
        else if (state != expected && state == "symlink")
        {
            state = (await Lab.RunOnSbcAsync($"sudo -n rm -f '{target}'\n{Check()}")).Trim();
        }

        if (state == expected)
        {
            await Lab.RunOnSbcAsync($"sudo -n rm -f '{Backup}'; sudo -n rmdir '{ApprovalHelpers.SbcDirectory}' 2>/dev/null || true");
        }
        return state;
    }

    /// <summary>
    /// Permissions of a file and the AppArmor label of DCS, for an inconclusive result
    /// </summary>
    private static async Task<string> DescribeAccessAsync(string file) =>
        (await Lab.RunOnSbcAsync($"""
            ls -lL '{file}' 2>&1 | tr '\n' ' '
            echo "AppArmor label of DCS: $(cat /proc/$(systemctl show -p MainPID --value duetcontrolserver)/attr/current 2>&1)"
            """)).Trim();
}
