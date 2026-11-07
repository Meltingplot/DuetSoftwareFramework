using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Reproducers of bugs that the DWS tests found. They are explicit so that the suite stays green; each one asserts the documented or
/// reference behaviour and fails until the bug is fixed
/// </summary>
[TestFixture]
public class WebServerFindings : WebServerFixture
{
    protected override void ConfigureDws(JsonObject config) => config["SessionTimeout"] = 3000;

    [SetUp]
    public Task ResetAsync() => ClearSessionsAsync();

    /// <summary>
    /// SessionStorage.MaintainSessions removes every expired session from DCS with an IPC connection of its own, all started at once
    /// (SessionStorage.cs:404 and 420-422). DCS listens with a backlog of 4 (DuetControlServer/Settings.cs:116, the same in
    /// pkg/common/duetcontrolserver/opt/dsf/conf/config.json), so when more sessions expire in the same pass, some connects fail with
    /// EAGAIN ("Resource temporarily unavailable"), DWS only logs "Failed to unregister expired user session" and the user sessions stay
    /// in sbc.dsf.userSessions for good
    /// </summary>
    [Test]
    [Explicit("Bug: expired sessions stay in DCS when more than the IPC backlog expire at once")]
    public async Task ExpiredSessionsAreRemovedFromDcs()
    {
        using HttpClient client = CreateClient(0);
        for (int i = 0; i < 32; i++)
        {
            await ConnectAsync(client);
        }
        Assert.That(await GetUserSessionsAsync(), Has.Length.EqualTo(33), "32 sessions plus the IP session of the first request");

        ExpireIdleSessions();
        await WaitUntilAsync(async () => (await GetUserSessionsAsync()).Length == 0, "Expired sessions are still in the DCS object model", TimeSpan.FromSeconds(5));
    }
}
