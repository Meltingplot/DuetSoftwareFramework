using System;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Unbuffered codes from a client while a macro runs on the same channel
/// </summary>
/// <remarks>
/// DSF 3.7.0+mp.7 deadlocked the channel: every macro level waited for the M409, and the M409 waited for the macro (fixed in #51, replaced by upstream 30ea08be)
/// </remarks>
[TestFixture]
public class UnbufferedCodes : LabTest
{
    private const string OuterMacro = $"{Lab.MacroDirectory}/m409-outer.g", InnerMacro = $"{Lab.MacroDirectory}/m409-inner.g";

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        await Lab.UploadAsync(session, OuterMacro, $"G4 S3\nM98 P\"{InnerMacro}\"\n");
        await Lab.UploadAsync(session, InnerMacro, "G4 P10\n");
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, OuterMacro);
            await Lab.DeleteAsync(session, InnerMacro);
        }
    }

    [TestCase("state.status", TestName = "M409AnsweredByDsfDuringMacro")]
    [TestCase("state.messageBox.title", TestName = "M409ForwardedToRrfDuringMacro")]
    public async Task M409DuringMacro(string key)
    {
        // The M409 arrives while the outer macro is in its G4, before it calls the inner macro
        Task<string> macro = SendCodeAsync($"M98 P\"{OuterMacro}\"", 20);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Task<string> query = SendCodeAsync($"M409 K\"{key}\"", 20);

        try
        {
            await Task.WhenAll(macro, query);
        }
        catch (OperationCanceledException)
        {
            string state = $"macro {(macro.IsCompletedSuccessfully ? "finished" : "stuck")}, M409 {(query.IsCompletedSuccessfully ? "finished" : "stuck")}";
            await Lab.RecoverAsync();
            Assert.Fail($"The HTTP channel deadlocked ({state})");
        }

        Assert.That(await macro, Does.Not.Contain("Error"));
        Assert.That(await query, Does.Contain($"\"key\":\"{key}\""));
    }
}
