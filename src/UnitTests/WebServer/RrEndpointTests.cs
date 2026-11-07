using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Tests of the rr_ requests that DWS emulates (DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs). Expected responses follow the RRF HTTP
/// request docs (github.com/Duet3D/RepRapFirmware/wiki/HTTP-requests) unless DWS deviates on purpose; deviations that look like bugs are
/// in <see cref="WebServerFindings"/>
/// </summary>
[TestFixture]
public class RrEndpointTests : WebServerFixture
{
    private HttpClient _client = null!;

    /// <summary>
    /// Sample files of the file info tests
    /// </summary>
    private static string SampleDirectory => Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes");

    [OneTimeSetUp]
    public async Task ConnectAsync()
    {
        _client = CreateClient(0);

        // No password is set, so rr_connect succeeds and the requests below use the IP session of the client
        using JsonDocument json = JsonDocument.Parse(await _client.GetStringAsync("rr_connect?password=reprap&time=2026-01-02T03:04:05"));
        Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.Zero);

        File.Copy(Path.Combine(SampleDirectory, "Thumbnail_QOI.gcode"), Path.Combine(Dcs.BaseDirectory, "gcodes", "qoi.gcode"));
        File.Copy(Path.Combine(SampleDirectory, "Thumbnail_JPG.gcode"), Path.Combine(Dcs.BaseDirectory, "gcodes", "jpg.gcode"));
        File.Copy(Path.Combine(SampleDirectory, "Circle.gcode"), Path.Combine(Dcs.BaseDirectory, "gcodes", "circle.gcode"));
    }

    [OneTimeTearDown]
    public void DisposeClient() => _client?.Dispose();

    private async Task<JsonElement> GetJsonAsync(string uri)
    {
        using HttpResponseMessage response = await _client.GetAsync(uri);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), uri);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    /// <summary>
    /// Board types as RRF reports them in rr_connect, see Platform::GetBoardString via RepRapFirmwareEndpoints.cs:91-105
    /// </summary>
    [TestCase("Mini5plus", "duet5lcunknown")]
    [TestCase("MB6XD", "duet3mb6xd100")]
    [TestCase("FMDC", "fmdc")]
    [TestCase("2WiFi", "duetwifi10")]
    [TestCase("2Ethernet", "duetethernet10")]
    [TestCase("2SBC", "duet2sbc10")]
    [TestCase("2Maestro", "duetmaestro100")]
    [TestCase("PC001373", "pc001373")]
    [TestCase("EXP3HC", "unknown")]
    [TestCase("MB6HC", "duet3mb6hc100")]
    public async Task ConnectReportsBoardType(string shortName, string boardType)
    {
        // The board comes from the object model of the firmware, so change it there and wait for DCS to read it
        await SetBoardAsync(shortName);
        try
        {
            JsonElement json = await GetJsonAsync("rr_connect");
            Assert.That(json.GetProperty("boardType").GetString(), Is.EqualTo(boardType));
            Assert.That(json.GetProperty("apiLevel").GetInt32(), Is.EqualTo(1));
            Assert.That(json.GetProperty("isEmulated").GetBoolean(), Is.True);
        }
        finally
        {
            await SetBoardAsync("MB6HC");
        }
    }

    private async Task SetBoardAsync(string shortName)
    {
        Dcs.Firmware.ObjectModel.Set("boards[0].shortName", shortName);
        await WaitUntilAsync(async () =>
        {
            using (await Dcs.Model.AccessReadOnlyAsync())
            {
                return Dcs.Model.Boards[0].ShortName == shortName;
            }
        }, "DCS did not read the new board");
    }

    [Test]
    public async Task GcodeRepliesAreCachedForRrReply()
    {
        // An empty code only reports the buffer space (RRF HttpResponder.cpp:559), DWS does not run anything then (RepRapFirmwareEndpoints.cs:176)
        Assert.That((await GetJsonAsync("rr_gcode?gcode=")).GetProperty("err").GetInt32(), Is.Zero);

        // rr_gcode runs the code asynchronously, so its reply arrives as generic message that DWS caches per IP session for rr_reply
        // (ModelObserver.cs:134-138, RepRapFirmwareEndpoints.cs:180)
        int replySeq = (await GetJsonAsync("rr_model?flags=d99fn")).GetProperty("result").GetProperty("seqs").GetProperty("reply").GetInt32();
        Assert.That((await GetJsonAsync("rr_gcode?gcode=" + Uri.EscapeDataString("echo \"hello from rr_gcode\""))).GetProperty("err").GetInt32(), Is.Zero);

        string replies = string.Empty;
        await WaitUntilAsync(async () => (replies += await _client.GetStringAsync("rr_reply")).Contains("hello from rr_gcode"), "rr_reply did not return the reply");

        // seqs.reply counts the messages DWS has seen (ModelObserver.cs:145-151, patched into rr_model at RepRapFirmwareEndpoints.cs:489-501)
        Assert.That((await GetJsonAsync("rr_model?flags=d99fn")).GetProperty("result").GetProperty("seqs").GetProperty("reply").GetInt32(), Is.GreaterThan(replySeq));
    }

    [Test]
    public async Task ModelChecksKeyAndFlags()
    {
        JsonElement json = await GetJsonAsync("rr_model?key=state.status&flags=v");
        Assert.That(json.GetProperty("key").GetString(), Is.EqualTo("state.status"));
        Assert.That(json.GetProperty("result").GetString(), Is.Not.Empty);

        // Only letters, digits, '.', '[' and ']' are allowed in the key and only letters and digits in the flags (RepRapFirmwareEndpoints.cs:458-480)
        Assert.That((await GetJsonAsync("rr_model?key=state%2Fstatus")).GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That((await GetJsonAsync("rr_model?key=state&flags=d-1")).GetProperty("err").GetInt32(), Is.EqualTo(1));

        // A root query without 'f' is passed on as it is
        Assert.That((await GetJsonAsync("rr_model?flags=d1")).GetProperty("result").TryGetProperty("state", out _), Is.True);
    }

    [Test]
    public async Task UploadDownloadMoveAndDelete()
    {
        byte[] content = Encoding.UTF8.GetBytes("G1 X1 Y2\n");
        using (HttpResponseMessage response = await _client.PostAsync("rr_upload?name=" + Uri.EscapeDataString("0:/gcodes/rr/new dir/file.g") + "&time=2024-01-02T03:04:05", new ByteArrayContent(content)))
        {
            // Missing directories are created (RepRapFirmwareEndpoints.cs:252-256) and the time is applied (line 291-294)
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"err\":0}"));
        }
        string file = Path.Combine(Dcs.BaseDirectory, "gcodes", "rr", "new dir", "file.g");
        Assert.That(File.ReadAllBytes(file), Is.EqualTo(content));
        Assert.That(File.GetLastWriteTime(file), Is.EqualTo(new DateTime(2024, 1, 2, 3, 4, 5)));
        Assert.That(await _client.GetStringAsync("rr_upload"), Is.EqualTo("{\"err\":0}"));

        // Without a name nothing is uploaded (RepRapFirmwareEndpoints.cs:244)
        using (HttpResponseMessage response = await _client.PostAsync("rr_upload", new ByteArrayContent(content)))
        {
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("{\"err\":1}"));
        }

        // Download: the content, or 404 with the name if the file is missing (RRF HTTP request docs)
        Assert.That(await _client.GetByteArrayAsync("rr_download?name=" + Uri.EscapeDataString("0:/gcodes/rr/new dir/file.g")), Is.EqualTo(content));
        using (HttpResponseMessage response = await _client.GetAsync("rr_download?name=0:/gcodes/missing.g"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        Assert.That(await _client.GetStringAsync("rr_download"), Is.EqualTo("{\"err\":1}"));

        // Move files and directories, with and without deleteexisting
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "gcodes", "rr", "other.g"), "other");
        Assert.That(await _client.GetStringAsync("rr_move?old=0:/gcodes/rr/other.g&new=" + Uri.EscapeDataString("0:/gcodes/rr/new dir/file.g")), Is.EqualTo("{\"err\":1}"));
        Assert.That(await _client.GetStringAsync("rr_move?old=0:/gcodes/rr/other.g&new=" + Uri.EscapeDataString("0:/gcodes/rr/new dir/file.g") + "&deleteexisting=yes"), Is.EqualTo("{\"err\":0}"));
        Assert.That(File.ReadAllText(file), Is.EqualTo("other"));
        Assert.That(await _client.GetStringAsync("rr_mkdir?dir=0:/gcodes/rr/target"), Is.EqualTo("{\"err\":0}"));
        Assert.That(await _client.GetStringAsync("rr_move?old=" + Uri.EscapeDataString("0:/gcodes/rr/new dir") + "&new=0:/gcodes/rr/target"), Is.EqualTo("{\"err\":1}"));
        Assert.That(await _client.GetStringAsync("rr_move?old=0:/gcodes/rr/target&new=" + Uri.EscapeDataString("0:/gcodes/rr/new dir") + "&deleteexisting=yes"), Is.EqualTo("{\"err\":1}"), "the target directory is not empty");
        Assert.That(await _client.GetStringAsync("rr_move?old=" + Uri.EscapeDataString("0:/gcodes/rr/new dir") + "&new=0:/gcodes/rr/target&deleteexisting=yes"), Is.EqualTo("{\"err\":0}"));
        Assert.That(File.Exists(Path.Combine(Dcs.BaseDirectory, "gcodes", "rr", "target", "file.g")), Is.True);
        Assert.That(await _client.GetStringAsync("rr_move?old=0:/gcodes/rr/missing&new=0:/gcodes/rr/x"), Is.EqualTo("{\"err\":1}"));

        // Delete: files and empty directories, err 1 for missing ones
        Assert.That(await _client.GetStringAsync("rr_delete?name=0:/gcodes/rr/target/file.g"), Is.EqualTo("{\"err\":0}"));
        Assert.That(await _client.GetStringAsync("rr_delete?name=0:/gcodes/rr/target"), Is.EqualTo("{\"err\":0}"));
        Assert.That(await _client.GetStringAsync("rr_delete?name=0:/gcodes/rr/target"), Is.EqualTo("{\"err\":1}"));
        Assert.That(await _client.GetStringAsync("rr_delete"), Is.EqualTo("{\"err\":1}"));
        Assert.That(await _client.GetStringAsync("rr_mkdir"), Is.EqualTo("{\"err\":1}"));
    }

    [Test]
    public async Task FileListsArePaged()
    {
        string directory = Path.Combine(Dcs.BaseDirectory, "macros", "list");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllText(Path.Combine(directory, "a.g"), "a");
        File.WriteAllText(Path.Combine(directory, "b.g"), "bb");

        // rr_filelist: directories first, next is the index to continue with or 0 at the end (RRF HTTP request docs, FileLists.cs:140-264)
        // (the files come in the order of the file system)
        JsonElement first = await GetJsonAsync("rr_filelist?dir=0:/macros/list&max=2");
        Assert.That(first.GetProperty("dir").GetString(), Is.EqualTo("0:/macros/list"));
        string[] firstItems = [.. first.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("type").GetString() + f.GetProperty("name").GetString())];
        Assert.That(firstItems, Has.Length.EqualTo(2));
        Assert.That(firstItems[0], Is.EqualTo("dsub"));
        Assert.That(first.GetProperty("next").GetInt32(), Is.EqualTo(2));
        JsonElement second = await GetJsonAsync("rr_filelist?dir=0:/macros/list&first=2");
        JsonElement lastItem = second.GetProperty("files").EnumerateArray().Single();
        Assert.That(new[] { firstItems[1], "f" + lastItem.GetProperty("name").GetString() }, Is.EquivalentTo(new[] { "fa.g", "fb.g" }));
        Assert.That(lastItem.GetProperty("size").GetInt64(), Is.EqualTo(lastItem.GetProperty("name").GetString() == "a.g" ? 1 : 2));
        Assert.That(second.GetProperty("next").GetInt32(), Is.Zero);
        Assert.That(second.GetProperty("err").GetInt32(), Is.Zero);

        // err 2 if the directory does not exist, err 1 without a directory
        Assert.That((await GetJsonAsync("rr_filelist?dir=0:/macros/missing")).GetProperty("err").GetInt32(), Is.EqualTo(2));
        Assert.That(await _client.GetStringAsync("rr_filelist"), Is.EqualTo("{\"err\":1}"));

        // rr_files: names only, directories prefixed with '*' if flagDirs is 1 (RRF HTTP request docs)
        JsonElement files = await GetJsonAsync("rr_files?dir=0:/macros/list&flagDirs=1");
        string[] names = [.. files.GetProperty("files").EnumerateArray().Select(f => f.GetString()!)];
        Assert.That(names[0], Is.EqualTo("*sub"));
        Assert.That(names, Is.EquivalentTo(new[] { "*sub", "a.g", "b.g" }));
        Assert.That((await GetJsonAsync("rr_files?dir=0:/macros/list&first=1&max=1")).GetProperty("files").EnumerateArray().Select(f => f.GetString()), Is.EqualTo(new[] { names[1] }));
        Assert.That(await _client.GetStringAsync("rr_files"), Is.EqualTo("{\"err\":1}"));
    }

    [Test]
    public async Task FileInfoAndThumbnails()
    {
        JsonElement info = await GetJsonAsync("rr_fileinfo?name=0:/gcodes/qoi.gcode");
        Assert.That(info.GetProperty("err").GetInt32(), Is.Zero);
        Assert.That(info.GetProperty("fileName").GetString(), Is.EqualTo("0:/gcodes/qoi.gcode"));
        Assert.That(info.GetProperty("size").GetInt64(), Is.EqualTo(new FileInfo(Path.Combine(Dcs.BaseDirectory, "gcodes", "qoi.gcode")).Length));
        Assert.That(info.TryGetProperty("lastModified", out _), Is.True);
        Assert.That(info.TryGetProperty("generatedBy", out _), Is.True);

        // The thumbnails of the file info are fetched with rr_thumbnail from their offset; DWS returns the whole base64 data from there on
        // with next 0 (RepRapFirmwareEndpoints.cs:785-808)
        JsonElement[] thumbnails = [.. info.GetProperty("thumbnails").EnumerateArray()];
        Assert.That(thumbnails, Has.Length.EqualTo(2));
        foreach (JsonElement thumbnail in thumbnails)
        {
            Assert.That(thumbnail.GetProperty("format").GetString(), Is.EqualTo("qoi"));
            long offset = thumbnail.GetProperty("offset").GetInt64();
            JsonElement chunk = await GetJsonAsync($"rr_thumbnail?name=0:/gcodes/qoi.gcode&offset={offset}");
            Assert.That(chunk.GetProperty("err").GetInt32(), Is.Zero);
            Assert.That(chunk.GetProperty("offset").GetInt64(), Is.EqualTo(offset));
            Assert.That(chunk.GetProperty("next").GetInt32(), Is.Zero);
            Assert.That(Convert.FromBase64String(chunk.GetProperty("data").GetString()!), Is.Not.Empty);
        }

        // JPEG thumbnails are reported as such (RepRapFirmwareEndpoints.cs:711-717)
        JsonElement jpeg = await GetJsonAsync("rr_fileinfo?name=0:/gcodes/jpg.gcode");
        Assert.That(jpeg.GetProperty("thumbnails").EnumerateArray().Select(t => t.GetProperty("format").GetString()), Is.EqualTo(new[] { "jpeg" }));

        // The file info of another file is parsed again for rr_thumbnail (RepRapFirmwareEndpoints.cs:773-781)
        Assert.That((await GetJsonAsync("rr_fileinfo?name=0:/gcodes/circle.gcode")).GetProperty("err").GetInt32(), Is.Zero);
        Assert.That((await GetJsonAsync($"rr_thumbnail?name=0:/gcodes/qoi.gcode&offset={thumbnails[0].GetProperty("offset").GetInt64()}")).GetProperty("err").GetInt32(), Is.Zero);

        // Errors: no file being printed, missing file, missing name, offset outside the thumbnails
        Assert.That((await GetJsonAsync("rr_fileinfo")).GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That((await GetJsonAsync("rr_fileinfo?name=0:/gcodes/missing.g")).GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That((await GetJsonAsync("rr_thumbnail?name=0:/gcodes/missing.g&offset=1")).GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That((await GetJsonAsync("rr_thumbnail?offset=1")).GetProperty("err").GetInt32(), Is.EqualTo(1));
        Assert.That((await GetJsonAsync("rr_thumbnail?name=0:/gcodes/qoi.gcode&offset=1")).GetProperty("err").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task FileInfoOfTheJobFile()
    {
        // Without a name, rr_fileinfo reports the file being printed plus the print duration (RepRapFirmwareEndpoints.cs:641-651, RRF HTTP request docs)
        using (await Dcs.Model.AccessReadWriteAsync())
        {
            Dcs.Model.Job.File.FileName = "0:/gcodes/circle.gcode";
            Dcs.Model.Job.Duration = 42;
        }
        try
        {
            JsonElement info = await GetJsonAsync("rr_fileinfo");
            Assert.That(info.GetProperty("err").GetInt32(), Is.Zero);
            Assert.That(info.GetProperty("fileName").GetString(), Is.EqualTo("0:/gcodes/circle.gcode"));
            Assert.That(info.GetProperty("printDuration").GetInt32(), Is.EqualTo(42));
            Assert.That(info.TryGetProperty("filament", out _) || info.TryGetProperty("height", out _), Is.True);
        }
        finally
        {
            using (await Dcs.Model.AccessReadWriteAsync())
            {
                Dcs.Model.Job.File.FileName = string.Empty;
                Dcs.Model.Job.Duration = null;
            }
        }
    }

    [Test]
    public async Task DisconnectRemovesTheIpSession()
    {
        using HttpClient client = CreateClient(5);
        await SetPasswordAsync("rr-secret");
        try
        {
            Assert.That((await client.GetAsync("rr_model?key=state")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            using (JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("rr_connect?password=wrong")))
            {
                Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.EqualTo(1));
            }
            using (JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("rr_connect?password=rr-secret")))
            {
                Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.Zero);
            }
            Assert.That((await client.GetAsync("rr_model?key=state")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            string ipAddress = ClientAddress(5).ToString();
            Assert.That((await GetUserSessionsAsync()).Count(s => s.Origin == ipAddress), Is.EqualTo(1));

            Assert.That((await client.GetAsync("rr_disconnect")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await client.GetAsync("rr_model?key=state")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await GetUserSessionsAsync()).Count(s => s.Origin == ipAddress), Is.Zero);
        }
        finally
        {
            await SetPasswordAsync(DuetAPI.Connection.Defaults.Password);
        }
    }
}
