using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Access to the lab printer that the tests run against
/// </summary>
/// <remarks>
/// run-lab-tests.sh sets both environment variables. Without them every test is ignored, so the project builds and runs anywhere
/// </remarks>
public static class Lab
{
    /// <summary>
    /// Base URL of the printer's HTTP API, normally the plink tunnel opened by run-lab-tests.sh
    /// </summary>
    public static readonly Uri? BaseUri = Environment.GetEnvironmentVariable("LAB_PRINTER_URL") is { Length: > 0 } url ? new Uri(url) : null;

    /// <summary>
    /// Shell command that runs a script read from stdin on the SBC as a user with passwordless sudo
    /// </summary>
    public static readonly string? SbcShell = Environment.GetEnvironmentVariable("LAB_PRINTER_SHELL") is { Length: > 0 } shell ? shell : null;

    /// <summary>
    /// Directory on the printer that holds the test macros
    /// </summary>
    public const string MacroDirectory = "0:/macros/lab-tests";

    /// <summary>
    /// Whether a lab printer is configured
    /// </summary>
    public static bool IsConfigured => BaseUri is not null && SbcShell is not null;

    /// <summary>
    /// Skip the current test unless a lab printer is configured
    /// </summary>
    public static void RequirePrinter()
    {
        if (!IsConfigured)
        {
            Assert.Ignore("LAB_PRINTER_URL and LAB_PRINTER_SHELL are not set, run the tests with run-lab-tests.sh");
        }
    }

    /// <summary>
    /// Open a new session. Codes are never retried, because a retry could run a code twice
    /// </summary>
    public static Task<DuetHttpSession> ConnectAsync(CancellationToken cancellationToken = default) =>
        DuetHttpSession.ConnectAsync(BaseUri!, new DuetHttpOptions { MaxRetries = 0 }, cancellationToken);

    /// <summary>
    /// Send a code and wait at most the given time for its reply
    /// </summary>
    /// <exception cref="OperationCanceledException">No reply in time</exception>
    public static async Task<string> SendCodeAsync(DuetHttpSession session, string code, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        return await session.SendCodeAsync(code, cts.Token);
    }

    /// <summary>
    /// Read a value from the object model of a session
    /// </summary>
    public static T Read<T>(DuetHttpSession session, Func<ObjectModel, T> getter)
    {
        lock (session.Model)
        {
            return getter(session.Model);
        }
    }

    /// <summary>
    /// Wait until the object model of a session meets a condition, the model keeps updating while DCS restarts
    /// </summary>
    /// <returns>Whether the condition was met in time</returns>
    public static async Task<bool> TryWaitForModelAsync(DuetHttpSession session, Func<ObjectModel, bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (Read(session, condition))
            {
                return true;
            }

            using CancellationTokenSource tick = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            tick.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await session.WaitForModelUpdateAsync(tick.Token);
            }
            catch (OperationCanceledException)
            {
                // Check again, the model is not updated while DCS is down
            }
        }
        return Read(session, condition);
    }

    /// <summary>
    /// Wait until the object model of a session meets a condition or fail the test
    /// </summary>
    public static async Task WaitForModelAsync(DuetHttpSession session, Func<ObjectModel, bool> condition, TimeSpan timeout, string what)
    {
        if (!await TryWaitForModelAsync(session, condition, timeout))
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:0} s waiting for {what} (status {Read(session, model => model.State.Status)})");
        }
    }

    /// <summary>
    /// Check if the printer is idle without a job and every expansion board is running
    /// </summary>
    public static bool IsIdle(ObjectModel model) =>
        model.State.Status == MachineStatus.Idle && string.IsNullOrEmpty(model.Job.File.FileName) &&
        model.Boards.Count > 0 && model.Boards.OfType<ExpansionBoard>().All(board => board.State == BoardState.Running);

    /// <summary>
    /// Run a shell script on the SBC
    /// </summary>
    /// <returns>Standard output of the script</returns>
    /// <exception cref="InvalidOperationException">Script failed</exception>
    public static async Task<string> RunOnSbcAsync(string script, CancellationToken cancellationToken = default)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo("bash", ["-c", SbcShell!])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        await process.StandardInput.WriteAsync(script.ReplaceLineEndings("\n") + "\n");
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken), error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"SBC script failed with exit code {process.ExitCode}: {await error}");
        }
        return await output;
    }

    /// <summary>
    /// Get a journal cursor to read the DCS log from this point on
    /// </summary>
    /// <remarks>
    /// A cursor does not depend on the clocks of the SBC and the test machine
    /// </remarks>
    public static async Task<string> GetJournalCursorAsync()
    {
        string output = await RunOnSbcAsync("sudo -n journalctl -u duetcontrolserver -n 1 --show-cursor -o cat --no-pager");
        string? cursorLine = output.Split('\n').LastOrDefault(line => line.StartsWith("-- cursor: "));
        return cursorLine?["-- cursor: ".Length..].Trim() ?? throw new InvalidOperationException($"journalctl printed no cursor: {output}");
    }

    /// <summary>
    /// Read the DCS log and the systemd messages about its unit since a cursor
    /// </summary>
    public static Task<string> ReadJournalAsync(string cursor) =>
        RunOnSbcAsync($"sudo -n journalctl -u duetcontrolserver --after-cursor='{cursor}' -o cat --no-pager");

    /// <summary>
    /// Get the process ID of DCS, to tell when systemd has started a new instance
    /// </summary>
    public static async Task<string> GetControlServerPidAsync() =>
        (await RunOnSbcAsync("systemctl show -p MainPID --value duetcontrolserver")).Trim();

    /// <summary>
    /// Wait until systemd runs a new DCS instance
    /// </summary>
    public static Task WaitForControlServerRestartAsync(string oldPid) =>
        RunOnSbcAsync($"""
            for i in $(seq 120); do
              pid=$(systemctl show -p MainPID --value duetcontrolserver)
              [ "$pid" != 0 ] && [ "$pid" != "{oldPid}" ] && exit 0
              sleep 1
            done
            echo "DCS was not restarted" >&2
            exit 1
            """);

    /// <summary>
    /// Upload a test macro
    /// </summary>
    public static Task UploadAsync(DuetHttpSession session, string filename, string content) =>
        session.UploadAsync(filename, new MemoryStream(Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"))));

    /// <summary>
    /// Delete a test file or directory if it exists
    /// </summary>
    public static async Task DeleteAsync(DuetHttpSession session, string filename)
    {
        try
        {
            await session.DeleteAsync(filename);
        }
        catch (FileNotFoundException)
        {
            // Already gone
        }
    }

    /// <summary>
    /// Bring DCS and RRF back to normal operation, e.g. after a test left a channel stuck
    /// </summary>
    /// <remarks>
    /// Restarting DCS halts RRF on purpose, so M999 follows. On 3.7 DCS exits after sending the reset and systemd starts it again
    /// </remarks>
    public static async Task RecoverAsync()
    {
        TestContext.Progress.WriteLine("Recovering: restarting DCS and resetting the firmware");
        await RunOnSbcAsync("sudo -n systemctl restart duetcontrolserver");
        await ResetHaltedFirmwareAsync();
    }

    /// <summary>
    /// Open a new session once DCS accepts one again, e.g. after a restart
    /// </summary>
    /// <remarks>
    /// The model of a new session reads <see cref="MachineStatus.Starting"/> until DCS has sent it, so it never shows a state from before a restart
    /// </remarks>
    public static async Task<DuetHttpSession> ReconnectAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (true)
        {
            try
            {
                return await ConnectAsync(cts.Token);
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }
    }

    /// <summary>
    /// After DCS has been restarted, reset the firmware with M999 if DCS reports it halted and wait until the printer is idle again
    /// </summary>
    public static async Task ResetHaltedFirmwareAsync()
    {
        await using DuetHttpSession session = await ReconnectAsync(TimeSpan.FromMinutes(2));
        await WaitForModelAsync(session, model => model.State.Status is MachineStatus.Halted or MachineStatus.Idle, TimeSpan.FromMinutes(2), "DCS to report the firmware state");
        if (Read(session, model => model.State.Status) == MachineStatus.Halted)
        {
            try
            {
                await SendCodeAsync(session, "M999", TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                // DCS exits after sending the reset
            }
        }
        await WaitForModelAsync(session, IsIdle, TimeSpan.FromMinutes(2), "the printer to become idle");
    }
}
