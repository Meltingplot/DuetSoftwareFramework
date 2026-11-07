using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.WebServer;

using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Session expiry in real time: the SessionExpiry service removes sessions that were idle for longer than Settings.SessionTimeout once per
/// second (SessionExpiry.cs:21-37), from DWS and from DCS (SessionStorage.cs:390-428)
/// </summary>
[TestFixture]
public class SessionExpiryTests : WebServerFixture
{
    private const int SessionTimeout = 500;

    protected override void ConfigureDws(JsonObject config) => config["SessionTimeout"] = SessionTimeout;

    [Test]
    public async Task IdleSessionsExpire()
    {
        await SetPasswordAsync("expiry");
        try
        {
            using HttpClient active = CreateClient(0), idle = CreateClient(1);
            string activeKey = await ConnectAsync(active, "expiry");
            string idleKey = await ConnectAsync(idle, "expiry");
            Stopwatch stopwatch = Stopwatch.StartNew();

            // The active client keeps its session alive with requests (SessionStorage.cs:177, 256, 277)
            while (stopwatch.ElapsedMilliseconds < SessionTimeout + 1500)
            {
                using HttpResponseMessage response = await SendAsync(active, HttpMethod.Get, "machine/noop", activeKey);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"after {stopwatch.ElapsedMilliseconds} ms");
                await Task.Delay(SessionTimeout / 5);
            }

            // The idle one is gone, from DCS as well; this does not touch the DWS session, which a request would refresh
            await WaitUntilAsync(async () => (await GetUserSessionsAsync()).Length == 1, "The idle session was not removed from DCS");
            using (HttpResponseMessage response = await SendAsync(idle, HttpMethod.Get, "machine/noop", idleKey))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            }
            Assert.That(Dws.Log.Entries.Any(e => e.Message == $"Session {idleKey} expired"), Is.True);
            Assert.That(Dws.Log.Entries.Any(e => e.Message == $"Session {activeKey} expired"), Is.False);
        }
        finally
        {
            await SetPasswordAsync(DuetAPI.Connection.Defaults.Password);
        }
    }
}
