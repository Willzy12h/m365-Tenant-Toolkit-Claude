using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BDIT.TenantToolkit.Core.Json;

/// <summary>
/// Deterministic JSON helpers used for integrity digests, subset comparison and template resolution.
/// The canonical form sorts object keys ordinally, omits whitespace and renders numbers invariantly,
/// so the same logical document always produces the same SHA-256 digest regardless of key order.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    public static string SerializeValue<T>(T value) => Serialize(ToolkitJson.ToNode(value));

    public static string Sha256(JsonNode? node) => Sha256Hex(Serialize(node));

    public static string Sha256Value<T>(T value) => Sha256Hex(SerializeValue(value));

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(pair.Key));
                    sb.Append(':');
                    Write(pair.Value, sb);
                }
                sb.Append('}');
                return;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                return;
            case JsonValue value:
                sb.Append(ScalarText(value));
                return;
            default:
                sb.Append(node.ToJsonString());
                return;
        }
    }

    /// <summary>Renders a scalar in a normalised textual form used for both canonical output and equality.</summary>
    public static string ScalarText(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (value.TryGetValue<string>(out var s)) return JsonSerializer.Serialize(s);
        if (value.TryGetValue<decimal>(out var d)) return d.ToString("G29", CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var dbl)) return dbl.ToString("R", CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
                JsonValueKind.Number => element.TryGetDecimal(out var dec)
                    ? dec.ToString("G29", CultureInfo.InvariantCulture)
                    : element.GetRawText(),
                _ => element.GetRawText()
            };
        }
        return value.ToJsonString();
    }

    /// <summary>
    /// Returns true when every value present in <paramref name="wanted"/> is present in <paramref name="actual"/>.
    /// Objects are compared key by key (extra keys in actual are ignored, so server metadata does not break matches).
    /// Arrays must have the same length and every wanted element must match a distinct actual element, in any order.
    /// Scalars must be equal after normalisation.
    /// </summary>
    public static bool IsSubset(JsonNode? actual, JsonNode? wanted)
    {
        switch (wanted)
        {
            case null:
                return actual is null;
            case JsonArray wantedArray:
            {
                if (actual is not JsonArray actualArray) return false;
                if (actualArray.Count != wantedArray.Count) return false;
                var remaining = actualArray.ToList();
                foreach (var w in wantedArray)
                {
                    var index = remaining.FindIndex(a => IsSubset(a, w));
                    if (index < 0) return false;
                    remaining.RemoveAt(index);
                }
                return true;
            }
            case JsonObject wantedObject:
            {
                if (actual is not JsonObject actualObject) return false;
                foreach (var pair in wantedObject)
                {
                    actualObject.TryGetPropertyValue(pair.Key, out var actualMember);
                    if (!IsSubset(actualMember, pair.Value)) return false;
                }
                return true;
            }
            case JsonValue wantedValue:
                return actual is JsonValue actualValue && ScalarText(actualValue) == ScalarText(wantedValue);
            default:
                return false;
        }
    }

    public static bool ScalarEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb) return ScalarText(va) == ScalarText(vb);
        return Serialize(a) == Serialize(b);
    }

    /// <summary>
    /// Resolves <c>{{parameter}}</c> placeholders. A string consisting solely of a placeholder is replaced by the
    /// parameter node itself (so arrays can be injected); placeholders embedded in longer strings are replaced textually.
    /// Missing or empty parameters raise <see cref="MissingParameterException"/> so absence is never silently written.
    /// </summary>
    public static JsonNode? Resolve(JsonNode? template, IReadOnlyDictionary<string, JsonNode?> parameters)
    {
        switch (template)
        {
            case null:
                return null;
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr) result.Add(Resolve(item, parameters));
                return result;
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var pair in obj) result[pair.Key] = Resolve(pair.Value, parameters);
                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
            {
                if (text.Length > 4 && text.StartsWith("{{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal)
                    && !text[2..^2].Contains("{{", StringComparison.Ordinal) && IsIdentifier(text[2..^2]))
                {
                    var key = text[2..^2];
                    var node = Lookup(parameters, key);
                    return node?.DeepClone();
                }
                if (!text.Contains("{{", StringComparison.Ordinal)) return JsonValue.Create(text);
                var sb = new StringBuilder();
                var i = 0;
                while (i < text.Length)
                {
                    var start = text.IndexOf("{{", i, StringComparison.Ordinal);
                    if (start < 0) { sb.Append(text, i, text.Length - i); break; }
                    var end = text.IndexOf("}}", start + 2, StringComparison.Ordinal);
                    if (end < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, start - i);
                    var key = text[(start + 2)..end];
                    if (!IsIdentifier(key)) throw new ConfigurationException($"Invalid template placeholder '{{{{{key}}}}}'.");
                    var node = Lookup(parameters, key);
                    if (node is JsonValue scalar && scalar.TryGetValue<string>(out var s)) sb.Append(s);
                    else if (node is JsonValue other) sb.Append(ScalarText(other).Trim('"'));
                    else throw new ConfigurationException($"Parameter '{key}' cannot be embedded inside a string because it is not a scalar.");
                    i = end + 2;
                }
                return JsonValue.Create(sb.ToString());
            }
            case JsonValue value:
                return value.DeepClone();
            default:
                return template.DeepClone();
        }
    }

    private static JsonNode? Lookup(IReadOnlyDictionary<string, JsonNode?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var node) || node is null) throw new MissingParameterException(key);
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)) throw new MissingParameterException(key);
        if (node is JsonArray a && a.Count == 0) throw new MissingParameterException(key);
        return node;
    }

    private static bool IsIdentifier(string key) => key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Flattens an object into dotted leaf paths, which is how property-level differences are reported.</summary>
    public static IReadOnlyList<(string Path, JsonNode? Value)> Leaves(JsonNode? node, string prefix = "")
    {
        var list = new List<(string, JsonNode?)>();
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                list.AddRange(Leaves(pair.Value, prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key));
            return list;
        }
        list.Add((prefix, node));
        return list;
    }

    /// <summary>Navigates a dotted path produced by <see cref="Leaves"/>.</summary>
    public static JsonNode? At(JsonNode? node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        var current = node;
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next)) current = next;
            else return null;
        }
        return current;
    }

    /// <summary>Order-insensitive normalisation used by drift comparison: arrays are sorted by canonical text.</summary>
    public static JsonNode? Normalise(JsonNode? node, ISet<string>? dropKeys = null)
    {
        switch (node)
        {
            case null: return null;
            case JsonArray arr:
            {
                var items = arr.Select(i => Normalise(i, dropKeys)).ToList();
                items.Sort((x, y) => string.CompareOrdinal(Serialize(x), Serialize(y)));
                var result = new JsonArray();
                foreach (var item in items) result.Add(item);
                return result;
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (dropKeys is not null && dropKeys.Contains(pair.Key)) continue;
                    result[pair.Key] = Normalise(pair.Value, dropKeys);
                }
                return result;
            }
            default:
                return node.DeepClone();
        }
    }
}
