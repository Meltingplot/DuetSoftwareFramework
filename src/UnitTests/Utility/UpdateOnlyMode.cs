using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Utility;

/// <summary>
/// DCS -u (Settings.UpdateOnly), which the reprapfirmware package runs after it has installed new firmware files (pkg/deb/reprapfirmware/DEBIAN/postinst:11).
/// DCS first asks a running DCS to update the boards over IPC (FirmwareUpdater.TryRemoteFirmwareUpdateAsync, Program.cs:203-219), otherwise it
/// connects to RRF itself and updates them once it has the object model (FirmwareUpdater.UpdateFirmwareAsync, UpdateService.cs:339-357).
/// Both compare boards[].firmwareVersion with the version in boards[].firmwareFileName, update the outdated expansion boards with M997 B, which RRF
/// carries out, and then the main board with M997, which DCS carries out. When only expansion boards were updated, the main board is reset with M999.
/// The tests run without a console, so FirmwareUpdater updates every outdated board without asking (FirmwareUpdater.cs:109-113)
/// </summary>
[TestFixture]
public class UpdateOnlyMode
{
    private const string MainFirmwareFile = "Duet3Firmware_MB6HC.bin", ExpansionFirmwareFile = "Duet3Firmware_TOOL1LC.bin";
    private const string MainVersion = "3.7.0+1.mp.5", ExpansionVersion = "3.7.0+1.mp.4", NewVersion = "3.7.0+1.mp.9";

    private FakeFirmwareHost? _host;
    private TextWriter _console = null!;
    private StringWriter _output = null!;

    [SetUp]
    public void CaptureConsole()
    {
        // Without a redirected input, FirmwareUpdater would wait for a key press (FirmwareUpdater.cs:114-152)
        Assert.That(Console.IsInputRedirected, Is.True, "The test runner must not give the tests a console input");
        _console = Console.Out;
        _output = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(_output));
    }

    [TearDown]
    public async Task TearDown()
    {
        Console.SetOut(_console);
        TestContext.Out.WriteLine(_output.ToString());
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private string Output => _output.ToString();

    /// <summary>
    /// Main board and a tool board at CAN address 121 as RRF reports them (Platform.cpp:212-229, ExpansionManager.cpp:50-68)
    /// </summary>
    private static JsonNode Boards() => JsonNode.Parse($$"""
        [
            {
                "canAddress": 0,
                "firmwareFileName": "{{MainFirmwareFile}}",
                "firmwareName": "RepRapFirmware",
                "firmwareVersion": "{{MainVersion}}",
                "iapFileNameSBC": "Duet3_SBCiap32_MB6HC.bin",
                "name": "Duet 3 MB6HC (fake)",
                "shortName": "MB6HC",
                "state": "running"
            },
            {
                "canAddress": 121,
                "firmwareFileName": "{{ExpansionFirmwareFile}}",
                "firmwareVersion": "{{ExpansionVersion}}",
                "name": "Duet 3 Tool Board 1LC (fake)",
                "shortName": "TOOL1LC",
                "state": "running"
            }
        ]
        """)!;

    private static byte[] MainFirmware(string version) => FirmwareImage.Build($"RepRapFirmware for Duet 3 MB6HC version {version}", loadAddress: 0x00400000);

    private static byte[] ExpansionFirmware(string version) => FirmwareImage.Build($"Duet TOOL1LC firmware version {version}");

    /// <summary>
    /// Write the IAP binary to the firmware directory and the firmware files to the given directory of the virtual SD card
    /// </summary>
    private static void WriteFirmware(string baseDirectory, string? mainVersion, string? expansionVersion, string directory)
    {
        System.IO.File.WriteAllBytes(Path.Combine(baseDirectory, "firmware", "Duet3_SBCiap32_MB6HC.bin"), new byte[Consts.IapSegmentSize + 4]);
        if (mainVersion is not null)
        {
            System.IO.File.WriteAllBytes(Path.Combine(baseDirectory, directory, MainFirmwareFile), MainFirmware(mainVersion));
        }
        if (expansionVersion is not null)
        {
            System.IO.File.WriteAllBytes(Path.Combine(baseDirectory, directory, ExpansionFirmwareFile), ExpansionFirmware(expansionVersion));
        }
    }

    /// <summary>
    /// Start DCS with the two boards
    /// </summary>
    /// <param name="mainVersion">Version in the main board firmware file or null if there is none</param>
    /// <param name="expansionVersion">Version in the tool board firmware file or null if there is none</param>
    /// <param name="ipc">Whether to start the IPC server, so that FirmwareUpdater can use this instance remotely</param>
    /// <param name="updateOnly">Whether to start DCS with -u</param>
    /// <param name="configure">Optional callback to set up the fake firmware</param>
    /// <param name="directory">Directory of the firmware files</param>
    /// <param name="editBoards">Optional callback to change the boards RRF reports</param>
    private async Task<FakeFirmwareHost> StartAsync(string? mainVersion, string? expansionVersion, bool ipc = false, bool updateOnly = false,
        Action<FakeFirmware.FakeFirmware>? configure = null, string directory = "firmware", Action<JsonArray>? editBoards = null)
    {
        _host = await FakeFirmwareHost.StartAsync(
            firmware =>
            {
                JsonArray boards = Boards().AsArray();
                editBoards?.Invoke(boards);
                firmware.ObjectModel.Set("boards", boards);
                configure?.Invoke(firmware);
            },
            settings =>
            {
                settings.StartErrorFile = Path.Combine(settings.BaseDirectory, "startError.txt");
                settings.UpdateOnly = updateOnly;
                WriteFirmware(settings.BaseDirectory, mainVersion, expansionVersion, directory);
            },
            ipc);
        return _host;
    }

    /// <summary>
    /// Run the update of the local instance, or the one of a remote instance, which needs the IPC server
    /// </summary>
    private async Task RunAsync(bool remote)
    {
        if (remote)
        {
            Assert.That(await Updater.TryRemoteFirmwareUpdateAsync(CancellationToken.None).WaitAsync(UpdateTimeout), Is.True);
        }
        else
        {
            await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);
        }
    }

    /// <summary>
    /// Let RRF carry out M997 B121 like ExpansionManager::UpdateRemoteFirmware: it replies at once and the board reports the new version once it
    /// has flashed it and restarted (ExpansionManager.cpp:448-517)
    /// </summary>
    private static void UpdateExpansionBoardOnM997(FakeFirmware.FakeFirmware firmware, string newVersion) => firmware.OnCode("M997", code =>
    {
        firmware.ObjectModel.Set("boards[1].firmwareVersion", newVersion);
        return FakeReply.Ok();
    });

    private FirmwareUpdater Updater => _host!.Services.GetRequiredService<FirmwareUpdater>();

    private bool Stopping => _host!.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

    private string[] ReceivedCodes() => [.. _host!.Firmware.GetReceivedCodes().Select(code => code.Text)];

    private bool MainBoardWasReset() => _host!.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.Reset);

    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(20);

    #region Local instance
    [Test]
    public async Task AllBoardsUpToDate()
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, ExpansionVersion);
        await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("All boards are up-to-date!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
        Assert.That(Stopping, Is.True, "DCS -u must stop when it is done");
    }

    /// <summary>
    /// Firmware files that are not in directories.firmware are looked for in directories.system, where they were kept before RRF 3.3
    /// (FirmwareUpdater.cs:59-63 and 291-295, the wiki: Gcodes.md, M997 notes)
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task FirmwareFilesInSystemDirectory(bool remote)
    {
        await StartAsync(MainVersion, ExpansionVersion, ipc: remote, directory: "sys");
        await RunAsync(remote);

        Assert.That(Output, Does.Contain("All boards are up-to-date!"));
        Assert.That(Output, Does.Not.Contain("Warning"));
        Assert.That(ReceivedCodes(), Is.Empty);
    }

    /// <summary>
    /// A board without firmwareFileName, e.g. the main board when RRF did not report its verbose fields (see VerboseFieldsAfterReset), is
    /// left alone with a warning (FirmwareUpdater.cs:84-88 and 314-318)
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BoardWithoutFirmwareFileName(bool remote)
    {
        await StartAsync(NewVersion, ExpansionVersion, ipc: remote, editBoards: boards => boards[0]!.AsObject().Remove("firmwareFileName"));
        await RunAsync(remote);

        Assert.That(Output, Does.Contain($"Warning: Failed to get corresponding firmware version for Duet 3 MB6HC (fake), RRF version {MainVersion}, firmware filename ''"));
        Assert.That(Output, Does.Contain("All boards are up-to-date!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(_host!.Firmware.GetIapSegments(), Is.Empty);
    }

    /// <summary>
    /// The main board is updated by DCS's own M997 handler, so nothing reaches RRF but the IAP binary
    /// </summary>
    [Test]
    public async Task OutdatedMainBoard()
    {
        FakeFirmwareHost host = await StartAsync(NewVersion, ExpansionVersion);
        await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("There is 1 outdated board:"));
        Assert.That(Output, Does.Contain($"- Duet 3 MB6HC (fake) ({MainVersion} -> {NewVersion})" + Environment.NewLine));
        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(host.Firmware.GetFlashedFirmware()?[..MainFirmware(NewVersion).Length], Is.EqualTo(MainFirmware(NewVersion)));
        Assert.That(MainBoardWasReset(), Is.False);
        Assert.That(Stopping, Is.True);
    }

    /// <summary>
    /// FirmwareUpdater waits for the board to report the new version, polling every 2 s (FirmwareUpdater.cs:175-198), then resets the main board
    /// because it was not updated itself (FirmwareUpdater.cs:229-248)
    /// </summary>
    [Test]
    public async Task OutdatedExpansionBoard()
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, NewVersion, configure: firmware => UpdateExpansionBoardOnM997(firmware, NewVersion));
        await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain($"- Duet 3 Tool Board 1LC (fake) ({ExpansionVersion} -> {NewVersion}) @ CAN address 121"));
        Assert.That(Output, Does.Contain("Updating firmware on board #121... Done!"));
        Assert.That(Output, Does.Contain("Resetting mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.EqualTo(new[] { "M997 B121" }));
        Assert.That(MainBoardWasReset(), Is.True);
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
        Assert.That(Stopping, Is.True);
    }

    /// <summary>
    /// Expansion boards come first, because updating the main board restarts it and ends DCS (FirmwareUpdater.cs:154-228)
    /// </summary>
    [Test]
    public async Task OutdatedMainAndExpansionBoards()
    {
        bool expansionFirst = false;
        FakeFirmwareHost host = await StartAsync(NewVersion, NewVersion, configure: firmware => firmware.OnCode("M997", code =>
        {
            expansionFirst = firmware.GetIapSegments().Count == 0;
            firmware.ObjectModel.Set("boards[1].firmwareVersion", NewVersion);
            return FakeReply.Ok();
        }));
        await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("There are 2 outdated boards:"));
        Assert.That(Output, Does.Contain("Updating firmware on board #121... Done!"));
        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.EqualTo(new[] { "M997 B121" }));
        Assert.That(expansionFirst, Is.True, "The expansion board must be updated before the main board");
        Assert.That(host.Firmware.IapRebooted, Is.True);
        Assert.That(MainBoardWasReset(), Is.False, "Updating the main board restarts it anyway");
    }

    /// <summary>
    /// A board whose firmware file is missing gets no version in the table of versions (FirmwareUpdater.cs:44-69), so it is left alone with the
    /// warning for a board without a firmware version (FirmwareUpdater.cs:77-88 and 307-318), like a board without a file name. Updating it would
    /// send M997 B121, which RRF refuses ("Firmware file ... not found", ExpansionManager.cpp:484-489), and then wait for a version that never
    /// comes. The same holds for a firmware file whose version cannot be read, e.g. one without the version text at 0x20 (Firmware.cs:141-171)
    /// </summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BoardWithoutFirmwareFileIsLeftAlone(bool remote, bool fileWithoutVersion)
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, null, ipc: remote,
            configure: firmware => firmware.OnCode("M997", code => FakeReply.Error($"Firmware file {ExpansionFirmwareFile} not found")));
        if (fileWithoutVersion)
        {
            System.IO.File.WriteAllBytes(Path.Combine(host.BaseDirectory, "firmware", ExpansionFirmwareFile), new byte[0x1000]);
        }
        await RunAsync(remote);

        Assert.That(ReceivedCodes(), Is.Empty, "DCS -u must not update a board without a firmware file");
        Assert.That(Output, Does.Contain($"Warning: Failed to get corresponding firmware version for Duet 3 Tool Board 1LC (fake) @ 121, RRF version {ExpansionVersion}, firmware filename '{ExpansionFirmwareFile}'"));
        Assert.That(Output, Does.Contain("All boards are up-to-date!"));
        Assert.That(MainBoardWasReset(), Is.False);
    }

    /// <summary>
    /// A firmware file that cannot be read, here an invalid UF2 file of a tool board that asks for one (ExpansionManager.cpp:478-480), is reported
    /// and leaves only its boards alone (Firmware.cs:40-76 throws an IOException for it), the main board is still updated
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task InvalidFirmwareFileLeavesOnlyItsBoardAlone(bool remote)
    {
        const string uf2File = "Duet3Firmware_TOOL1LC.uf2";
        FakeFirmwareHost host = await StartAsync(NewVersion, null, ipc: remote, editBoards: boards => boards[1]!["firmwareFileName"] = uf2File);
        string path = Path.Combine(host.BaseDirectory, "firmware", uf2File);
        System.IO.File.WriteAllBytes(path, new byte[513]);
        await RunAsync(remote);

        Assert.That(Output, Does.Contain($"Error: Failed to read firmware version from {path}: UF2 file size must be a multiple of 512 bytes"));
        Assert.That(Output, Does.Contain($"Warning: Failed to get corresponding firmware version for Duet 3 Tool Board 1LC (fake) @ 121, RRF version {ExpansionVersion}, firmware filename '{uf2File}'"));
        Assert.That(Output, Does.Contain("There is 1 outdated board:"));
        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(host.Firmware.IapRebooted, Is.True);
    }

    /// <summary>
    /// The other boards are still compared and updated
    /// </summary>
    [Test]
    public async Task BoardWithoutFirmwareFileDoesNotStopTheOthers()
    {
        FakeFirmwareHost host = await StartAsync(NewVersion, null);
        await Updater.UpdateFirmwareAsync().WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("Warning: Failed to get corresponding firmware version for Duet 3 Tool Board 1LC (fake) @ 121"));
        Assert.That(Output, Does.Contain("There is 1 outdated board:"));
        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(host.Firmware.IapRebooted, Is.True);
    }

    /// <summary>
    /// RRF replies to M997 B at once, with an error if it cannot start the update, e.g. when the board does not answer (ExpansionManager.cpp:459-473,
    /// CanInterface.cpp:1003) or the file is missing (ExpansionManager.cpp:484-489). FirmwareUpdater looks at the reply only after the board has
    /// reported the new version (FirmwareUpdater.cs:173-198), so after an error it waits the full 3 minutes, prints a timeout instead of RRF's
    /// error and then resets the main board (FirmwareUpdater.cs:229-248). The test cancels the wait after 5 s
    /// </summary>
    [Test, Explicit("Bug: DCS -u ignores an error reply of M997 B, waits 3 minutes, drops RRF's message and resets the main board")]
    public async Task ExpansionUpdateErrorIsReported()
    {
        const string error = "Timed out awaiting response from board 121 to request type 6024";
        await StartAsync(MainVersion, NewVersion, configure: firmware => firmware.OnCode("M997", code => FakeReply.Error(error)));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await Updater.UpdateFirmwareAsync(cts.Token).WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain(error));
        Assert.That(MainBoardWasReset(), Is.False);
    }

    /// <summary>
    /// Stopping DCS -u while it waits for a tool board ends the wait, and the reset of the main board that follows is cancelled as well, because
    /// both codes get the token of the update (FirmwareUpdater.cs:173-204 and 229-248)
    /// </summary>
    [Test]
    public async Task CancelledWhileWaitingForExpansionBoard()
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, NewVersion, configure: firmware => firmware.OnCode("M997", code => FakeReply.Ok()));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(0.5));
        await Updater.UpdateFirmwareAsync(cts.Token).WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("Updating firmware on board #121... Error: "));
        Assert.That(Output, Does.Contain("Resetting mainboard... Error: "));
        Assert.That(ReceivedCodes(), Is.EqualTo(new[] { "M997 B121" }));
        Assert.That(MainBoardWasReset(), Is.False);
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
        Assert.That(Stopping, Is.True);
    }

    /// <summary>
    /// The LinkService is the only way to RRF, so in update-only mode UpdateService starts the update as soon as it has the object model
    /// (UpdateService.cs:339-357) and only the keys the update needs are queried (UpdateService.cs:303)
    /// </summary>
    [Test]
    public async Task UpdateOnlyModeUpdatesAndStops()
    {
        // UpdateService starts the update only once per process because its flag is static (UpdateService.cs:389), and other tests run hosts in
        // update-only mode before this one (IpcServer.UpdateOnlyModeServesNoConnections)
        typeof(DuetControlServer.Model.UpdateService).GetField("_updatingFirmware", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);

        FakeFirmwareHost host = await StartAsync(NewVersion, ExpansionVersion, updateOnly: true);
        CancellationToken stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await Task.Delay(Timeout.Infinite, stopping).ContinueWith(_ => { }).WaitAsync(UpdateTimeout);

        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(host.Firmware.IapRebooted, Is.True);
    }
    #endregion

    #region Remote instance
    /// <summary>
    /// Without a running DCS, DCS -u goes on with its own instance (Program.cs:210-214)
    /// </summary>
    [Test]
    public async Task NoRemoteInstance()
    {
        string socketDirectory = Path.Combine(Path.GetTempPath(), "dsf-no-dcs-" + Guid.NewGuid().ToString("N"));
        await using ServiceProvider services = DcsServices.Build(new Settings { SocketDirectory = socketDirectory });
        Assert.That(await services.GetRequiredService<FirmwareUpdater>().TryRemoteFirmwareUpdateAsync(CancellationToken.None), Is.False);
        Assert.That(Output, Is.Empty);
    }

    [Test]
    public async Task RemoteAllBoardsUpToDate()
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, ExpansionVersion, ipc: true);
        Assert.That(await Updater.TryRemoteFirmwareUpdateAsync(CancellationToken.None).WaitAsync(UpdateTimeout), Is.True);

        Assert.That(Output, Does.Contain("All boards are up-to-date!"));
        Assert.That(ReceivedCodes(), Is.Empty);
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
    }

    /// <summary>
    /// The running DCS carries out the M997 it gets over IPC, the firmware file is resolved through it as well (FirmwareUpdater.cs:289-301, 431-453)
    /// </summary>
    [Test]
    public async Task RemoteOutdatedMainBoard()
    {
        FakeFirmwareHost host = await StartAsync(NewVersion, ExpansionVersion, ipc: true);
        Assert.That(await Updater.TryRemoteFirmwareUpdateAsync(CancellationToken.None).WaitAsync(UpdateTimeout), Is.True);

        Assert.That(Output, Does.Contain($"- Duet 3 MB6HC (fake) ({MainVersion} -> {NewVersion})" + Environment.NewLine));
        Assert.That(Output, Does.Contain("Updating firmware on mainboard... Done!"));
        Assert.That(host.Firmware.IapRebooted, Is.True);
        Assert.That(MainBoardWasReset(), Is.False);
    }

    /// <summary>
    /// The new version of the tool board comes in through the object model subscription (FirmwareUpdater.cs:399-412), then the main board is reset
    /// </summary>
    [Test]
    public async Task RemoteOutdatedExpansionBoard()
    {
        FakeFirmwareHost host = await StartAsync(MainVersion, NewVersion, ipc: true, configure: firmware => UpdateExpansionBoardOnM997(firmware, NewVersion));
        Assert.That(await Updater.TryRemoteFirmwareUpdateAsync(CancellationToken.None).WaitAsync(UpdateTimeout), Is.True);

        Assert.That(Output, Does.Contain("Updating firmware on board #121... Done!"));
        Assert.That(Output, Does.Contain("Resetting mainboard... Done!"));
        Assert.That(ReceivedCodes(), Is.EqualTo(new[] { "M997 B121" }));
        Assert.That(MainBoardWasReset(), Is.True);
        Assert.That(host.Firmware.GetIapSegments(), Is.Empty);
    }
    #endregion
}
