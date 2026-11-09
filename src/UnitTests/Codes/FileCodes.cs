using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the M-codes that DSF handles itself because it owns the virtual SD card: M20-M22, M28-M30, M36, M38, M39, M470-M472, M503 and M505.
/// Expected results come from MCodeHandler, the Duet3D G-code dictionary and RRF's handlers in GCodes2.cpp for standalone mode
/// </summary>
[TestFixture]
public class FileCodes
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    /// <summary>
    /// Get the physical path of a file on the virtual SD card
    /// </summary>
    private string PhysicalPath(params string[] segments) => Path.Combine([_host.BaseDirectory, .. segments]);

    /// <summary>
    /// Write a file to the virtual SD card
    /// </summary>
    private string WriteFile(string content, params string[] segments)
    {
        string path = PhysicalPath(segments);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Codes that reached the firmware, including those it discarded
    /// </summary>
    private string[] Received() => [.. _host.Firmware.GetReceivedCodes().Select(code => code.Text)];

    /// <summary>
    /// Make a channel emulate Marlin. The fake firmware does not report inputs, so the value set here is kept
    /// </summary>
    private async Task EmulateMarlinAsync(CodeChannel channel)
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Inputs[channel]!.Compatibility = Compatibility.Marlin;
        }

        // IsEmulatingMarlin is updated after each object model update
        await _host.Model.WaitForFullUpdateAsync();
        Assert.That(_host.Model.IsEmulatingMarlin(channel), Is.True);
    }

    [Test]
    public async Task ListFiles()
    {
        WriteFile("G1 X1\n", "gcodes", "a.g");

        // Same format as the M20 case in GCodes::HandleMcode for RepRapFirmware compatibility and the wiki
        Assert.That(await _host.ExecuteAsync("M20"), Is.EqualTo("GCode files:\n\"a.g\"\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ListFilesAsJson()
    {
        WriteFile("G1 X1\n", "gcodes", "a.g");
        System.IO.Directory.CreateDirectory(PhysicalPath("gcodes", "sub"));

        // The wiki: S2 returns "files" with a '*' in front of directories, and "err"
        using JsonDocument reply = JsonDocument.Parse(await _host.ExecuteAsync("M20 S2"));
        Assert.That(reply.RootElement.GetProperty("err").GetInt32(), Is.Zero);
        Assert.That(reply.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()), Is.EquivalentTo(new[] { "a.g", "*sub" }));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ListFilesAsVerboseJson()
    {
        WriteFile("G1 X1\n", "gcodes", "a.g");

        // The wiki: S3 is like S2 but each file has "type", "name", "size" and "date"
        using JsonDocument reply = JsonDocument.Parse(await _host.ExecuteAsync("M20 S3"));
        JsonElement file = reply.RootElement.GetProperty("files").EnumerateArray().Single();
        Assert.That(file.GetProperty("type").GetString(), Is.EqualTo("f"));
        Assert.That(file.GetProperty("name").GetString(), Is.EqualTo("a.g"));
        Assert.That(file.GetProperty("size").GetInt64(), Is.EqualTo(6));
        Assert.That(file.TryGetProperty("date", out _), Is.True);
        Assert.That(Received(), Is.Empty);
    }

    [TestCase(true, "Begin file list\na.g\nEnd file list\nok\n")]
    [TestCase(false, "Begin file list\nNONE\nEnd file list\nok\n")]
    public async Task ListFilesInMarlinMode(bool withFile, string expected)
    {
        if (withFile)
        {
            WriteFile("G1 X1\n", "gcodes", "a.g");
        }
        await EmulateMarlinAsync(CodeChannel.HTTP);

        // The wiki (Gcodes.md:1666-1674) shows "Begin file list" without a colon. GCodes2.cpp:1061-1074 lists one name per line or NONE, and
        // GCodes.cpp:4596-4598 (HandleReply) wraps it in "Begin file list\n" and "End file list\nok\n". MCodeHandler used to write "Begin file list:"
        Assert.That(await _host.ExecuteAsync("M20"), Is.EqualTo(expected));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ListFilesWithTypeOutOfRangeIsAnError()
    {
        // Input of the weekly fuzz run (GCodeExecutionFuzz seed 1754056457). DSF parses 31e9 as a float that does not fit the int that
        // M20 reads for S (MCodeHandler.cs:142). It is refused like a parameter of the wrong type (Code.cs:276-281); before, the
        // OverflowException of Convert.ToInt32 reached the client. RRF reads integers with StrToI32 and would stop at the e (StringParser.cpp:2049-2051)
        Assert.That(await _host.ExecuteAsync("M20 S31e9 P\"0:/macs\""), Does.StartWith("Error: M20: Cannot convert S parameter to Int32 (value 31e9)"));
    }

    [Test]
    public async Task MountDefaultVolume()
    {
        // The internal volume is always mounted, so M21 (P0) succeeds without the firmware
        Assert.That(await _host.ExecuteAsync("M21"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M21 P1")]
    [TestCase("M22")]
    public void MountCodesNeedManagementPlugin(string code)
    {
        // The wiki: in SBC mode M21 with a device and M22 require the DuetPiManagementPlugin, which intercepts them before DCS.
        // Without it DCS throws NotSupportedException, which the IPC server reports to the client
        Assert.That(async () => await _host.ExecuteAsync(code), Throws.TypeOf<NotSupportedException>());
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task WriteFile()
    {
        // Codes between M28 and M29 are written to the file instead of being executed
        Assert.That(await _host.ExecuteAsync("M28 \"written.g\""), Is.EqualTo("Writing to file: written.g\n"));
        Assert.That(await _host.ExecuteAsync("G1 X5"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M104 S200"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M29"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.Empty);

        // RRF writes the file to the G-code directory (GCodes2.cpp: OpenFileToWrite(Platform::GetGCodeDir(), ...)), without a BOM (upstream 9001d770)
        byte[] content = System.IO.File.ReadAllBytes(PhysicalPath("gcodes", "written.g"));
        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("G1 X5\nM104 S200\n"));

        // Codes after M29 are executed again
        Assert.That(await _host.ExecuteAsync("G1 X6"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "G1 X6" }));
    }

    [Test]
    public async Task WriteFileWithoutName()
    {
        Assert.That(await _host.ExecuteAsync("M28"), Is.EqualTo("Error: M28: Filename expected\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task WriteFileThatCannotBeOpened()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("gcodes", "folder.g"));

        // Same text as the M28 case in GCodes2.cpp. The codes after it are executed as usual
        Assert.That(await _host.ExecuteAsync("M28 \"folder.g\""), Is.EqualTo("Error: M28: Can't open file folder.g for writing.\n"));
        Assert.That(await _host.ExecuteAsync("G1 X5"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "G1 X5" }));
    }

    [Test]
    public async Task WriteFileInMarlinMode()
    {
        await EmulateMarlinAsync(CodeChannel.HTTP);
        await _host.ExecuteAsync("M28 \"written.g\"");
        await _host.ExecuteAsync("G1 X5");

        // StringParser::WriteToFile replies "Done saving file." to M29 in Marlin mode and GCodes::HandleReplyPreserveResult adds "\nok\n"
        Assert.That(await _host.ExecuteAsync("M29"), Is.EqualTo("Done saving file.\nok\n"));
        Assert.That(System.IO.File.ReadAllText(PhysicalPath("gcodes", "written.g")), Is.EqualTo("G1 X5\n"));
    }

    [Test]
    public async Task EndWriteFileWithoutFile()
    {
        // Without a file being written DCS passes M29 on to RRF
        Assert.That(await _host.ExecuteAsync("M29"), Is.EqualTo("\n"));
        Assert.That(Received(), Is.EqualTo(new[] { "M29" }));
    }

    [Test]
    public async Task DeleteFile()
    {
        string file = WriteFile("G1 X1\n", "gcodes", "a.g");

        Assert.That(await _host.ExecuteAsync("M30 \"0:/gcodes/a.g\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(file), Is.False);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteFileWithRelativePath()
    {
        string file = WriteFile("G1 X1\n", "gcodes", "a.g");
        string fileInRoot = WriteFile("G1 X1\n", "a.g");

        // GCodes2.cpp:1393: M30 calls platform.Delete(Platform::GetGCodeDir(), filename). GetGCodeDir is "0:/gcodes/" (Configuration.h:299),
        // which RRF reports as directories.gCodes (RepRap.cpp:286). MCodeHandler used to resolve the name against 0:/ and so deleted 0:/a.g
        Assert.That(await _host.ExecuteAsync("M30 \"a.g\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(file), Is.False);
        Assert.That(System.IO.File.Exists(fileInRoot), Is.True);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteFileWithPathFromRoot()
    {
        string file = WriteFile("G1 X1\n", "sys", "b.g");

        // MassStorage.cpp:208 (CombineName) ignores the G-code directory if the name starts with '/' or a drive number
        Assert.That(await _host.ExecuteAsync("M30 \"/sys/b.g\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(file), Is.False);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteMissingFile()
    {
        // MassStorage.cpp:725-737 (InternalDelete) returns false for a missing file, and GCodes2.cpp:1397-1399 then replies "delete failed" as an error.
        // MCodeHandler used to report success because File.Delete does not fail for a missing file
        Message? result = await _host.ExecuteCodeAsync("M30 \"missing.g\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain("missing.g"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task FileInfo()
    {
        WriteFile("G1 X1\n", "gcodes", "a.g");

        // The wiki: "err" is zero if successful and "size" is always present then. RRF resolves the name against the G-code directory
        using JsonDocument reply = JsonDocument.Parse(await _host.ExecuteAsync("M36 \"a.g\""));
        Assert.That(reply.RootElement.GetProperty("err").GetInt32(), Is.Zero);
        Assert.That(reply.RootElement.GetProperty("size").GetInt64(), Is.EqualTo(6));
        Assert.That(reply.RootElement.GetProperty("thumbnails").GetArrayLength(), Is.Zero);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task FileInfoOfMissingFile()
    {
        // The wiki: "err" is nonzero if the file was not found. RepRap.cpp:1788 (GetFileInfoResponse) replies {"err":1,"fileName":"missing.g"}
        // with the name as given. MCodeHandler used to write "fileName: instead of "fileName": and so replied with invalid JSON
        Assert.That(await _host.ExecuteAsync("M36 \"missing.g\""), Is.EqualTo("{\"err\":1,\"fileName\":\"missing.g\"}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task FileInfoOfMissingFileWithLineNumber()
    {
        // RepRap.cpp:1359 (StartJsonResponse) puts "line" first if the code had an explicit line number
        Assert.That(await _host.ExecuteAsync("N5 M36 \"missing.g\""), Is.EqualTo("{\"line\":5,\"err\":1,\"fileName\":\"missing.g\"}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task FileInfoWithoutJob()
    {
        // RRF reports job.file.fileName as null unless it is printing (PrintMonitor object model table)
        _host.Firmware.ObjectModel.Set("job", JsonNode.Parse("{\"file\":{\"fileName\":null}}"));
        await _host.Model.WaitForFullUpdateAsync();

        Assert.That(await _host.ExecuteAsync("M36"), Is.EqualTo("{\"err\":1}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task FileFragment()
    {
        WriteFile("a,b\n1,2\n", "sys", "heightmap.csv");

        // The wiki: M36.2 returns the data with line breaks replaced by "\n", "next" is zero if there is no more data
        using JsonDocument reply = JsonDocument.Parse(await _host.ExecuteAsync("M36.2 P\"0:/sys/heightmap.csv\" S0"));
        JsonElement fragment = reply.RootElement.GetProperty("fragment");
        Assert.That(fragment.GetProperty("offset").GetInt64(), Is.Zero);
        Assert.That(fragment.GetProperty("data").GetString(), Is.EqualTo("a,b\n1,2\n"));
        Assert.That(fragment.GetProperty("next").GetInt64(), Is.Zero);
        Assert.That(fragment.GetProperty("err").GetInt32(), Is.Zero);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task Checksum()
    {
        WriteFile("G1 X1\n", "gcodes", "a.g");

        // CRC32 of "G1 X1\n" is 0xdbcb34db (zlib.crc32). Like RRF (platform.OpenFile(FS_PREFIX, ...)) a relative path starts at 0:/
        Assert.That(await _host.ExecuteAsync("M38 \"gcodes/a.g\""), Is.EqualTo("dbcb34db\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ChecksumOfMissingFile()
    {
        // The wiki and GCodes2.cpp report an error with the file name ("Cannot open file: ..."); DCS words it differently
        Message? result = await _host.ExecuteCodeAsync("M38 \"missing.g\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain("missing.g"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SdCardInfoWithoutVolume()
    {
        // Same replies as the M39 case in GCodes2.cpp for a slot that does not exist
        Assert.That(await _host.ExecuteAsync("M39 P1"), Is.EqualTo("Error: M39: Bad SD slot number: 1\n"));
        Assert.That(await _host.ExecuteAsync("M39 P1 S2"), Is.EqualTo("{\"SDinfo\":{\"slot\":1,\"present\":0}}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SdCardInfoAsJson()
    {
        // Volumes are provided by DCS, the periodic update service that fills them in is not started by the fake host
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Volumes.Add(new Volume { Capacity = 3965190144, PartitionSize = 3964665856, FreeSpace = 3807379456, Speed = 20000000, Mounted = true, Path = _host.BaseDirectory });
        }

        // Fields of the wiki example for RRF 3.7 (Gcodes.md:2112) and GCodes2.cpp:1548. DCS reports the block size of the file system as "clsize"
        using JsonDocument reply = JsonDocument.Parse(await _host.ExecuteAsync("M39 S2"));
        JsonElement info = reply.RootElement.GetProperty("SDinfo");
        Assert.That(info.GetProperty("slot").GetInt32(), Is.Zero);
        Assert.That(info.GetProperty("present").GetInt32(), Is.EqualTo(1));
        Assert.That(info.GetProperty("capacity").GetInt64(), Is.EqualTo(3965190144));
        Assert.That(info.GetProperty("partitionSize").GetInt64(), Is.EqualTo(3964665856));
        Assert.That(info.GetProperty("free").GetInt64(), Is.EqualTo(3807379456));
        Assert.That(info.GetProperty("speed").GetInt64(), Is.EqualTo(20000000));
        Assert.That(info.GetProperty("clsize").GetInt64(), Is.EqualTo(DuetSharedLibrary.FileExtensions.GetFileSystemBlockSize(_host.BaseDirectory)));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SdCardInfoAsText()
    {
        using (await _host.Model.AccessReadWriteAsync())
        {
            _host.Model.Volumes.Add(new Volume { Capacity = 3965190144, PartitionSize = 3964665856, FreeSpace = 3807379456, Speed = 20000000, Mounted = true, Path = _host.BaseDirectory });
        }

        // Same text as the wiki example for RRF 3.7 (Gcodes.md:2110) and GCodes2.cpp:1572-1580, which prints the cluster size in bytes below 1024 and in kB otherwise
        long clusterSize = DuetSharedLibrary.FileExtensions.GetFileSystemBlockSize(_host.BaseDirectory);
        string clusterSizeText = (clusterSize < 1024) ? $"{clusterSize} bytes" : $"{clusterSize / 1024}kB";
        Assert.That(await _host.ExecuteAsync("M39"), Is.EqualTo($"SD card in slot 0: capacity 3.97GB, partition size 3.96GB, free space 3.81GB, speed 20.00MBytes/sec, cluster size {clusterSizeText}\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task CreateDirectory()
    {
        Assert.That(await _host.ExecuteAsync("M470 P\"/sys/config.d\""), Is.EqualTo("\n"));
        Assert.That(System.IO.Directory.Exists(PhysicalPath("sys", "config.d")), Is.True);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task RenameFile()
    {
        WriteFile("old", "sys", "config-override.g");

        Assert.That(await _host.ExecuteAsync("M471 S\"/sys/config-override.g\" T\"/sys/config-override.g.bak\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(PhysicalPath("sys", "config-override.g")), Is.False);
        Assert.That(System.IO.File.ReadAllText(PhysicalPath("sys", "config-override.g.bak")), Is.EqualTo("old"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task RenameDirectory()
    {
        WriteFile("x", "macros", "a.g");

        Assert.That(await _host.ExecuteAsync("M471 S\"0:/macros\" T\"0:/macros2\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(PhysicalPath("macros2", "a.g")), Is.True);
        Assert.That(System.IO.Directory.Exists(PhysicalPath("macros")), Is.False);
    }

    [Test]
    public async Task RenameOverExistingFile()
    {
        WriteFile("new", "sys", "a.g");
        WriteFile("old", "sys", "b.g");

        // The wiki: D1 deletes an existing file with the target name. Without it the rename fails like MassStorage::Rename does
        Message? result = await _host.ExecuteCodeAsync("M471 S\"/sys/a.g\" T\"/sys/b.g\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(System.IO.File.ReadAllText(PhysicalPath("sys", "b.g")), Is.EqualTo("old"));

        Assert.That(await _host.ExecuteAsync("M471 S\"/sys/a.g\" T\"/sys/b.g\" D1"), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(PhysicalPath("sys", "a.g")), Is.False);
        Assert.That(System.IO.File.ReadAllText(PhysicalPath("sys", "b.g")), Is.EqualTo("new"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task RenameMissingFile()
    {
        Message? result = await _host.ExecuteCodeAsync("M471 S\"/sys/missing.g\" T\"/sys/other.g\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteFileOrDirectory()
    {
        string file = WriteFile("x", "sys", "foobar.g");
        System.IO.Directory.CreateDirectory(PhysicalPath("sys", "empty"));

        Assert.That(await _host.ExecuteAsync("M472 P\"/sys/foobar.g\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(file), Is.False);
        Assert.That(await _host.ExecuteAsync("M472 P\"/sys/empty\""), Is.EqualTo("\n"));
        Assert.That(System.IO.Directory.Exists(PhysicalPath("sys", "empty")), Is.False);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteDirectoryRecursively()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("sys", "foobar"));
        WriteFile("x", "sys", "foobar", "a.g");

        // Without R1 a directory that is not empty stays, like MassStorage::Delete without recursive
        Message? result = await _host.ExecuteCodeAsync("M472 P\"/sys/foobar\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(System.IO.File.Exists(PhysicalPath("sys", "foobar", "a.g")), Is.True);

        Assert.That(await _host.ExecuteAsync("M472 P\"/sys/foobar\" R1"), Is.EqualTo("\n"));
        Assert.That(System.IO.Directory.Exists(PhysicalPath("sys", "foobar")), Is.False);
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M472 P\"0:/\" R1")]
    [TestCase("M472 P\"/\" R1")]
    [TestCase("M472 P\"0:/sys/..\" R1")]
    [TestCase("M472 P\"0:/sys/../\" R1")]
    [TestCase("M472 P\"0:/./\" R1")]
    [TestCase("M472 P\"./\" R1")]
    [TestCase("M472 P\"0:/\"")]
    public async Task DeleteRootDirectory(string code)
    {
        string config = WriteFile("M550 P\"test\"\n", "sys", "config.g");

        // RRF refuses to delete the root folder (MassStorage.cpp:840-858). M472 resolves its path outside its try block (MCodeHandler.cs:907), so
        // the refusal fails the code like every other path that FilePathResolver refuses
        Assert.That(async () => await _host.ExecuteCodeAsync(code), Throws.ArgumentException.With.Message.EqualTo("Delete root folder is not allowed"));
        Assert.That(System.IO.File.Exists(config), Is.True);

        // The channel goes on
        Assert.That(await _host.ExecuteAsync("M472 P\"/sys/config.g\""), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(config), Is.False);
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task DeleteRootDirectoryAsFile()
    {
        string config = WriteFile("M550 P\"test\"\n", "sys", "config.g");

        // M30 deletes files only (MCodeHandler.cs:428), and File.Delete does not remove a directory
        Message? result = await _host.ExecuteCodeAsync("M30 \"0:/\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(System.IO.File.Exists(config), Is.True);
        Assert.That(Received(), Is.Empty);
    }

    [TestCase("M472 P\"/sys/missing.g\"")]
    [TestCase("M472 P\"/sys/missing\" R1")]
    public async Task DeleteMissingFileOrDirectory(string code)
    {
        // GCodes2.cpp:3431-3438: MassStorage::Delete fails for a missing file or directory and M472 replies "delete failed" as an error
        Message? result = await _host.ExecuteCodeAsync(code);
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        Assert.That(result?.Content, Does.Contain("/sys/missing"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task PrintSettings()
    {
        WriteFile("M550 P\"test\"\nM584 X0\n", "sys", "config.g");

        // GCodes2.cpp: M503 replies with the content of config.g in the system directory
        Assert.That(await _host.ExecuteAsync("M503"), Is.EqualTo("M550 P\"test\"\nM584 X0\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task PrintSettingsFromBackup()
    {
        // DCS falls back to config.g.bak like it does when it runs the config file
        WriteFile("M550 P\"backup\"", "sys", "config.g.bak");

        Assert.That(await _host.ExecuteAsync("M503"), Is.EqualTo("M550 P\"backup\"\n"));
    }

    [Test]
    public async Task PrintSettingsWithoutConfig()
    {
        // Same text as the M503 case in GCodes2.cpp
        Assert.That(await _host.ExecuteAsync("M503"), Is.EqualTo("Error: M503: Configuration file not found\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task ReportSystemDirectory()
    {
        // Same wording as the M505 case in GCodes2.cpp
        Assert.That(await _host.ExecuteAsync("M505"), Is.EqualTo("Sys file path is 0:/sys/\n"));
        Assert.That(await _host.ExecuteAsync("M505.1"), Is.EqualTo("HTTP file path is 0:/www/\n"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetSystemDirectory()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("sys", "experimental"));

        // The wiki: M505 P"experimental" changes the config file path from /sys/ to /sys/experimental/
        Assert.That(await _host.ExecuteAsync("M505 P\"experimental\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M505"), Does.StartWith("Sys file path is 0:/sys/experimental"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Directories.System, Does.StartWith("0:/sys/experimental"));
        }

        // RRF locks all movement systems while it changes the path, so does DCS
        Assert.That(_host.Firmware.GetReceivedRequests().Select(request => request.Request).Where(request => request is SbcRequest.LockAllMovementSystemsAndWaitForStandstill or SbcRequest.Unlock),
            Is.EqualTo(new[] { SbcRequest.LockAllMovementSystemsAndWaitForStandstill, SbcRequest.Unlock }));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetWebDirectory()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("www", "mcui"));

        // The wiki: M505.1 P"mcui" changes the HTTP server root folder from 0:/www to 0:/www/mcui
        Assert.That(await _host.ExecuteAsync("M505.1 P\"mcui\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M505.1"), Does.StartWith("HTTP file path is 0:/www/mcui"));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Directories.Web, Does.StartWith("0:/www/mcui"));
            Assert.That(_host.Model.Directories.System, Is.EqualTo("0:/sys/"));
        }
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetMissingSystemDirectory()
    {
        // The wiki: if the specified folder does not exist, an error message is returned
        Message? result = await _host.ExecuteCodeAsync("M505 P\"missing\"");
        Assert.That(result?.Type, Is.EqualTo(MessageType.Error));
        using (await _host.Model.AccessReadOnlyAsync())
        {
            Assert.That(_host.Model.Directories.System, Is.EqualTo("0:/sys/"));
        }
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetSystemDirectoryRelativeToCurrent()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("sys", "config1", "sub"));
        Assert.That(await _host.ExecuteAsync("M505 P\"config1\""), Is.EqualTo("\n"));

        // The wiki (Gcodes.md:5371): "default path is the existing sys path if a relative path is given". Platform.cpp:3959 (ConfigurableFolder::Configure)
        // combines the new path with the current folder. MCodeHandler used to resolve it against 0:/sys
        Assert.That(await _host.ExecuteAsync("M505 P\"sub\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M505"), Does.StartWith("Sys file path is 0:/sys/config1/sub"));

        // MassStorage.cpp:208 (CombineName) ignores the current folder if the path starts with '/' or a drive number
        Assert.That(await _host.ExecuteAsync("M505 P\"/sys\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M505"), Does.StartWith("Sys file path is 0:/sys").And.Not.Contains("config1"));
        Assert.That(Received(), Is.Empty);
    }

    [Test]
    public async Task SetWebDirectoryRelativeToCurrent()
    {
        System.IO.Directory.CreateDirectory(PhysicalPath("www", "mcui", "sub"));
        Assert.That(await _host.ExecuteAsync("M505.1 P\"mcui\""), Is.EqualTo("\n"));

        // The wiki (Gcodes.md:5399): "default path is the existing HTTP root folder if a relative path is given". Platform.h:788 uses ConfigurableFolder::Configure as for M505
        Assert.That(await _host.ExecuteAsync("M505.1 P\"sub\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M505.1"), Does.StartWith("HTTP file path is 0:/www/mcui/sub"));
        Assert.That(Received(), Is.Empty);
    }
}
