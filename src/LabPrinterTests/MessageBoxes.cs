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
/// queued M122 replies (fixed by RRF f2e44bcc)
/// </remarks>
[TestFixture]
public class MessageBoxes : LabTest
{
    [Test]
    public async Task OneBoxAfterM122()
    {
        Task<string>[] diagnostics = [.. Enumerable.Range(0, 3).Select(_ => SendCodeAsync("M122", 30))];
        Task<string> messageBox = SendCodeAsync("M291 P\"Lab printer test, this box closes by itself\" S2", 60);

        // Close every box that opens, until none has opened for 3 s
        List<int> boxes = [];
        try
        {
            while (boxes.Count < 5 &&
                   await Lab.TryWaitForModelAsync(Session, model => model.State.MessageBox is not null, TimeSpan.FromSeconds(boxes.Count == 0 ? 30 : 3)))
            {
                int seq = Lab.Read(Session, model => model.State.MessageBox!.Seq);
                boxes.Add(seq);
                await SendCodeAsync($"M292 P0 S{seq}");
                await Lab.WaitForModelAsync(Session, model => model.State.MessageBox is null || model.State.MessageBox.Seq != seq, TimeSpan.FromSeconds(10), $"message box {seq} to close");
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
