using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Engine.Standards;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class StandardsTests
{
    private static JsonObject Control(JsonObject root, string id) =>
        root["controls"]!.AsArray().OfType<JsonObject>().First(c => c["id"]!.GetValue<string>() == id);

    private static string Mutate(Action<JsonObject> change)
    {
        var root = ToolkitJson.ParseObject(TestData.StandardJson);
        change(root);
        return root.ToJsonString(ToolkitJson.Options);
    }

    [Fact]
    public void Valid_standard_parses_with_recipes_and_manual_controls()
    {
        var standard = TestData.Standard();
        Assert.Equal("test.1", standard.Release);
        Assert.Equal(4, standard.Controls.Count);
        Assert.Equal(3, standard.Controls.Count(c => c.HasRecipe));
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", standard.WriteScopes());
        Assert.True(standard.Collections["settingsCatalogue"].ApiVersion == Core.Models.GraphApi.Beta);
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        var json = Mutate(r => r["schemaVersion"] = 2);
        var ex = Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(json, "x.json"));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_control_ids_are_rejected()
    {
        var json = Mutate(r => Control(r, "CA-003")["id"] = "CA-001");
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(json, "x.json"));
    }

    [Fact]
    public void Conditional_access_recipe_cannot_request_enabled_state()
    {
        var json = Mutate(r => Control(r, "CA-001")["payload"]!["state"] = "enabled");
        var ex = Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(json, "x.json"));
        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conditional_access_recipe_must_declare_disabled_safe_state()
    {
        var json = Mutate(r => Control(r, "CA-001")["safeDeployment"]!["state"] = "reportOnly");
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(json, "x.json"));
    }

    [Fact]
    public void Payload_with_assignments_or_manual_control_with_payload_is_rejected()
    {
        var withAssignments = Mutate(r => Control(r, "CMP-WIN-001")["payload"]!["assignments"] = new JsonArray());
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(withAssignments, "x.json"));
        var manualWithPayload = Mutate(r => Control(r, "ID-001")["payload"] = new JsonObject { ["displayName"] = "x" });
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(manualWithPayload, "x.json"));
        var unknownCollection = Mutate(r => Control(r, "CA-001")["collection"] = "nope");
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(unknownCollection, "x.json"));
        var versionInPath = Mutate(r => r["collections"]!["groups"]!["path"] = "/v1.0/groups");
        Assert.Throws<ConfigurationException>(() => StandardsLoader.Parse(versionInPath, "x.json"));
    }

    [Fact]
    public void Missing_manifest_blocks_load()
    {
        using var root = new TempRoot();
        root.WriteStandard("test.json", TestData.StandardJson);
        var loader = new StandardsLoader(root.Paths, NullLog.Instance);
        Assert.Throws<IntegrityException>(() => loader.Load("test.json"));
    }

    [Fact]
    public void Modified_standard_blocks_load_and_intact_standard_loads()
    {
        using var root = new TempRoot();
        var file = root.WriteStandard("test.json", TestData.StandardJson);
        root.WriteManifest();
        var loader = new StandardsLoader(root.Paths, NullLog.Instance);
        var loaded = loader.Load("test.json");
        Assert.Equal(64, loaded.IntegrityDigest.Length);

        File.AppendAllText(file, "\n");
        var ex = Assert.Throws<IntegrityException>(() => loader.Load("test.json"));
        Assert.Contains("modified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unlisted_file_blocks_load()
    {
        using var root = new TempRoot();
        root.WriteStandard("test.json", TestData.StandardJson);
        root.WriteManifest();
        root.WriteStandard("later.json", Mutate(r => r["release"] = "test.2"));
        var loader = new StandardsLoader(root.Paths, NullLog.Instance);
        Assert.Throws<IntegrityException>(() => loader.Load("later.json"));
        Assert.Equal(2, loader.ListReleases().Count);
    }

    [Fact]
    public void Shipped_standard_release_is_valid()
    {
        var repoStandards = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "standards"));
        if (!Directory.Exists(repoStandards)) return;
        foreach (var file in Directory.EnumerateFiles(repoStandards, "*.json").Where(f => !f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            var catalogue = StandardsLoader.Parse(File.ReadAllText(file), Path.GetFileName(file));
            Assert.NotEmpty(catalogue.Controls);
            foreach (var control in catalogue.Controls.Where(c => c.Collection == "conditionalAccess" && c.HasRecipe))
                Assert.Equal("disabled", control.Payload!["state"]!.GetValue<string>());
        }
    }
}
