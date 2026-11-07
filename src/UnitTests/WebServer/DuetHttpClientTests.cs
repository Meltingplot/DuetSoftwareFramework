using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using DuetHttpClient.Exceptions;
using DuetHttpClient.Utility;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// The scenarios of UnitTests/HttpClient/Session.cs against the in-process DWS instead of upstream's test printer, for both connectors.
/// </summary>
/// <remarks>
/// DuetHttpSession.ConnectAsync tries the PollConnector first and falls back to the RestConnector on an HttpRequestException
/// (DuetHttpSession.cs:35-45). Against DWS, rr_connect reports isEmulated, so the PollConnector logs out again and throws
/// "HTTP backend is emulated" (PollConnector.cs:61-82): DuetHttpSession always picks the RestConnector here. The PollConnector tests create it
/// through its private constructor instead, which is what PollConnector.ConnectAsync does after a successful rr_connect of a standalone
/// board, and run it against the rr_ requests that DWS emulates. These run in this namespace, not in UnitTests.HttpClient, whose
/// SessionSetUp skips everything without upstream's printer
/// </remarks>
[TestFixture]
public class DuetHttpClientTests : WebServerFixture
{
    private static string SampleDirectory => Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes");

    private static DuetHttpOptions Options(string password = "reprap", bool observe = true) => new()
    {
        Password = password,
        ObserveObjectModel = observe,
        UpdateInterval = TimeSpan.FromMilliseconds(50),
        UpdateDelay = TimeSpan.FromMilliseconds(10),
        RetryDelay = TimeSpan.FromMilliseconds(100),
        SessionKeepAliveInterval = TimeSpan.FromMilliseconds(200)
    };

    [OneTimeSetUp]
    public void CopySampleFiles()
    {
        File.Copy(Path.Combine(SampleDirectory, "Thumbnail_QOI.gcode"), Path.Combine(Dcs.BaseDirectory, "gcodes", "qoi.gcode"));
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "sys", "config.g"), "; config\nM550 P\"fake\"\n");
    }

    /// <summary>
    /// Wait for the next model update of a session while the fake firmware keeps changing its model, so that the WebSocket of the
    /// RestConnector gets a patch before its PING interval ends (see RestKeepsAQuietWebSocket)
    /// </summary>
    private async Task WaitForModelUpdateAsync(DuetHttpSession session)
    {
        Task update = session.WaitForModelUpdateAsync();
        for (int i = 0; !update.IsCompleted; i++)
        {
            Dcs.Firmware.ObjectModel.Set("state.upTime", 100_000 + i);
            await Task.WhenAny(update, Task.Delay(50));
        }
        await update;
    }

    [SetUp]
    public async Task ResetAsync()
    {
        await SetPasswordAsync(DuetAPI.Connection.Defaults.Password);
        await ClearSessionsAsync();
    }

    #region RestConnector
    [Test]
    public async Task RestObjectModelAndCodes()
    {
        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options());

        // The WebSocket delivers the model, then patches or PONGs keep it up to date (RestConnector.cs:184-310)
        await WaitForModelUpdateAsync(session).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        lock (session.Model)
        {
            Assert.That(session.Model.Boards[0].ShortName, Is.EqualTo("MB6HC"));
        }
        Dcs.Firmware.ObjectModel.Set("state.upTime", 1234);
        await WaitUntilAsync(() => { lock (session.Model) { return session.Model.State.UpTime == 1234; } }, "The model was not updated");

        Assert.That(await session.SendCodeAsync("echo 1 + 2"), Is.EqualTo("3\n"));
    }

    [Test]
    public async Task RestFiles()
    {
        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options(observe: false));
        await CheckFilesAsync(session);

        // Errors of the REST API become exceptions (RestConnector.cs:420-427, 449-452, 552-555, 616-619, 670-673)
        Assert.That(async () => await session.DownloadAsync("0:/sys/missing.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.DeleteAsync("0:/sys/missing.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.MoveAsync("0:/sys/missing.txt", "0:/sys/other.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.GetFileListAsync("0:/missing"), Throws.InstanceOf<DirectoryNotFoundException>());
        Assert.That(async () => await session.GetFileInfoAsync("0:/gcodes/missing.g"), Throws.InstanceOf<FileNotFoundException>());
    }

    [Test]
    public async Task RestPassword()
    {
        await SetPasswordAsync("client-secret");

        // The PollConnector gets err 1 from rr_connect first (PollConnector.cs:54-60), which is no HttpRequestException
        Assert.That(async () => await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options("wrong")), Throws.InstanceOf<InvalidPasswordException>());

        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options("client-secret"));
        Assert.That(await session.SendCodeAsync("echo 4"), Is.EqualTo("4\n"));
        await WaitForModelUpdateAsync(session).WaitAsync(FakeFirmwareHost.DefaultTimeout);
    }

    [Test]
    public async Task RestReconnectsWhenTheSessionExpired()
    {
        await SetPasswordAsync("client-secret");
        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options("client-secret", observe: false));
        Assert.That(async () => await session.WaitForModelUpdateAsync(), Throws.InstanceOf<InvalidOperationException>());

        // Without the WebSocket nothing holds the session, so it expires; the next request gets 401, the connector logs in again and
        // repeats the request (BaseConnector.cs:57-61, RestConnector.cs:95-129 and 383-405)
        int logStart = Dws.Log.Count;
        ExpireIdleSessions();
        Assert.That(await session.SendCodeAsync("echo 5"), Is.EqualTo("5\n"));
        Assert.That(Dws.Log.Since(logStart).Count(e => e.Message.EndsWith(" added (readWrite)")), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task RestDisconnectRemovesTheSession()
    {
        DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, Options(observe: false));
        Assert.That(await session.SendCodeAsync("echo 6"), Is.EqualTo("6\n"));
        int sessions = (await GetUserSessionsAsync()).Length;

        // DisposeAsync sends /machine/disconnect with the key (RestConnector.cs:329-357)
        await session.DisposeAsync();
        Assert.That(await GetUserSessionsAsync(), Has.Length.EqualTo(sessions - 1));
        Assert.That(async () => await session.WaitForModelUpdateAsync(), Throws.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public async Task RestKeepsMessagesIfObserved()
    {
        DuetHttpOptions options = Options();
        options.ObserveMessages = true;
        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, options);
        await WaitForModelUpdateAsync(session).WaitAsync(FakeFirmwareHost.DefaultTimeout);

        Dcs.Firmware.SendMessage(DuetControlServer.Link.Protocol.Shared.MessageTypeFlags.GenericMessage, "hello client\n");
        await WaitUntilAsync(() => { lock (session.Model) { return session.Model.Messages.Any(m => m.Content.Contains("hello client")); } }, "The message was not received");
    }
    /// <summary>
    /// When no patch arrives within PingInterval, RestConnector cancels its ReceiveAsync to send "PING\n" (RestConnector.cs:242-289), but
    /// cancelling a pending ReceiveAsync aborts a .NET WebSocket, so the PING cannot be sent: the connector marks the machine as
    /// disconnected, logs in again with a new session and opens a new socket (RestConnector.cs:291-319) every PingInterval while the model
    /// does not change. The DSF REST API wiki page describes the intended PING/PONG keep-alive
    /// </summary>
    [Test]
    [Explicit("Bug: RestConnector reconnects instead of sending PING when the model does not change")]
    public async Task RestKeepsAQuietWebSocket()
    {
        DuetHttpOptions options = Options();
        options.PingInterval = TimeSpan.FromMilliseconds(300);
        await using DuetHttpSession session = await DuetHttpSession.ConnectAsync(Dws.BaseUri, options);
        await WaitForModelUpdateAsync(session).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        int connections = Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket connected from")), logins = Dws.Log.Entries.Count(e => e.Message.EndsWith(" added (readWrite)"));

        await Task.Delay(1500);
        object connector = typeof(DuetHttpSession).GetField("_connector", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        Exception? lastError = (Exception?)connector.GetType().GetProperty("LastConnectionError")!.GetValue(connector);
        Assert.That(Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket connected from")), Is.EqualTo(connections), $"The connector opened new WebSockets, last connection error: {lastError}");
        Assert.That(Dws.Log.Entries.Count(e => e.Message.EndsWith(" added (readWrite)")), Is.EqualTo(logins), "The connector logged in again");
    }
    #endregion

    #region PollConnector
    /// <summary>
    /// Create a PollConnector session the way PollConnector.ConnectAsync does after its rr_connect (PollConnector.cs:87), without a session key
    /// because DWS returns none (RepRapFirmwareConnectResponse has no sessionKey), so DWS authenticates it by IP address
    /// </summary>
    private DuetHttpSession CreatePollSession(DuetHttpOptions options)
    {
        Assembly assembly = typeof(DuetHttpSession).Assembly;
        Type pollConnector = assembly.GetType("DuetHttpClient.Connector.PollConnector", true)!;
        Type baseConnector = assembly.GetType("DuetHttpClient.Connector.BaseConnector", true)!;
        object connector = pollConnector.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Uri), typeof(DuetHttpOptions), typeof(uint?)])!
            .Invoke([Dws.BaseUri, options, null]);
        return (DuetHttpSession)typeof(DuetHttpSession).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [baseConnector])!.Invoke([connector]);
    }

    [Test]
    public async Task PollObjectModelAndMessages()
    {
        // PollConnector fetches a key in full only when its sequence number has grown (see PollLoadsKeysWithSequenceZero), so change the boards first
        Dcs.Firmware.ObjectModel.Set("boards[0].shortName", "MB6HC");
        Dcs.Firmware.ObjectModel.Set("move.axes[0].letter", "X");
        DuetHttpOptions options = Options();
        options.ObserveMessages = true;
        await using DuetHttpSession session = CreatePollSession(options);

        // limits, then the frequently changing values and every key whose sequence number changed (PollConnector.cs:285-419)
        await session.WaitForModelUpdateAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);
        await WaitUntilAsync(() =>
        {
            lock (session.Model)
            {
                return session.Model.Boards.Count == 1 && session.Model.Boards[0].ShortName == "MB6HC" && session.Model.Move.Axes.Select(a => a.Letter).SequenceEqual(['X', 'Y', 'Z']);
            }
        }, "The boards and axes were not loaded");
        Dcs.Firmware.ObjectModel.Set("state.upTime", 4321);
        await WaitUntilAsync(() => { lock (session.Model) { return session.Model.State.UpTime == 4321; } }, "The model was not updated");

        // A new seqs.reply makes it fetch rr_reply, and unclaimed replies become messages (PollConnector.cs:824-882)
        Dcs.Firmware.SendMessage(DuetControlServer.Link.Protocol.Shared.MessageTypeFlags.GenericMessage, "poll message\n");
        await WaitUntilAsync(() => { lock (session.Model) { return session.Model.Messages.Any(m => m.Content.Contains("poll message")); } }, "The reply was not fetched");
    }

    /// <summary>
    /// PollConnector fetches a key in full only if its sequence number is greater than the last one it knows, which starts at 0
    /// (PollConnector.cs:320-330). RRF starts all sequence numbers at 0 (RepRap.cpp:450) and increments e.g. directoriesSeq only when a
    /// folder is configured (Platform.cpp:3976), so keys that never change are never loaded; the root query with flag f has only the live
    /// fields. The connector of DWC queries every key once after connecting (@duet3d/connectors PollConnector.js, doUpdate)
    /// </summary>
    [Test]
    [Explicit("Bug: DuetHttpClient's PollConnector never loads keys whose sequence number is 0")]
    public async Task PollLoadsKeysWithSequenceZero()
    {
        await using DuetHttpSession session = CreatePollSession(Options());
        await session.WaitForModelUpdateAsync().WaitAsync(FakeFirmwareHost.DefaultTimeout);

        // Give it 40 more poll cycles
        await WaitUntilAsync(() => { lock (session.Model) { return session.Model.Boards.Count == 1 && session.Model.Boards[0].ShortName == "MB6HC"; } },
            "boards[0].shortName was never loaded", TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task PollFiles()
    {
        await using DuetHttpSession session = CreatePollSession(Options(observe: false));
        await CheckFilesAsync(session);

        // rr_ errors become exceptions (PollConnector.cs:958-961, 1008-1011, 1092-1095, 1139-1142, 1209-1212)
        Assert.That(async () => await session.DownloadAsync("0:/sys/missing.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.DeleteAsync("0:/sys/missing.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.MoveAsync("0:/sys/missing.txt", "0:/sys/other.txt"), Throws.InstanceOf<FileNotFoundException>());
        Assert.That(async () => await session.GetFileListAsync("0:/missing"), Throws.InstanceOf<DirectoryNotFoundException>());
        Assert.That(async () => await session.GetFileInfoAsync("0:/gcodes/missing.g"), Throws.InstanceOf<FileNotFoundException>());
    }

    [Test]
    public async Task PollWithPassword()
    {
        await SetPasswordAsync("poll-secret");

        // DWS keeps IP sessions, so the session of an rr_connect from this address serves the connector (SessionKeyAuthenticationHandler.cs:58-60)
        using (HttpClient client = new() { BaseAddress = Dws.BaseUri })
        {
            Assert.That(await client.GetStringAsync("rr_connect?password=poll-secret"), Does.StartWith("{\"apiLevel\":1,\"err\":0"));
        }
        DuetHttpSession session = CreatePollSession(Options("poll-secret", observe: false));
        Assert.That((await session.GetFileListAsync("0:/")).Any(item => item.IsDirectory && item.Filename == "sys"), Is.True);

        // DisposeAsync sends rr_disconnect, which removes that IP session (PollConnector.cs:666-680)
        int sessions = (await GetUserSessionsAsync()).Length;
        await session.DisposeAsync();
        Assert.That(await GetUserSessionsAsync(), Has.Length.EqualTo(sessions - 1));
    }
    #endregion

    /// <summary>
    /// The Files, Directories, FileList and FileInfo scenarios of UnitTests/HttpClient/Session.cs
    /// </summary>
    private async Task CheckFilesAsync(DuetHttpSession session)
    {
        string uploadContent = Guid.NewGuid().ToString();
        await using (MemoryStream uploadStream = new(Encoding.UTF8.GetBytes(uploadContent)))
        {
            await session.UploadAsync("0:/sys/unitTest.txt", uploadStream, new DateTime(2025, 1, 2, 3, 4, 5));
        }
        using (HttpResponseMessage downloadResponse = await session.DownloadAsync("0:/sys/unitTest.txt"))
        {
            Assert.That(await downloadResponse.Content.ReadAsStringAsync(), Is.EqualTo(uploadContent));
        }
        await session.MoveAsync("0:/sys/unitTest.txt", "0:/sys/unitTest2.txt", true);
        Assert.That(File.ReadAllText(Path.Combine(Dcs.BaseDirectory, "sys", "unitTest2.txt")), Is.EqualTo(uploadContent));
        await session.DeleteAsync("0:/sys/unitTest2.txt");
        Assert.That(File.Exists(Path.Combine(Dcs.BaseDirectory, "sys", "unitTest2.txt")), Is.False);

        await session.MakeDirectory("0:/sys/unitTest");
        Assert.That(Directory.Exists(Path.Combine(Dcs.BaseDirectory, "sys", "unitTest")), Is.True);
        await session.DeleteAsync("0:/sys/unitTest");
        Assert.That(Directory.Exists(Path.Combine(Dcs.BaseDirectory, "sys", "unitTest")), Is.False);

        IList<FileListItem> fileList = await session.GetFileListAsync("0:/sys");
        Assert.That(fileList.Any(item => !item.IsDirectory && item.Filename == "config.g" && item.Size == new FileInfo(Path.Combine(Dcs.BaseDirectory, "sys", "config.g")).Length), Is.True);
        fileList = await session.GetFileListAsync("0:/");
        Assert.That(fileList.Any(item => item.IsDirectory && item.Filename == "sys"), Is.True);

        GCodeFileInfo info = await session.GetFileInfoAsync("0:/sys/config.g");
        Assert.That(info.Size, Is.EqualTo(new FileInfo(Path.Combine(Dcs.BaseDirectory, "sys", "config.g")).Length));
        GCodeFileInfo thumbnails = await session.GetFileInfoAsync("0:/gcodes/qoi.gcode", true);
        Assert.That(thumbnails.Thumbnails, Has.Count.EqualTo(2));
        Assert.That(thumbnails.Thumbnails.All(t => !string.IsNullOrEmpty(t.Data)), Is.True);
    }
}
