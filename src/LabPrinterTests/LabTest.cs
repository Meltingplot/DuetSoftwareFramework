using System;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Checks once per run that the printer may be used and logs the versions under test
/// </summary>
[SetUpFixture]
public class LabSetUp
{
    [OneTimeSetUp]
    public async Task CheckPrinterAsync()
    {
        if (!Lab.IsConfigured)
        {
            // Every test reports this itself
            return;
        }

        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.TryWaitForModelAsync(session, model => model.State.Status != MachineStatus.Starting, TimeSpan.FromSeconds(30));
        (MachineStatus status, string? jobFile, string? dsfVersion, string? firmwareVersion) = Lab.Read(session, model =>
            (model.State.Status, model.Job.File.FileName, model.SBC?.DSF.Version, model.Boards.Count > 0 ? model.Boards[0].FirmwareVersion : null));
        TestContext.Progress.WriteLine($"Lab printer at {Lab.BaseUri}: DSF {dsfVersion}, RRF {firmwareVersion}, status {status}");

        if (!string.IsNullOrEmpty(jobFile))
        {
            Assert.Ignore($"The printer is printing {jobFile}, so no test touches it");
        }
    }

    [OneTimeTearDown]
    public async Task DeleteMacroDirectoryAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, Lab.MacroDirectory);
        }
    }
}

/// <summary>
/// Base class of the lab printer tests. Every test gets its own session and runs only while the printer is idle
/// </summary>
public abstract class LabTest
{
    private DuetHttpSession? _session;

    /// <summary>
    /// Session of the current test
    /// </summary>
    protected DuetHttpSession Session => _session!;

    [SetUp]
    public async Task ConnectAsync()
    {
        Lab.RequirePrinter();
        _session = await Lab.ConnectAsync();
        if (!await Lab.TryWaitForModelAsync(Session, Lab.IsIdle, TimeSpan.FromSeconds(30)))
        {
            Assert.Ignore($"The printer is not idle (status {Lab.Read(Session, model => model.State.Status)}), so the test does not touch it");
        }
    }

    [TearDown]
    public async Task DisconnectAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    /// <summary>
    /// Send a code on the session of the current test
    /// </summary>
    protected Task<string> SendCodeAsync(string code, int timeoutSeconds = 10) => Lab.SendCodeAsync(Session, code, TimeSpan.FromSeconds(timeoutSeconds));

    /// <summary>
    /// Query an object model value with M409, e.g. a global variable set by a test macro
    /// </summary>
    /// <remarks>
    /// Echo output of a macro does not reach the reply of the M98 that started it, so test macros report through global variables.
    /// DSF answers M409 only once pending codes have finished and its object model is up-to-date
    /// </remarks>
    protected async Task<JsonElement> QueryAsync(string key)
    {
        using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync($"M409 K\"{key}\""));
        return reply.RootElement.GetProperty("result").Clone();
    }
}
