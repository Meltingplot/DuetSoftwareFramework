using System.Reflection;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Codes;

/// <summary>
/// Tests for M581.1 triggers that DSF passes on to RRF
/// </summary>
[TestFixture]
public class SbcTriggers
{
    private ServiceProvider _provider = null!;
    private SbcTriggerService _triggers = null!;

    [SetUp]
    public void SetUp()
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(DispatchProxy.Create<ILinkAdapter, UnitTests.SPI.ChannelProcessor.NullLinkAdapter>())
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();
        _triggers = _provider.GetRequiredService<SbcTriggerService>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private Code MakeM581Point1(params CodeParameter[] parameters)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Type = CodeType.MCode;
        code.MajorNumber = 581;
        code.MinorNumber = 1;
        code.Parameters = [.. parameters];
        return code;
    }

    [Test]
    public async Task DeleteIsPassedToFirmware()
    {
        // RRF evaluates triggers without SBC fields itself, so M581.1 P-1 must reach it like plain M581 P-1 does
        Assert.That(await _triggers.ConfigureAsync(MakeM581Point1(new('T', 30), new('P', -1)), default), Is.Null);
    }
}
