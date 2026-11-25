using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Sequence numbers of RRF's object model for keys that DSF provides
/// </summary>
[TestFixture]
public class ObjectModelSeqs : LabTest
{
    private const string FillFile = $"{Lab.MacroDirectory}/volume-seq.bin";

    /// <summary>
    /// seqs.volumes of RRF never changed when DSF reported a change of its volumes (RRF #105, review finding RV-1)
    /// </summary>
    /// <remarks>
    /// In SBC mode DSF provides the key volumes. Every HostUpdateInterval (4 s by default, DuetControlServer/Settings.cs:136) it reads the volumes and,
    /// when one has changed, e.g. its free space, sends ObjectModelKeyChanged("volumes") (Model/PeriodicUpdateService.cs:183, :251-254, :685-716).
    /// RRF compared the start of the packet, its StringHeader, with "volumes", so VolumesUpdated never ran. Since #105 the key is read with its
    /// header (RRF SbcInterface.cpp:1457-1470) and seqs.volumes counts up (RepRap.h:170, RepRap.cpp:413). M409 with R1 reads RRF's own value
    /// (MCodeHandler.cs:846-850). The upload of 1 MiB changes the free space for certain. DSF then fetches the key from RRF but skips its content,
    /// because Volumes is an SBC property ([SbcProperty(true)], DuetAPI/ObjectModel/ObjectModel.cs:145; the generated update skips it with
    /// ignoreSbcProperties, DuetAPI.SourceGenerators/ObjectModel/Generator.cs:179-189), so DSF's volumes stay as DSF reads them. Unfixed:
    /// seqs.volumes stays where it was
    /// </remarks>
    [Test]
    public async Task VolumesSeqFollowsDsf()
    {
        G7Helpers.RequireMainBoardFixes(Session, G7Helpers.Rrf105);
        JsonElement first = await G1Helpers.QueryFirmwareAsync(Session, "seqs.volumes");
        if (first.ValueKind != JsonValueKind.Number)
        {
            Assert.Ignore($"RRF has no seqs.volumes ({first}), it is reported only with HAS_MASS_STORAGE (RepRap.cpp:411-414)");
        }
        int seqBefore = first.GetInt32();
        (int count, long? capacity) volumesBefore = Lab.Read(Session, model => (model.Volumes.Count, model.Volumes.FirstOrDefault()?.Capacity));

        int seqAfter = seqBefore;
        try
        {
            await Session.UploadAsync(FillFile, new MemoryStream(new byte[1024 * 1024]));
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (seqAfter == seqBefore && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                seqAfter = (await G1Helpers.QueryFirmwareAsync(Session, "seqs.volumes")).GetInt32();
            }
        }
        finally
        {
            await Lab.DeleteAsync(Session, FillFile);
        }

        (int count, long? capacity) volumesAfter = Lab.Read(Session, model => (model.Volumes.Count, model.Volumes.FirstOrDefault()?.Capacity));
        TestContext.Progress.WriteLine($"seqs.volumes of RRF: {seqBefore} before, {seqAfter} after the upload; volumes of DSF {volumesBefore} before, {volumesAfter} after");
        Assert.Multiple(() =>
        {
            Assert.That(seqAfter, Is.Not.EqualTo(seqBefore), "seqs.volumes of RRF did not change within 30 s after DSF reported a change of its volumes");
            Assert.That(volumesAfter, Is.EqualTo(volumesBefore), "The volumes of DSF changed when DSF fetched the key from RRF");
            Assert.That(volumesAfter.capacity, Is.Positive, "The first volume of DSF has no capacity");
        });
    }
}
