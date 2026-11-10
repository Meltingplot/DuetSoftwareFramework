using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Message boxes opened while code replies are still queued
/// </summary>
/// <remarks>
/// With RRF before 3.7.0+1.mp.5, three M122 followed by M291 S2 opened the box several times, because the request for the box overtook the
/// queued M122 replies (fixed by RRF f2e44bcc).
/// Only the box of this test counts and only that one is closed. The machine opens boxes of its own, e.g. its self-test, which starts on HTTP about
/// a minute after every boot of RRF and shows two of them (sys/meltingplot/self-test/check-quick.g, macros/meltingplot/maintenance/self-test).
/// On 2026-11-10 they made this test count 3 boxes (design/lab-test-plan.md, section 14)
/// </remarks>
[TestFixture]
public class MessageBoxes : LabTest
{
    /// <summary>
    /// Text of the box of this test, by which it is told from the boxes of the machine
    /// </summary>
    private const string BoxText = "Lab printer test, this box closes by itself";

    [Test]
    public async Task OneBoxAfterM122()
    {
        Task<string>[] diagnostics = [.. Enumerable.Range(0, 3).Select(_ => SendCodeAsync("M122", 30))];
        Task<string> messageBox = SendCodeAsync($"M291 P\"{BoxText}\" S2", 60);

        // Close every box of this test that opens, until none has opened for 3 s. A box of the machine may be shown first; it is left alone
        List<int> boxes = [];
        try
        {
            while (boxes.Count < 5 &&
                   await Lab.TryWaitForModelAsync(Session, model => model.State.MessageBox is { Message: BoxText } box && !boxes.Contains(box.Seq), TimeSpan.FromSeconds(boxes.Count == 0 ? 30 : 3)))
            {
                int seq = Lab.Read(Session, model => model.State.MessageBox!.Seq);
                boxes.Add(seq);
                await SendCodeAsync($"M292 P0 S{seq}");
                await Lab.WaitForModelAsync(Session, model => model.State.MessageBox is null || model.State.MessageBox.Seq != seq, TimeSpan.FromSeconds(10), $"message box {seq} to close");
            }
            if (boxes.Count == 0)
            {
                // The M291 would wait for its box for good
                string? shown = Lab.Read(Session, model => model.State.MessageBox?.Message);
                await Lab.RecoverAsync();
                Assert.Fail($"The box of the test did not open within 30 s, the box shown is {(shown is null ? "none" : $"\"{shown}\"")}");
            }
            await Task.WhenAll([.. diagnostics, messageBox]);
        }
        catch (OperationCanceledException)
        {
            await Lab.RecoverAsync();
            Assert.Fail($"The HTTP channel got stuck after {boxes.Count} message box(es)");
        }

        Assert.That(boxes, Has.Count.EqualTo(1), $"M291 S2 opened {boxes.Count} message boxes (sequence numbers {string.Join(", ", boxes)})");
        Assert.That(await messageBox, Does.Not.Contain("Error"));
    }
}
