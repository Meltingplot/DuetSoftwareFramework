using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// Further cases of the codes that DCS handles itself, mostly their error paths, which the file and system code tests leave out
/// </summary>
[TestFixture]
public class MoreMCodes
{
    private FakeFirmwareHost _host = null!;

    [SetUp]
    public async Task SetUp() => _host = await FakeFirmwareHost.StartAsync();

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    private string Physical(string relative) => Path.Combine(_host.BaseDirectory, relative);

    [Test]
    public async Task DeleteDirectoryWithM30()
    {
        // M30 deletes files only; File.Delete fails for a directory and DCS reports the exception (MCodeHandler.cs:426-435)
        Directory.CreateDirectory(Physical("gcodes/folder"));
        Assert.That(await _host.ExecuteAsync("M30 \"folder\""), Does.StartWith("Error: M30: Failed to delete file folder: "));
        Assert.That(Directory.Exists(Physical("gcodes/folder")), Is.True);
    }

    [Test]
    public async Task CreateDirectoryOverFile()
    {
        // Directory.CreateDirectory fails where a file exists (MCodeHandler.cs:851-860)
        System.IO.File.WriteAllText(Physical("sys/taken"), "x");
        Assert.That(await _host.ExecuteAsync("M470 P\"/sys/taken\""), Does.StartWith("Error: M470: Failed to create directory /sys/taken: "));
    }

    [Test]
    public async Task RenameDirectoryOverEmptyDirectory()
    {
        // With D1 an existing empty target directory is deleted first, like RRF (MCodeHandler.cs:880-888)
        Directory.CreateDirectory(Physical("sys/a"));
        System.IO.File.WriteAllText(Physical("sys/a/file.g"), "G1 X1\n");
        Directory.CreateDirectory(Physical("sys/b"));
        Assert.That(await _host.ExecuteAsync("M471 S\"/sys/a\" T\"/sys/b\" D1"), Is.EqualTo("\n"));
        Assert.That(System.IO.File.Exists(Physical("sys/b/file.g")), Is.True);
        Assert.That(Directory.Exists(Physical("sys/a")), Is.False);
    }

    [Test]
    public async Task ReportCors()
    {
        // M586 without parameters reports the CORS site (MCodeHandler.cs:1087-1095)
        Assert.That(await _host.ExecuteAsync("M586"), Is.EqualTo("CORS disabled\n"));
        Assert.That(await _host.ExecuteAsync("M586 C\"http://example.com\""), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M586"), Is.EqualTo("CORS enabled for site 'http://example.com'\n"));
    }

    [Test]
    public async Task SelectMovementQueue()
    {
        // DCS keeps track of the queue that M596 selects (MCodeHandler.cs:1462-1469), RRF gets the code as well
        Assert.That(await _host.ExecuteAsync("M596 P0"), Is.EqualTo("\n"));
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.HTTP).Select(code => code.Text), Has.Some.EqualTo("M596 P0"));
    }

    [Test]
    public async Task EventLogFileIsKept()
    {
        // M929 S2 without P while logging goes on with the current file (MCodeHandler.cs:1174-1183); stopping clears it (Logger.cs:174)
        Assert.That(await _host.ExecuteAsync("M929 P\"custom.log\" S1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M929 S2"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M929"), Is.EqualTo("Event logging is enabled at log level info\n"));
        Assert.That(await _host.ExecuteAsync("M929 S0"), Is.EqualTo("\n"));
        Assert.That(System.IO.File.ReadAllText(Physical("sys/custom.log")).Split('\n').Count(line => line.EndsWith("Event logging started")), Is.EqualTo(2));
    }

    [Test]
    public async Task SyncInJob()
    {
        // M598 in a job makes the file readers meet before it goes to RRF (MCodeHandler.cs:1107-1114)
        System.IO.File.WriteAllText(Physical("gcodes/job.g"), "G1 X1\nM598\nG1 X2\n");
        Assert.That(await _host.ExecuteAsync("M32 \"job.g\""), Is.EqualTo("\n"));
        JobProcessor jobs = _host.Services.GetRequiredService<JobProcessor>();
        using CancellationTokenSource cts = new(FakeFirmwareHost.DefaultTimeout);
        while (jobs.IsFileSelected)
        {
            await Task.Delay(10, cts.Token);
        }
        Assert.That(_host.Firmware.GetExecutedCodes(CodeChannel.File).Select(code => code.Text), Is.EqualTo(new[] { "G1 X1", "M598", "G1 X2", "M400" }));
    }

    [TestCase("set global.m[\"x]y\"] = 1", "global.m[\"x]y\"]")]
    [TestCase("set global.m['a'] = 1", "global.m['a']")]
    [TestCase("set global.m[\"a\"\"b\"][0] = 1", "global.m[\"a\"\"b\"][0]")]
    public async Task QuotesInIndex(string code, string varName)
    {
        // Quotes inside the brackets of an assignment are kept and a bracket inside them does not end the index (KeywordHandler.cs:194-223)
        await _host.ExecuteAsync(code);
        Assert.That(_host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.SetVariable).Select(request => request.Text), Has.Some.EqualTo(varName));
    }

    [Test]
    public async Task ReenableLogMessages()
    {
        // O0 disables the provider that O1 added and a second O1 enables it again (MCodeHandler.cs:714-736)
        Assert.That(await _host.ExecuteAsync("M111 P-1 O1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M111 P-1 O0"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M111 P-1 O1"), Is.EqualTo("\n"));
        Assert.That(await _host.ExecuteAsync("M111 P-1 O0"), Is.EqualTo("\n"));
    }
}
