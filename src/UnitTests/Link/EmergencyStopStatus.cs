using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Link;

/// <summary>
/// The halted status that M112 sets once its stop has gone out (MCodeHandler.cs, M112) and the object model data RRF sent in the transfer that
/// carried the stop. RRF wrote that data before it read the stop, so its state.status is the one from before the stop. From the stop on, RRF
/// reports halted (RepRap::EmergencyStop sets stopped, RepRap.cpp:1055, and GetStatusIndex reports halted while stopped, :1957, at
/// Meltingplot/RepRapFirmware a7199f29f). DSF processes that data in the cycle after the transfer of the stop, after M112 has completed, and the
/// update service would apply it in a race with the M112 handler, so DSF asks again instead (LinkService.HandleObjectModel). Before that the halted
/// status was replaced in about 1 of 4 runs
/// </summary>
[TestFixture]
public class EmergencyStopStatus
{
    private static readonly TimeSpan Timeout = FakeFirmwareHost.DefaultTimeout;

    /// <summary>
    /// Maximum number of transfers to step through until DSF has written a query of the live fields
    /// </summary>
    private const int MaxSteps = 200;

    [Test]
    public async Task HaltedIsNotReplacedByDataFromBeforeTheStop()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        LinkInterface link = host.Services.GetRequiredService<LinkInterface>();
        Assert.That(SpinWait.SpinUntil(() =>
        {
            using (host.Model.AccessReadOnly())
            {
                return host.Model.State.Status == MachineStatus.Idle;
            }
        }, Timeout), Is.True, "The model did not get the idle status");

        // Step until DSF has written a query of the live fields, which include state.status, into the held transfer. RRF answers it while it
        // has not seen the stop yet, and the answer comes with the next transfer, which carries the stop
        static bool IsLiveQuery(FakeSbcRequest request) => request.Request == SbcRequest.GetObjectModel && request.Text == string.Empty;
        host.Firmware.HoldTransfers();
        Assert.That(host.Firmware.WaitUntil(firmware => firmware.IsTransferHeld, Timeout), Is.True, "The transfers were not held");
        for (int i = 0; i < MaxSteps && !host.Firmware.GetPendingRequests().Any(IsLiveQuery); i++)
        {
            int transfer = host.Firmware.TransferCount;
            host.Firmware.HoldTransfers(1);
            Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer && firmware.IsTransferHeld, Timeout), Is.True, "No transfer");
        }
        Assert.That(host.Firmware.GetPendingRequests().Any(IsLiveQuery), Is.True, "DSF wrote no query of the live fields");

        // Record every status from now on. The model raises PropertyChanged while the writer holds its lock
        List<MachineStatus> statuses = [];
        void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State.Status))
            {
                lock (statuses)
                {
                    statuses.Add(host.Model.State.Status);
                }
            }
        }
        host.Model.State.PropertyChanged += OnStateChanged;
        try
        {
            Task<string> m112 = host.StartCode("M112");
            Assert.That(SpinWait.SpinUntil(() => link.FirmwareHaltRequest is not null, Timeout), Is.True, "M112 did not request the stop");
            host.Firmware.ReleaseTransfers();
            Assert.That(await m112.WaitAsync(Timeout), Is.EqualTo("\n"));

            // Let RRF answer further model queries, which report halted
            int transfer = host.Firmware.TransferCount;
            Assert.That(host.Firmware.WaitUntil(firmware => firmware.TransferCount > transfer + 20, Timeout), Is.True, "No transfers after the stop");
            await host.Model.WaitForFullUpdateAsync().WaitAsync(Timeout);
        }
        finally
        {
            host.Model.State.PropertyChanged -= OnStateChanged;
        }

        lock (statuses)
        {
            Assert.That(statuses, Is.Not.Empty.And.All.EqualTo(MachineStatus.Halted), "The halted status was replaced");
        }
    }
}
