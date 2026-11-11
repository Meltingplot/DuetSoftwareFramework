using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace UnitTests.Fuzz;

/// <summary>
/// Fuzz tests for the JSON update paths of the DuetAPI object model (DuetAPI/ObjectModel/Base and the generated UpdateFromJson methods).
/// Clients apply patches from the IPC server with them and DCS applies the object model responses of the firmware.
/// Properties: a model update either succeeds or fails with an exception that the callers expect (see <see cref="IsExpected"/>),
/// the model stays serializable afterwards, and a serialized model updates a fresh model to the same JSON again
/// </summary>
[TestFixture]
public class ObjectModelJsonFuzz
{
    /// <summary>
    /// Exceptions an update may throw.
    /// JsonException is the documented one (exception cref of IStaticModelObject.UpdateFromJson and the generated methods);
    /// UpdateService, which applies the firmware responses, also handles ArgumentException (setters that validate the value, e.g.
    /// PluginManifest.Id) and InvalidOperationException (System.Text.Json getters called on another token type), see UpdateService.ExecuteAsync
    /// </summary>
    private static bool IsExpected(Exception e) => e is JsonException or ArgumentException or InvalidOperationException;

    /// <summary>
    /// Backing field of the static event ObjectModel.OnDeserializationFailed
    /// </summary>
    private static readonly FieldInfo DeserializationFailedField = typeof(ObjectModel).GetField(nameof(ObjectModel.OnDeserializationFailed), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Handlers of ObjectModel.OnDeserializationFailed that were registered before this fixture ran
    /// </summary>
    private Delegate? _deserializationFailedHandlers;

    /// <summary>
    /// Every DCS model registers a handler for ObjectModel.OnDeserializationFailed (DuetControlServer/Model/ObjectModel.cs:83), and with a handler
    /// the collections skip items that fail to deserialize instead of throwing (ObjectModel.DeserializationFailed). The handlers are static and stay
    /// registered after a host is disposed, so whether the fuzz tests ran like a client of DuetAPI (no handler) or like DCS depended on whether a
    /// test with a DCS model had run before. The fuzz tests run like a client; see <see cref="ScalarListItemIsSkippedTheSameWayByBothUpdateMethods"/>
    /// for what differs with a handler
    /// </summary>
    [OneTimeSetUp]
    public void RemoveDeserializationFailedHandlers()
    {
        _deserializationFailedHandlers = (Delegate?)DeserializationFailedField.GetValue(null);
        DeserializationFailedField.SetValue(null, null);
    }

    [OneTimeTearDown]
    public void RestoreDeserializationFailedHandlers()
    {
        DeserializationFailedField.SetValue(null, Delegate.Combine((Delegate?)DeserializationFailedField.GetValue(null), _deserializationFailedHandlers));
    }

    /// <summary>
    /// With a handler for ObjectModel.OnDeserializationFailed, as DCS registers one, a list item that fails to deserialize is skipped
    /// (DuetAPI/ObjectModel/Base/StaticModelCollection.cs:176-179 and 249-253). UpdateFromJson counts the skipped item and keeps the existing item
    /// at that index, but UpdateFromJsonReader ignores every item that is neither an object nor null without counting it (StaticModelCollection.cs:220-267),
    /// so the following items are applied to the wrong indices and the last existing item is removed. StructurallyMutatedJson found this with
    /// "$.sbc.dsf.httpEndpoints[0] = -0.5" while a handler of an earlier DCS test was registered
    /// </summary>
    [Test]
    [Explicit("Bug: with a DeserializationFailed handler, UpdateFromJsonReader drops a scalar list item instead of skipping it like UpdateFromJson")]
    public void ScalarListItemIsSkippedTheSameWayByBothUpdateMethods()
    {
        const string start = "{\"sbc\":{\"dsf\":{\"httpEndpoints\":[{\"endpointType\":\"GET\",\"namespace\":\"a\",\"path\":\"x\",\"unixSocket\":\"/run/a.sock\"},{\"endpointType\":\"GET\",\"namespace\":\"b\",\"path\":\"y\",\"unixSocket\":\"/run/b.sock\"}]}}}";
        byte[] patch = "{\"sbc\":{\"dsf\":{\"httpEndpoints\":[-0.5,{\"endpointType\":\"POST\",\"namespace\":\"b\",\"path\":\"y\",\"unixSocket\":\"/run/b.sock\"}]}}}"u8.ToArray();
        DeserializationFailedEventHandler handler = delegate { };
        ObjectModel.OnDeserializationFailed += handler;
        try
        {
            ObjectModel elementModel = new(), readerModel = new();
            using (JsonDocument document = JsonDocument.Parse(start))
            {
                elementModel.UpdateFromJson(document.RootElement, false);
                readerModel.UpdateFromJson(document.RootElement, false);
            }
            using (JsonDocument document = JsonDocument.Parse(patch))
            {
                elementModel.UpdateFromJson(document.RootElement, false);
            }
            Utf8JsonReader reader = new(patch);
            reader.Read();
            readerModel.UpdateFromJsonReader(ref reader, false);
            Assert.That(JsonSerializer.Serialize(readerModel, ObjectModelContext.Default.ObjectModel), Is.EqualTo(JsonSerializer.Serialize(elementModel, ObjectModelContext.Default.ObjectModel)));
        }
        finally
        {
            ObjectModel.OnDeserializationFailed -= handler;
        }
    }

    private static List<byte[]>? _corpus;

    private static ObjectModel? _populated;

    /// <summary>
    /// A model with an item in every collection, so that mutations reach every model class
    /// </summary>
    private static ObjectModel PopulatedModel()
    {
        if (_populated is not null)
        {
            return (ObjectModel)_populated.Clone();
        }

        ObjectModel model = new();
        using (JsonDocument json = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/model.json"))))
        {
            model.UpdateFromJson(json.RootElement, false);
        }
        using JsonDocument extra = JsonDocument.Parse("""
            {
                "boards": [
                    { "canAddress": 0, "firmwareName": "RepRapFirmware for Duet 3 MB6HC", "firmwareVersion": "3.7.0", "mcuTemp": { "current": 40.5, "min": 20, "max": 50 }, "v12": { "current": 12.1 } },
                    { "canAddress": 1, "state": "running", "drivers": [ { "status": 0 } ] }
                ],
                "fans": [ { "actualValue": 0.5, "name": "Part fan", "requestedValue": 0.5, "rpm": 1200, "thermostatic": { "heaters": [ 1 ], "highTemperature": 50 } }, null ],
                "global": { "number": 1, "text": "a", "list": [ 1, 2, 3 ], "nested": { "a": [ { "b": null } ] } },
                "ledStrips": [ { "type": "NeoPixel_RGB", "board": 0 } ],
                "messages": [ { "time": "2026-11-07T12:00:00", "type": 1, "content": "warning" } ],
                "plugins": { "Plugin": { "id": "Plugin", "name": "A plugin", "version": "1.0", "sbcPermissions": [ "commandExecution", "objectModelRead" ], "data": { "key": [ 1, "two" ] }, "pid": 12 } },
                "sbc": { "cpu": { "avgFrequency": 1500, "cores": 4 }, "dsf": { "version": "3.7.0", "httpEndpoints": [ { "endpointType": "GET", "namespace": "ns", "path": "x", "unixSocket": "/run/x.sock" } ], "userSessions": [ { "accessLevel": "readWrite", "id": 1, "origin": "1.2.3.4", "sessionType": "http" } ] }, "memory": { "available": 1000, "total": 2000 } },
                "spindles": [ { "current": 0, "max": 10000, "state": "unconfigured" } ],
                "tools": [ { "number": 0, "name": "Tool", "heaters": [ 1 ], "extruders": [ 0 ], "offsets": [ 0, 0, 0 ], "active": [ 200 ], "standby": [ 150 ], "retraction": { "length": 1.5 } } ]
            }
            """);
        model.UpdateFromJson(extra.RootElement, false);
        _populated = (ObjectModel)model.Clone();
        return model;
    }

    /// <summary>
    /// Every top-level key of the populated model as its own object, short enough for the mutations of <see cref="Fuzzer"/>
    /// </summary>
    private static List<byte[]> Corpus
    {
        get
        {
            if (_corpus is null)
            {
                JsonObject model = JsonSerializer.SerializeToNode(PopulatedModel(), ObjectModelContext.Default.ObjectModel)!.AsObject();
                _corpus = [];
                foreach ((string key, JsonNode? value) in model)
                {
                    byte[] entry = Encoding.UTF8.GetBytes(new JsonObject { [key] = value?.DeepClone() }.ToJsonString());
                    if (entry.Length > 3800 && value is JsonObject valueObject)
                    {
                        // Split large keys such as inputs and move into one entry per member
                        foreach ((string subKey, JsonNode? subValue) in valueObject)
                        {
                            _corpus.Add(Encoding.UTF8.GetBytes(new JsonObject { [key] = new JsonObject { [subKey] = subValue?.DeepClone() } }.ToJsonString()));
                        }
                    }
                    else
                    {
                        _corpus.Add(entry);
                    }
                }
                _corpus.Add(System.IO.File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "../../../Machine/JSON/patch.json")));
            }
            return _corpus;
        }
    }

    /// <summary>
    /// Tokens of the object model JSON
    /// </summary>
    private static IReadOnlyList<byte[]> Dictionary => [.. new[]
    {
        "null", "true", "false", "0", "-1", "1.5", "1e308", "-1e308", "1e400", "99999999999", "2147483648", "-2147483649", "18446744073709551616",
        "\"\"", "\"x\"", "[]", "{}", "[null]", "[{}]", "{\"a\":1}", ",", ":", "\"\\u0000\"", "\"\\ud800\"", "\"2026-13-45T25:61:61\"",
        "\"boards\"", "\"heaters\"", "\"axes\"", "\"kinematics\"", "\"name\"", "\"type\"", "\"status\"", "\"id\"", "\"plugins\"", "\"global\"",
        "\"messageBox\"", "\"sbcPermissions\"", "\"filamentMonitors\"", "\"driverId\"", "\"letter\"", "\"seq\"", "\"time\""
    }.Select(Encoding.UTF8.GetBytes)];

    /// <summary>
    /// Apply JSON to a model the way clients apply patches and DCS applies firmware responses, and check the properties
    /// </summary>
    /// <param name="input">UTF-8 JSON to apply</param>
    /// <param name="startFromPopulated">Whether to patch a populated model instead of a fresh one</param>
    private static void Check(byte[] input, bool startFromPopulated)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException)
        {
            // Not JSON, only the reader variant gets it
            CheckReader(input, startFromPopulated);
            return;
        }

        using (document)
        {
            // JsonElement variant, as used by clients for patches (ObjectModel.UpdateFromJson)
            ObjectModel model = startFromPopulated ? PopulatedModel() : new ObjectModel();
            bool elementApplied = Apply(() => model.UpdateFromJson(document.RootElement, false), "UpdateFromJson");
            string? elementJson = SerializeChecked(model, "UpdateFromJson");

            // Utf8JsonReader variant, as used by the IPC clients for the first message (SubscribeConnection.GetObjectModel)
            ObjectModel readerModel = startFromPopulated ? PopulatedModel() : new ObjectModel();
            bool readerApplied = Apply(() =>
            {
                Utf8JsonReader reader = new(input);
                reader.Read();
                readerModel.UpdateFromJsonReader(ref reader, false);
            }, "UpdateFromJsonReader");
            string? readerJson = SerializeChecked(readerModel, "UpdateFromJsonReader");
            if (elementJson is null || readerJson is null)
            {
                return;
            }

            // Both variants implement the same update, so a successful one must give the same model
            if (elementApplied && readerApplied && elementJson != readerJson)
            {
                string? difference = IPC.IpcSubscription.FirstDifference(JsonNode.Parse(elementJson), JsonNode.Parse(readerJson), string.Empty);
                if (difference is not null && (difference.EndsWith("got \"[\"") || difference.EndsWith("got \"{\"")))
                {
                    // Known bug, see ObjectPropertyWithArrayIsReadLikeUpdateFromJson
                    return;
                }
                throw new FuzzFailure("element vs reader", $"UpdateFromJson and UpdateFromJsonReader give different models at {difference} (UpdateFromJson first)");
            }

            // Firmware variant, as UpdateService applies the response of a key (UpdateFromFirmwareJsonReader)
            if (document.RootElement.ValueKind == JsonValueKind.Object && HasValidPropertyNames(document.RootElement))
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    ObjectModel firmwareModel = startFromPopulated ? PopulatedModel() : new ObjectModel();
                    byte[] value = JsonMarshal.GetRawUtf8Value(property.Value).ToArray();
                    Apply(() =>
                    {
                        Utf8JsonReader reader = new(value);
                        reader.Read();
                        firmwareModel.UpdateFromFirmwareJsonReader(property.Name, ref reader, 0, true, ModelUpdateScope.Full);
                    }, "UpdateFromFirmwareJsonReader");
                    SerializeChecked(firmwareModel, "UpdateFromFirmwareJsonReader");
                }
            }

            // Whatever the model looks like now, its JSON must build the same model again
            CheckRoundTrip(elementJson);
        }
    }

    /// <summary>
    /// Check if the top-level property names can be read as strings. JsonDocument accepts invalid UTF-8 in them, but reading the name
    /// then throws an InvalidOperationException, which is one of the expected exceptions of the reader variants anyway
    /// </summary>
    private static bool HasValidPropertyNames(JsonElement element)
    {
        try
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                _ = property.Name;
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Feed bytes that are not valid JSON to the reader variant, which must fail with a JsonException
    /// </summary>
    private static void CheckReader(byte[] input, bool startFromPopulated)
    {
        ObjectModel model = startFromPopulated ? PopulatedModel() : new ObjectModel();
        Apply(() =>
        {
            Utf8JsonReader reader = new(input);
            if (reader.Read())
            {
                model.UpdateFromJsonReader(ref reader, false);
            }
        }, "UpdateFromJsonReader");
        SerializeChecked(model, "UpdateFromJsonReader");
    }

    /// <summary>
    /// Run an update and check the exception it throws
    /// </summary>
    /// <returns>True if the update succeeded</returns>
    private static bool Apply(Action update, string name)
    {
        try
        {
            update();
            return true;
        }
        catch (Exception e) when (IsExpected(e))
        {
            return false;
        }
        catch (Exception e)
        {
            throw new FuzzFailure($"{name} exception", $"{name} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// Serialize a model, which must always work
    /// </summary>
    private static string? SerializeChecked(ObjectModel model, string after)
    {
        try
        {
            return JsonSerializer.Serialize(model, ObjectModelContext.Default.ObjectModel);
        }
        catch (ArgumentException e) when (e.TargetSite?.DeclaringType?.FullName == "System.Text.Json.ThrowHelper" && e.Message.Contains("infinity"))
        {
            // Known bug, see InfiniteNumberKeepsTheModelSerializable
            return null;
        }
        catch (JsonException e) when (e.InnerException is InvalidOperationException { InnerException: DecoderFallbackException })
        {
            // Known bug, see InvalidUtf8KeepsTheModelSerializable
            return null;
        }
        catch (Exception e)
        {
            throw new FuzzFailure("serialize", $"Model cannot be serialized after {after}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// A serialized model must update a fresh model to the same JSON
    /// </summary>
    private static void CheckRoundTrip(string json)
    {
        ObjectModel copy = new();
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            try
            {
                copy.UpdateFromJson(document.RootElement, false);
            }
            catch (Exception e)
            {
                throw new FuzzFailure("round trip exception", $"Serialized model cannot be applied again: {e.GetType().Name}: {e.Message}\n{json}");
            }
        }
        string? copyJson = SerializeChecked(copy, "round trip");
        if (copyJson is not null && copyJson != json)
        {
            string? difference = IPC.IpcSubscription.FirstDifference(JsonNode.Parse(json), JsonNode.Parse(copyJson), string.Empty);
            throw new FuzzFailure("round trip", $"Serialize, update and serialize again differs at {difference}");
        }
    }

    [Test]
    public void CorpusRoundTrips()
    {
        // Every corpus entry is a serialized model part, so it must apply without exception and round-trip
        foreach (byte[] entry in Corpus)
        {
            Check(entry, false);
            Check(entry, true);
        }
        CheckRoundTrip(JsonSerializer.Serialize(PopulatedModel(), ObjectModelContext.Default.ObjectModel));
    }

    /// <summary>
    /// A value that does not fit the type of a property is invalid data, which the update methods document as JsonException ("Failed to deserialize
    /// data") and which UpdateService handles. The getters of JsonElement and Utf8JsonReader throw a FormatException for a number or date that does
    /// not fit, DateTime.Parse in the date converters did the same, and the first character of an empty string gave an IndexOutOfRangeException;
    /// UpdateService.ExecuteAsync catches only ArgumentException, InvalidOperationException and JsonException. Finding O2 of design/open-findings.md
    /// </summary>
    /// <param name="json">Update of the object model</param>
    [TestCase("{\"spindles\":[{\"current\":9223372036854775807}]}", TestName = "{m}(int)")]
    [TestCase("{\"inputs\":[{\"lineNumber\":9223372036854775808}]}", TestName = "{m}(long)")]
    [TestCase("{\"inputs\":[{\"stackDepth\":256}]}", TestName = "{m}(byte)")]
    [TestCase("{\"ledStrips\":[{\"maxLeds\":-1}]}", TestName = "{m}(uint)")]
    [TestCase("{\"state\":{\"atxPower\":9223372036854775807}}", TestName = "{m}(bool as number)")]
    [TestCase("{\"state\":{\"time\":\"2026-11-07T012:00:00\"}}", TestName = "{m}(DateTime)")]
    [TestCase("{\"heat\":{\"bedHeaters\":[9223372036854775807]}}", TestName = "{m}(int in a list)")]
    [TestCase("{\"heat\":{\"bedHeaterMapping\":[[9223372036854775807]]}}", TestName = "{m}(int in an array in a list)")]
    [TestCase("{\"move\":{\"axes\":[{\"letter\":\"\"}]}}", TestName = "{m}(char)")]
    [TestCase("{\"move\":{\"compensation\":{\"probeGrid\":{\"axes\":[\"\",\"Y\"]}}}}", TestName = "{m}(char in a list)")]
    [TestCase("{\"messages\":[{\"time\":\"2026-11-07T012:00:00\",\"type\":0,\"content\":\"x\"}]}", TestName = "{m}(date of a message)")]
    public void ValueThatDoesNotFitFailsWithJsonException(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws(Is.InstanceOf<JsonException>(), () => new ObjectModel().UpdateFromJson(document.RootElement, false));
        Assert.Throws(Is.InstanceOf<JsonException>(), () =>
        {
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
            reader.Read();
            new ObjectModel().UpdateFromJsonReader(ref reader, false);
        });
    }

    /// <summary>
    /// The update of a single key, as UpdateService applies the responses of the firmware, fails the same way
    /// </summary>
    [Test]
    public void ValueThatDoesNotFitFailsWithJsonExceptionInAFirmwareResponse()
    {
        Assert.Throws(Is.InstanceOf<JsonException>(), () =>
        {
            Utf8JsonReader reader = new("[{\"current\":9223372036854775807}]"u8);
            reader.Read();
            new ObjectModel().UpdateFromFirmwareJsonReader("spindles", ref reader);
        });
    }

    /// <summary>
    /// With a handler for ObjectModel.OnDeserializationFailed, as DCS registers one (DuetControlServer/Model/ObjectModel.cs:83), a property whose
    /// value does not fit is reported to the handler and skipped, and the rest of the update is applied. The handler gets the JsonException
    /// </summary>
    /// <param name="json">Update of the first axis with a value that does not fit and a maximum of 123</param>
    /// <param name="type">Type of the property that does not fit</param>
    [TestCase("{\"move\":{\"axes\":[{\"letter\":\"\",\"max\":123}]}}", typeof(char))]
    [TestCase("{\"move\":{\"axes\":[{\"microstepping\":{\"value\":9223372036854775807},\"max\":123}]}}", typeof(int))]
    public void ValueThatDoesNotFitIsReportedToTheHandler(string json, Type type)
    {
        List<DeserializationFailedEventArgs> failures = [];
        DeserializationFailedEventHandler handler = (_, e) => failures.Add(e);
        ObjectModel.OnDeserializationFailed += handler;
        try
        {
            ObjectModel elementModel = new(), readerModel = new();
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                elementModel.UpdateFromJson(document.RootElement, false);
            }
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
            reader.Read();
            readerModel.UpdateFromJsonReader(ref reader, false);

            Assert.That(failures.Select(failure => failure.TargetType), Is.EqualTo(new[] { type, type }));
            Assert.That(failures.Select(failure => failure.Exception), Is.All.InstanceOf<JsonException>());
            foreach (ObjectModel model in (ObjectModel[])[elementModel, readerModel])
            {
                Assert.That(model.Move.Axes[0].Max, Is.EqualTo(123F));
                Assert.That(model.Move.Axes[0].Letter, Is.EqualTo(new Axis().Letter));
                Assert.That(model.Move.Axes[0].Microstepping.Value, Is.EqualTo(new Axis().Microstepping.Value));
            }
        }
        finally
        {
            ObjectModel.OnDeserializationFailed -= handler;
        }
    }

    private static IEnumerable<TestCaseData> ObjectsWithTwoTypeKeys()
    {
        static object? Kinematics(ObjectModel model) => model.Move.Kinematics;
        static object? FilamentMonitor(ObjectModel model) => model.Sensors.FilamentMonitors[0];
        static object? Screen(ObjectModel model) => ((MainBoard)model.Boards[0]).DirectDisplay?.Screen;
        yield return new TestCaseData("{\"move\":{\"kinematics\":{\"name\":\"delta\",\"name\":\"cartesian\"}}}", (Func<ObjectModel, object?>)Kinematics, typeof(CoreKinematics)).SetName("{m}(kinematics)");
        yield return new TestCaseData("{\"move\":{\"kinematics\":{\"name\":\"cartesian\",\"name\":\"delta\",\"name\":\"polar\"}}}", (Func<ObjectModel, object?>)Kinematics, typeof(PolarKinematics)).SetName("{m}(kinematics, three)");
        yield return new TestCaseData("{\"move\":{\"kinematics\":{\"name\":\"delta\",\"name\":\"unknown\"}}}", (Func<ObjectModel, object?>)Kinematics, typeof(Kinematics)).SetName("{m}(kinematics, unknown last)");
        yield return new TestCaseData("{\"sensors\":{\"filamentMonitors\":[{\"type\":\"laser\",\"type\":\"pulsed\"}]}}", (Func<ObjectModel, object?>)FilamentMonitor, typeof(PulsedFilamentMonitor)).SetName("{m}(filament monitor)");
        yield return new TestCaseData("{\"sensors\":{\"filamentMonitors\":[{\"type\":\"rotatingMagnet\",\"type\":\"simple\"}]}}", (Func<ObjectModel, object?>)FilamentMonitor, typeof(FilamentMonitor)).SetName("{m}(filament monitor, unknown last)");
        yield return new TestCaseData("{\"boards\":[{\"directDisplay\":{\"screen\":{\"controller\":\"ST7567\",\"controller\":\"ST7920\"}}}]}", (Func<ObjectModel, object?>)Screen, typeof(DirectDisplayScreen)).SetName("{m}(screen)");
        yield return new TestCaseData("{\"boards\":[{\"directDisplay\":{\"screen\":{\"controller\":\"ST7920\",\"controller\":\"ST7567\"}}}]}", (Func<ObjectModel, object?>)Screen, typeof(DirectDisplayScreenST7567)).SetName("{m}(screen, ST7567 last)");
    }

    /// <summary>
    /// The class of the kinematics, of a filament monitor and of a screen depends on a key of the object (name, type, controller). If the object
    /// has that key more than once, UpdateFromJson takes the last one (JsonElement.TryGetProperty). UpdateFromJsonReader acted on the first one it
    /// found that asked for another class and handed the same reader to a new instance of that class, which found the other key and handed it back,
    /// until the stack overflowed and the process ended. Finding O6 of design/open-findings.md; found by MutatedJson with seed 3 and 50000 inputs
    /// </summary>
    /// <param name="json">Update with an object that has its type key more than once</param>
    /// <param name="select">Get that object from the model</param>
    /// <param name="type">Class the last key asks for</param>
    [TestCaseSource(nameof(ObjectsWithTwoTypeKeys))]
    public void ObjectWithTwoTypeKeysIsReadLikeUpdateFromJson(string json, Func<ObjectModel, object?> select, Type type)
    {
        ObjectModel elementModel = new(), readerModel = new();
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            elementModel.UpdateFromJson(document.RootElement, false);
        }
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        reader.Read();
        readerModel.UpdateFromJsonReader(ref reader, false);

        Assert.That(select(elementModel), Is.TypeOf(type), "UpdateFromJson");
        Assert.That(select(readerModel), Is.TypeOf(type), "UpdateFromJsonReader");
        Assert.That(JsonSerializer.Serialize(readerModel, ObjectModelContext.Default.ObjectModel), Is.EqualTo(JsonSerializer.Serialize(elementModel, ObjectModelContext.Default.ObjectModel)));
    }

    private static IEnumerable<TestCaseData> TypeKeysInOtherSpellings()
    {
        static object? FilamentMonitor(ObjectModel model) => model.Sensors.FilamentMonitors[0];
        static object? Screen(ObjectModel model) => ((MainBoard)model.Boards[0]).DirectDisplay?.Screen;
        static TestCaseData Monitor(string type, Type expected) =>
            new TestCaseData($"{{\"sensors\":{{\"filamentMonitors\":[{{\"type\":{type}}}]}}}}", (Func<ObjectModel, object?>)FilamentMonitor, expected).SetName($"{{m}}(filament monitor {type.Replace('"', '\'')})");
        static TestCaseData Display(string controller, Type expected) =>
            new TestCaseData($"{{\"boards\":[{{\"directDisplay\":{{\"screen\":{{\"controller\":{controller}}}}}}}]}}", (Func<ObjectModel, object?>)Screen, expected).SetName($"{{m}}(screen {controller.Replace('"', '\'')})");

        yield return Monitor("\"rotatingMagnet\"", typeof(RotatingMagnetFilamentMonitor));
        yield return Monitor("\"rotatingMagnEt\"", typeof(RotatingMagnetFilamentMonitor));
        yield return Monitor("\"LASER\"", typeof(LaserFilamentMonitor));
        yield return Monitor("\"2\"", typeof(PulsedFilamentMonitor));
        yield return Monitor("2", typeof(PulsedFilamentMonitor));
        yield return Monitor("\"simple\"", typeof(FilamentMonitor));
        yield return Monitor("\"SIMPLE\"", typeof(FilamentMonitor));
        yield return Monitor("\"Unknown\"", typeof(FilamentMonitor));
        yield return Display("\"ST7567\"", typeof(DirectDisplayScreenST7567));
        yield return Display("\"st7567\"", typeof(DirectDisplayScreenST7567));
        yield return Display("\"1\"", typeof(DirectDisplayScreenST7567));
        yield return Display("1", typeof(DirectDisplayScreenST7567));
        yield return Display("\"st7920\"", typeof(DirectDisplayScreen));
        yield return Display("\"ILI9488\"", typeof(DirectDisplayScreen));
    }

    /// <summary>
    /// The class of a filament monitor and of a screen follows the same key as their Type and Controller property. The property is read by the
    /// converter of its enumeration, which also takes another case and the number of the value. The class was chosen by comparing the text, so
    /// "rotatingMagnEt" gave a FilamentMonitor of the base class whose Type was RotatingMagnet: its own properties were missing, and a model made
    /// from its JSON had a RotatingMagnetFilamentMonitor instead. Finding O7 of design/open-findings.md; found by MutatedJson with seed 2 and
    /// 50000 inputs. RRF writes the names as the comparison expected them (FilamentMonitors/*.h, GetTypeText)
    /// </summary>
    /// <param name="json">Update with one filament monitor or screen</param>
    /// <param name="select">Get that object from the model</param>
    /// <param name="type">Class that belongs to the value the property gets</param>
    [TestCaseSource(nameof(TypeKeysInOtherSpellings))]
    public void ClassFollowsTheTypeProperty(string json, Func<ObjectModel, object?> select, Type type)
    {
        ObjectModel elementModel = new(), readerModel = new(), copy = new();
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            elementModel.UpdateFromJson(document.RootElement, false);
        }
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        reader.Read();
        readerModel.UpdateFromJsonReader(ref reader, false);

        Assert.That(select(elementModel), Is.TypeOf(type), "UpdateFromJson");
        Assert.That(select(readerModel), Is.TypeOf(type), "UpdateFromJsonReader");
        string serialized = JsonSerializer.Serialize(elementModel, ObjectModelContext.Default.ObjectModel);
        Assert.That(JsonSerializer.Serialize(readerModel, ObjectModelContext.Default.ObjectModel), Is.EqualTo(serialized));

        // A model made from the JSON of this one is the same
        using (JsonDocument document = JsonDocument.Parse(serialized))
        {
            copy.UpdateFromJson(document.RootElement, false);
        }
        Assert.That(select(copy), Is.TypeOf(type), "Round trip");
        Assert.That(JsonSerializer.Serialize(copy, ObjectModelContext.Default.ObjectModel), Is.EqualTo(serialized));
    }

    /// <summary>
    /// A type that the enumeration does not know still fails with a JsonException, and with a handler for OnDeserializationFailed, as in DCS,
    /// the object keeps the base class and its type
    /// </summary>
    [Test]
    public void UnknownTypeKeyIsHandledAsBefore()
    {
        const string json = "{\"sensors\":{\"filamentMonitors\":[{\"type\":\"nonsense\",\"status\":\"ok\"}]}}";
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws(Is.InstanceOf<JsonException>(), () => new ObjectModel().UpdateFromJson(document.RootElement, false));
        Assert.Throws(Is.InstanceOf<JsonException>(), () =>
        {
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
            reader.Read();
            new ObjectModel().UpdateFromJsonReader(ref reader, false);
        });

        DeserializationFailedEventHandler handler = delegate { };
        ObjectModel.OnDeserializationFailed += handler;
        try
        {
            ObjectModel elementModel = new(), readerModel = new();
            elementModel.UpdateFromJson(document.RootElement, false);
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
            reader.Read();
            readerModel.UpdateFromJsonReader(ref reader, false);
            foreach (ObjectModel model in (ObjectModel[])[elementModel, readerModel])
            {
                Assert.That(model.Sensors.FilamentMonitors[0], Is.TypeOf<FilamentMonitor>());
                Assert.That(model.Sensors.FilamentMonitors[0]!.Type, Is.EqualTo(FilamentMonitorType.Unknown));
                Assert.That(model.Sensors.FilamentMonitors[0]!.Status, Is.EqualTo(FilamentMonitorStatus.Ok));
            }
        }
        finally
        {
            ObjectModel.OnDeserializationFailed -= handler;
        }
    }

    /// <summary>
    /// RRF reports NaN and infinity as null (ObjectModel::ReportFloat), so DCS never sends them either. A JSON number beyond the float range is
    /// read as infinity by JsonElement.GetSingle and Utf8JsonReader.GetSingle, the update accepts it and the model cannot be serialized any more:
    /// Utf8JsonWriter throws an ArgumentException for infinity, so GetObjectModel, the subscriptions and DWS would fail on every request
    /// </summary>
    [Test]
    [Explicit("Bug: an out-of-range float is stored as infinity, after which the object model cannot be serialized")]
    public void InfiniteNumberKeepsTheModelSerializable()
    {
        using JsonDocument document = JsonDocument.Parse("{\"heat\":{\"heaters\":[{\"current\":1e39}]}}");
        ObjectModel model = new();
        try
        {
            model.UpdateFromJson(document.RootElement, false);
        }
        catch (JsonException)
        {
            // Refusing the value is fine as well
        }
        Assert.DoesNotThrow(() => JsonSerializer.Serialize(model, ObjectModelContext.Default.ObjectModel));
    }

    /// <summary>
    /// RRF copies the bytes of strings into its JSON as they are (OutputBuffer::EncodeChar escapes only control characters, quotes and
    /// backslashes), so a global string set from a host that does not send UTF-8 reaches DCS as invalid UTF-8. UpdateService applies the
    /// response with UpdateFromFirmwareJsonReader, which stores the value without checking it, and afterwards the model cannot be serialized
    /// </summary>
    [Test]
    [Explicit("Bug: a global value with invalid UTF-8 is stored, after which the object model cannot be serialized")]
    public void InvalidUtf8KeepsTheModelSerializable()
    {
        byte[] global = [.. "{\"text\":\""u8, 0xE9, .. "\"}"u8];
        ObjectModel model = new();
        try
        {
            Utf8JsonReader reader = new(global);
            reader.Read();
            model.UpdateFromFirmwareJsonReader("global", ref reader, 0, true, ModelUpdateScope.Full);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            // Refusing the value is fine as well (UpdateService logs these)
        }
        Assert.DoesNotThrow(() => JsonSerializer.Serialize(model, ObjectModelContext.Default.ObjectModel));
    }

    /// <summary>
    /// For a property of type object (such as MessageBox.Default) the generated UpdateFromJson stores the raw JSON of an array or object, but the
    /// generated UpdateFromJsonReader stores the text of the start token ("[" or "{") and does not skip the value, so the reader goes on with the
    /// tokens inside it as if they were members of the parent object (DuetAPI.SourceGenerators/ObjectModel/ModelObject/UpdateFromJsonReader.cs)
    /// </summary>
    [Test]
    [Explicit("Bug: UpdateFromJsonReader stores \"[\" for an array in a property of type object and reads the array items as members of the parent")]
    public void ObjectPropertyWithArrayIsReadLikeUpdateFromJson()
    {
        byte[] json = "{\"state\":{\"messageBox\":{\"default\":[{\"title\":\"inner\"}],\"title\":\"outer\"}}}"u8.ToArray();
        ObjectModel elementModel = new(), readerModel = new();
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            elementModel.UpdateFromJson(document.RootElement, false);
        }
        Utf8JsonReader reader = new(json);
        reader.Read();
        readerModel.UpdateFromJsonReader(ref reader, false);
        Assert.That(readerModel.State.MessageBox!.Title, Is.EqualTo("outer"));
        Assert.That(JsonSerializer.Serialize(readerModel, ObjectModelContext.Default.ObjectModel), Is.EqualTo(JsonSerializer.Serialize(elementModel, ObjectModelContext.Default.ObjectModel)));
    }

    [Test]
    public void MutatedJson() => Fuzzer.Run(Corpus, input => Check(input, false), Dictionary);

    /// <summary>
    /// Number of inputs of the generated tests, which apply each input several times
    /// </summary>
    private static int GeneratedIterations => Fuzzer.Scale(700);

    /// <summary>
    /// Structural mutation of a model part
    /// </summary>
    /// <param name="Json">Mutated JSON</param>
    /// <param name="Mutations">Description of the applied mutations</param>
    /// <param name="FromPopulated">Whether to patch a populated model</param>
    public sealed record StructuredInput(string Json, string Mutations, bool FromPopulated);

    /// <summary>
    /// Values that are valid JSON but of every type and range
    /// </summary>
    private static readonly string[] ReplacementValues =
    [
        "null", "true", "false", "0", "-1", "1", "0.5", "-0.5", "1e308", "-1e308", "2147483647", "2147483648", "-2147483649", "4294967296",
        "9223372036854775807", "18446744073709551616", "1e-320", "\"\"", "\"x\"", "\"0\"", "\"2026-11-07T12:00:00\"", "\"idle\"", "\"\\u0000\"",
        "[]", "[null]", "[1,2,3]", "[\"a\"]", "[{}]", "[[1]]", "{}", "{\"x\":1}", "{\"name\":null}", "{\"type\":\"rotatingMagnet\"}"
    ];

    /// <summary>
    /// Draw a corpus entry and change its structure: replace values with values of other types, drop and add keys, resize arrays and nest values
    /// </summary>
    private static StructuredInput Generate(Random random)
    {
        JsonNode root = JsonNode.Parse(Corpus[random.Next(Corpus.Count)])!;
        List<string> mutations = [];
        for (int count = 1 + random.Next(4); count > 0; count--)
        {
            List<(JsonNode? parent, object key, JsonNode? node)> nodes = [];
            Collect(root, null, string.Empty, nodes);
            (JsonNode? parent, object key, JsonNode? node) = nodes[random.Next(nodes.Count)];
            if (parent is null)
            {
                continue;
            }

            string path = NodePath(parent, key);
            switch (random.Next(6))
            {
                case 0:
                case 1:
                    {
                        string value = ReplacementValues[random.Next(ReplacementValues.Length)];
                        Set(parent, key, JsonNode.Parse(value));
                        mutations.Add($"{path} = {value}");
                    }
                    break;
                case 2:
                    if (parent is JsonObject parentObject)
                    {
                        parentObject.Remove((string)key);
                        mutations.Add($"remove {path}");
                    }
                    else if (parent is JsonArray parentArray)
                    {
                        parentArray.RemoveAt((int)key);
                        mutations.Add($"remove {path}");
                    }
                    break;
                case 3:
                    if (node is JsonArray array)
                    {
                        int copies = 1 + random.Next(random.Next(2) == 0 ? 3 : 40);
                        JsonNode? template = (array.Count > 0) ? array[random.Next(array.Count)] : JsonNode.Parse(ReplacementValues[random.Next(ReplacementValues.Length)]);
                        for (int i = 0; i < copies; i++)
                        {
                            array.Add(template?.DeepClone());
                        }
                        mutations.Add($"append {copies} items to {path}");
                    }
                    else if (node is JsonObject obj)
                    {
                        string newKey = random.Next(2) == 0 ? "unknownKey" : (obj.Count > 0 ? obj.ElementAt(random.Next(obj.Count)).Key.ToUpperInvariant() : "X");
                        obj[newKey] = JsonNode.Parse(ReplacementValues[random.Next(ReplacementValues.Length)]);
                        mutations.Add($"add {path}/{newKey}");
                    }
                    break;
                case 4:
                    {
                        // Wrap the value in an array or an object
                        JsonNode? wrapped = random.Next(2) == 0 ? new JsonArray(node?.DeepClone()) : new JsonObject { ["value"] = node?.DeepClone() };
                        Set(parent, key, wrapped);
                        mutations.Add($"wrap {path}");
                    }
                    break;
                case 5:
                    {
                        // Copy a value of another key here, so that values of a plausible but wrong type appear
                        (_, _, JsonNode? other) = nodes[random.Next(nodes.Count)];
                        if (!ReferenceEquals(other, node) && !IsAncestor(node, other))
                        {
                            Set(parent, key, other?.DeepClone());
                            mutations.Add($"{path} = copy of {other?.GetPath()}");
                        }
                    }
                    break;
            }
        }
        return new StructuredInput(root.ToJsonString(), string.Join("; ", mutations), random.Next(2) == 0);
    }

    private static string NodePath(JsonNode parent, object key) => (key is int index) ? $"{parent.GetPath()}[{index}]" : $"{parent.GetPath()}.{key}";

    private static bool IsAncestor(JsonNode? ancestor, JsonNode? node)
    {
        for (JsonNode? current = node?.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private static void Set(JsonNode parent, object key, JsonNode? value)
    {
        if (parent is JsonObject obj)
        {
            obj[(string)key] = value;
        }
        else if (parent is JsonArray array)
        {
            array[(int)key] = value;
        }
    }

    private static void Collect(JsonNode? node, JsonNode? parent, object key, List<(JsonNode?, object, JsonNode?)> nodes)
    {
        nodes.Add((parent, key, node));
        if (node is JsonObject obj)
        {
            foreach ((string childKey, JsonNode? child) in obj.ToList())
            {
                Collect(child, node, childKey, nodes);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                Collect(array[i], node, i, nodes);
            }
        }
    }

    [Test]
    public void StructurallyMutatedJson() => Fuzzer.Run(Generate, input => Check(Encoding.UTF8.GetBytes(input.Json), input.FromPopulated),
        input => $"{input.Mutations} (from populated model: {input.FromPopulated})\n{input.Json}", iterations: GeneratedIterations);
}
