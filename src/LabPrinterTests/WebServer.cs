using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DuetHttpClient;
using NUnit.Framework;
using RawHttpSession = LabPrinterTests.G3Helpers.RawHttpSession;
using StoredFile = LabPrinterTests.G3Helpers.StoredFile;

namespace LabPrinterTests;

/// <summary>
/// Fixes of DuetWebServer and its systemd unit. run-lab-tests.py side-loads no DWS (run-lab-tests.py:36) and none of these fixes is in a release up to
/// <see cref="G3Helpers.LastReleaseBeforeNewFixes"/>, so each test first finds out whether the DWS or unit on the printer has its fix and is skipped
/// when it cannot tell
/// </summary>
[TestFixture]
public class WebServer : LabTest
{
    private const string UploadDirectory = $"{Lab.MacroDirectory}/rr-upload";
    private const string UploadFile = $"{UploadDirectory}/keep.g";

    [OneTimeTearDown]
    public async Task DeleteFilesAsync()
    {
        if (Lab.IsConfigured)
        {
            await using DuetHttpSession session = await Lab.ConnectAsync();
            await Lab.DeleteAsync(session, $"{UploadFile}.part");
            await Lab.DeleteAsync(session, UploadFile);
            await Lab.DeleteAsync(session, UploadDirectory);
        }
    }

    /// <summary>
    /// Upload a file with POST /rr_upload in a session of its own
    /// </summary>
    private static async Task<string> RrUploadAsync(RawHttpSession session, string content, uint crc32)
    {
        (int status, string body) = await session.SendAsync(HttpMethod.Post, $"rr_upload?name={HttpUtility.UrlEncode(UploadFile)}&crc32={crc32:x8}",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content)));
        Assert.That(status, Is.EqualTo(200), $"POST rr_upload answered {status}: {body}");
        return body;
    }

    /// <summary>
    /// An rr_upload whose crc32 does not match its body is refused and keeps the file of that name, and a good one replaces it by a new file.
    /// Since #222 rr_upload writes name.part, deletes only that on a CRC32 mismatch and moves it over the file on success
    /// (DuetWebServer/Endpoints/RepRapFirmwareEndpoints.cs:258-298). Without #222 it wrote the file itself with FileMode.Create and deleted it on a mismatch
    /// (PR #222 diff, H3), so a failed upload destroyed the file it was to replace, and a good one truncated the existing file in place, under a job
    /// that reads it (FS2). The test sees the first as a missing file after the refused upload, and the second as the same inode before and after
    /// the good upload; since #222 rename(2) gives the name the inode of the .part file. The first part is UnitTests/WebServer/WebServerFindings.cs
    /// FailedUploadKeepsExistingFile on the printer's file system. Deterministic, one run catches it. Not in DSF 3.7.0+mp.15
    /// </summary>
    [Test]
    public async Task FailedRrUploadKeepsTheFile()
    {
        await G3Helpers.RequireWebServerWithNewFixesAsync("#222");

        const string original = "; lab test: rr_upload must keep this content\n";
        const string refused = "; lab test: rr_upload with a wrong crc32\n";
        const string replacement = "; lab test: rr_upload with the right crc32\n";
        await Lab.UploadAsync(Session, UploadFile, original);
        (string path, StoredFile? before, _) = await G3Helpers.GetStoredFileAsync(UploadFile);
        Assert.That(before?.Content, Is.EqualTo(original), $"PUT of {UploadFile} ({path})");

        await using RawHttpSession session = await RawHttpSession.ConnectAsync();
        uint wrongCrc = G3Helpers.Crc32(Encoding.UTF8.GetBytes(refused)) ^ 1;
        string refusedReply = await RrUploadAsync(session, refused, wrongCrc);
        (int status, string lastResult) = await session.SendAsync(HttpMethod.Get, "rr_upload");
        (_, StoredFile? afterRefused, StoredFile? partAfterRefused) = await G3Helpers.GetStoredFileAsync(UploadFile);
        TestContext.Progress.WriteLine($"rr_upload with a wrong crc32: {refusedReply}, GET rr_upload {status} {lastResult}, file {afterRefused}, part {partAfterRefused}");

        string goodReply = await RrUploadAsync(session, replacement, G3Helpers.Crc32(Encoding.UTF8.GetBytes(replacement)));
        (_, StoredFile? afterGood, StoredFile? partAfterGood) = await G3Helpers.GetStoredFileAsync(UploadFile);
        TestContext.Progress.WriteLine($"rr_upload with the right crc32: {goodReply}, file {afterGood}, part {partAfterGood}");

        Assert.Multiple(() =>
        {
            Assert.That(refusedReply, Is.EqualTo("{\"err\":1}"), "Reply to the upload with a wrong crc32");
            Assert.That(lastResult, Is.EqualTo("{\"err\":1}"), "GET rr_upload after the refused upload (RepRapFirmwareEndpoints.cs:223)");
            Assert.That(afterRefused, Is.Not.Null, "The refused upload deleted the file it was to replace");
            Assert.That(afterRefused?.Content, Is.EqualTo(original), "Content after the refused upload");
            Assert.That(partAfterRefused, Is.Null, "The refused upload left its .part file");

            Assert.That(goodReply, Is.EqualTo("{\"err\":0}"), "Reply to the upload with the right crc32");
            Assert.That(afterGood?.Content, Is.EqualTo(replacement), "Content after the good upload");
            if (afterRefused is not null)
            {
                Assert.That(afterGood?.Inode, Is.Not.EqualTo(afterRefused.Inode), "The good upload wrote the existing file in place instead of moving a new one over it");
            }
            Assert.That(partAfterGood, Is.Null, "The good upload left its .part file");
        });
    }

    private const string EndpointNamespace = "labtest";
    private const string EndpointPath = "stall";

    /// <summary>
    /// Port of DWS on the SBC: http.json sets Kestrel to http://* (pkg/common/duetwebserver/opt/dsf/conf/http.json:10)
    /// </summary>
    private const int WebServerPort = 80;

    /// <summary>
    /// A plugin endpoint whose plugin accepts and reads the request but never answers, with a client on the SBC that sends a request for it, gives up
    /// after two seconds and closes its connection. The script plays the plugin itself, as PluginEndpointStallTests.AddEndpointThatDoesNotAcceptAsync
    /// does, so that it can see when DWS closes its end: it registers the endpoint with the IPC command AddHttpEndpoint, which returns the socket path
    /// /run/dsf/namespace/path-GET.sock (DuetControlServer/Commands/HttpEndpoints/AddHttpEndpoint.cs:50), listens there with the backlog 4 of
    /// DuetAPIClient and the group access DWS (user dsf) needs (DuetAPIClient/HttpEndpoints/HttpEndpointUnixSocket.cs:18, :72-76). DWS learns of the
    /// endpoint from the object model (Services/ModelObserver.cs:180) and answers 404 until then, so the client tries again until the request arrives.
    /// The client talks to 127.0.0.1 so that its close does not depend on the tunnel of the tests
    /// </summary>
    private const string StallScript = """
        import select

        ipc = Ipc()
        added = ipc.request({"command": "AddHttpEndpoint", "endpointType": "GET", "namespace": ARGS["namespace"], "path": ARGS["path"], "isUploadRequest": False}, 10)
        if added is None or not added.get("success"):
            finish({"error": "AddHttpEndpoint failed: %s" % added})
        socketPath = added["result"]
        result = {"socket": socketPath, "registered": False}
        listener = plugin = client = None
        try:
            try:
                os.unlink(socketPath)
            except FileNotFoundError:
                pass
            listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            listener.bind(socketPath)
            os.chown(socketPath, 0, grp.getgrnam("dsf").gr_gid)
            os.chmod(socketPath, 0o660)
            listener.listen(4)

            request = ("GET /machine/%s/%s HTTP/1.1\r\nHost: localhost\r\n\r\n" % (ARGS["namespace"], ARGS["path"])).encode()
            attempts, answers = 0, []
            deadline = time.monotonic() + 30
            while plugin is None and time.monotonic() < deadline:
                attempts += 1
                client = socket.create_connection(("127.0.0.1", ARGS["port"]), timeout=5)
                client.sendall(request)
                ready, _, _ = select.select([listener, client], [], [], 5)
                if listener in ready:
                    plugin, _ = listener.accept()
                else:
                    if client in ready:
                        answers.append(client.recv(200).decode("latin-1").split("\r\n", 1)[0])
                    client.close()
                    client = None
                    time.sleep(0.5)
            result.update({"attempts": attempts, "answers": answers[-3:]})

            if plugin is not None:
                result["registered"] = True
                plugin.settimeout(10)
                try:
                    result["requestBytes"] = len(plugin.recv(65536))
                except socket.timeout:
                    result["requestBytes"] = 0

                # The plugin never answers. The client gives up and closes its connection
                time.sleep(ARGS["abortAfter"])
                client.close()
                client = None
                aborted = time.monotonic()

                # DWS closes its connection to the plugin once it has ended the request
                result["closedByWebServer"] = False
                plugin.settimeout(ARGS["closeTimeout"])
                try:
                    while plugin.recv(65536):
                        pass
                    result["closedByWebServer"] = True
                except ConnectionResetError:
                    result["closedByWebServer"] = True
                except socket.timeout:
                    pass
                result["closeSeconds"] = round(time.monotonic() - aborted, 2)
        finally:
            for s in (client, plugin, listener):
                if s is not None:
                    s.close()
            result["removed"] = ipc.request({"command": "RemoveHttpEndpoint", "endpointType": "GET", "namespace": ARGS["namespace"], "path": ARGS["path"]}, 10)
            try:
                os.unlink(socketPath)
            except FileNotFoundError:
                pass
        finish(result)
        """;

    /// <summary>
    /// A request for a plugin endpoint ends when the client aborts it, and DWS closes its connection to the plugin.
    /// Since #229 CustomEndpointMiddleware passes context.RequestAborted to SendHttpRequest and GetHttpResponse
    /// (DuetWebServer/Middleware/CustomEndpointMiddleware.cs:279-280), and the endpoint connection is disposed when the request ends (:65).
    /// Without #229 both calls had no token (PR #229 diff), so a request whose plugin does not answer waited after the client had gone, with its
    /// connection to the plugin open, until the plugin closed it or DWS stopped (TP1). The test sees that as no close within
    /// <see cref="CloseTimeout"/> seconds after the client closed. Kestrel signals RequestAborted when the client closes its connection; the unit
    /// test UnitTests/WebServer/PluginEndpointStallTests.cs RequestThatThePluginDoesNotAnswerEndsWhenTheClientAborts relies on the same.
    /// Default run: it registers a temporary endpoint and removes it again, nothing else. Deterministic, one run catches it. Not in DSF 3.7.0+mp.15
    /// </summary>
    [Test]
    public async Task PluginRequestEndsWhenTheClientAborts()
    {
        await G3Helpers.RequireWebServerWithNewFixesAsync("#229");

        JsonElement result;
        try
        {
            result = await G3Helpers.RunPythonAsync(StallScript, new
            {
                @namespace = EndpointNamespace,
                path = EndpointPath,
                port = WebServerPort,
                abortAfter = 2,
                closeTimeout = CloseTimeout
            }, 120);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            // The script did not get to its cleanup, e.g. because timeout killed it
            await G3Helpers.RemoveHttpEndpointAsync(EndpointNamespace, EndpointPath);
            throw;
        }
        TestContext.Progress.WriteLine($"Plugin endpoint: {result}");

        Assert.That(G3Helpers.GetString(result, "error"), Is.Null);
        if (!result.GetProperty("registered").GetBoolean())
        {
            Assert.Inconclusive($"DWS did not forward a request for /machine/{EndpointNamespace}/{EndpointPath} within 30 s, so the test cannot abort one");
        }
        Assert.That(result.GetProperty("requestBytes").GetInt32(), Is.Positive, "DWS connected to the plugin but sent no request");
        Assert.That(result.GetProperty("closedByWebServer").GetBoolean(), Is.True,
            $"DWS kept the connection to the plugin open {CloseTimeout} s after the client had closed its connection");
    }

    /// <summary>
    /// Seconds the script waits for DWS to close its connection to the plugin after the client closed
    /// </summary>
    private const int CloseTimeout = 10;

    /// <summary>
    /// Restart settings of a unit as systemd uses them, including drop-ins
    /// </summary>
    private static async Task<(string Restart, string RestartUSec, string StartLimitIntervalUSec, string MainPid, string ActiveState)> GetUnitAsync(string unit)
    {
        string[] values = (await Lab.RunOnSbcAsync($"systemctl show -p Restart -p RestartUSec -p StartLimitIntervalUSec -p MainPID -p ActiveState {unit}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string Get(string property) => Array.Find(values, line => line.StartsWith($"{property}="))?[(property.Length + 1)..] ?? string.Empty;
        return (Get("Restart"), Get("RestartUSec"), Get("StartLimitIntervalUSec"), Get("MainPID"), Get("ActiveState"));
    }

    /// <summary>
    /// Skip a test of #225 when the unit of DWS predates it. #225 is in no release up to <see cref="G3Helpers.LastReleaseBeforeNewFixes"/>, but an image
    /// or a v3.7-dev build of that version may set Restart=always too, so the unit decides then
    /// </summary>
    private static async Task RequireRestartingWebServerUnitAsync(string restart)
    {
        (string? version, bool newer) = await G3Helpers.ComparePackageVersionAsync("duetwebserver", "gt", G3Helpers.LastReleaseBeforeNewFixes);
        if (version is null)
        {
            Assert.Ignore("duetwebserver is not installed as a package");
        }
        if (!newer && restart != "always")
        {
            Assert.Ignore($"duetwebserver {version} predates #225: it is not newer than {G3Helpers.LastReleaseBeforeNewFixes} and its unit has Restart={restart}");
        }
    }

    /// <summary>
    /// systemd starts DWS again after it ended, with the same settings as DCS. Since #225 duetwebserver.service has Restart=always, RestartSec=5 and
    /// StartLimitIntervalSec=0 (pkg/common/duetwebserver/usr/lib/systemd/system/duetwebserver.service:5, :11-12), "the same values as
    /// duetcontrolserver.service" (#225 commit, duetcontrolserver.service:3, :9-10). Without #225 the unit had no Restart= (PR #225 diff), so systemd
    /// left DWS down after a crash or the OOM killer until the next reboot (LX4), which the test sees as Restart=no.
    /// The same check as UnitTests Startup.ServiceUnits.WebServerIsRestartedLikeControlServer, on the unit systemd loaded with its drop-ins.
    /// Read-only and deterministic. Not in DSF 3.7.0+mp.15, an image with an older package is skipped
    /// </summary>
    [Test]
    public async Task WebServerUnitRestartsIt()
    {
        var webServer = await GetUnitAsync("duetwebserver");
        var controlServer = await GetUnitAsync("duetcontrolserver");
        TestContext.Progress.WriteLine($"duetwebserver: {webServer}, duetcontrolserver: {controlServer}");
        await RequireRestartingWebServerUnitAsync(webServer.Restart);

        Assert.That((webServer.Restart, webServer.RestartUSec, webServer.StartLimitIntervalUSec),
            Is.EqualTo(("always", controlServer.RestartUSec, controlServer.StartLimitIntervalUSec)),
            "Restart, RestartUSec and StartLimitIntervalUSec of duetwebserver, expected those of duetcontrolserver");
    }

    /// <summary>
    /// After SIGKILL systemd starts a new DWS and the web interface answers again. This is what #225 is for (see <see cref="WebServerUnitRestartsIt"/>);
    /// without it DWS stays down after the kill and the test fails. It kills the whole unit, as the OOM killer would end DWS, waits for a new active
    /// DWS (RestartSec=5 plus its start, Type=notify) and for a session. DCS and RRF keep running: duetwebserver.service has no PartOf= or BindsTo=
    /// on DCS. HTTP clients, among them DWC and the HMI, lose their sessions, which DWS keeps in memory. Skipped when the unit predates #225, and
    /// it starts DWS again in any case. Category Approval: it stops the web interface for about ten seconds
    /// </summary>
    [Test, Category("Approval"), Explicit("Kills DuetWebServer, needs Tim's approval for each run")]
    public async Task WebServerComesBackAfterAKill()
    {
        var before = await GetUnitAsync("duetwebserver");
        TestContext.Progress.WriteLine($"duetwebserver before the kill: {before}");
        await RequireRestartingWebServerUnitAsync(before.Restart);
        Assert.That(before.Restart, Is.EqualTo("always"), "Restart of duetwebserver");
        Assert.That(before.ActiveState, Is.EqualTo("active"), "duetwebserver is not running");
        Assert.That(before.MainPid, Is.Not.EqualTo("0"), "duetwebserver has no main process");

        Stopwatch watch = Stopwatch.StartNew();
        string? newPid = null;
        TimeSpan restarted = TimeSpan.Zero, answering = TimeSpan.Zero;
        try
        {
            await Lab.RunOnSbcAsync("sudo -n systemctl kill -s SIGKILL duetwebserver");
            newPid = (await Lab.RunOnSbcAsync($"""
                for i in $(seq 120); do
                  state=$(systemctl show -p ActiveState --value duetwebserver)
                  pid=$(systemctl show -p MainPID --value duetwebserver)
                  [ "$state" = active ] && [ "$pid" != 0 ] && [ "$pid" != "{before.MainPid}" ] && echo "$pid" && exit 0
                  sleep 0.5
                done
                exit 0
                """)).Trim();
            restarted = watch.Elapsed;
            if (!string.IsNullOrEmpty(newPid))
            {
                await using DuetHttpSession session = await Lab.ReconnectAsync(TimeSpan.FromSeconds(60));
                answering = watch.Elapsed;
            }
        }
        finally
        {
            // Never leave the web interface down
            string state = (await Lab.RunOnSbcAsync("systemctl show -p ActiveState --value duetwebserver")).Trim();
            if (state != "active" && state != "activating")
            {
                TestContext.Progress.WriteLine($"duetwebserver is {state} after the kill, starting it");
                await Lab.RunOnSbcAsync("sudo -n systemctl start duetwebserver");
            }
            await ReconnectSessionAsync();
        }

        TestContext.Progress.WriteLine($"New DWS {newPid} active after {restarted.TotalSeconds:F1} s, a session after {answering.TotalSeconds:F1} s");
        Assert.That(newPid, Is.Not.Null.And.Not.Empty, "systemd did not start a new DWS within 60 s of the kill");
        Assert.That(answering, Is.Positive, "No session within 60 s of the new DWS");
    }
}
