using System.Threading.Tasks;
using DuetAPI;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// The fake firmware host can run the IPC server of DCS, so that tests connect the way plugins and DWS do
/// </summary>
[TestFixture]
public class IpcHost
{
    [Test]
    public async Task CommandConnectionRunsCodes()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync(ipc: true);
        using CommandConnection connection = new();
        await connection.ConnectAsync(host.SocketPath);

        string reply = await connection.PerformSimpleCodeAsync("echo 1 + 2", CodeChannel.SBC).WaitAsync(FakeFirmwareHost.DefaultTimeout);
        Assert.That(reply, Is.EqualTo("3\n"));
    }
}
