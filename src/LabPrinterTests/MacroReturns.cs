using System;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// M99 in nested macros
/// </summary>
/// <remarks>
/// RRF wrote the empty reply of an M99 after its abort request, so with DSF 3.7.0+mp.5 the late reply completed the caller's next M98, whose macro
/// then ran in the wrong context ("unknown variable" in set-nozzle-diameter, fixed by DSF upstream da8e9bb8 with RRF 4d80f4663 in DSF 3.7.0+mp.6).
/// The macros follow the reproducer from 2026-10-23, which hit the race in about 40 % of the iterations
/// </remarks>
[TestFixture]
public class MacroReturns : LabTest
{
    private const string Directory = $"{Lab.MacroDirectory}/m99";
    private const int Iterations = 20;

    private static readonly (string Name, string Content)[] Macros =
    [
        ("a.g", $"""
            if !exists(global.labTestResult)
              global labTestResult = 0
            if !exists(global.labTestOutcome)
              global labTestOutcome = ""
            set global.labTestOutcome = "running"
            var guided = true
            while iterations < {Iterations}
              M98 P"{Directory}/b.g"
              M98 P"{Directory}/d.g"
              if !exists(var.guided)
                set global.labTestOutcome = "race in iteration " ^ iterations
                M99
            set global.labTestOutcome = "done"
            """),
        // Shaped like confirm-nozzle-diameter.g
        ("b.g", $"""
            var tool = 0
            M98 P"{Directory}/e.g"
            var key = global.labTestResult
            if true
              if true
                M98 P"{Directory}/c.g"
                if global.labTestResult == 0
                  echo "never"
              M99
            M98 P"{Directory}/e.g"
            set var.key = global.labTestResult
            echo "never"
            """),
        // Shaped like create-nozzle-file.g, the M291 is never reached
        ("c.g", $"""
            var tool = 0
            M98 P"{Directory}/e.g"
            if global.labTestResult != ""
              set global.labTestResult = 1
              M99
            M98 P"{Directory}/e.g"
            var key = global.labTestResult
            var file = "x"
            var flow = 20
            M291 P"never" S3
            """),
        ("d.g", "G4 S1"),
        ("e.g", "set global.labTestResult = \"path\"")
    ];

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();
        foreach ((string name, string content) in Macros)
        {
            await Lab.UploadAsync(session, $"{Directory}/{name}", content + "\n");
        }
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            foreach ((string name, _) in Macros)
            {
                await Lab.DeleteAsync(session, $"{Directory}/{name}");
            }
            await Lab.DeleteAsync(session, Directory);
        }
    }

    [Test]
    public async Task NestedM99KeepsCallerVariables()
    {
        string reply = string.Empty;
        try
        {
            reply = await SendCodeAsync($"M98 P\"{Directory}/a.g\"", 30 + 5 * Iterations);
        }
        catch (OperationCanceledException)
        {
            await Lab.RecoverAsync();
            Assert.Fail("The macro did not finish");
        }

        Assert.That(reply, Does.Not.Contain("Error"));
        Assert.That((await QueryAsync("global.labTestOutcome")).GetString(), Is.EqualTo("done"));
    }
}
