using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using NUnit.Framework;
using UnitTests.Fuzz;

namespace UnitTests.WebServer;

using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Fuzz test of HTTP sessions: random sequences of logins, logouts, requests with valid and invalid session keys, WebSockets and idle periods
/// from several clients, checked against a model of the sessions that DWS and DCS must hold.
/// </summary>
/// <remarks>
/// The model follows the source, not RRF where the two differ (see the report of this branch):
/// <list type="bullet">
/// <item>Authentication (SessionKeyAuthenticationHandler.cs:38-85) runs for every request. With an X-Session-Key header the request is
/// authenticated if and only if one of its values is the key of a live session, from any client. Without the header the first live session
/// of the client's IP address is used; if there is none and no password is set (CheckPassword.cs:21: the password is "reprap" or empty),
/// a new read-write session is created for that address, in DWS and as DCS user session</item>
/// <item>rr_connect and /machine/connect check the password via DCS (RepRapFirmwareEndpoints.cs:85, MachineEndpoints.cs:74). On success
/// rr_connect adds another session for the client's address (whose key it does not return), /machine/connect a session without address and
/// returns its key; on failure rr_connect returns {"err":1,"isEmulated":true} and /machine/connect 403</item>
/// <item>rr_disconnect and /machine/disconnect remove the session the request was authenticated with, in DWS and DCS, and return 204</item>
/// <item>Protected endpoints return 401 without a valid session (AuthenticationHandler challenge) and 403 if a read-only session asks for
/// a read-write endpoint (Program.cs:71-73). The DSF REST API wiki page says 403 for a missing or invalid X-Session-Key; that is an open
/// question in the report, the model follows the code</item>
/// <item>WS /machine checks the sessionKey query parameter itself (WebSocketEndpoint.cs:79-123): without it the socket is accepted only if
/// no password is set, with it only if it is a live key; an accepted socket with a key keeps its session from expiring</item>
/// <item>Idle lets every session expire that has no WebSocket, via the maintenance of the SessionExpiry service (see
/// <see cref="WebServerFixture.ExpireIdleSessions"/>); the configured SessionTimeout is long, so sessions expire only there. Idle is skipped while more than four sessions would expire, see
/// WebServerFindings.ExpiredSessionsAreRemovedFromDcs</item>
/// </list>
/// No HTTP request creates a read-only session (all logins pass readWrite: true), so the fuzzer adds some the way a read-only login would,
/// via AddUserSession in DCS and SessionStorage.MakeSessionKey with readWrite: false, to check the read-write policy
/// </remarks>
[TestFixture]
public class SessionFuzz : WebServerFixture
{
    private const int NumClients = 3;

    /// <summary>
    /// IPC backlog of DCS (DuetControlServer/Settings.cs:116, also in pkg/common/duetcontrolserver/opt/dsf/conf/config.json)
    /// </summary>
    private const int DcsBacklog = 4;
    private const string SecretPassword = "s3cret pass";

    private readonly HttpClient[] _clients = new HttpClient[NumClients];

    protected override void ConfigureDws(JsonObject config) => config["SessionTimeout"] = 600_000;

    [OneTimeSetUp]
    public void CreateClients()
    {
        for (int i = 0; i < NumClients; i++)
        {
            _clients[i] = CreateClient(i);
        }
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "sys", "fuzz.txt"), "fuzz");
        File.WriteAllText(Path.Combine(Dcs.BaseDirectory, "gcodes", "fuzz.gcode"), "G1 X10\n");
    }

    [OneTimeTearDown]
    public void DisposeClients()
    {
        foreach (HttpClient client in _clients)
        {
            client?.Dispose();
        }
    }

    #region Inputs
    public enum Op { RrConnect, RrDisconnect, MachineConnect, MachineDisconnect, Request, OpenWebSocket, CloseWebSocket, Idle, SetPassword, AddReadOnlySession }

    public enum KeyChoice { None, Own, Other, Stale, Random, Garbage, Empty }

    public enum PasswordChoice { Right, Wrong, Missing, Default }

    /// <summary>
    /// Step of a sequence
    /// </summary>
    /// <param name="Op">Operation</param>
    /// <param name="Client">Client number</param>
    /// <param name="Key">Session key to send</param>
    /// <param name="KeyValue">Value for random or garbage keys</param>
    /// <param name="Password">Password to send</param>
    /// <param name="Endpoint">Index into <see cref="Endpoints"/> or the new password for SetPassword</param>
    /// <param name="Pick">Picks one of several known keys or WebSockets</param>
    public sealed record Step(Op Op, int Client, KeyChoice Key, string KeyValue, PasswordChoice Password, int Endpoint, int Pick)
    {
        public override string ToString() => Op switch
        {
            Op.Request => $"Request(client {Client}, {Endpoints[Endpoint].Name}, key {Key}{(Key is KeyChoice.Random or KeyChoice.Garbage ? $" '{KeyValue}'" : string.Empty)}, pick {Pick})",
            Op.RrConnect or Op.MachineConnect => $"{Op}(client {Client}, password {Password}, key {Key}, pick {Pick})",
            Op.OpenWebSocket => $"OpenWebSocket(client {Client}, sessionKey {Key}{(Key is KeyChoice.Random or KeyChoice.Garbage ? $" '{KeyValue}'" : string.Empty)}, pick {Pick})",
            Op.SetPassword => $"SetPassword('{Passwords[Endpoint]}')",
            Op.Idle => "Idle",
            _ => $"{Op}(client {Client}, key {Key}, pick {Pick})"
        };
    }

    /// <summary>
    /// Sequence of steps that starts without sessions and with the given password
    /// </summary>
    public sealed record Sequence(int InitialPassword, Step[] Steps)
    {
        public override string ToString() => $"password '{Passwords[InitialPassword]}'\n  " + string.Join("\n  ", Steps.Select(s => s.ToString()));
    }

    /// <summary>
    /// Passwords to set via M551: none (the default), none (empty, CheckPassword.cs:21) and a real one
    /// </summary>
    private static readonly string[] Passwords = [DuetAPI.Connection.Defaults.Password, string.Empty, SecretPassword];

    /// <summary>
    /// Protected endpoint used by a request step
    /// </summary>
    /// <param name="Name">Name for the failure output</param>
    /// <param name="Method">HTTP method</param>
    /// <param name="Uri">Relative URI</param>
    /// <param name="Body">Request body or null</param>
    /// <param name="ReadWrite">Whether the endpoint requires the read-write policy (null for endpoints without authorization)</param>
    /// <param name="Expected">Status codes of an authorized request</param>
    public sealed record Endpoint(string Name, string Method, string Uri, string? Body, bool? ReadWrite, HttpStatusCode[] Expected);

    private static readonly HttpStatusCode[] Ok = [HttpStatusCode.OK];

    /// <summary>
    /// Endpoints of RepRapFirmwareEndpoints.Map and MachineEndpoints.Map with harmless arguments, plus two unmapped paths
    /// </summary>
    public static readonly Endpoint[] Endpoints =
    [
        new("rr_model", "GET", "rr_model?key=state.status", null, false, Ok),
        new("rr_model f", "GET", "rr_model?flags=d99fn", null, false, Ok),
        new("rr_reply", "GET", "rr_reply", null, false, Ok),
        new("rr_upload GET", "GET", "rr_upload", null, false, Ok),
        new("rr_filelist", "GET", "rr_filelist?dir=0:/sys", null, false, Ok),
        new("rr_files", "GET", "rr_files?dir=0:/sys", null, false, Ok),
        new("rr_download", "GET", "rr_download?name=0:/sys/fuzz.txt", null, false, Ok),
        new("rr_fileinfo", "GET", "rr_fileinfo?name=0:/gcodes/fuzz.gcode", null, false, Ok),
        new("rr_thumbnail", "GET", "rr_thumbnail?name=0:/gcodes/fuzz.gcode&offset=1", null, false, Ok),
        new("rr_gcode", "GET", "rr_gcode?gcode=M115", null, true, Ok),
        new("rr_mkdir", "GET", "rr_mkdir?dir=0:/gcodes/session-fuzz", null, true, Ok),
        new("rr_delete", "GET", "rr_delete?name=0:/gcodes/session-fuzz", null, true, Ok),
        new("rr_move", "GET", "rr_move?old=0:/gcodes/missing.g&new=0:/gcodes/missing2.g", null, true, Ok),
        new("rr_upload POST", "POST", "rr_upload?name=0:/gcodes/session-upload.g", "G1 X1\n", true, Ok),
        new("noop", "GET", "machine/noop", null, false, [HttpStatusCode.NoContent]),
        new("model", "GET", "machine/model", null, false, Ok),
        new("status", "GET", "machine/status", null, false, Ok),
        new("code", "POST", "machine/code", "M115", true, Ok),
        new("file GET", "GET", "machine/file/0%3A%2Fsys%2Ffuzz.txt", null, false, Ok),
        new("fileinfo", "GET", "machine/fileinfo/0%3A%2Fgcodes%2Ffuzz.gcode", null, false, Ok),
        new("directory GET", "GET", "machine/directory/0%3A%2Fsys", null, false, Ok),
        new("directory PUT", "PUT", "machine/directory/0%3A%2Fgcodes%2Fsession-dir", null, true, [HttpStatusCode.Created]),
        new("file PUT", "PUT", "machine/file/0%3A%2Fgcodes%2Fsession-put.g", "G1 X2\n", true, [HttpStatusCode.Created]),
        new("file DELETE", "DELETE", "machine/file/0%3A%2Fgcodes%2Fsession-put.g", null, true, [HttpStatusCode.NoContent, HttpStatusCode.NotFound]),
        // Unmapped paths: no authorization, the static file middleware answers 404 (Program.cs:104-135, FallbackMiddleware.cs:32)
        new("unknown rr_", "GET", "rr_unknown", null, null, [HttpStatusCode.NotFound]),
        new("index", "GET", "", null, null, [HttpStatusCode.NotFound])
    ];

    private static Sequence Generate(Random random)
    {
        int numSteps = random.Next(3, 21);
        Step[] steps = new Step[numSteps];
        for (int i = 0; i < numSteps; i++)
        {
            Op op = random.Next(100) switch
            {
                < 10 => Op.RrConnect,
                < 15 => Op.RrDisconnect,
                < 30 => Op.MachineConnect,
                < 37 => Op.MachineDisconnect,
                < 75 => Op.Request,
                < 82 => Op.OpenWebSocket,
                < 87 => Op.CloseWebSocket,
                < 92 => Op.Idle,
                < 95 => Op.SetPassword,
                _ => Op.AddReadOnlySession
            };
            KeyChoice key = random.Next(100) switch
            {
                < 30 => KeyChoice.None,
                < 65 => KeyChoice.Own,
                < 75 => KeyChoice.Other,
                < 83 => KeyChoice.Stale,
                < 90 => KeyChoice.Random,
                < 97 => KeyChoice.Garbage,
                _ => KeyChoice.Empty
            };
            string keyValue = key switch
            {
                KeyChoice.Random => RandomKey(random),
                KeyChoice.Garbage => RandomGarbage(random),
                _ => string.Empty
            };
            PasswordChoice password = (PasswordChoice)random.Next(4);
            int endpoint = (op == Op.SetPassword) ? random.Next(Passwords.Length) : random.Next(Endpoints.Length);
            steps[i] = new Step(op, random.Next(NumClients), key, keyValue, password, endpoint, random.Next(4));
        }
        return new Sequence(random.Next(Passwords.Length), steps);
    }

    private static string RandomKey(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes).ToString("N");
    }

    private static string RandomGarbage(Random random)
    {
        const string chars = "abcdefABCDEF0123456789 -_,;:\"'{}[]()<>=%&*+!?#$|\\/.~`^";
        StringBuilder result = new();
        for (int i = random.Next(1, 48); i > 0; i--)
        {
            result.Append(chars[random.Next(chars.Length)]);
        }
        return result.ToString().Trim() is { Length: > 0 } trimmed ? trimmed : "x";
    }

    private static IEnumerable<Sequence> Shrink(Sequence sequence)
    {
        // Drop the second half, then single steps from the end
        if (sequence.Steps.Length > 1)
        {
            yield return sequence with { Steps = sequence.Steps[..(sequence.Steps.Length / 2)] };
        }
        for (int i = sequence.Steps.Length - 1; i >= 0; i--)
        {
            yield return sequence with { Steps = [.. sequence.Steps[..i], .. sequence.Steps[(i + 1)..]] };
        }
        if (sequence.InitialPassword != 0)
        {
            yield return sequence with { InitialPassword = 0 };
        }
    }
    #endregion

    #region Model
    /// <summary>
    /// Session that DWS should hold
    /// </summary>
    private sealed class ModelSession
    {
        /// <summary>
        /// Session key if the test knows it, i.e. sessions made via /machine/connect or injected read-only sessions
        /// </summary>
        public string? Key { get; init; }

        /// <summary>
        /// IP address the session is bound to for requests without X-Session-Key, or empty
        /// </summary>
        public string IpAddress { get; init; } = string.Empty;

        /// <summary>
        /// Origin of the DCS user session, the address of the client that created it
        /// </summary>
        public string Origin { get; init; } = string.Empty;

        public bool ReadWrite { get; init; } = true;

        public int NumWebSockets { get; set; }

        public string Describe() => $"{(ReadWrite ? "RW" : "RO")} origin {Origin}" + (Key is not null ? $" key {Key}" : string.Empty) + (IpAddress != string.Empty ? $" ip {IpAddress}" : string.Empty) + (NumWebSockets > 0 ? $" ws {NumWebSockets}" : string.Empty);
    }

    /// <summary>
    /// WebSocket that a client holds open
    /// </summary>
    private sealed class OpenSocket(ClientWebSocket webSocket, ModelSession? session, string? key)
    {
        public ClientWebSocket WebSocket { get; } = webSocket;
        public ModelSession? Session { get; } = session;
        public string? Key { get; } = key;
    }

    private sealed class Model
    {
        public string Password = DuetAPI.Connection.Defaults.Password;
        public readonly List<ModelSession> Sessions = [];
        public readonly List<string>[] KnownKeys = [.. Enumerable.Range(0, NumClients).Select(_ => new List<string>())];
        public readonly List<string> StaleKeys = [];
        public readonly List<OpenSocket>[] WebSockets = [.. Enumerable.Range(0, NumClients).Select(_ => new List<OpenSocket>())];

        public bool PasswordSet => Password != DuetAPI.Connection.Defaults.Password && Password != string.Empty;

        public bool CheckPassword(string? password) => !PasswordSet || (password ?? string.Empty) == Password;

        public void Remove(ModelSession session)
        {
            Sessions.Remove(session);
            if (session.Key is not null)
            {
                StaleKeys.Add(session.Key);
            }
        }

        public string Describe() => (PasswordSet ? $"password '{Password}'" : "no password") + "\n    " + string.Join("\n    ", Sessions.Select(s => s.Describe()));
    }

    /// <summary>
    /// Value of the X-Session-Key header (or the sessionKey parameter) of a step, null to leave it out
    /// </summary>
    private static string? ResolveKey(Model model, Step step)
    {
        List<string> own = model.KnownKeys[step.Client];
        List<string> other = [.. Enumerable.Range(0, NumClients).Where(c => c != step.Client).SelectMany(c => model.KnownKeys[c])];
        return step.Key switch
        {
            KeyChoice.None => null,
            KeyChoice.Own when own.Count > 0 => own[step.Pick % own.Count],
            KeyChoice.Other when other.Count > 0 => other[step.Pick % other.Count],
            KeyChoice.Stale when model.StaleKeys.Count > 0 => model.StaleKeys[step.Pick % model.StaleKeys.Count],
            KeyChoice.Garbage => step.KeyValue,
            KeyChoice.Empty => string.Empty,
            KeyChoice.Random => step.KeyValue,
            // A key that is not available yet becomes a key that was never valid
            _ => "00000000000000000000000000000000"
        };
    }

    /// <summary>
    /// Authenticate a request like SessionKeyAuthenticationHandler.HandleAuthenticateAsync and apply its side effect
    /// </summary>
    /// <returns>Session the request is authenticated with or null</returns>
    private static ModelSession? Authenticate(Model model, int client, string? key)
    {
        if (key is not null)
        {
            // SessionKeyAuthenticationHandler.cs:40-54, else Fail at line 84
            return model.Sessions.FirstOrDefault(s => s.Key == key);
        }

        // SessionKeyAuthenticationHandler.cs:58-82
        string ipAddress = ClientAddress(client).ToString();
        ModelSession? session = model.Sessions.FirstOrDefault(s => s.IpAddress == ipAddress);
        if (session is null && !model.PasswordSet)
        {
            session = new ModelSession { IpAddress = ipAddress, Origin = ipAddress };
            model.Sessions.Add(session);
        }
        return session;
    }
    #endregion

    [Test]
    public void Sessions()
    {
        Fuzzer.Run(Generate, sequence => RunAsync(sequence).GetAwaiter().GetResult(), sequence => sequence.ToString(), Shrink, Fuzzer.Scale(50), TimeSpan.FromSeconds(30));
    }

    private async Task RunAsync(Sequence sequence)
    {
        Model model = new();
        int logStart = Dws.Log.Count;
        try
        {
            // Start without sessions
            await SetPasswordAsync(Passwords[sequence.InitialPassword]);
            model.Password = Passwords[sequence.InitialPassword];
            await ClearSessionsAsync();

            for (int i = 0; i < sequence.Steps.Length; i++)
            {
                Step step = sequence.Steps[i];
                try
                {
                    await RunStepAsync(model, step);
                }
                catch (FuzzFailure)
                {
                    throw;
                }
                catch (Exception e) when (e is HttpRequestException or WebSocketException or OperationCanceledException or IOException)
                {
                    throw new FuzzFailure("transport", $"Step {i} {step} failed: {e}");
                }

                LogEntry[] unhandled = [.. Dws.Log.Since(logStart).Where(entry => entry.IsUnhandledException)];
                if (unhandled.Length > 0)
                {
                    throw new FuzzFailure("unhandled exception", $"after step {i} {step}:\n{string.Join('\n', unhandled.Select(e => e.ToString()))}");
                }
                await CheckUserSessionsAsync(model, $"after step {i} {step}", step.Op == Op.Idle);
            }
        }
        finally
        {
            foreach (List<OpenSocket> sockets in model.WebSockets)
            {
                foreach (OpenSocket socket in sockets)
                {
                    await CloseSocketAsync(socket);
                }
                sockets.Clear();
            }
        }
    }

    private async Task RunStepAsync(Model model, Step step)
    {
        HttpClient client = _clients[step.Client];
        string ipAddress = ClientAddress(step.Client).ToString();
        string? key = ResolveKey(model, step);

        switch (step.Op)
        {
            case Op.RrConnect:
            case Op.MachineConnect:
                {
                    string? password = step.Password switch
                    {
                        PasswordChoice.Right => model.Password,
                        PasswordChoice.Wrong => "wrong",
                        PasswordChoice.Default => DuetAPI.Connection.Defaults.Password,
                        _ => null
                    };
                    string uri = (step.Op == Op.RrConnect ? "rr_connect" : "machine/connect") + (password is null ? string.Empty : $"?password={Uri.EscapeDataString(password)}");
                    using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, uri, key);
                    string body = await response.Content.ReadAsStringAsync();

                    // Both endpoints allow anonymous access but the authentication handler runs first
                    Authenticate(model, step.Client, key);
                    bool success = model.CheckPassword(password);
                    if (step.Op == Op.RrConnect)
                    {
                        Expect(response.StatusCode == HttpStatusCode.OK, step, $"HTTP {response.StatusCode}", model);
                        using JsonDocument json = JsonDocument.Parse(body);
                        Expect(json.RootElement.GetProperty("err").GetInt32() == (success ? 0 : 1) && json.RootElement.GetProperty("isEmulated").GetBoolean(), step, body, model);
                        if (success)
                        {
                            // RepRapFirmwareEndpoints.cs:107-114, board MB6HC of the fake firmware
                            Expect(json.RootElement.GetProperty("apiLevel").GetInt32() == 1 && json.RootElement.GetProperty("boardType").GetString() == "duet3mb6hc100", step, body, model);
                            model.Sessions.Add(new ModelSession { IpAddress = ipAddress, Origin = ipAddress });
                        }
                    }
                    else if (success)
                    {
                        Expect(response.StatusCode == HttpStatusCode.OK, step, $"HTTP {response.StatusCode} {body}", model);
                        using JsonDocument json = JsonDocument.Parse(body);
                        string newKey = json.RootElement.GetProperty("sessionKey").GetString()!;
                        Expect(newKey.Length == 32 && model.Sessions.All(s => s.Key != newKey) && !model.StaleKeys.Contains(newKey), step, $"new key {newKey}", model);
                        model.Sessions.Add(new ModelSession { Key = newKey, Origin = ipAddress });
                        model.KnownKeys[step.Client].Add(newKey);
                    }
                    else
                    {
                        Expect(response.StatusCode == HttpStatusCode.Forbidden, step, $"HTTP {response.StatusCode} {body}", model);
                    }
                    break;
                }

            case Op.RrDisconnect:
            case Op.MachineDisconnect:
                {
                    using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, step.Op == Op.RrDisconnect ? "rr_disconnect" : "machine/disconnect", key);
                    Expect(response.StatusCode == HttpStatusCode.NoContent, step, $"HTTP {response.StatusCode}", model);
                    ModelSession? session = Authenticate(model, step.Client, key);
                    if (session is not null)
                    {
                        model.Remove(session);
                    }
                    break;
                }

            case Op.Request:
                {
                    Endpoint endpoint = Endpoints[step.Endpoint];
                    using HttpContent? content = (endpoint.Body is null) ? null : new StringContent(endpoint.Body);
                    using HttpResponseMessage response = await SendAsync(client, new HttpMethod(endpoint.Method), endpoint.Uri, key, content);
                    string body = await response.Content.ReadAsStringAsync();

                    ModelSession? session = Authenticate(model, step.Client, key);
                    HttpStatusCode[] expected = (endpoint.ReadWrite is null) ? endpoint.Expected
                        : (session is null) ? [HttpStatusCode.Unauthorized]
                        : (endpoint.ReadWrite.Value && !session.ReadWrite) ? [HttpStatusCode.Forbidden]
                        : endpoint.Expected;
                    Expect(expected.Contains(response.StatusCode), step, $"HTTP {(int)response.StatusCode} {body}, expected {string.Join(" or ", expected.Select(s => (int)s))}", model);
                    break;
                }

            case Op.OpenWebSocket:
                {
                    if (model.WebSockets[step.Client].Count >= 2)
                    {
                        // Keep the number of sockets small
                        break;
                    }

                    // The handshake request has no X-Session-Key header, so the authentication handler looks for an IP session first
                    string query = (key is null) ? "machine" : "machine?sessionKey=" + Uri.EscapeDataString(key);
                    (ClientWebSocket? webSocket, HttpStatusCode status) = await TryConnectWebSocketAsync(step.Client, query);

                    Authenticate(model, step.Client, null);
                    ModelSession? session = string.IsNullOrEmpty(key) ? null : model.Sessions.FirstOrDefault(s => s.Key == key);
                    bool accepted = string.IsNullOrEmpty(key) ? !model.PasswordSet : session is not null;
                    if (webSocket is not null)
                    {
                        // DWS sends the full object model first (WebSocketEndpoint.cs:210-213)
                        string firstMessage = await ReceiveTextAsync(webSocket) ?? "closed";
                        using JsonDocument json = JsonDocument.Parse(firstMessage);
                        OpenSocket socket = new(webSocket!, session, string.IsNullOrEmpty(key) ? null : key);
                        model.WebSockets[step.Client].Add(socket);
                        if (session is not null)
                        {
                            session.NumWebSockets++;
                        }
                        Expect(accepted && json.RootElement.TryGetProperty("state", out _), step, "WebSocket accepted", model);
                    }
                    else
                    {
                        // WebSocketEndpoint.cs:88 and 121
                        Expect(!accepted && status == HttpStatusCode.Forbidden, step, $"WebSocket rejected with HTTP {(int)status}", model);
                    }
                    break;
                }

            case Op.CloseWebSocket:
                {
                    List<OpenSocket> sockets = model.WebSockets[step.Client];
                    if (sockets.Count > 0)
                    {
                        OpenSocket socket = sockets[step.Pick % sockets.Count];
                        sockets.Remove(socket);
                        await CloseSocketAsync(socket);
                        if (socket.Session is not null)
                        {
                            socket.Session.NumWebSockets--;
                        }
                    }
                    break;
                }

            case Op.Idle:
                {
                    List<ModelSession> expiring = [.. model.Sessions.Where(s => s.NumWebSockets == 0)];
                    if (expiring.Count > DcsBacklog)
                    {
                        // More sessions expiring at once than the DCS IPC backlog takes leave user sessions behind in DCS, which
                        // WebServerFindings.ExpiredSessionsAreRemovedFromDcs reproduces. Skip the step so that the fuzzer can find other bugs
                        break;
                    }
                    foreach (ModelSession session in expiring)
                    {
                        model.Remove(session);
                    }
                    ExpireIdleSessions();
                    break;
                }

            case Op.SetPassword:
                await SetPasswordAsync(Passwords[step.Endpoint]);
                model.Password = Passwords[step.Endpoint];
                break;

            case Op.AddReadOnlySession:
                {
                    using CommandConnection connection = new();
                    await connection.ConnectAsync(Dcs.SocketPath);
                    int sessionId = await connection.AddUserSessionAsync(AccessLevel.ReadOnly, SessionType.HTTP, ipAddress);
                    string newKey = SessionStorage.MakeSessionKey(sessionId, string.Empty, false);
                    model.Sessions.Add(new ModelSession { Key = newKey, Origin = ipAddress, ReadWrite = false });
                    model.KnownKeys[step.Client].Add(newKey);
                    break;
                }
        }
    }

    /// <summary>
    /// Close a WebSocket and wait until DWS has released it, so that the next step sees the session without it
    /// </summary>
    private async Task CloseSocketAsync(OpenSocket socket)
    {
        // DWS logs this when its Process method returns (WebSocketEndpoint.cs:264), then WebSocketEndpoint.Get unregisters the key (line 139),
        // which SessionStorage logs if the session still exists (SessionStorage.cs:349)
        int disconnected = Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket disconnected from"));
        string unregistered = $"Session {socket.Key} unregistered a WebSocket connection";
        int unregisteredCount = Dws.Log.Entries.Count(e => e.Message == unregistered);
        bool sessionAlive = socket.Key is not null && SessionStorage.GetTicketFromKey(socket.Key) is not null;

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            await socket.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The server may have closed it already
        }

        await WaitUntilAsync(() => Dws.Log.Entries.Count(e => e.Message.Contains("WebSocket disconnected from")) > disconnected, "DWS did not close the WebSocket");
        if (sessionAlive)
        {
            await WaitUntilAsync(() => Dws.Log.Entries.Count(e => e.Message == unregistered) > unregisteredCount, "DWS did not unregister the WebSocket");
        }
        socket.WebSocket.Dispose();
    }

    /// <summary>
    /// The HTTP user sessions in the DCS object model must be the sessions of the model, compared by origin and access level
    /// </summary>
    private async Task CheckUserSessionsAsync(Model model, string context, bool eventually)
    {
        string[] expected = [.. model.Sessions.Select(s => $"{s.Origin} {(s.ReadWrite ? AccessLevel.ReadWrite : AccessLevel.ReadOnly)}").Order()];
        string[] actual = [];
        DateTime end = DateTime.Now.AddSeconds(eventually ? 5 : 0);
        do
        {
            actual = [.. (await GetUserSessionsAsync()).Where(s => s.SessionType == SessionType.HTTP).Select(s => $"{s.Origin} {s.AccessLevel}").Order()];
            if (actual.SequenceEqual(expected))
            {
                return;
            }
            await Task.Delay(10);
        }
        while (DateTime.Now < end);
        throw new FuzzFailure("user sessions", $"{context}: DCS has [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]\n  model: {model.Describe()}");
    }

    private static void Expect(bool condition, Step step, string observed, Model model)
    {
        if (!condition)
        {
            throw new FuzzFailure($"response of {step.Op}", $"{step}: {observed}\n  model: {model.Describe()}");
        }
    }
}
