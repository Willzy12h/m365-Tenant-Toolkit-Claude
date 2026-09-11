using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BDIT.TenantToolkit.Core.Json;

/// <summary>Shared serializer settings so evidence files, settings and catalogues all use one JSON dialect.</summary>
public static class ToolkitJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, Options);
        return value is null ? throw new ConfigurationException("JSON document was empty or null.") : value;
    }

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static JsonNode? ParseNode(string json) => JsonNode.Parse(json, NodeOptions, DocumentOptions);

    public static JsonObject ParseObject(string json)
    {
        var node = ParseNode(json);
        return node as JsonObject ?? throw new ConfigurationException("Expected a JSON object.");
    }
}
