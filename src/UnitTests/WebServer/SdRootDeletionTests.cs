using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DuetAPIClient;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Deleting the root of the virtual SD card. This fixture has DCS and DWS of its own because its explicit test may delete the whole
/// temporary SD card, which it recreates between the requests
/// </summary>
/// <remarks>
/// FilePathResolver.CombineContained accepts a path that is the drive root itself (FilePathResolver.cs:62-66), so 0:/, 0:, 0:/. and /
/// resolve to the SD card directory, and DELETE /machine/file passes the recursive flag straight to Directory.Delete
/// (MachineEndpoints.cs:362-367). RRF refuses to delete the root recursively: MassStorage::Delete strips the drive and slashes and stops
/// with "Delete root folder is not allowed" if nothing is left (MassStorage.cpp:839-858); rr_delete passes recursive=yes to it
/// (HttpResponder.cpp:612-617). In SBC mode MassStorage::Delete hands the request to DSF before that check (MassStorage.cpp:822-836), and
/// DCS deletes what the firmware asks for without a root check either (LinkService.HandleDeleteFileOrDirectory, LinkService.cs:1168-1192);
/// that path is not tested here
/// </remarks>
[TestFixture]
public class SdRootDeletionTests : WebServerFixture
{
    private static readonly string[] Directories = ["sys", "macros", "gcodes", "filaments", "firmware", "menu", "www"];

    private HttpClient _client = null!;
    private string _key = null!;

    [OneTimeSetUp]
    public async Task ConnectAsync()
    {
        _client = CreateClient(0);
        _key = await ConnectAsync(_client);
    }

    [OneTimeTearDown]
    public void DisposeClient() => _client?.Dispose();

    /// <summary>
    /// Recreate the SD card directories and a file in each
    /// </summary>
    private void RestoreSdCard()
    {
        foreach (string directory in Directories)
        {
            Directory.CreateDirectory(Path.Combine(Dcs.BaseDirectory, directory));
            File.WriteAllText(Path.Combine(Dcs.BaseDirectory, directory, "keep.txt"), directory);
        }
    }

    private bool SdCardIntact => Directory.Exists(Dcs.BaseDirectory) && Directories.All(d => File.Exists(Path.Combine(Dcs.BaseDirectory, d, "keep.txt")));

    /// <summary>
    /// Make sure that a path resolves to the temporary SD card of this fixture and to nothing else before anything is deleted
    /// </summary>
    private async Task<bool> ResolvesToTheRootAsync(string path)
    {
        string root = Path.GetFullPath(Dcs.BaseDirectory);
        Assert.That(root, Does.StartWith(Path.GetFullPath(Path.GetTempPath())), "The SD card must be a temporary directory");
        Assert.That(Path.GetFileName(root), Does.StartWith("dsf-fake-firmware-"), "The SD card must be the one of FakeFirmwareHost");

        using CommandConnection connection = new();
        await connection.ConnectAsync(Dcs.SocketPath!);
        string resolved = Path.GetFullPath(await connection.ResolvePathAsync(path));
        Assert.That(resolved == root || resolved.StartsWith(root + "/"), Is.True, $"{path} resolves to {resolved} outside the SD card");

        // Path.GetFullPath keeps a trailing separator, e.g. of 0:/sys/../
        return resolved == root || resolved == root + "/";
    }

    /// <summary>
    /// A recursive delete of a directory below the root works, as documented on the DSF REST API wiki page ("Version 3.5b3 and newer support
    /// the optional recursive parameter to recursively delete a directory") and as RRF allows it for everything but the root
    /// </summary>
    [Test]
    public async Task RecursiveDeleteOfATopLevelDirectory()
    {
        RestoreSdCard();
        Assert.That(await ResolvesToTheRootAsync("0:/sys"), Is.False);
        try
        {
            using HttpResponseMessage response = await SendAsync(_client, HttpMethod.Delete, "machine/file/0%3A%2Fsys?recursive=true", _key);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(Directory.Exists(Path.Combine(Dcs.BaseDirectory, "sys")), Is.False);
            Assert.That(Directories.Where(d => d != "sys").All(d => File.Exists(Path.Combine(Dcs.BaseDirectory, d, "keep.txt"))), Is.True);
        }
        finally
        {
            RestoreSdCard();
        }
    }

    /// <summary>
    /// No request may delete the root of the SD card. Each request runs on a freshly restored SD card, so that every variant is judged on
    /// its own. Result in-process (not run on the printer): DELETE /machine/file with recursive=true answers 204 and deletes the whole SD card
    /// directory including the directory itself for 0:/, 0:, 0:/., / and 0:/sys/..; without recursive it is refused with 500 "Directory not
    /// empty" (which names the physical path). rr_delete passes recursive=yes on since upstream 5130ca43 (WebServerFindings.RrDeleteDeletesRecursively),
    /// so only the root check refuses it.
    /// 0:/sys/../, 0:/./ and ./ resolve to the root with a trailing separator, which Path.GetFullPath keeps
    /// </summary>
    [Test]
    public async Task RootCannotBeDeleted()
    {
        (string Path, string Uri)[] requests =
        [
            ("0:/", "machine/file/0%3A%2F"),
            ("0:/", "machine/file/0%3A%2F?recursive=true"),
            ("0:", "machine/file/0%3A?recursive=true"),
            ("0:/.", "machine/file/0%3A%2F.?recursive=true"),
            ("/", "machine/file/%2F?recursive=true"),
            ("0:/sys/..", "machine/file/0%3A%2Fsys%2F..?recursive=true"),
            ("0:/sys/../", "machine/file/0%3A%2Fsys%2F..%2F?recursive=true"),
            ("0:/./", "machine/file/0%3A%2F.%2F?recursive=true"),
            ("./", "machine/file/.%2F?recursive=true"),
            ("0:/", "rr_delete?name=0:/"),
            ("0:/", "rr_delete?name=0:/&recursive=yes"),
            ("0:", "rr_delete?name=0:&recursive=yes"),
            ("0:/sys/../", "rr_delete?name=0:/sys/../&recursive=yes")
        ];

        List<string> results = [], failures = [];
        try
        {
            foreach ((string path, string uri) in requests)
            {
                RestoreSdCard();
                if (!await ResolvesToTheRootAsync(path))
                {
                    // Every path of the list names the root, so this is a mistake of the list and the request is not sent to be safe
                    results.Add($"{uri}: {path} does not resolve to the root, not sent");
                    failures.Add(results[^1]);
                    continue;
                }

                using HttpResponseMessage response = await SendAsync(_client, uri.StartsWith("rr_") ? HttpMethod.Get : HttpMethod.Delete, uri, _key);
                string body = await response.Content.ReadAsStringAsync();
                bool intact = SdCardIntact, rootExists = Directory.Exists(Dcs.BaseDirectory);
                string result = $"{uri}: HTTP {(int)response.StatusCode} {(body.Length > 80 ? body[..80] : body)}, SD card {(intact ? "intact" : rootExists ? "emptied" : "deleted including its directory")}";
                results.Add(result);

                // Refused means: no success status or err, and every directory and file still there
                bool success = response.IsSuccessStatusCode && (!uri.StartsWith("rr_") || body.Contains("\"err\":0"));
                if (success || !intact)
                {
                    failures.Add(result);
                }
            }
        }
        finally
        {
            RestoreSdCard();
            TestContext.Out.WriteLine(string.Join('\n', results));
        }
        Assert.That(failures, Is.Empty, "Requests that deleted the SD card root:\n" + string.Join('\n', failures) + "\nAll results:\n" + string.Join('\n', results));
    }

    /// <summary>
    /// A path can also name the root through the name of the SD card directory, which no check of the path string like RRF's (MassStorage.cpp:841-851)
    /// would see. DCS refuses the resolved path instead, with RRF's message (FilePathResolver.CombineContained), and DWS reports it like every other
    /// path that DCS refuses
    /// </summary>
    [Test]
    public async Task RootCannotBeDeletedThroughItsName()
    {
        RestoreSdCard();
        string name = Path.GetFileName(Path.GetFullPath(Dcs.BaseDirectory));
        Assert.That(await ResolvesToTheRootAsync($"0:/../{name}"), Is.True);
        try
        {
            using HttpResponseMessage response = await SendAsync(_client, HttpMethod.Delete, $"machine/file/0%3A%2F..%2F{name}?recursive=true", _key);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Delete root folder is not allowed"));
            Assert.That(SdCardIntact, Is.True);
        }
        finally
        {
            RestoreSdCard();
        }
    }

    /// <summary>
    /// Only a delete refuses the root, a directory listing of it still works (MachineEndpoints.GetFileList, RepRapFirmwareEndpoints.GetFileList)
    /// </summary>
    [Test]
    public async Task RootCanStillBeListed()
    {
        RestoreSdCard();
        using (HttpResponseMessage response = await SendAsync(_client, HttpMethod.Get, "machine/directory/0%3A%2F", _key))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            string body = await response.Content.ReadAsStringAsync();
            Assert.That(Directories.All(d => body.Contains($"\"name\":\"{d}\"")), Is.True, body);
        }
        using (HttpResponseMessage response = await SendAsync(_client, HttpMethod.Get, "rr_filelist?dir=0:/", _key))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.That(Directories.All(d => body.Contains($"\"name\":\"{d}\"")), Is.True, body);
        }
    }
}
