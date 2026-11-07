using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DuetWebServer.Authorization;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace UnitTests.WebServer;

/// <summary>
/// Tests of the DWS session storage on its own (DuetWebServer/Singletons/SessionStorage.cs)
/// </summary>
[TestFixture]
public class SessionStorageTests
{
    private ListLogger<SessionStorage> _logger = null!;
    private SessionStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new ListLogger<SessionStorage>();
        _storage = new SessionStorage(_logger);
    }

    [Test]
    public void KeysGrantTheirAccessLevel()
    {
        string readWrite = _storage.MakeSessionKey(1, string.Empty, true);
        string readOnly = _storage.MakeSessionKey(2, string.Empty, false);

        // SessionStorage.cs:175: readWrite grants both policies, readOnly only the read-only one
        Assert.That(_storage.CheckSessionKey(readWrite, true), Is.True);
        Assert.That(_storage.CheckSessionKey(readWrite, false), Is.True);
        Assert.That(_storage.CheckSessionKey(readOnly, true), Is.False);
        Assert.That(_storage.CheckSessionKey(readOnly, false), Is.True);
        Assert.That(_storage.CheckSessionKey(Guid.NewGuid().ToString("N"), false), Is.False);

        AuthenticationTicket ticket = _storage.GetTicketFromKey(readOnly)!;
        Assert.That(ticket.AuthenticationScheme, Is.EqualTo(SessionKeyAuthenticationHandler.SchemeName));
        Assert.That(ticket.Principal.FindFirstValue("access"), Is.EqualTo(Policies.ReadOnly));
        Assert.That(ticket.Principal.FindFirstValue("key"), Is.EqualTo(readOnly));
        Assert.That(ticket.Principal.FindFirstValue("sessionId"), Is.EqualTo("2"));
        Assert.That(_storage.GetSessionId(readWrite), Is.EqualTo(1));
        Assert.That(_storage.GetSessionId("unknown"), Is.EqualTo(-1));
        Assert.That(_storage.GetTicketFromKey("unknown"), Is.Null);
    }

    [Test]
    public void SessionsWithoutValidIdAreNotRegistered()
    {
        // SessionStorage.cs:206 and 234 register a session only for a positive DCS session ID
        string key = _storage.MakeSessionKey(0, "127.0.0.1", true);
        AuthenticationTicket ticket = _storage.MakeSessionTicket(-1, "127.0.0.1", true);

        Assert.That(key, Has.Length.EqualTo(32));
        Assert.That(ticket.Principal.FindFirstValue("access"), Is.EqualTo(Policies.ReadWrite));
        Assert.That(_storage.CheckSessionKey(key, false), Is.False);
        Assert.That(_storage.GetTicketFromIpAddress("127.0.0.1"), Is.Null);
        Assert.That(_storage.RemoveTicket(ticket.Principal), Is.Zero);
    }

    [Test]
    public void IpAddressLookupReturnsTheFirstSessionOfThatAddress()
    {
        AuthenticationTicket first = _storage.MakeSessionTicket(1, "10.0.0.1", true);
        _storage.MakeSessionKey(2, "10.0.0.1", true);
        _storage.MakeSessionKey(3, string.Empty, true);

        Assert.That(_storage.GetTicketFromIpAddress("10.0.0.1"), Is.SameAs(first));
        Assert.That(_storage.GetTicketFromIpAddress("10.0.0.2"), Is.Null);

        Assert.That(_storage.RemoveTicket(first.Principal), Is.EqualTo(1));
        Assert.That(_storage.GetTicketFromIpAddress("10.0.0.1")!.Principal.FindFirstValue("sessionId"), Is.EqualTo("2"));
        Assert.That(_storage.RemoveTicket(first.Principal), Is.Zero);
    }

    [Test]
    public void MessagesAreCachedForIpSessionsOnly()
    {
        AuthenticationTicket ipSession = _storage.MakeSessionTicket(1, "10.0.0.1", true);
        AuthenticationTicket keySession = _storage.MakeSessionTicket(2, string.Empty, true);

        _storage.CacheMessage("first\n");
        _storage.CacheMessage("second  ");

        // SessionStorage.cs:134-139: trailing whitespace is trimmed and each message ends with a new line
        Assert.That(_storage.GetCachedMessages(ipSession.Principal), Is.EqualTo("first" + Environment.NewLine + "second" + Environment.NewLine));
        Assert.That(_storage.GetCachedMessages(ipSession.Principal), Is.Empty, "Fetching the messages clears them");
        Assert.That(_storage.GetCachedMessages(keySession.Principal), Is.Empty);
        Assert.That(_storage.GetCachedMessages(new ClaimsPrincipal()), Is.Empty);
    }

    [Test]
    public async Task MaintenanceKeepsSessionsWithWebSocketsOrRunningRequests()
    {
        string socketPath = Path.Combine(Path.GetTempPath(), "dsf-missing-" + Guid.NewGuid().ToString("N") + ".sock");
        string idle = _storage.MakeSessionKey(1, string.Empty, true);
        string webSocket = _storage.MakeSessionKey(2, string.Empty, true);
        AuthenticationTicket request = _storage.MakeSessionTicket(3, string.Empty, true);

        _storage.SetWebSocketState(webSocket, true);
        _storage.SetLongRunningHttpRequest(request.Principal, true);
        _storage.SetWebSocketState("unknown", true);
        _storage.SetLongRunningHttpRequest(new ClaimsPrincipal(), true);

        // A negative timeout counts every session as idle for longer than the timeout (SessionStorage.cs:397)
        _storage.MaintainSessions(TimeSpan.FromTicks(-1), socketPath);
        Assert.That(_storage.CheckSessionKey(idle, false), Is.False);
        Assert.That(_storage.CheckSessionKey(webSocket, false), Is.True);
        Assert.That(_storage.GetSessionId(request.Principal.FindFirstValue("key")!), Is.EqualTo(3));

        // The expired session is removed from DCS in the background, which fails without DCS and is logged (SessionStorage.cs:424-427)
        await WaitForAsync(() => _logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message == "Failed to unregister expired user session"));

        // A long timeout keeps the sessions
        _storage.SetWebSocketState(webSocket, false);
        _storage.SetLongRunningHttpRequest(request.Principal, false);
        _storage.MaintainSessions(TimeSpan.FromHours(1), socketPath);
        Assert.That(_storage.CheckSessionKey(webSocket, true), Is.True);
        Assert.That(_storage.CheckSessionKey(request.Principal.FindFirstValue("key")!, true), Is.True);

        // Once the WebSocket is closed and the request has finished, they expire too
        _storage.MaintainSessions(TimeSpan.FromTicks(-1), socketPath);
        Assert.That(_storage.CheckSessionKey(webSocket, false), Is.False);
        Assert.That(_storage.GetTicketFromKey(request.Principal.FindFirstValue("key")!), Is.Null);
        Assert.That(_logger.Entries.Count(e => e.Message.EndsWith(" expired")), Is.EqualTo(3));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime end = DateTime.Now.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.Now > end)
            {
                Assert.Fail("Condition not met within 10 s");
            }
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Logger that keeps its entries in a list
/// </summary>
/// <typeparam name="T">Category type</typeparam>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>
    /// Copy of the entries
    /// </summary>
    public LogEntry[] Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(DateTime.Now, logLevel, typeof(T).FullName!, eventId, formatter(state, exception), exception));
        }
    }
}
