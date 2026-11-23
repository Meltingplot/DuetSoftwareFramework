using System;
using System.Text.Json;
using System.Threading.Tasks;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Variables that are assigned a value of the object model keep a copy of it
/// </summary>
/// <remarks>
/// Before RRF fork PR #57 (first in the test build 3.7.0+1.mp.11-dev.1), a variable kept a raw pointer into the object model for an array of
/// objects, a string of type CString and a port, which dangled once the object was deleted. On the CHX, sys/daemon.g keeps the filament name of
/// extruder 0 in a variable and compares it later, so it never saw a change. Tools/variable-test in RRF checks the fix offline with every kind of
/// value. These tests check it end to end with values the test can change without touching the machine: the text of a message box of its own,
/// the heaters and the port of a temperature sensor. Each macro reports through global variables, because its output does not reach the reply
/// of the M98 that started it
/// </remarks>
[TestFixture]
public class VariableCopies : LabTest
{
    private const string MacroFolder = $"{Lab.MacroDirectory}/variable-copies";
    private const string ObjectArrayMacro = $"{MacroFolder}/object-array.g";
    private const string MessageBoxMacro = $"{MacroFolder}/message-box.g";
    private const string PortMacro = $"{MacroFolder}/port.g";

    /// <summary>
    /// Texts of the two boxes of the string test, by which they are told from the boxes of the machine
    /// </summary>
    private const string FirstBox = "Lab printer test, first box, closes by itself", SecondBox = "Lab printer test, second box, closes by itself";

    /// <summary>
    /// Create a global variable, or set it if an earlier run has left it, since globals stay until the next reset
    /// </summary>
    private static string Global(string name, string value) => $"""
        if !exists(global.{name})
          global {name} = {value}
        else
          set global.{name} = {value}

        """;

    [OneTimeSetUp]
    public async Task UploadMacrosAsync()
    {
        Lab.RequirePrinter();
        await using DuetHttpSession session = await Lab.ConnectAsync();

        // Variable::Assign rejects an array that holds an object (Variable.cpp MakeStorable), so the global is never created. The number of heaters
        // still works, it is not an object. The line after the assignment must not run once RRF has refused it. Before DSF #204, DSF still ran
        // the codes of a macro that it had read ahead (design/open-findings.md, L7)
        await Lab.UploadAsync(session, ObjectArrayMacro,
            Global("labTestHeaterCount", "#heat.heaters") +
            Global("labTestAfterRefusal", "false") +
            "global labTestHeaterObjects = heat.heaters\n" +
            "set global.labTestAfterRefusal = true\n");

        // state.messageBox.message is a CString (MessageBox.cpp:24). Closing the box deletes the MessageBox that holds it, and the second box
        // may reuse its memory. Non-blocking boxes without a timeout (S1 T0), so no channel waits for them. The global keeps null if another
        // box than the first one of this test is shown
        await Lab.UploadAsync(session, MessageBoxMacro,
            Global("labTestBoxText", "null") +
            Global("labTestBoxTextAfterClose", "null") +
            $"M291 P\"{FirstBox}\" S1 T0\n" +
            $"if state.messageBox != null && state.messageBox.message == \"{FirstBox}\"\n" +
            "  set global.labTestBoxText = state.messageBox.message\n" +
            "  M292 P0 S{state.messageBox.seq}\n" +
            $"  M291 P\"{SecondBox}\" S1 T0\n" +
            "  set global.labTestBoxTextAfterClose = global.labTestBoxText\n" +
            $"  if state.messageBox != null && state.messageBox.message == \"{SecondBox}\"\n" +
            "    M292 P0 S{state.messageBox.seq}\n");
    }

    [OneTimeTearDown]
    public async Task DeleteMacrosAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            foreach (string macro in new[] { ObjectArrayMacro, MessageBoxMacro, PortMacro })
            {
                await Lab.DeleteAsync(session, macro);
            }
            await Lab.DeleteAsync(session, MacroFolder);
        }
    }

    [Test]
    public async Task ObjectArrayIsRefused()
    {
        // Globals stay until the next reset of RRF, so one left by an RRF without the fix would hide the result
        if ((await QueryAsync("global.labTestHeaterObjects")).ValueKind != JsonValueKind.Null)
        {
            Assert.Ignore("global.labTestHeaterObjects exists from an earlier run, the test needs a reset of RRF first");
        }

        string cursor = await Lab.GetJournalCursorAsync();
        string reply = await SendCodeAsync($"M98 P\"{ObjectArrayMacro}\"", 30);
        TestContext.Progress.WriteLine($"M98 replied \"{reply.Trim()}\"");
        await Task.Delay(TimeSpan.FromSeconds(1));
        string journal = await Lab.ReadJournalAsync(cursor);

        JsonElement count = await QueryAsync("global.labTestHeaterCount"), stored = await QueryAsync("global.labTestHeaterObjects");
        JsonElement after = await QueryAsync("global.labTestAfterRefusal");
        Assert.That(count.ValueKind, Is.EqualTo(JsonValueKind.Number), "The macro did not count the heaters");
        Assume.That(count.GetInt32(), Is.GreaterThan(0), "The machine has no heater, so heat.heaters holds no object");
        Assert.Multiple(() =>
        {
            Assert.That(stored.ValueKind, Is.EqualTo(JsonValueKind.Null),
                $"global labTestHeaterObjects = heat.heaters was accepted, so a variable holds pointers to the {count.GetInt32()} heater objects");
            Assert.That(journal, Does.Contain("Cannot assign a value of type 'object' to a variable"), "The journal shows no refusal by RRF");
            Assert.That(after.ValueKind, Is.EqualTo(JsonValueKind.False), "The line after the refused assignment ran (design/open-findings.md, L7)");
        });
    }

    [Test]
    public async Task StringKeepsItsValueAfterItsObjectIsDeleted()
    {
        // A box of the machine, e.g. of its self-test after a boot, would be shown instead of the boxes of the test
        if (!await Lab.TryWaitForModelAsync(Session, model => model.State.MessageBox is null, TimeSpan.FromSeconds(30)))
        {
            Assert.Ignore($"The machine shows the box \"{Lab.Read(Session, model => model.State.MessageBox?.Message)}\", so the test opens none");
        }

        await SendCodeAsync($"M98 P\"{MessageBoxMacro}\"", 30);
        JsonElement text = await QueryAsync("global.labTestBoxText"), afterClose = await QueryAsync("global.labTestBoxTextAfterClose");
        if (text.ValueKind == JsonValueKind.Null)
        {
            Assert.Ignore("Another box was shown when the macro read the first box of the test");
        }

        Assert.Multiple(() =>
        {
            Assert.That(text.GetString(), Is.EqualTo(FirstBox), "The global does not hold the text of the box it was given");
            Assert.That(afterClose.ValueKind == JsonValueKind.String ? afterClose.GetString() : afterClose.ToString(), Is.EqualTo(FirstBox),
                "After the box was closed and another one opened, the global no longer held the text it was given");
        });
        Assert.That(Lab.Read(Session, model => model.State.MessageBox?.Message), Is.Not.EqualTo(FirstBox).And.Not.EqualTo(SecondBox),
            "A box of the test is still open");
    }

    [Test]
    public async Task PortIsStoredAsItsPinName()
    {
        // The first temperature sensor with a port (SensorWithPort.cpp:21), e.g. a thermistor on the mainboard. RRF answers M409 for keys with [
        int sensors = Lab.Read(Session, model => model.Sensors.Analog.Count);
        int index = -1;
        string? pin = null;
        for (int i = 0; i < sensors && pin is null; i++)
        {
            using JsonDocument reply = JsonDocument.Parse(await SendCodeAsync($"M409 K\"sensors.analog[{i}].port\""));
            if (reply.RootElement.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.String)
            {
                index = i;
                pin = result.GetString();
            }
        }
        Assume.That(pin, Is.Not.Null.And.Not.Empty, "No temperature sensor of this machine has a port");

        // The length of a port fails unless the global holds the pin name as a string (#57: "A stored port is a string, so #var.p and indexing
        // work on it")
        await Lab.UploadAsync(Session, PortMacro,
            Global("labTestPort", "null") +
            Global("labTestPortLength", "-1") +
            $"set global.labTestPort = sensors.analog[{index}].port\n" +
            "set global.labTestPortLength = #global.labTestPort\n");
        await SendCodeAsync($"M98 P\"{PortMacro}\"", 30);

        JsonElement stored = await QueryAsync("global.labTestPort"), length = await QueryAsync("global.labTestPortLength");
        Assert.Multiple(() =>
        {
            Assert.That(stored.ValueKind == JsonValueKind.String ? stored.GetString() : stored.ToString(), Is.EqualTo(pin),
                $"The global does not hold the pin name of sensor {index}");
            Assert.That(length.ValueKind == JsonValueKind.Number ? length.GetInt32() : -1, Is.EqualTo(pin!.Length),
                "The length of the stored port is not that of its pin name, so it was not stored as a string");
        });
    }
}
