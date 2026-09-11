using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void Canonical_form_is_independent_of_key_order_and_whitespace()
    {
        var a = ToolkitJson.ParseNode("""{ "b": [1, 2, {"z": true, "y": "x"}], "a": null }""");
        var b = ToolkitJson.ParseNode("""{"a":null,"b":[1,2,{"y":"x","z":true}]}""");
        Assert.Equal(CanonicalJson.Serialize(a), CanonicalJson.Serialize(b));
        Assert.Equal(CanonicalJson.Sha256(a), CanonicalJson.Sha256(b));
        Assert.Equal("{\"a\":null,\"b\":[1,2,{\"y\":\"x\",\"z\":true}]}", CanonicalJson.Serialize(a));
    }

    [Fact]
    public void Subset_ignores_server_metadata_but_not_setting_changes()
    {
        var actual = ToolkitJson.ParseNode("""{"id":"server","value":true,"list":[{"id":"1","v":2},{"v":true}]}""");
        Assert.True(CanonicalJson.IsSubset(actual, ToolkitJson.ParseNode("""{"value":true,"list":[{"v":true},{"v":2}]}""")));
        Assert.False(CanonicalJson.IsSubset(actual, ToolkitJson.ParseNode("""{"value":false}""")));
        Assert.False(CanonicalJson.IsSubset(ToolkitJson.ParseNode("""["a","b"]"""), ToolkitJson.ParseNode("""["a","a"]""")));
        Assert.False(CanonicalJson.IsSubset(ToolkitJson.ParseNode("""["a"]"""), ToolkitJson.ParseNode("""["a","b"]""")));
        Assert.True(CanonicalJson.IsSubset(ToolkitJson.ParseNode("1.0"), ToolkitJson.ParseNode("1")));
    }

    [Fact]
    public void Template_injects_arrays_and_embeds_scalars()
    {
        var template = ToolkitJson.ParseNode("""{"excludeUsers":"{{ids}}","uri":"./Vendor/{{tenantId}}/x","fixed":"text"}""");
        var values = new Dictionary<string, JsonNode?>
        {
            ["ids"] = new JsonArray("a", "b"),
            ["tenantId"] = JsonValue.Create("t1")
        };
        var resolved = (JsonObject)CanonicalJson.Resolve(template, values)!;
        Assert.Equal(2, resolved["excludeUsers"]!.AsArray().Count);
        Assert.Equal("./Vendor/t1/x", resolved["uri"]!.GetValue<string>());
        Assert.Equal("text", resolved["fixed"]!.GetValue<string>());
    }

    [Fact]
    public void Template_missing_or_empty_parameter_fails_loudly()
    {
        var template = ToolkitJson.ParseNode("""{"excludeUsers":"{{ids}}"}""");
        Assert.Throws<MissingParameterException>(() => CanonicalJson.Resolve(template, new Dictionary<string, JsonNode?>()));
        Assert.Throws<MissingParameterException>(() => CanonicalJson.Resolve(template, new Dictionary<string, JsonNode?> { ["ids"] = new JsonArray() }));
        Assert.Throws<MissingParameterException>(() => CanonicalJson.Resolve(template, new Dictionary<string, JsonNode?> { ["ids"] = JsonValue.Create("") }));
    }

    [Fact]
    public void Normalise_makes_array_order_irrelevant_and_drops_keys()
    {
        var a = ToolkitJson.ParseNode("""{"items":[{"id":"b"},{"id":"a"}],"@odata.etag":"1"}""");
        var b = ToolkitJson.ParseNode("""{"items":[{"id":"a"},{"id":"b"}],"@odata.etag":"2"}""");
        var drop = new HashSet<string> { "@odata.etag" };
        Assert.Equal(CanonicalJson.Serialize(CanonicalJson.Normalise(a, drop)), CanonicalJson.Serialize(CanonicalJson.Normalise(b, drop)));
    }
}
