using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.HttpClient;

/// <summary>
/// Skips the session tests unless the printer they connect to is reachable
/// </summary>
/// <remarks>
/// Session.cs connects to http://ender6, upstream's test printer, so anywhere else its tests can only fail.
/// The lab printer tests in src/LabPrinterTests use DuetHttpClient against real hardware instead
/// </remarks>
[SetUpFixture]
public class SessionSetUp
{
    [OneTimeSetUp]
    public async Task RequireTestPrinterAsync()
    {
        using TcpClient client = new();
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
            await client.ConnectAsync("ender6", 80, cts.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Assert.Ignore("These tests connect to http://ender6, which is not reachable");
        }
    }
}
