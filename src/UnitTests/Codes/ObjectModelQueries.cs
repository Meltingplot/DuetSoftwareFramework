using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Codes;

/// <summary>
/// Tests for M409 queries that DSF passes on to RRF instead of answering them from its own object model
/// </summary>
[TestFixture]
public class ObjectModelQueries
{
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp() => _provider = DcsServices.Build();

    [TearDown]
    public void TearDown()
    {
        // Stop the pipeline tasks, a flush is still waiting for the firmware
        ((ApplicationLifetime)_provider.GetRequiredService<IHostApplicationLifetime>()).StopApplication();
        _provider.Dispose();
    }

    private async Task<bool> IsPassedToFirmwareAsync(params CodeParameter[] parameters)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        code.Type = CodeType.MCode;
        code.MajorNumber = 409;
        code.Parameters = [.. parameters];

        // DSF answers other queries only after a flush, which needs a firmware, so a timeout means DSF tried to answer it itself
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        try
        {
            return await _provider.GetRequiredKeyedService<ICodeHandler>(Keys.MCodes).ProcessAsync(code, cts.Token) is null;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [Test]
    public async Task ExplicitFirmwareQuery()
    {
        // R1 asks RRF for its own object model
        Assert.That(await IsPassedToFirmwareAsync(new('K', "state.status"), new('R', 1)), Is.True);
    }

    [TestCase("move.axes[0].letter")]
    [TestCase("#move.axes")]
    public async Task IndexOrCountQuery(string key)
    {
        // RRF reports indexed keys and array lengths as scalars, whereas DSF's model filter keeps the surrounding arrays
        Assert.That(await IsPassedToFirmwareAsync(new CodeParameter('K', key)), Is.True);
    }

    [Test]
    public async Task MirroredQuery()
    {
        // DSF answers this one from its own object model
        Assert.That(await IsPassedToFirmwareAsync(new CodeParameter('K', "state.status")), Is.False);
    }
}
