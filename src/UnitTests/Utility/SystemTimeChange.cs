using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.Utility;

/// <summary>
/// PeriodicUpdateService sends the local time to RRF with M905 when the system time changes (PeriodicUpdateService.cs:188-202). It notices the
/// change by comparing how far the local time moved since its last check with how much time passed on the monotonic clock
/// (PeriodicUpdateService.cs:181-187), so a change at any point of the loop is noticed, not only one during the wait between two updates
/// </summary>
[TestFixture]
public class SystemTimeChange
{
    private static readonly Regex TimeCode = new("^M905 P\"([0-9]{4}-[0-9]{2}-[0-9]{2})\" S\"([0-9]{2}:[0-9]{2}:[0-9]{2})\"$");

    private static string[] TimeCodes(FakeFirmwareHost host) => [.. host.Firmware.GetReceivedCodes().Select(code => code.Text).Where(text => TimeCode.IsMatch(text))];

    /// <summary>
    /// Start DCS with a short update interval, then the PeriodicUpdateService, which FakeFirmwareHost does not start on its own
    /// </summary>
    private static async Task<(FakeFirmwareHost, PeriodicUpdateService)> StartAsync(ShiftableClock clock)
    {
        FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(
            configureSettings: settings => settings.HostUpdateInterval = 200,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        PeriodicUpdateService service = host.Services.GetServices<IHostedService>().OfType<PeriodicUpdateService>().Single();
        await service.StartAsync(CancellationToken.None);
        return (host, service);
    }

    /// <summary>
    /// A change of the system time in either direction is sent to RRF, for example the end of daylight saving time, when the local time goes
    /// back by an hour
    /// </summary>
    [TestCase(1)]
    [TestCase(-1)]
    public async Task SystemTimeChangeIsSent(int hours)
    {
        ShiftableClock clock = new();
        (FakeFirmwareHost host, PeriodicUpdateService service) = await StartAsync(clock);
        try
        {
            await Task.Delay(1000);
            int before = TimeCodes(host).Length;
            clock.Offset = TimeSpan.FromHours(hours);

            DateTime end = DateTime.Now.AddSeconds(10);
            while (TimeCodes(host).Length == before)
            {
                Assert.That(DateTime.Now, Is.LessThan(end), "No M905 within 10 s of the time change");
                await Task.Delay(50);
            }

            // The code carries the changed local time, date and time read at the same instant
            Match match = TimeCode.Match(TimeCodes(host)[before]);
            DateTime sent = DateTime.ParseExact(match.Groups[1].Value + " " + match.Groups[2].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Assert.That(sent, Is.EqualTo(clock.GetLocalNow().DateTime).Within(TimeSpan.FromSeconds(15)));

            // and only once
            await Task.Delay(1000);
            Assert.That(TimeCodes(host), Has.Length.EqualTo(before + 1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// Without a change of the system time no M905 is sent
    /// </summary>
    [Test]
    public async Task NoTimeCodeWithoutSystemTimeChange()
    {
        ShiftableClock clock = new();
        (FakeFirmwareHost host, PeriodicUpdateService service) = await StartAsync(clock);
        try
        {
            int before = TimeCodes(host).Length;
            await Task.Delay(1500);
            Assert.That(TimeCodes(host), Has.Length.EqualTo(before));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }
}
