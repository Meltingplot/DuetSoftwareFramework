using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Delete requests that name the root of the virtual SD card. <c>DELETE /machine/file/0%3A%2F?recursive=true</c> deleted the whole SD directory, config.g
/// included (H1); the same went for rr_delete and M472 and for other names of the root (fixed in DSF #136 for 3.7.0+mp.14, replaced by upstream 61df011ec in
/// 3.7.0+mp.15). These tests send only NON-recursive requests: <c>Directory.Delete(path, false)</c> cannot delete a directory that is not empty
/// (MachineEndpoints.cs:374, RepRapFirmwareEndpoints.cs:379, MCodeHandler.cs:957-959 without R), and 0:/ never is. Without the fix the root is therefore not
/// deleted either, only the answer differs; the tests check that answer and that nothing was deleted
/// </summary>
/// <remarks>
/// Guards, before any request names the root:
/// - The same request form is sent to a folder of the test that is not empty and must leave it in place. If it deletes it, the form is recursive and the
///   test fails before it names the root. The same form must delete an empty folder of the test, so that a refusal at the root means something
/// - Each name of the root is listed first (GET /machine/directory, resolved by DCS like every request here, EndpointHelper.ResolvePathAsync and
///   Commands/Files/ResolvePath.cs). A name that does not list 0:/ is not sent
/// - After every request the listing of 0:/ and 0:/sys/config.g are checked, the test stops at the first difference
/// Builds: DELETE and rr_delete are answered by DuetWebServer, which comes with the image unless the side-load includes it, and resolve through DCS.
/// M472 is answered by DCS. The tests print the DWS package and whether DuetWebServer.dll is side-loaded
/// </remarks>
[TestFixture, Category("Approval"), Explicit("Needs Tim's approval for each run")]
public class RootDeletion : LabTest
{
    private const string GuardDirectory = "0:/gcodes/lab-tests/root-guard";
    private const string GuardFile = $"{GuardDirectory}/sub/keep.g";

    /// <summary>
    /// Names of the root of drive 0, from the inventory of #136 (H1 and the security review: trailing separators and relative segments)
    /// </summary>
    private static readonly string[] RootNames = ["0:/", "0:", "0:/.", "0:/./", "/", "0:/sys/..", "0:/sys/../", "0://"];

    /// <summary>
    /// Answers that refuse the root: upstream 61df011ec (DSF 3.7.0+mp.15 and later) and DSF #136 (3.7.0+mp.14)
    /// </summary>
    private const string RootRefusal = "Cannot delete the root directory of a volume", Mp14RootRefusal = "Delete root folder is not allowed";

    private RawMachineSession? _http;

    private RawMachineSession Http => _http!;

    [SetUp]
    public async Task PrepareAsync()
    {
        string sideLoaded = (await Lab.RunOnSbcAsync("findmnt -n -o SOURCE /opt/dsf/bin/DuetWebServer.dll || echo no")).Trim();
        string package = (await Lab.RunOnSbcAsync("dpkg-query -W -f='${Version}' duetwebserver 2>/dev/null || echo unknown")).Trim();
        TestContext.Progress.WriteLine($"DuetWebServer package {package}, DuetWebServer.dll side-loaded: {sideLoaded}; DCS {Lab.Read(Session, model => model.SBC?.DSF.Version)}");

        _http = await RawMachineSession.ConnectAsync();
        await Lab.UploadAsync(Session, GuardFile, "; lab printer test: a folder that is not empty, a non-recursive delete must keep it\n");
    }

    [TearDown]
    public async Task CleanUpAsync()
    {
        if (_http is not null)
        {
            await _http.DisposeAsync();
            _http = null;
        }
        if (Lab.IsConfigured)
        {
            await using DuetHttpClient.DuetHttpSession session = await Lab.ConnectAsync();
            foreach (string item in new[] { GuardFile, $"{GuardDirectory}/sub", $"{GuardDirectory}/empty", GuardDirectory, "0:/gcodes/lab-tests" })
            {
                try
                {
                    await Lab.DeleteAsync(session, item);
                }
                catch (HttpRequestException e)
                {
                    // 0:/gcodes/lab-tests may still hold files of another test, which deletes them itself
                    TestContext.Progress.WriteLine($"Kept {item}: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// #136a: DELETE /machine/file without <c>recursive</c> (default false, MachineEndpoints.cs:354). Fixed: 500 "Cannot delete the root directory of a
    /// volume" (MachineEndpoints.cs:365-369, EndpointHelper.IsVolumeRootAsync :120-129) or, with the DWS of 3.7.0+mp.14, 500 "Delete root folder is not
    /// allowed". Unfixed: 500 with the IOException of Directory.Delete on a directory that is not empty (EndpointHelper.cs:171-172), so the text tells.
    /// Deterministic
    /// </summary>
    [Test]
    public async Task NonRecursiveDeleteRequestOfTheRootIsRefused()
    {
        await RunGuardedAsync("DELETE /machine/file", async path =>
        {
            (HttpStatusCode status, string body) = await Http.SendAsync(HttpMethod.Delete, $"machine/file/{Uri.EscapeDataString(path)}");
            return (status == HttpStatusCode.NoContent, $"{(int)status} {body}");
        }, (name, answer, _) => answer.Contains(RootRefusal) || answer.Contains(Mp14RootRefusal));
    }

    /// <summary>
    /// #136b: <c>rr_delete?name=</c> without <c>recursive</c> (default "no", RepRapFirmwareEndpoints.cs:359). The answer is <c>{"err":1}</c> with and
    /// without the fix, only the DWS log tells: fixed "Refusing to delete volume root directory &lt;name&gt;" (RepRapFirmwareEndpoints.cs:370-374) or, with
    /// the DWS of 3.7.0+mp.14, "Failed to handle rr_delete request" with "Delete root folder is not allowed"; unfixed "Failed to handle rr_delete request"
    /// with the IOException of Directory.Delete (:392-395). The answer has no name, so a log line of the mp.14 text counts for the name just sent. Deterministic
    /// </summary>
    [Test]
    public async Task NonRecursiveRrDeleteOfTheRootIsRefused()
    {
        string connect = await Http.SendRepRapFirmwareRequestAsync($"rr_connect?password={Uri.EscapeDataString(DuetAPI.Connection.Defaults.Password)}");
        Assert.That(connect, Does.Contain("\"err\":0"), $"rr_connect failed: {connect}");
        try
        {
            await RunGuardedAsync("rr_delete", async path =>
            {
                string answer = await Http.SendRepRapFirmwareRequestAsync($"rr_delete?name={Uri.EscapeDataString(path)}");
                return (answer.Contains("\"err\":0"), answer);
            }, (name, answer, journal) => answer.Contains("\"err\":1") &&
                (journal.Contains($"Refusing to delete volume root directory {name} ") || journal.Contains(Mp14RootRefusal)));
        }
        finally
        {
            await Http.SendRepRapFirmwareRequestAsync("rr_disconnect");
        }
    }

    /// <summary>
    /// #136c: M472 without R, handled by DCS (MCodeHandler.cs:939-970). Fixed: "Cannot delete &lt;name&gt; because it is the root directory of a volume"
    /// (MCodeHandler.cs:943-947, FilePathResolver.IsVolumeRootAsync :282-303) or, with DCS 3.7.0+mp.14, "Delete root folder is not allowed". Unfixed:
    /// "Failed to delete file or directory &lt;name&gt;: &lt;IOException&gt;" (MCodeHandler.cs:967-971). Deterministic
    /// </summary>
    [Test]
    public async Task M472OfTheRootIsRefused()
    {
        await RunGuardedAsync("M472", async path =>
        {
            string reply = await SendCodeAsync($"M472 P\"{path}\"");
            return (!reply.Contains("Error"), reply);
        }, (name, answer, _) => answer.Contains($"Cannot delete {name} because it is the root directory of a volume") || answer.Contains(Mp14RootRefusal));
    }

    /// <summary>
    /// Run the guards, then send the delete request for each name of the root and check the answer
    /// </summary>
    /// <param name="kind">Kind of request for the messages</param>
    /// <param name="delete">Sends the request for a path and returns whether it reported success and its answer</param>
    /// <param name="isRefusal">Whether the answer and the DuetWebServer log since the request refuse the root</param>
    private async Task RunGuardedAsync(string kind, Func<string, Task<(bool Deleted, string Answer)>> delete, Func<string, string, string, bool> isRefusal)
    {
        // Guard 1: the request form deletes an empty folder
        (HttpStatusCode created, string createAnswer) = await Http.SendAsync(HttpMethod.Put, $"machine/directory/{Uri.EscapeDataString($"{GuardDirectory}/empty")}");
        Assert.That(created, Is.EqualTo(HttpStatusCode.Created).Or.EqualTo(HttpStatusCode.NoContent).Or.EqualTo(HttpStatusCode.OK), $"Could not create the empty folder: {createAnswer}");
        (bool deletedEmpty, string emptyAnswer) = await delete($"{GuardDirectory}/empty");
        Assert.That(deletedEmpty && !(await ListAsync(GuardDirectory) ?? []).Contains("empty"), Is.True,
            $"Guard: {kind} did not delete an empty folder ({emptyAnswer}), so a refusal at the root would show nothing. No request named the root");

        // Guard 2: the request form keeps a folder that is not empty
        (bool deletedFull, string fullAnswer) = await delete(GuardDirectory);
        if (deletedFull || (await ListAsync($"{GuardDirectory}/sub") ?? []).Length == 0)
        {
            Assert.Fail($"Guard: {kind} deleted a folder that was not empty ({fullAnswer}), so it deletes recursively. No request named the root");
        }

        string[] root = await ListAsync("0:/") ?? throw new InvalidOperationException("0:/ cannot be listed");
        Assert.That(await HasConfigFileAsync(), Is.True, "0:/sys/config.g is missing before the test");

        List<string> wrong = [], skipped = [];
        foreach (string name in RootNames)
        {
            string[]? listing = await ListAsync(name);
            if (listing is null || !listing.SequenceEqual(root))
            {
                skipped.Add(name);
                continue;
            }

            string cursor = await Lab.GetJournalCursorAsync();
            (bool deleted, string answer) = await delete(name);
            string[]? after = await ListAsync("0:/");
            if (after is null || !after.SequenceEqual(root) || !await HasConfigFileAsync())
            {
                Assert.Fail($"{kind} {name} ({answer}) changed 0:/: before [{string.Join(", ", root)}], after [{string.Join(", ", after ?? [])}]. No further request was sent");
            }

            string journal = kind == "rr_delete" ? await Lab.ReadJournalAsync(cursor, "duetwebserver") : string.Empty;
            TestContext.Progress.WriteLine($"{kind} {name}: {answer.Trim()}");
            if (deleted || !isRefusal(name, answer, journal))
            {
                wrong.Add($"{name}: {answer.Trim()}{(journal.Length > 0 ? $" (DWS log: {string.Join(" | ", journal.Split('\n').Where(line => line.Contains("delete", StringComparison.OrdinalIgnoreCase)))})" : string.Empty)}");
            }
        }

        if (skipped.Count > 0)
        {
            TestContext.Progress.WriteLine($"Not sent, these names do not list 0:/ here: {string.Join(", ", skipped)}");
        }
        Assert.That(skipped, Has.Count.LessThan(RootNames.Length), "No name of the root lists 0:/");
        Assert.That(wrong, Is.Empty, $"{kind} did not refuse the root as such");
    }

    /// <summary>
    /// Names in a folder, sorted, or null if it cannot be listed
    /// </summary>
    private async Task<string[]?> ListAsync(string directory)
    {
        (HttpStatusCode status, string body) = await Http.SendAsync(HttpMethod.Get, $"machine/directory/{Uri.EscapeDataString(directory)}");
        if (status != HttpStatusCode.OK)
        {
            return null;
        }
        using JsonDocument json = JsonDocument.Parse(body);
        return [.. json.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()!).Order(StringComparer.Ordinal)];
    }

    private async Task<bool> HasConfigFileAsync() => (await ListAsync("0:/sys") ?? []).Contains("config.g");
}
