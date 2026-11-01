using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitTests.FakeFirmware;

/// <summary>
/// In-memory object model of the fake firmware. Tests can seed it, DSF queries it like RRF's object model.
/// Every change of a top-level key increments its sequence number in "seqs", like RRF does for the keys DSF polls
/// </summary>
public sealed class FakeObjectModel
{
    /// <summary>
    /// Lock around the model
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Root of the model
    /// </summary>
    private readonly JsonObject _root;

    /// <summary>
    /// Sequence numbers of the top-level keys
    /// </summary>
    private readonly Dictionary<string, int> _seqs = [];

    /// <summary>
    /// Create a new model with a minimal plausible content: one board, three axes, idle state and no globals
    /// </summary>
    public FakeObjectModel()
    {
        _root = JsonNode.Parse("""
            {
                "boards": [
                    {
                        "firmwareFileName": "Duet3Firmware_MB6HC.bin",
                        "firmwareName": "RepRapFirmware",
                        "firmwareVersion": "3.7.0+1.mp.5",
                        "name": "Duet 3 MB6HC (fake)",
                        "shortName": "MB6HC",
                        "state": "running"
                    }
                ],
                "global": {},
                "limits": {},
                "move": {
                    "axes": [
                        { "letter": "X", "homed": false, "machinePosition": 0, "userPosition": 0 },
                        { "letter": "Y", "homed": false, "machinePosition": 0, "userPosition": 0 },
                        { "letter": "Z", "homed": false, "machinePosition": 0, "userPosition": 0 }
                    ]
                },
                "state": {
                    "status": "idle"
                }
            }
            """)!.AsObject();
    }

    /// <summary>
    /// Whether the fake firmware updates state.status from its own state (halted, busy or idle) like RepRap::GetStatusIndex does.
    /// Set this to false to keep a seeded status
    /// </summary>
    public bool AutoStatus { get; set; } = true;

    /// <summary>
    /// Get a copy of a value
    /// </summary>
    /// <param name="path">Path of the value, e.g. "state.status" or "move.axes[0].letter"</param>
    /// <returns>Copy of the value or null if it does not exist</returns>
    public JsonNode? Get(string path)
    {
        lock (_lock)
        {
            return TryGetNode(path, out JsonNode? node) ? node?.DeepClone() : null;
        }
    }

    /// <summary>
    /// Set a value and increment the sequence number of its top-level key
    /// </summary>
    /// <param name="path">Path of the value, e.g. "state.status". Missing objects on the way are created</param>
    /// <param name="value">New value</param>
    public void Set(string path, JsonNode? value)
    {
        lock (_lock)
        {
            List<object> segments = ParsePath(path);
            JsonNode parent = _root;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                JsonNode? next = GetChild(parent, segments[i]);
                if (next is null)
                {
                    if (segments[i] is not string name || parent is not JsonObject parentObject)
                    {
                        throw new ArgumentException($"Cannot create {path}", nameof(path));
                    }
                    next = new JsonObject();
                    parentObject[name] = next;
                }
                parent = next;
            }

            object last = segments[^1];
            if (last is string lastName && parent is JsonObject lastObject)
            {
                lastObject[lastName] = value?.DeepClone();
            }
            else if (last is int lastIndex && parent is JsonArray lastArray && lastIndex < lastArray.Count)
            {
                lastArray[lastIndex] = value?.DeepClone();
            }
            else
            {
                throw new ArgumentException($"Cannot set {path}", nameof(path));
            }
            IncrementSeq((string)segments[0]);
        }
    }

    /// <summary>
    /// Get the whole model as JSON including the sequence numbers
    /// </summary>
    /// <returns>JSON text</returns>
    public string ToJson()
    {
        lock (_lock)
        {
            return GetRootWithSeqs().ToJsonString();
        }
    }

    /// <summary>
    /// Get the sequence number of a top-level key
    /// </summary>
    /// <param name="key">Top-level key</param>
    /// <returns>Sequence number</returns>
    public int GetSeq(string key)
    {
        lock (_lock)
        {
            return _seqs.TryGetValue(key, out int seq) ? seq : 0;
        }
    }

    /// <summary>
    /// Increment the sequence number of a top-level key, e.g. RepRap::GlobalUpdated
    /// </summary>
    /// <param name="key">Top-level key</param>
    internal void IncrementSeq(string key)
    {
        lock (_lock)
        {
            _seqs[key] = (_seqs.TryGetValue(key, out int seq) ? seq : 0) + 1;
        }
    }

    /// <summary>
    /// Set the machine status without changing a sequence number, because state.status is a live field in RRF
    /// </summary>
    /// <param name="status">New status</param>
    internal void SetComputedStatus(string status)
    {
        lock (_lock)
        {
            if (AutoStatus)
            {
                if (_root["state"] is not JsonObject state)
                {
                    state = [];
                    _root["state"] = state;
                }
                state["status"] = status;
            }
        }
    }

    /// <summary>
    /// Build the response to an object model query, see RepRap::GetModelResponse.
    /// Arrays are always reported in full, there is no support for the "a" flag and the "next" field
    /// </summary>
    /// <param name="key">Key to query</param>
    /// <param name="flags">Query flags</param>
    /// <returns>JSON response terminated by a newline</returns>
    internal string GetModelResponse(string key, string flags)
    {
        lock (_lock)
        {
            StringBuilder builder = new();
            builder.Append("{\"key\":");
            builder.Append(JsonSerializer.Serialize(key));
            builder.Append(",\"flags\":");
            builder.Append(JsonSerializer.Serialize(flags));
            builder.Append(",\"result\":");

            bool wantArrayLength = key.StartsWith('#');
            string path = wantArrayLength ? key[1..] : key;
            if (string.IsNullOrEmpty(path))
            {
                // The flags would select the fields RRF reports (e.g. live fields only), the fake always reports everything
                builder.Append(GetRootWithSeqs().ToJsonString());
            }
            else if (TryGetNode(path, out JsonNode? node))
            {
                if (wantArrayLength)
                {
                    builder.Append((node is JsonArray array) ? array.Count.ToString(CultureInfo.InvariantCulture) : "null");
                }
                else
                {
                    builder.Append(node?.ToJsonString() ?? "null");
                }
            }
            else
            {
                // ObjectModel::ReportAsJson reports keys that do not match as null
                builder.Append("null");
            }
            builder.Append("}\n");
            return builder.ToString();
        }
    }

    #region Values for expressions
    /// <summary>
    /// Try to get a value for an expression
    /// </summary>
    /// <param name="path">Path of the value</param>
    /// <param name="value">Value converted to int, float, bool, string, object?[] or Dictionary</param>
    /// <returns>Whether the value exists</returns>
    internal bool TryGetValue(string path, out object? value)
    {
        lock (_lock)
        {
            if (TryGetNode(path, out JsonNode? node))
            {
                value = ToValue(node);
                return true;
            }
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Try to get a global variable
    /// </summary>
    /// <param name="name">Name of the variable without global prefix</param>
    /// <param name="value">Value of the variable</param>
    /// <returns>Whether the variable exists</returns>
    internal bool TryGetGlobal(string name, out object? value)
    {
        lock (_lock)
        {
            if (_root["global"] is JsonObject globals && globals.TryGetPropertyValue(name, out JsonNode? node))
            {
                value = ToValue(node);
                return true;
            }
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Create or assign a global variable and increment the "global" sequence number, see RepRap::GlobalUpdated
    /// </summary>
    /// <param name="name">Name of the variable without global prefix</param>
    /// <param name="value">New value</param>
    internal void SetGlobal(string name, object? value)
    {
        lock (_lock)
        {
            if (_root["global"] is not JsonObject globals)
            {
                globals = [];
                _root["global"] = globals;
            }
            globals[name] = ToNode(value);
            IncrementSeq("global");
        }
    }

    /// <summary>
    /// Delete all global variables, e.g. after a firmware reset
    /// </summary>
    internal void ClearGlobals()
    {
        lock (_lock)
        {
            _root["global"] = new JsonObject();
            IncrementSeq("global");
        }
    }

    /// <summary>
    /// Convert a JSON node to a value as DSF reads it from an expression result
    /// (see Reader.ReadEvaluationResult: numbers in arrays and objects become doubles)
    /// </summary>
    /// <param name="node">JSON node</param>
    /// <param name="nested">Whether this node is part of an array or object</param>
    /// <returns>Converted value</returns>
    internal static object? ToValue(JsonNode? node, bool nested = false)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(item => ToValue(item, true)).ToArray();
            case JsonObject obj:
                return obj.ToDictionary(item => item.Key, item => ToValue(item.Value, true));
            case JsonValue value:
                JsonValueKind kind = value.GetValueKind();
                if (kind is JsonValueKind.String)
                {
                    return value.GetValue<string>();
                }
                if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetValue<bool>();
                }
                if (kind is JsonValueKind.Number)
                {
                    if (nested)
                    {
                        return value.GetValue<double>();
                    }
                    if (value.TryGetValue(out int intValue))
                    {
                        return intValue;
                    }
                    if (value.TryGetValue(out float floatValue))
                    {
                        return floatValue;
                    }
                    double doubleValue = value.GetValue<double>();
                    return (doubleValue == Math.Floor(doubleValue) && doubleValue >= int.MinValue && doubleValue <= int.MaxValue) ? (int)doubleValue : (float)doubleValue;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Convert a value to a JSON node
    /// </summary>
    /// <param name="value">Value to convert</param>
    /// <returns>JSON node</returns>
    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool boolValue => JsonValue.Create(boolValue),
        int intValue => JsonValue.Create(intValue),
        uint uintValue => JsonValue.Create(uintValue),
        float floatValue => JsonValue.Create(floatValue),
        double doubleValue => JsonValue.Create(doubleValue),
        char charValue => JsonValue.Create(charValue.ToString()),
        string stringValue => JsonValue.Create(stringValue),
        object?[] array => new JsonArray([.. array.Select(ToNode)]),
        Dictionary<string, object?> dictionary => new JsonObject(dictionary.Select(item => KeyValuePair.Create(item.Key, ToNode(item.Value)))),
        _ => JsonValue.Create(value.ToString())
    };
    #endregion

    /// <summary>
    /// Get the root object with the "seqs" key
    /// </summary>
    /// <returns>Copy of the root object</returns>
    private JsonObject GetRootWithSeqs()
    {
        JsonObject result = _root.DeepClone().AsObject();
        JsonObject seqs = [];
        foreach (string key in _root.Select(item => item.Key))
        {
            if (key is not "limits" and not "seqs")
            {
                seqs[key] = _seqs.TryGetValue(key, out int seq) ? seq : 0;
            }
        }
        result["seqs"] = seqs;
        return result;
    }

    /// <summary>
    /// Try to find a node
    /// </summary>
    /// <param name="path">Path of the node</param>
    /// <param name="node">Found node</param>
    /// <returns>Whether the node exists</returns>
    private bool TryGetNode(string path, out JsonNode? node)
    {
        node = _root;
        foreach (object segment in ParsePath(path))
        {
            if (node is null)
            {
                return false;
            }

            if (segment is string name)
            {
                if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out node))
                {
                    return false;
                }
            }
            else if (node is JsonArray array && (int)segment >= 0 && (int)segment < array.Count)
            {
                node = array[(int)segment];
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Get a child of a node
    /// </summary>
    /// <param name="parent">Parent node</param>
    /// <param name="segment">Property name or array index</param>
    /// <returns>Child node or null</returns>
    private static JsonNode? GetChild(JsonNode parent, object segment)
    {
        if (segment is string name)
        {
            return (parent is JsonObject obj && obj.TryGetPropertyValue(name, out JsonNode? child)) ? child : null;
        }
        return (parent is JsonArray array && (int)segment < array.Count) ? array[(int)segment] : null;
    }

    /// <summary>
    /// Split a path like "move.axes[0].letter" into property names and array indices
    /// </summary>
    /// <param name="path">Path to split</param>
    /// <returns>Property names (strings) and array indices (ints)</returns>
    private static List<object> ParsePath(string path)
    {
        List<object> segments = [];
        foreach (string part in path.Split('.'))
        {
            int bracket = part.IndexOf('[');
            string name = (bracket < 0) ? part : part[..bracket];
            if (name.Length > 0)
            {
                segments.Add(name);
            }
            while (bracket >= 0)
            {
                int end = part.IndexOf(']', bracket);
                if (end < 0)
                {
                    throw new ArgumentException($"Invalid path {path}", nameof(path));
                }
                segments.Add(int.Parse(part[(bracket + 1)..end], CultureInfo.InvariantCulture));
                bracket = part.IndexOf('[', end);
            }
        }
        if (segments.Count == 0 || segments[0] is not string)
        {
            throw new ArgumentException($"Invalid path {path}", nameof(path));
        }
        return segments;
    }
}
