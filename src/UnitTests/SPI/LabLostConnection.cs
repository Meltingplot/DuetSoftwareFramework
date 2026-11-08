using System;
using System.Collections.Generic;
using System.Globalization;
using DuetControlServer;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using FirmwareRequest = DuetControlServer.Link.Protocol.FirmwareRequests.Request;
using SpiAdapter = DuetControlServer.Link.Adapter.SPI;

namespace UnitTests.SPI;

/// <summary>
/// The fault of the lab test SpiFaultLostConnection, through the injector of the fault injection build. Keep it in line with the lab test
/// </summary>
public partial class LinkRecovery
{
    /// <summary>
    /// Fault of the lab test for a counter value: DSF's header reaches RRF with a bad checksum in every exchange of one transfer, so RRF answers
    /// BadHeaderChecksum each time (DataTransfer.cpp:1099-1108). DSF retries the header (SPI.ExchangeHeader) and the full transfer (SPI.PerformFullTransfer)
    /// until it gives up and loses the connection. The counter puts that transfer on the extended counter value of DSF
    /// </summary>
    internal static string LabLostConnectionFault(int counter) => string.Create(CultureInfo.InvariantCulture, $"Any Header ToRrf times=1000 counter={counter} flip 96");

    /// <summary>
    /// What the lab fault did
    /// </summary>
    /// <param name="Hits">Exchanges the injector corrupted</param>
    /// <param name="DsfLosses">Connections DSF lost</param>
    /// <param name="RrfDisconnects">Established connections RRF lost, each with an emergency stop (SbcInterface.cpp:194-230)</param>
    /// <param name="Recovered">Whether clean transfers followed</param>
    /// <param name="Details">Description</param>
    private sealed record LabLostConnectionResult(int Hits, int DsfLosses, int RrfDisconnects, bool Recovered, string Details);

    /// <summary>
    /// Connect on the extended link, arm the lab fault and see what both sides did
    /// </summary>
    /// <param name="variant">Kind of every transfer, so also of the one the fault hits</param>
    /// <param name="counter">Counter value of the transfer the fault hits</param>
    private LabLostConnectionResult RunLabLostConnection(Variant variant, int counter)
    {
        bool rrfData = variant is Variant.BothData or Variant.RrfData, dsfData = variant is Variant.BothData or Variant.DsfData;
        FakeRrfSpi rrf = new(_ => rrfData, pollWhileDisconnected: true, capabilities: LinkCapabilities.All)
        {
            ExchangeBudget = 20000,
            BootArmDelay = FakeRrfSpi.ForkBootArmDelay,
            CheckIntegrity = true
        };
        FaultInjectingSpiDevice injector = new(rrf, null, NullLogger.Instance);
        int hits = 0, losses = 0, dsfId = 0;
        injector.Injected += (_, _) => hits++;
        void OnConnectionLost(object? sender, EventArgs e) => losses++;
        SpiAdapter dsf = CreateSpiAdapter(rrf, null, injector);

        // Perform a full transfer and return whether it was clean
        bool Transfer(bool connecting = false, bool activation = false)
        {
            if (activation && !dsf.WriteActivateLinkCapabilities(LinkCapabilities.All))
            {
                throw new InvalidOperationException("DSF did not request the capabilities");
            }
            if (dsfData)
            {
                dsf.WriteMessage(MessageTypeFlags.UsbMessage, $"dsf:{++dsfId}");
            }
            int lossesBefore = losses, disconnectsBefore = rrf.Disconnects;
            dsf.PerformFullTransfer(connecting, rrf.Budget.Token);
            bool hadReset = dsf.HadReset();
            for (int i = 0; i < dsf.PacketsToRead && dsf.ReadNextPacket() is PacketHeader packet; i++)
            {
                if (packet.Request == (ushort)FirmwareRequest.Message)
                {
                    dsf.ReadMessage(out _, out _);
                }
            }
            return !hadReset && losses == lossesBefore && rrf.Disconnects == disconnectsBefore;
        }

        _model.OnConnectionLost += OnConnectionLost;
        try
        {
            Transfer(connecting: true);
            Transfer(activation: true);
            for (int i = 0; i < 8 && !rrf.DsfUsesExtendedLink; i++)
            {
                Transfer();
            }
            if (rrf.ActiveCapabilities != LinkCapabilities.All || !rrf.DsfUsesExtendedLink)
            {
                return new(0, losses, rrf.Disconnects, false, $"setup failed: RRF has activated {rrf.ActiveCapabilities}, DSF uses the extended link: {rrf.DsfUsesExtendedLink}");
            }

            // The fault hits the next transfer with the counter value, which comes within 32 transfers
            int disconnectsBefore = rrf.Disconnects;
            injector.Arm(SpiFaultSpec.Parse(LabLostConnectionFault(counter)));
            for (int i = 0; i <= ExtendedLink.CounterMask + 1 && hits == 0; i++)
            {
                Transfer();
            }

            // Clean transfers must follow, also after RRF stopped
            int clean = 0;
            for (int i = 0; i < MaxTransfersAfterFault && clean < RecoveredTransfers; i++)
            {
                clean = Transfer() ? clean + 1 : 0;
            }
            int rrfDisconnects = rrf.Disconnects - disconnectsBefore;
            return new(hits, losses, rrfDisconnects, clean == RecoveredTransfers && rrf.CorruptedTransfers == 0,
                $"{hits} exchanges corrupted, DSF lost the connection {losses}x, RRF lost it {rrfDisconnects}x ({rrf.ConnectionResets} resets), " +
                $"RRF processed {rrf.CorruptedTransfers} transfers DSF never sent, {clean} clean transfers after it");
        }
        catch (Exception e) when (rrf.Budget.IsCancellationRequested || e is OperationCanceledException or TimeoutException)
        {
            return new(hits, losses, rrf.Disconnects, false, $"{e.GetType().Name}: {e.Message} after {rrf.Exchanges} exchanges");
        }
        finally
        {
            _model.OnConnectionLost -= OnConnectionLost;
        }
    }

    private static IEnumerable<TestCaseData> LabLostConnectionCases()
    {
        foreach (Variant variant in (Variant[])[Variant.NoData, Variant.BothData, Variant.RrfData, Variant.DsfData])
        {
            for (int counter = 0; counter <= ExtendedLink.CounterMask; counter++)
            {
                TestCaseData data = new TestCaseData(variant, counter).SetName($"LabFaultLosesConnectionAndRrfStops({variant}, {counter})");
                yield return (counter == 0)
                    ? data.Explicit("With counter value 0 DSF leaves the extended link with 0x0000, which follows the 0xFFFF RRF got last, so RRF keeps the session (P4, fix/extended-link-reset-sequence)")
                    : data;
            }
        }
    }

    /// <summary>
    /// The lab fault makes DSF lose the connection in a transfer with the given counter value on the extended link, and the link comes back.
    /// RRF has not completed that transfer, so it got 0xFFE0 | (counter - 1) from DSF last. DSF exchanges the same transfer again in a new session
    /// (SPI.PerformFullTransfer), numbered from its counter on (SPI.EndSession), which RRF must not take for the next transfer of the session:
    /// it leaves the extended link (DataTransfer.cpp:1125-1131), resets the connection (IsConnectionReset, DataTransfer.h:212-215) and performs an
    /// emergency stop (SbcInterface.cpp:194-230). DSF drops what was running meanwhile, e.g. a trigger macro (LinkService.cs:333-340)
    /// </summary>
    /// <param name="variant">Kind of the transfer that gets the fault</param>
    /// <param name="counter">Counter value of that transfer</param>
    [TestCaseSource(nameof(LabLostConnectionCases))]
    public void LabFaultLosesConnectionAndRrfStops(Variant variant, int counter)
    {
        LabLostConnectionResult result = RunLabLostConnection(variant, counter);
        Assert.Multiple(() =>
        {
            Assert.That(result.Hits, Is.EqualTo((new Settings().MaxSbcRetries + 1) * new Settings().MaxSbcRetries), "Every header exchange until DSF gives up: " + result.Details);
            Assert.That(result.DsfLosses, Is.EqualTo(1), result.Details);
            Assert.That(result.Recovered, Is.True, result.Details);
            Assert.That(result.RrfDisconnects, Is.EqualTo(1), "RRF did not notice the new session: " + result.Details);
        });
    }
}
