using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace LabPrinterTests;

/// <summary>
/// Helpers of the tests that restart RRF or DCS or run DSF processes next to the running ones: FirmwareRestart, ControlServerRestart,
/// ControlServerStartup and PluginServices
/// </summary>
public static partial class RestartHelpers
{
    /// <summary>
    /// A line of the journal and the time journald got it, in seconds of the SBC clock
    /// </summary>
    public readonly record struct JournalLine(double Time, string Text);

    /// <summary>
    /// Lines with which systemd reports that the DCS process has ended: after a stop job ("Deactivated successfully", or "Main process exited" and "Failed with
    /// result" after a SIGKILL), and after DCS stopped on its own, e.g. after M999, where no stop job runs
    /// </summary>
    public static readonly string[] ControlServerEndedLines =
    [
        "duetcontrolserver.service: Deactivated successfully", "duetcontrolserver.service: Main process exited", "duetcontrolserver.service: Failed with result"
    ];

    [GeneratedRegex(@"^(\d+\.\d+) (.*)$")]
    private static partial Regex ShortUnixLineRegex();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiColorRegex();

    /// <summary>
    /// Read the log of the given units since a cursor with the time of each line (journalctl -o short-unix, in microseconds). Without units it reads the log
    /// of DCS and the systemd messages about its unit, as <see cref="Lab.ReadJournalAsync(string)"/>
    /// </summary>
    public static async Task<JournalLine[]> ReadTimedJournalAsync(string cursor, params string[] units)
    {
        string[] read = (units.Length > 0) ? units : ["duetcontrolserver"];
        string output = await Lab.RunOnSbcAsync($"sudo -n journalctl {string.Join(' ', read.Select(unit => $"-u {unit}"))} --after-cursor='{cursor}' -o short-unix --no-pager");
        List<JournalLine> lines = [];
        foreach (string line in output.Split('\n'))
        {
            Match match = ShortUnixLineRegex().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                lines.Add(new JournalLine(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value));
            }
        }
        return [.. lines];
    }

    /// <summary>
    /// Time of the first line at or after a time that contains one of the texts, or null if there is none
    /// </summary>
    public static double? FirstTime(IEnumerable<JournalLine> lines, double from, params string[] texts) =>
        lines.Where(line => line.Time >= from && texts.Any(text => line.Text.Contains(text, StringComparison.Ordinal))).Select(line => (double?)line.Time).FirstOrDefault();

    /// <summary>
    /// Number of lines that contain a text
    /// </summary>
    public static int Count(IEnumerable<JournalLine> lines, string text) => lines.Count(line => line.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// Remove the color codes that DSF writes when its output does not go to the journal (CommonLogFormatter.cs:51-52)
    /// </summary>
    public static string StripColors(string text) => AnsiColorRegex().Replace(text, string.Empty);

    /// <summary>
    /// Read the errors that DCS logged since a cursor: journalctl -p err shows the lines of priority 3 (error) and lower numbers only. DSF writes the priority of
    /// each line in front of it when it logs to the journal (CommonLogFormatter.cs:92-100, GetSyslogPriority :110-121), before #118 every line had priority 6
    /// </summary>
    public static Task<string> ReadJournalErrorsAsync(string cursor) =>
        Lab.RunOnSbcAsync($"sudo -n journalctl -u duetcontrolserver --after-cursor='{cursor}' -p err -o cat --no-pager");

    /// <summary>
    /// Send POST /machine/{path} with a plain text body in its own HTTP session, for the requests DuetHttpClient has no call for, as
    /// PluginPermissions.UninstallSystemPackageAsync does
    /// </summary>
    public static async Task<(HttpStatusCode Status, string Body)> PostMachineAsync(string path, string body)
    {
        using HttpClient client = new() { BaseAddress = Lab.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
        string sessionKey;
        using (HttpResponseMessage connect = await client.GetAsync($"machine/connect?password={HttpUtility.UrlEncode(DuetAPI.Connection.Defaults.Password)}"))
        {
            connect.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await connect.Content.ReadAsStringAsync());
            sessionKey = json.RootElement.GetProperty("sessionKey").GetString()!;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"machine/{path}") { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            request.Headers.Add("X-Session-Key", sessionKey);
            using HttpResponseMessage response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            using HttpRequestMessage disconnect = new(HttpMethod.Get, "machine/disconnect");
            disconnect.Headers.Add("X-Session-Key", sessionKey);
            using HttpResponseMessage _ = await client.SendAsync(disconnect);
        }
    }

    /// <summary>
    /// IPC client for a Python script that runs as root on the SBC, as in AbortsCrossingMacroRequests: root gets every command (Connection.cs:83-91), one JSON
    /// object per message (ipc.md). simple_code sends a code and returns what became of it and how long it took: "reply: ..." with the reply, "empty reply"
    /// for an empty one (SimpleCode leaves a cancelled code out of its reply, Commands/Generic/SimpleCode.cs:189-203), "error: ..." for an
    /// error of DCS, "closed: ..." when DCS closed the connection and "no reply" after the timeout
    /// </summary>
    public const string IpcClient = """
        import json, socket, sys, time

        class Ipc:
            def __init__(self):
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect("/run/dsf/dcs.sock")
                self.data = b""
                init = self.receive(5)
                self.send({"mode": "Command", "version": init["version"]})
                if not self.receive(5).get("success"):
                    raise RuntimeError("DCS refused the command mode")

            def send(self, message):
                self.sock.sendall(json.dumps(message).encode())

            def receive(self, timeout):
                deadline = time.monotonic() + timeout
                while True:
                    try:
                        text = self.data.decode().lstrip()
                        message, end = json.JSONDecoder().raw_decode(text)
                        self.data = text[end:].encode()
                        return message
                    except ValueError:
                        pass
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        return None
                    self.sock.settimeout(remaining)
                    try:
                        chunk = self.sock.recv(65536)
                    except socket.timeout:
                        return None
                    if not chunk:
                        raise ConnectionError("DCS closed the connection")
                    self.data += chunk

        def simple_code(ipc, text, channel, timeout):
            start = time.monotonic()
            try:
                ipc.send({"command": "SimpleCode", "code": text, "channel": channel})
                reply = ipc.receive(timeout)
            except OSError as e:
                return "closed: " + str(e), time.monotonic() - start
            if reply is None:
                return "no reply", time.monotonic() - start
            if reply.get("success"):
                result = (reply.get("result") or "").strip()
                return ("empty reply" if result == "" else "reply: " + result[:80]), time.monotonic() - start
            return "error: %s %s" % (reply.get("errorType"), (reply.get("errorMessage") or "")[:80]), time.monotonic() - start
        """;
}
