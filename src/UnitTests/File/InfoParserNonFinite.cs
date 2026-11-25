using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer;
using DuetControlServer.Files.Parser;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.File;

/// <summary>
/// G-code files with a value beyond the float range. Parameter.cs:153 parses "1e39" to +Infinity, and a finite filament value can overflow when
/// FileInfoParser converts it to mm. System.Text.Json refuses non-finite floats, so a GCodeFileInfo holding one cannot be returned by M36
/// (MCodeHandler.cs:496) or GetFileInfo (Connection.cs:721), and once the file is selected (JobProcessor.cs:352) the object model cannot be
/// serialized either (ObjectModel.cs:202). FileInfoParser stores only finite values, like it already did for the object height comment
/// (FileInfoParser.cs:386) and like RRF's footer scan for G0/G1 Z (FileInfoParser.cpp:497-498)
/// </summary>
[TestFixture]
public class InfoParserNonFinite
{
    /// <summary>
    /// Filament usage in m by extruder index. None of the default filters (Settings.cs:361-374) has both an index and the m unit, but the
    /// filters are configurable, and this one reaches the indexed branch of FileInfoParser.FindFilamentUsed for the m unit
    /// </summary>
    private const string IndexedMetresFilter = @"filament used extruder (?<index>\d+) \(m\) = (?<m>\d+\.?\d*)";

    /// <summary>
    /// 1e36 is a finite float, 1e39 (1e36 m in mm) is not
    /// </summary>
    private static readonly string FiniteButTooBigForMm = "1" + new string('0', 36);

    private static async Task<FakeFirmwareHost> StartAsync() => await FakeFirmwareHost.StartAsync(configureSettings: settings => settings.FilamentFilters.Add(IndexedMetresFilter));

    private static string WriteJob(FakeFirmwareHost host, string content)
    {
        string path = Path.Combine(host.BaseDirectory, "gcodes", "job.gcode");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static void AssertFinite(GCodeFileInfo info)
    {
        // M36 serializes it with the object model context, GetFileInfo with the default options
        Assert.DoesNotThrow(() => JsonSerializer.Serialize(info, ObjectModelContext.Default.GCodeFileInfo));
        Assert.DoesNotThrow(() => JsonSerializer.SerializeToUtf8Bytes(info, JsonHelper.DefaultJsonOptions.GetTypeInfo(typeof(GCodeFileInfo))));

        Assert.That(float.IsFinite(info.Height), Is.True, $"Height = {info.Height}");
        Assert.That(float.IsFinite(info.LayerHeight), Is.True, $"LayerHeight = {info.LayerHeight}");
        Assert.That(info.Filament.All(float.IsFinite), Is.True, $"Filament = [{string.Join(", ", info.Filament)}]");
    }

    [TestCase("1e39")]
    [TestCase("-1e39")]
    public async Task ZBeyondFloatRangeInFooter(string z)
    {
        // Without a height comment the footer scan takes the Z of the last absolute G0/G1 (FileInfoParser.cs:221-227). Like RRF, which
        // ignores a non-finite height (FileInfoParser.cpp:497-498), a move with a non-finite Z is skipped and the one before it is used
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, $"G1 X1 Z0.3\nG1 X2 Z0.6\nG1 Z{z}\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Height, Is.EqualTo(0.6F));

        // M36 reports err:1 when the file info cannot be serialized (MCodeHandler.cs:513-517)
        Assert.That(await host.ExecuteAsync("M36 \"job.gcode\""), Does.StartWith("{\"err\":0,"));
    }

    [Test]
    public async Task FilamentInMetresBeyondFloatRange()
    {
        // Cura reports the filament in m (Settings.cs:364). 1e36 m is a finite float, but its conversion to 1e39 mm is not, so the check is
        // made on the converted value that is stored (FileInfoParser.cs:489-490, 504-507)
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, $";Generated with Cura_SteamEngine 5.0.0\n;Filament used: {FiniteButTooBigForMm}m\n;Layer height: 0.2\nG1 X1 Z0.3\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Filament, Is.Empty);
        Assert.That(await host.ExecuteAsync("M36 \"job.gcode\""), Does.StartWith("{\"err\":0,"));
    }

    [Test]
    public async Task FilamentInMetresBeyondFloatRangeNextToFiniteOne()
    {
        // Each capture is checked on its own (FileInfoParser.cs:502-509), so the finite one is kept
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, $";Filament used: {FiniteButTooBigForMm}m, 1.5m\nG1 X1 Z0.3\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Filament, Is.EqualTo(new[] { 1500F }));
    }

    [Test]
    public async Task IndexedFilamentInMetresBeyondFloatRange()
    {
        // The indexed branch for the m unit stores the converted value, too (FileInfoParser.cs:489-490, 492-499)
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, $"; filament used extruder 0 (m) = 1.5\n; filament used extruder 1 (m) = {FiniteButTooBigForMm}\nG1 X1 Z0.3\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Filament, Is.EqualTo(new[] { 1500F }));
    }

    [Test]
    public async Task FilamentVolumeBeyondFloatRange()
    {
        // Old KISSlicer and Pathio report a volume in cm3 (Settings.cs:371), which is converted to a length with the filament diameter.
        // The length is stored only if it is finite (FileInfoParser.cs:515-526)
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, $"; Estimated Build Volume: {FiniteButTooBigForMm} cm3\nG1 X1 Z0.3\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Filament, Is.Empty);
    }

    [Test]
    public async Task FilamentVolume()
    {
        // A finite volume is still converted with the default filament diameter of 1.75 mm (FileInfoParser.cs:38)
        await using FakeFirmwareHost host = await StartAsync();
        string path = WriteJob(host, "; Estimated Build Volume: 2.405 cm3\nG1 X1 Z0.3\n");

        GCodeFileInfo info = await host.Services.GetRequiredService<FileInfoParser>().ParseAsync(path, false);
        AssertFinite(info);
        Assert.That(info.Filament, Has.Count.EqualTo(1));
        Assert.That(info.Filament[0], Is.EqualTo(2405F / (MathF.PI * 0.875F * 0.875F)).Within(0.01F));
    }

    [Test]
    public async Task SelectedFileKeepsObjectModelSerializable()
    {
        // M23 assigns the file info to the object model (JobProcessor.cs:352), which GetObjectModel and the subscriptions serialize
        // (GetObjectModel.cs:52, ModelSubscription.cs:658). M36 without a file serializes the file info of the selected job (MCodeHandler.cs:519-530)
        await using FakeFirmwareHost host = await StartAsync();
        WriteJob(host, $";Filament used: {FiniteButTooBigForMm}m\nG1 X1 Z0.3\nG1 Z1e39\n");
        Assert.That(await host.ExecuteAsync("M23 \"job.gcode\""), Is.EqualTo("\n"));

        using (await host.Model.AccessReadOnlyAsync())
        {
            Assert.That(host.Model.Job.File.FileName, Is.EqualTo("0:/gcodes/job.gcode"));
            AssertFinite(host.Model.Job.File);
            Assert.DoesNotThrow(() => host.Model.ToUtf8Json());
        }
        Assert.That(await host.ExecuteAsync("M36"), Does.StartWith("{\"err\":0,"));
    }
}
