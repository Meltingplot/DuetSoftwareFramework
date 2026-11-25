using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Values in the object model that DSF must still be able to send to new clients
/// </summary>
[TestFixture]
public class ObjectModelSerialization : LabTest
{
    private const string Utf8Global = "labTestUtf8";

    /// <summary>
    /// A global holding invalid UTF-8 made the object model of DSF unserializable: new WebSockets were closed with 1001, so DWC did not come back after
    /// a reload, until the global got another value (DSF #185, finding O4)
    /// </summary>
    /// <remarks>
    /// RRF cuts strings by bytes: take() of a heap string copies the first n bytes (RRF ExpressionParser.cpp:1117-1123), so take("é", 1) keeps
    /// the first byte of the two that UTF-8 needs for é. RRF writes it into the JSON of the key global as it is (ObjectModel.cpp:977-978; PutJson in
    /// RRFLibraries SafeVsnprintf.cpp escapes only quotes, backslashes and control characters). DSF keeps a global as JsonElement, and its converter
    /// wrote GetRawText(), which throws for invalid UTF-8; since #185 it writes with WriteTo (DuetAPI/ObjectModel/Base/JsonModelDictionary.cs:384),
    /// which puts U+FFFD in place of the byte. An existing patch subscription worked either way (PR #185), so the session of the test sees the value
    /// in both builds; a new session needs the full model first (DuetHttpClient/Connector/RestConnector.cs:203-235). Unfixed: the new session never
    /// gets a model, because DWS closes its WebSocket with 1001 "DCS has been stopped" (DuetWebServer/Endpoints/WebSocketEndpoint.cs:229-238).
    /// The cut by bytes is read from the code and was not run on RRF (PR #185). If RRF keeps the whole é, the global is valid UTF-8 and the test is
    /// inconclusive. The global is set back to an empty string in any case, which ends the state on the unfixed build too
    /// </remarks>
    [Test]
    public async Task GlobalWithInvalidUtf8KeepsTheModelServable()
    {
        await G1Helpers.SetGlobalAsync(Session, Utf8Global, "\"\"");
        await Lab.WaitForModelAsync(Session, model => GetString(model, Utf8Global) == string.Empty, TimeSpan.FromSeconds(30), $"global.{Utf8Global} = \"\" in the model");

        bool loaded;
        string? fromSession, fromNewSession = null, cut;
        try
        {
            string reply = await SendCodeAsync($"set global.{Utf8Global} = take(\"é\", 1)", 30);
            Assert.That(reply, Does.Not.Contain("Error"), "RRF did not take the global");
            cut = (await SendCodeAsync($"echo {{global.{Utf8Global} == \"é\"}}", 30)).Trim();

            await Lab.TryWaitForModelAsync(Session, model => !string.IsNullOrEmpty(GetString(model, Utf8Global)), TimeSpan.FromSeconds(30));
            fromSession = Lab.Read(Session, model => GetString(model, Utf8Global));

            await using DuetHttpSession newSession = await Lab.ConnectAsync();
            loaded = await Lab.TryWaitForModelAsync(newSession, model => model.State.Status != MachineStatus.Starting && model.Global.ContainsKey(Utf8Global),
                                                    TimeSpan.FromSeconds(30));
            if (loaded)
            {
                fromNewSession = Lab.Read(newSession, model => GetString(model, Utf8Global));
            }
        }
        finally
        {
            await G1Helpers.TryCleanUpAsync(Session, $"set global.{Utf8Global} = \"\"");
        }

        TestContext.Progress.WriteLine($"global.{Utf8Global} == \"é\": {cut}; session of the test: {Describe(fromSession)}, new session: {Describe(fromNewSession)}");
        if (cut == "true")
        {
            Assert.Inconclusive("RRF kept the whole é, so the global holds valid UTF-8 and shows nothing");
        }
        Assert.Multiple(() =>
        {
            Assert.That(cut, Is.EqualTo("false"), "RRF did not compare the global");
            Assert.That(loaded, Is.True, "A new session did not get the object model within 30 s");
            Assert.That(fromNewSession, Is.EqualTo("�"), "The new session does not show the replacement character");
        });
    }

    private static string? GetString(ObjectModel model, string name) =>
        model.Global.TryGetValue(name, out JsonElement? value) && value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;

    private static string Describe(string? text) => (text is null) ? "none" : $"\"{text}\" ({string.Join(' ', text.Select(c => $"U+{(int)c:X4}"))})";
}
