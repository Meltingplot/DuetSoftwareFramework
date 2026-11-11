using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DuetAPI.ObjectModel;
using DuetControlServer;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// Strings of the object model that are not valid UTF-8. RRF copies the bytes of a string into its JSON as they are (PutJson in
/// RRFLibraries SafeVsnprintf.cpp escapes only control characters, quotes and backslashes), and it cuts strings by bytes: an index, take, drop
/// and the length limit of a concatenation can split a character (ExpressionParser.cpp). So a global can hold a string that is not valid UTF-8
/// although every code was. System.Text.Json parses such a string, and a JsonElement keeps its bytes. Finding O4 of design/open-findings.md
/// </summary>
[TestFixture]
public class InvalidUtf8
{
    private static readonly FieldInfo DeserializationFailedField = typeof(ObjectModel).GetField(nameof(ObjectModel.OnDeserializationFailed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private Delegate? _deserializationFailedHandlers;

    /// <summary>
    /// The handlers of ObjectModel.OnDeserializationFailed are static and stay registered when a test with a DCS model has run before. The tests
    /// here run with no handler or with the handler of their own DCS model only
    /// </summary>
    [SetUp]
    public void RemoveDeserializationFailedHandlers()
    {
        _deserializationFailedHandlers = (Delegate?)DeserializationFailedField.GetValue(null);
        DeserializationFailedField.SetValue(null, null);
    }

    [TearDown]
    public void RestoreDeserializationFailedHandlers() => DeserializationFailedField.SetValue(null, _deserializationFailedHandlers);

    /// <summary>
    /// JSON with the first byte of a two-byte character where the second one is missing, as RRF reports "é"[0]
    /// </summary>
    /// <param name="before">Text before that byte</param>
    /// <param name="after">Text after that byte</param>
    /// <returns>JSON</returns>
    private static byte[] WithHalfCharacter(string before, string after) => [.. Encoding.UTF8.GetBytes(before), 0xC3, .. Encoding.UTF8.GetBytes(after)];

    /// <summary>
    /// Apply the response to the query of a key, as UpdateService does
    /// </summary>
    /// <param name="model">Object model</param>
    /// <param name="key">Key</param>
    /// <param name="json">Response of the firmware</param>
    private static void UpdateKey(ObjectModel model, string key, byte[] json)
    {
        Utf8JsonReader reader = new(json);
        reader.Read();
        model.UpdateFromFirmwareJsonReader(key, ref reader, 0, true, ModelUpdateScope.Full);
    }

    private static JsonElement SerializeAndParse(ObjectModel model)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(model, ObjectModelContext.Default.ObjectModel));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The dictionaries that hold JSON values (global, the data of a plugin, the custom info of a file) store a value as it comes. Their converter
    /// wrote the raw text of each value, and getting that text throws for a string that is not valid UTF-8, so the whole model could not be
    /// serialized: GetObjectModel, a new subscription and every new WebSocket of DWS failed until the global got another value. The converter
    /// now writes the value like the patches of a subscription do, which replaces the invalid bytes
    /// </summary>
    /// <param name="before">Response for the key global up to the invalid byte</param>
    /// <param name="after">Rest of the response</param>
    /// <param name="path">Where the string is in the global</param>
    [TestCase("{\"text\":\"a", "\"}", "text", TestName = "{m}(string)")]
    [TestCase("{\"list\":[\"ok\",\"a", "\"]}", "list/1", TestName = "{m}(string in an array)")]
    [TestCase("{\"list\":[1,[\"a", "\"]]}", "list/1/0", TestName = "{m}(string in a nested array)")]
    public void GlobalWithInvalidUtf8IsSerializedWithTheReplacementCharacter(string before, string after, string path)
    {
        ObjectModel model = new();
        UpdateKey(model, "global", WithHalfCharacter(before, after));

        JsonElement value = SerializeAndParse(model).GetProperty("global");
        foreach (string step in path.Split('/'))
        {
            value = int.TryParse(step, out int index) ? value[index] : value.GetProperty(step);
        }
        Assert.That(value.GetString(), Is.EqualTo("a\uFFFD"));
    }

    /// <summary>
    /// The data of a plugin is stored the same way; SetPluginData takes its value as a JsonElement (DuetControlServer/Commands/Plugins/SetPluginData.cs)
    /// </summary>
    [Test]
    public void PluginDataWithInvalidUtf8IsSerializedWithTheReplacementCharacter()
    {
        Utf8JsonReader reader = new(WithHalfCharacter("{\"s\":\"a", "\"}"));
        ObjectModel model = new();
        model.Plugins["P"] = new Plugin { Id = "P" };
        model.Plugins["P"]!.Data["k"] = JsonElement.ParseValue(ref reader);

        JsonElement data = SerializeAndParse(model).GetProperty("plugins").GetProperty("P").GetProperty("data");
        Assert.That(data.GetProperty("k").GetProperty("s").GetString(), Is.EqualTo("a\uFFFD"));
    }

    /// <summary>
    /// A value that RRF can report comes out as it went in. Numbers keep their text, also where no .NET number type holds them
    /// </summary>
    [Test]
    public void ValidGlobalsAreSerializedAsBefore()
    {
        const string Global = "{\"int\":-42,\"float\":0.125,\"big\":18446744073709551616,\"beyondFloat\":3.403e+38,\"beyondDouble\":1e400,\"exp\":1.5e-7," +
            "\"text\":\"é ° ü \\\"quoted\\\" back\\\\slash\\ttab\",\"empty\":\"\",\"char\":\"X\",\"yes\":true,\"no\":false,\"nothing\":null," +
            "\"array\":[1,\"two\",[3.5,null],{\"k\":\"v\"}],\"emptyArray\":[]}";
        ObjectModel model = new();
        UpdateKey(model, "global", Encoding.UTF8.GetBytes(Global));

        JsonElement serialized = SerializeAndParse(model).GetProperty("global");
        using JsonDocument expected = JsonDocument.Parse(Global);
        Assert.That(serialized.EnumerateObject().Select(property => property.Name), Is.EqualTo(expected.RootElement.EnumerateObject().Select(property => property.Name)));
        using (Assert.EnterMultipleScope())
        {
            foreach (JsonProperty property in expected.RootElement.EnumerateObject())
            {
                JsonElement value = serialized.GetProperty(property.Name);
                Assert.That(JsonElement.DeepEquals(value, property.Value), Is.True, property.Name);
                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    Assert.That(value.GetRawText(), Is.EqualTo(property.Value.GetRawText()), property.Name);
                }
            }
        }
    }

    /// <summary>
    /// Logger that keeps the messages of the DCS object model
    /// </summary>
    private sealed class ListLogger : ILogger<DuetControlServer.Model.ObjectModel>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static DuetControlServer.Model.ObjectModel CreateDcsModel(ListLogger logger) =>
        new(new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance), logger, Options.Create(new Settings()));

    /// <summary>
    /// A property of type string refuses a value that is not valid UTF-8 (GetString throws), and DCS registers a handler that skips such a
    /// property and logs it once per type (DuetControlServer/Model/ObjectModel.cs, DeserializationFailedHandler). The handler logged the raw text
    /// of the value, and getting that text threw for the very same reason. It is called from an exception filter, so the first such value of a
    /// type was not skipped: the update of the key ended with the InvalidOperationException ("Failed to merge JSON due to internal error" in
    /// UpdateService), and the rest of the response was not applied. Every later one was skipped without a log line.
    /// The log shows the invalid bytes as the escape of the replacement character
    /// </summary>
    [Test]
    public void FirstStringWithInvalidUtf8IsSkippedAndLogged()
    {
        ListLogger logger = new();
        DuetControlServer.Model.ObjectModel model = CreateDcsModel(logger);
        string displayMessage = model.State.DisplayMessage;

        Assert.DoesNotThrow(() => UpdateKey(model, "state", WithHalfCharacter("{\"displayMessage\":\"a", "\",\"msUpTime\":5}")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.State.DisplayMessage, Is.EqualTo(displayMessage), "The property was not skipped");
            Assert.That(model.State.MsUpTime, Is.EqualTo(5), "The rest of the response was not applied");
            Assert.That(logger.Lines, Has.Exactly(1).Contains("Failed to deserialize").And.Contains("\"a\\uFFFD\""));
        }

        // Logged once per type, as before
        Assert.DoesNotThrow(() => UpdateKey(model, "state", WithHalfCharacter("{\"displayMessage\":\"b", "\",\"msUpTime\":6}")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.State.MsUpTime, Is.EqualTo(6));
            Assert.That(logger.Lines, Has.Exactly(1).Contains("Failed to deserialize"));
        }
    }

    /// <summary>
    /// M122 lists the values that failed to be deserialized, with their text as well
    /// </summary>
    [Test]
    public void DiagnosticsListAStringWithInvalidUtf8()
    {
        DuetControlServer.Model.ObjectModel model = CreateDcsModel(new ListLogger());
        UpdateKey(model, "state", WithHalfCharacter("{\"displayMessage\":\"a", "\"}"));

        StringBuilder builder = new();
        Assert.DoesNotThrow(() => model.PrintDiagnostics(builder));
        Assert.That(builder.ToString(), Does.Contain("Failed to deserialize the following properties").And.Contain("\"a\\uFFFD\""));
    }
}
