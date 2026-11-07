using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnitTests.Fuzz;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Fuzz tests of the request inputs of the rr_ and /machine endpoints and of the static files: file names, query values, headers and bodies.
/// </summary>
/// <remarks>
/// Properties:
/// <list type="bullet">
/// <item>No file outside the virtual SD card is read, written or deleted. DWS resolves every name via the ResolvePath command of DCS
/// (EndpointHelper.cs:107-111), and FilePathResolver.CombineContained (FilePathResolver.cs:58-67) throws if the normalized path leaves the
/// root of the drive. A sentinel directory next to the SD card, whose name starts with the name of the SD card directory, must stay as it
/// is and its content must never show up in a response</item>
/// <item>Static files come only from the web directory (Program.cs:107-109, PhysicalFileProvider), so a static file response never
/// contains a file from 0:/sys</item>
/// <item>No 5xx status except where the source returns one on purpose: 500 from EndpointHelper.HandleDcsExceptionAsync for errors that
/// DCS reports (EndpointHelper.cs:145-146, the "500: Generic error" of the DSF REST API wiki page), which DWS logs as warning with the
/// exception, and 503 from rr_model (RepRapFirmwareEndpoints.cs:518, which the RRF HTTP request docs give for a lack of memory)</item>
/// <item>No unhandled exception in the DWS log</item>
/// </list>
/// Codes (rr_gcode, /machine/code) are left out, the G-code parsers have fuzz tests of their own and random codes would reset or stop
/// the fake firmware
/// </remarks>
[TestFixture]
public class RequestFuzz : WebServerFixture
{
    private HttpClient _client = null!;
    private string _sessionKey = null!;
    private string _sentinelDirectory = null!;
    private string _sentinelMarker = null!;
    private Dictionary<string, string> _sentinelSnapshot = null!;

    /// <summary>
    /// Content of a file in 0:/sys that static file requests must never return
    /// </summary>
    private const string SysMarker = "SYS-FILE-MARKER-6c1f";

    private string Sandbox => Path.Combine(Dcs.BaseDirectory, "gcodes", "fuzz");

    [OneTimeSetUp]
    public async Task SetUpFilesAsync()
    {
        _client = CreateClient(0);
        _sessionKey = await ConnectAsync(_client);

        // The sentinel shares the prefix of the SD card directory, so a containment check without the trailing separator would let it through
        _sentinelDirectory = Dcs.BaseDirectory + "-sentinel";
        _sentinelMarker = "SENTINEL-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_sentinelDirectory, "sub"));
        File.WriteAllText(Path.Combine(_sentinelDirectory, "secret.txt"), _sentinelMarker);
        File.WriteAllText(Path.Combine(_sentinelDirectory, "sub", "secret.g"), _sentinelMarker + "\nG1 X1\n");
        _sentinelSnapshot = Snapshot(_sentinelDirectory);

        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "index.html"), "<html>index</html>");
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "app.js"), "console.log(1)");
        RestoreSdCard();
    }

    [OneTimeTearDown]
    public void DeleteFiles()
    {
        _client?.Dispose();
        if (_sentinelDirectory is not null && Directory.Exists(_sentinelDirectory))
        {
            Directory.Delete(_sentinelDirectory, true);
        }
    }

    /// <summary>
    /// Restore the directories and files that a fuzzed request may have moved or deleted
    /// </summary>
    private void RestoreSdCard()
    {
        foreach (string directory in new[] { "sys", "macros", "gcodes", "filaments", "firmware", "menu", "www", "gcodes/fuzz", "gcodes/fuzz/dir" })
        {
            Directory.CreateDirectory(Path.Combine(Dcs.BaseDirectory, directory));
        }
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "sys", "marker.txt"), SysMarker);
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "index.html"), "<html>index</html>");
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "www", "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(Sandbox, "a.g"), "G1 X10\nG1 Y20\n");
        File.WriteAllText(Path.Combine(Sandbox, "b.txt"), "text");
    }

    /// <summary>
    /// Paths, sizes, times and hashes of all entries in a directory tree
    /// </summary>
    private static Dictionary<string, string> Snapshot(string directory)
    {
        Dictionary<string, string> result = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
            string hash = (info is FileInfo file) ? $"{file.Length} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))}" : "dir";
            result[Path.GetRelativePath(directory, entry)] = $"{hash} {info.LastWriteTimeUtc.Ticks}";
        }
        return result;
    }

    #region Inputs
    /// <summary>
    /// Pieces that fuzzed paths are made of
    /// </summary>
    private string[] PathTokens =>
    [
        "0:/", "1:/", "99:/", "-1:/", ":/", "0:", "/", "//", "../", "..", "./", ".", "..\\", "\\", "%2F", "%2f", "%2e%2e%2f", "%2E%2E/", "%00", "%",
        "sys", "sys/", "gcodes", "gcodes/", "fuzz/", "a.g", "b.txt", "dir", "missing.g", "www", "index.html", "marker.txt",
        "../" + Path.GetFileName(_sentinelDirectory) + "/secret.txt", "../../" + Path.GetFileName(_sentinelDirectory), Path.GetFileName(_sentinelDirectory),
        _sentinelDirectory, _sentinelDirectory + "/secret.txt", "/tmp", "/etc/passwd", "~", "$HOME", "\0", "\t", " ", "  ", "*", "?", "#", "&", "+", ";", "\"", "'",
        "ä", "€", "😀", "‮", "CON", ".part", new string('a', 300), new string('b', 5000), "%2e%2e%2f%2e%2e%2f" + Path.GetFileName(_sentinelDirectory)
    ];

    private static readonly string[] ValueTokens = ["", "0", "1", "-1", "2147483648", "99999999999999999999", "abc", "yes", "no", "true", "false", "d99vn", "f", "state", "move.axes[0]", "[", "]", "..", "\0", "ä", new string('z', 3000)];

    public enum ValueEncoding { Escaped, Raw }

    /// <summary>
    /// Fuzzed request
    /// </summary>
    /// <param name="Method">HTTP method</param>
    /// <param name="Path">Path without leading slash</param>
    /// <param name="Query">Query parameters</param>
    /// <param name="Form">Form fields for POST /machine/file/move or null</param>
    /// <param name="Body">Body or null</param>
    /// <param name="Raw">Whether the request is sent as is over a plain TCP connection</param>
    public sealed record Request(string Method, string Path, (string Key, string Value, ValueEncoding Encoding)[] Query, (string Key, string Value)[]? Form, byte[]? Body, bool Raw)
    {
        public string Target
        {
            get
            {
                string query = string.Join('&', Query.Select(q => q.Key + "=" + (q.Encoding == ValueEncoding.Escaped ? Uri.EscapeDataString(q.Value) : EscapeRaw(q.Value))));
                return "/" + Path + (query.Length > 0 ? "?" + query : string.Empty);
            }
        }

        public override string ToString() => $"{Method} {Escape(Target)}" + (Form is null ? string.Empty : " form " + string.Join('&', Form.Select(f => $"{f.Key}={Escape(f.Value)}")))
            + (Body is null ? string.Empty : $" body {Body.Length} bytes") + (Raw ? " (raw)" : string.Empty);

        private static string Escape(string value) => value.Length > 200 ? Escape(value[..100]) + $"...({value.Length} chars)" : string.Concat(value.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString()));
    }

    /// <summary>
    /// Leave percent signs and path characters as they are so that the server decodes them, but escape what would end the value
    /// </summary>
    private static string EscapeRaw(string value) => string.Concat(value.Select(c => c is '&' or '#' or ' ' or '+' or < (char)0x21 or > (char)0x7E ? Uri.EscapeDataString(c.ToString()) : c.ToString()));

    private string FuzzPath(Random random)
    {
        StringBuilder path = new();
        if (random.Next(10) < 3)
        {
            path.Append("0:/gcodes/fuzz/");
        }
        string[] tokens = PathTokens;
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            path.Append(tokens[random.Next(tokens.Length)]);
        }
        return path.ToString();
    }

    private static string FuzzValue(Random random) => (random.Next(3) == 0)
        ? ValueTokens[random.Next(ValueTokens.Length)] + ValueTokens[random.Next(ValueTokens.Length)]
        : ValueTokens[random.Next(ValueTokens.Length)];

    private static string MachinePath(string path, Random random) => random.Next(4) switch
    {
        // DWC encodes the whole path (RestConnector.EncodePath), DWS restores %2F (EndpointHelper.cs:45)
        0 => string.Join('/', path.Split('/').Select(Uri.EscapeDataString)),
        _ => Uri.EscapeDataString(path)
    };

    private Request Generate(Random random)
    {
        ValueEncoding encoding = (random.Next(5) == 0) ? ValueEncoding.Raw : ValueEncoding.Escaped;
        string p1 = FuzzPath(random), p2 = FuzzPath(random);
        byte[]? body = null;
        if (random.Next(2) == 0)
        {
            body = new byte[random.Next(0, 2048)];
            random.NextBytes(body);
        }

        switch (random.Next(20))
        {
            case 0: return new("GET", "rr_download", [("name", p1, encoding)], null, null, false);
            case 1: return new("GET", "rr_delete", [("name", p1, encoding)], null, null, false);
            case 2: return new("GET", "rr_filelist", [("dir", p1, encoding), ("first", FuzzValue(random), encoding), ("max", FuzzValue(random), encoding)], null, null, false);
            case 3: return new("GET", "rr_files", [("dir", p1, encoding), ("first", FuzzValue(random), encoding), ("flagDirs", FuzzValue(random), encoding), ("max", FuzzValue(random), encoding)], null, null, false);
            case 4: return new("GET", "rr_move", [("old", p1, encoding), ("new", p2, encoding), ("deleteexisting", random.Next(2) == 0 ? "yes" : FuzzValue(random), encoding)], null, null, false);
            case 5: return new("GET", "rr_mkdir", [("dir", p1, encoding)], null, null, false);
            case 6: return new("GET", "rr_fileinfo", [("name", p1, encoding)], null, null, false);
            case 7: return new("GET", "rr_thumbnail", [("name", p1, encoding), ("offset", FuzzValue(random), encoding)], null, null, false);
            case 8: return new("GET", "rr_model", [("key", FuzzValue(random), encoding), ("flags", FuzzValue(random), encoding)], null, null, false);
            case 9: return new("POST", "rr_upload", [("name", p1, encoding), ("time", FuzzValue(random), encoding), ("crc32", random.Next(2) == 0 ? FuzzValue(random) : "", encoding)], null, body ?? [], false);
            case 10: return new("GET", "machine/file/" + MachinePath(p1, random), [], null, null, false);
            // The Location header of the 201 response cannot hold a name with a question mark followed by a non-ASCII character, which
            // WebServerFindings.CreatedLocationIsEncoded reproduces, so leave out question marks here to let the fuzzer find other bugs
            case 11: return new("PUT", "machine/file/" + MachinePath(p1.Replace("?", ""), random), random.Next(2) == 0 ? [("timeModified", FuzzValue(random), encoding)] : [], null, body ?? [], false);
            case 12: return new("DELETE", "machine/file/" + MachinePath(p1, random), [], null, null, false);
            case 13: return new("GET", "machine/fileinfo/" + MachinePath(p1, random), [("readThumbnailContent", random.Next(2) == 0 ? "true" : FuzzValue(random), encoding)], null, null, false);
            case 14: return new("GET", "machine/directory/" + MachinePath(p1, random), [], null, null, false);
            case 15: return new("PUT", "machine/directory/" + MachinePath(p1.Replace("?", ""), random), [], null, null, false);
            case 16:
                // A NUL in a form value makes ReadFormAsync throw outside the try block of the handler, which
                // WebServerFindings.MoveWithInvalidFormIsHandled reproduces, so leave it out here to let the fuzzer find other bugs
                return new("POST", "machine/file/move", [], [("from", p1.Replace("\0", "")), ("to", p2.Replace("\0", "")), ("force", random.Next(2) == 0 ? "true" : FuzzValue(random).Replace("\0", ""))], null, false);
            default:
                {
                    // Static files and the SPA fallback, sent as is so that dot segments and escapes reach Kestrel unchanged
                    string target = string.Concat(Enumerable.Range(0, random.Next(1, 5)).Select(_ => new[] { "../", "..%2f", "%2e%2e/", "%2E%2E%2F", "sys/", "www/", "marker.txt", "index.html", "app.js", "Explorer/", "Jobs/", "x.y", "%00", "//", "./", Path.GetFileName(_sentinelDirectory) + "/", "secret.txt" }[random.Next(17)]));
                    return new("GET", target, [], null, null, true);
                }
        }
    }

    private static IEnumerable<Request> Shrink(Request request)
    {
        // Drop query parameters one at a time, then the body
        for (int i = 0; i < request.Query.Length; i++)
        {
            yield return request with { Query = [.. request.Query[..i], .. request.Query[(i + 1)..]] };
        }
        if (request.Body is { Length: > 0 })
        {
            yield return request with { Body = [] };
        }
    }
    #endregion

    [Test]
    public void FileRequests()
    {
        Fuzzer.Run(Generate, request => CheckAsync(request).GetAwaiter().GetResult(), request => request.ToString(), Shrink, Fuzzer.Scale(400), TimeSpan.FromSeconds(20));
    }

    private async Task CheckAsync(Request request)
    {
        int logStart = Dws.Log.Count;
        (HttpStatusCode status, string body) = request.Raw ? await SendRawAsync(request) : await SendAsync(request);
        try
        {
            LogEntry[] log = Dws.Log.Since(logStart);
            LogEntry[] unhandled = [.. log.Where(e => e.IsUnhandledException)];
            if (unhandled.Length > 0)
            {
                throw new FuzzFailure("unhandled exception", string.Join('\n', unhandled.Select(e => e.ToString())));
            }

            if ((int)status >= 500)
            {
                bool documented = status switch
                {
                    // EndpointHelper.HandleDcsExceptionAsync logs the generic error case as warning with the exception
                    HttpStatusCode.InternalServerError => request.Path.StartsWith("machine/") && log.Any(e => e.Category == "DuetWebServer.Endpoints.MachineEndpoints" && e.Level == LogLevel.Warning && e.Exception is not null),
                    HttpStatusCode.ServiceUnavailable => request.Path == "rr_model",
                    _ => false
                };
                if (!documented)
                {
                    throw new FuzzFailure($"HTTP {(int)status}", $"{request}: {body}");
                }
            }

            if (body.Contains(_sentinelMarker))
            {
                throw new FuzzFailure("sentinel read", $"{request} returned the content of a file outside the SD card");
            }
            if (request.Raw && status == HttpStatusCode.OK && body.Contains(SysMarker))
            {
                throw new FuzzFailure("static file outside www", $"{request} returned 0:/sys/marker.txt");
            }

            Dictionary<string, string> snapshot = Snapshot(_sentinelDirectory);
            if (!snapshot.OrderBy(kv => kv.Key).SequenceEqual(_sentinelSnapshot.OrderBy(kv => kv.Key)))
            {
                throw new FuzzFailure("sentinel changed", $"{request} changed the sentinel directory next to the SD card: [{string.Join(", ", snapshot.Keys)}]");
            }
            if (!Directory.Exists(Dcs.BaseDirectory))
            {
                throw new FuzzFailure("SD card deleted", $"{request} deleted the SD card directory");
            }
        }
        finally
        {
            RestoreSdCard();
        }
    }

    private async Task<(HttpStatusCode, string)> SendAsync(Request request)
    {
        using HttpRequestMessage message = new(new HttpMethod(request.Method), request.Target[1..]);
        message.Headers.TryAddWithoutValidation("X-Session-Key", _sessionKey);
        if (request.Form is not null)
        {
            message.Content = new FormUrlEncodedContent(request.Form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        }
        else if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body);
        }
        using HttpResponseMessage response = await _client.SendAsync(message);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode, string)> SendRawAsync(Request request)
    {
        using TcpClient tcp = new(new IPEndPoint(ClientAddress(0), 0));
        await tcp.ConnectAsync(Dws.BaseUri.Host, Dws.BaseUri.Port);
        await using NetworkStream stream = tcp.GetStream();
        byte[] head = System.Text.Encoding.ASCII.GetBytes($"{request.Method} {request.Target} HTTP/1.1\r\nHost: {Dws.BaseUri.Authority}\r\nX-Session-Key: {_sessionKey}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        using MemoryStream response = new();
        await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(10));
        string text = System.Text.Encoding.UTF8.GetString(response.ToArray());
        string statusLine = text.Split("\r\n", 2)[0];
        string[] parts = statusLine.Split(' ');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int status))
        {
            throw new FuzzFailure("raw response", $"{request}: no status line in '{statusLine}'");
        }
        int bodyStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return ((HttpStatusCode)status, (bodyStart >= 0) ? text[(bodyStart + 4)..] : string.Empty);
    }

    #region Uploads
    /// <summary>
    /// CRC32 as RRF and DWC compute it (IEEE 802.3, reflected polynomial 0xEDB88320, CRC32("123456789") = 0xCBF43926), written out here
    /// so that the test does not check DuetWebServer.Utility.CRC32 against itself
    /// </summary>
    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = ((crc & 1) != 0) ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }
        return ~crc;
    }

    [Test]
    public void Crc32Reference() => Assert.That(Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926));

    public enum CrcChoice { None, Correct, CorrectUpperCase, Wrong, Garbage, Prefixed }

    /// <summary>
    /// POST rr_upload with and without crc32: the file is stored if and only if no crc32 is given or it matches, and GET rr_upload reports the
    /// result (RRF HTTP request docs: crc32 is the "CRC32 checksum of the file content as hex string without leading 0x", err 1 e.g. on a
    /// CRC mismatch; RepRapFirmwareEndpoints.cs:258-297)
    /// </summary>
    [Test]
    public void Uploads()
    {
        Fuzzer.Run(random =>
        {
            byte[] body = new byte[random.Next(0, 20000)];
            random.NextBytes(body);
            return (Body: body, Crc: (CrcChoice)random.Next(6), Time: random.Next(3) == 0 ? "2024-05-06T07:08:09" : null);
        }, input => CheckUploadAsync(input.Body, input.Crc, input.Time).GetAwaiter().GetResult(), input => $"{input.Body.Length} bytes, crc32 {input.Crc}, time {input.Time}",
        null, Fuzzer.Scale(60));
    }

    private async Task CheckUploadAsync(byte[] body, CrcChoice crc, string? time)
    {
        uint checksum = Crc32(body);
        string? crc32 = crc switch
        {
            CrcChoice.None => null,
            CrcChoice.Correct => checksum.ToString("x8"),
            CrcChoice.CorrectUpperCase => checksum.ToString("X8"),
            CrcChoice.Wrong => (checksum ^ 1).ToString("x8"),
            CrcChoice.Prefixed => "0x" + checksum.ToString("x8"),
            _ => "nothex"
        };
        // DWS compares the hex strings ignoring the case (RepRapFirmwareEndpoints.cs:281), RRF parses the value as hex number
        bool accepted = crc is CrcChoice.None or CrcChoice.Correct or CrcChoice.CorrectUpperCase;

        string file = Path.Combine(Sandbox, "upload.bin");
        if (File.Exists(file))
        {
            File.Delete(file);
        }
        string uri = "rr_upload?name=" + Uri.EscapeDataString("0:/gcodes/fuzz/upload.bin") + (crc32 is null ? string.Empty : "&crc32=" + crc32) + (time is null ? string.Empty : "&time=" + time);
        using (HttpResponseMessage response = await SendAsync(_client, HttpMethod.Post, uri, _sessionKey, new ByteArrayContent(body)))
        {
            string json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK || json != (accepted ? "{\"err\":0}" : "{\"err\":1}"))
            {
                throw new FuzzFailure("upload response", $"HTTP {(int)response.StatusCode} {json}");
            }
        }
        if (accepted != File.Exists(file) || (accepted && !File.ReadAllBytes(file).SequenceEqual(body)))
        {
            throw new FuzzFailure("upload content", accepted ? "the file was not stored as sent" : "a file with a CRC mismatch was kept");
        }
        if (accepted && time is not null && File.GetLastWriteTime(file) != DateTime.Parse(time))
        {
            throw new FuzzFailure("upload time", $"last write time {File.GetLastWriteTime(file):s}, expected {time}");
        }
        using HttpResponseMessage result = await SendAsync(_client, HttpMethod.Get, "rr_upload", _sessionKey);
        string resultJson = await result.Content.ReadAsStringAsync();
        if (resultJson != (accepted ? "{\"err\":0}" : "{\"err\":1}"))
        {
            throw new FuzzFailure("upload result", $"GET rr_upload returned {resultJson}");
        }
    }
    #endregion
}
