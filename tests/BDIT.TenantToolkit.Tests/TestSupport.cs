using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Standards;

namespace BDIT.TenantToolkit.Tests;

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
}

/// <summary>Temporary portable root with a standards folder so ToolkitPaths and the evidence store can be exercised in isolation.</summary>
internal sealed class TempRoot : IDisposable
{
    public string Root { get; }
    public ToolkitPaths Paths { get; }

    public TempRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "bdit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "standards"));
        Paths = ToolkitPaths.Resolve(Root);
        Paths.EnsureWritableFolders();
    }

    public string WriteStandard(string fileName, string json)
    {
        var file = Path.Combine(Paths.StandardsDirectory, fileName);
        File.WriteAllText(file, json);
        return file;
    }

    public void WriteManifest()
    {
        StandardsManifest.Write(Paths.StandardsDirectory, StandardsManifest.Generate(Paths.StandardsDirectory, "tests", DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TestData
{
    public const string TenantA = "11111111-1111-4111-8111-111111111111";
    public const string TenantB = "22222222-2222-4222-8222-222222222222";
    public const string Emergency = "33333333-3333-4333-8333-333333333333";
    public const string Office = "44444444-4444-4444-8444-444444444444";
    public const string Operator = "55555555-5555-4555-8555-555555555555";
    public const string Mam = "66666666-6666-4666-8666-666666666666";
    public const string ClientId = "77777777-7777-4777-8777-777777777777";

    public const string StandardJson = """
        {
          "schemaVersion": 3,
          "release": "test.1",
          "status": "test",
          "collections": {
            "conditionalAccess": { "api": "v1.0", "path": "/identity/conditionalAccess/policies", "scope": "Policy.Read.All", "write": "Policy.ReadWrite.ConditionalAccess", "label": "Conditional Access policies" },
            "namedLocations": { "api": "v1.0", "path": "/identity/conditionalAccess/namedLocations", "scope": "Policy.Read.All", "label": "Named locations" },
            "compliance": { "api": "v1.0", "path": "/deviceManagement/deviceCompliancePolicies", "scope": "DeviceManagementConfiguration.Read.All", "write": "DeviceManagementConfiguration.ReadWrite.All", "assignments": true, "relationship": "scheduledActionsForRule?$expand=scheduledActionConfigurations", "label": "Device compliance policies" },
            "settingsCatalogue": { "api": "beta", "path": "/deviceManagement/configurationPolicies", "scope": "DeviceManagementConfiguration.Read.All", "assignments": true, "children": "settings", "nameProperty": "name", "label": "Settings catalogue" },
            "groups": { "api": "v1.0", "path": "/groups?$select=id,displayName", "scope": "Group.Read.All", "label": "Groups" },
            "users": { "api": "v1.0", "path": "/users?$select=id,displayName,userPrincipalName", "scope": "User.Read.All", "label": "Users" },
            "licences": { "api": "v1.0", "path": "/subscribedSkus", "scope": "Organization.Read.All", "label": "Subscribed licences" }
          },
          "parameters": [
            { "key": "emergencyAccountIds", "label": "Emergency accounts", "type": "guidList", "required": true },
            { "key": "officeLocationId", "label": "Office location", "type": "guid", "required": false }
          ],
          "controls": [
            {
              "id": "CA-001", "name": "Require MFA", "category": "Conditional Access", "severity": "Critical",
              "purpose": "Require MFA.", "desiredState": "All users.", "businessImpact": "Credential theft.", "engineerAction": "Enable after pilot.",
              "licence": { "servicePlans": [ "AAD_PREMIUM" ] },
              "collection": "conditionalAccess",
              "assessment": { "mode": "settings" },
              "expectedProduction": { "state": "enabled", "assignment": "All users" },
              "safeDeployment": { "state": "disabled", "assignment": "Disabled candidate" },
              "payload": {
                "displayName": "BDIT - CA-001 - Require MFA",
                "state": "disabled",
                "conditions": {
                  "users": { "includeUsers": [ "All" ], "excludeUsers": "{{emergencyAccountIds}}" },
                  "applications": { "includeApplications": [ "All" ] },
                  "clientAppTypes": [ "all" ],
                  "locations": { "includeLocations": [ "All" ], "excludeLocations": [ "{{officeLocationId}}" ] }
                },
                "grantControls": { "operator": "OR", "builtInControls": [ "mfa" ] }
              },
              "equivalence": {
                "note": "Any enabled policy that requires MFA for all users and all cloud apps covers this control however it is named.",
                "signals": [
                  { "key": "allUsers", "label": "Targets all users", "path": "conditions.users.includeUsers", "operator": "contains", "value": "All" },
                  { "key": "allApps", "label": "Targets all cloud apps", "path": "conditions.applications.includeApplications", "operator": "contains", "value": "All" },
                  { "key": "mfa", "label": "Requires multi-factor authentication", "path": "grantControls.builtInControls", "operator": "containsAny", "value": [ "mfa" ] }
                ],
                "caveats": [
                  { "key": "excludedUsers", "label": "Excludes named users", "path": "conditions.users.excludeUsers", "operator": "nonEmpty" }
                ]
              }
            },
            {
              "id": "CA-003", "name": "Block legacy authentication", "category": "Conditional Access", "severity": "Critical",
              "purpose": "Block legacy.", "desiredState": "All users.", "businessImpact": "Bypass.", "engineerAction": "Enable.",
              "collection": "conditionalAccess",
              "assessment": { "mode": "settings" },
              "expectedProduction": { "state": "enabled", "assignment": "All users" },
              "safeDeployment": { "state": "disabled", "assignment": "Disabled candidate" },
              "payload": {
                "displayName": "BDIT - CA-003 - Block legacy authentication",
                "state": "disabled",
                "conditions": {
                  "users": { "includeUsers": [ "All" ], "excludeUsers": "{{emergencyAccountIds}}" },
                  "applications": { "includeApplications": [ "All" ] },
                  "clientAppTypes": [ "exchangeActiveSync", "other" ]
                },
                "grantControls": { "operator": "OR", "builtInControls": [ "block" ] }
              }
            },
            {
              "id": "CMP-WIN-001", "name": "Windows core compliance", "category": "Compliance", "severity": "High",
              "purpose": "Compliance.", "desiredState": "BitLocker etc.", "businessImpact": "Unhealthy devices.", "engineerAction": "Assign after pilot.",
              "collection": "compliance",
              "assessment": { "mode": "settings" },
              "expectedProduction": { "state": "assigned", "assignment": "Managed Windows devices" },
              "safeDeployment": { "state": "unassigned", "assignment": "No assignment" },
              "payload": {
                "@odata.type": "#microsoft.graph.windows10CompliancePolicy",
                "displayName": "BDIT - CMP-WIN-001 - Windows core compliance",
                "bitLockerEnabled": true,
                "secureBootEnabled": true,
                "osMinimumVersion": "10.0.26200.0",
                "scheduledActionsForRule": [ { "ruleName": "PasswordRequired", "scheduledActionConfigurations": [ { "actionType": "block", "gracePeriodHours": 0 } ] } ]
              }
            },
            {
              "id": "ID-001", "name": "Emergency access accounts", "category": "Identity", "severity": "Critical",
              "purpose": "Break glass.", "desiredState": "Two accounts.", "businessImpact": "Lockout.", "engineerAction": "Verify.",
              "collection": "users",
              "assessment": { "mode": "manual", "manualInstructions": "Check the two accounts." },
              "expectedProduction": { "state": "Present", "assignment": "n/a" },
              "safeDeployment": { "state": "", "assignment": "" }
            }
          ]
        }
        """;

    public static StandardCatalogue Standard() => StandardsLoader.Parse(StandardJson, "test.json");

    public static TenantProfile Profile(string tenant = TenantA, bool emergency = true, string office = Office) =>
        ProfileValidator.Validate(new TenantProfile
        {
            Id = "88888888-8888-4888-8888-888888888888",
            Company = "Test client",
            TenantId = tenant,
            Parameters = new TenantParameters
            {
                EmergencyAccountIds = emergency ? new List<string> { Emergency } : new List<string>(),
                OfficeLocationId = office
            }
        }, new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));

    public static TenantSession Session(string tenant = TenantA, SessionMode mode = SessionMode.Deployment, bool operatorVerified = true, string? operatorId = Operator) => new()
    {
        TenantId = tenant,
        TenantName = "Test Ltd",
        PrimaryDomain = "test.example",
        Account = "engineer@test.example",
        AccountObjectId = operatorId ?? "",
        ClientId = ClientId,
        ClientLabel = "test app",
        Mode = mode,
        Scopes = new List<string> { "Policy.Read.All", "Policy.ReadWrite.ConditionalAccess", "DeviceManagementConfiguration.ReadWrite.All" },
        TenantVerified = true,
        OperatorObjectId = operatorId,
        OperatorDisplayName = "Engineer",
        OperatorUpn = "engineer@test.example",
        OperatorVerified = operatorVerified,
        ConnectedAt = "2026-09-11T09:00:00.000Z"
    };

    public static TenantSnapshot Snapshot(StandardCatalogue standard, string tenant = TenantA, DateTimeOffset? capturedAt = null, bool withLicence = true)
    {
        var snapshot = new TenantSnapshot
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenant,
            TenantName = "Test Ltd",
            PrimaryDomain = "test.example",
            ClientLabel = "Test client",
            CapturedAt = Timestamps.Format(capturedAt ?? new DateTimeOffset(2026, 9, 11, 9, 55, 0, TimeSpan.Zero)),
            CapturedBy = "engineer@test.example",
            SessionMode = "Deployment",
            StandardRelease = standard.Release,
            ToolkitVersion = "test"
        };
        foreach (var (key, def) in standard.Collections)
            snapshot.Collections[key] = new CollectionCapture { Status = CaptureStatus.Collected, Api = def.Api, Path = def.Path, Items = new List<JsonObject>(), Count = 0 };
        if (withLicence)
        {
            var sku = ToolkitJson.ParseObject("""{"id":"sku1","skuPartNumber":"SPB","servicePlans":[{"servicePlanName":"AAD_PREMIUM","provisioningStatus":"Success"},{"servicePlanName":"INTUNE_A","provisioningStatus":"Success"}]}""");
            snapshot.Collections["licences"].Items.Add(sku);
            snapshot.Collections["licences"].Count = 1;
        }
        snapshot.Complete = true;
        return snapshot;
    }

    public static JsonObject ConditionalAccessPolicy(string id, string name, string state, IEnumerable<string> excludeUsers, string[]? clientAppTypes = null, string grant = "mfa", bool withLocation = true)
    {
        var obj = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = name,
            ["state"] = state,
            ["createdDateTime"] = "2026-01-01T00:00:00Z",
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject
                {
                    ["includeUsers"] = new JsonArray("All"),
                    ["excludeUsers"] = new JsonArray(excludeUsers.Select(u => (JsonNode?)u).ToArray()),
                    ["includeGroups"] = new JsonArray(),
                    ["excludeGroups"] = new JsonArray()
                },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["clientAppTypes"] = new JsonArray((clientAppTypes ?? new[] { "all" }).Select(c => (JsonNode?)c).ToArray())
            },
            ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray(grant) }
        };
        if (withLocation)
            obj["conditions"]!["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("All"), ["excludeLocations"] = new JsonArray(Office) };
        return obj;
    }

    public static ManagedObjectMappings Mappings(string tenant = TenantA) => new() { TenantId = tenant };
}

/// <summary>Scripted Graph client. Stores objects per collection base path and records every write.</summary>
internal sealed class FakeGraphClient : IGraphClient
{
    private readonly Dictionary<string, List<JsonObject>> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> _singles = new(StringComparer.OrdinalIgnoreCase);
    private int _created;

    public string TenantId { get; set; } = TestData.TenantA;
    public SessionMode Mode { get; set; } = SessionMode.Deployment;
    public List<(GraphWriteMethod Method, string Path, JsonObject Payload)> Writes { get; } = new();
    public List<string> Reads { get; } = new();
    public Func<string, JsonObject, Task>? BeforeWrite { get; set; }
    public Exception? ThrowOnWrite { get; set; }
    public Func<JsonObject, JsonObject>? MutateReadback { get; set; }

    public FakeGraphClient(StandardCatalogue standard)
    {
        foreach (var def in standard.Collections.Values)
            if (!def.Singleton) _collections[def.BasePath] = new List<JsonObject>();
        _singles["/organization"] = ToolkitJson.ParseObject($$"""{"value":[{"id":"{{TestData.TenantA}}","displayName":"Test Ltd","verifiedDomains":[{"name":"test.example","isDefault":true}]}]}""");
        _singles["/me"] = ToolkitJson.ParseObject($$"""{"id":"{{TestData.Operator}}","displayName":"Engineer","userPrincipalName":"engineer@test.example"}""");
    }

    public List<JsonObject> Collection(string basePath) => _collections[basePath];

    public void Add(string basePath, JsonObject item) => _collections[basePath].Add(item);

    private (string Base, string? Id, string? Sub) Resolve(string path)
    {
        var basePath = path.Split('?')[0].TrimEnd('/');
        foreach (var key in _collections.Keys.OrderByDescending(k => k.Length))
        {
            if (string.Equals(basePath, key, StringComparison.OrdinalIgnoreCase)) return (key, null, null);
            if (basePath.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = basePath[(key.Length + 1)..].Split('/', 2);
                return (key, Uri.UnescapeDataString(rest[0]), rest.Length > 1 ? rest[1] : null);
            }
        }
        return (basePath, null, null);
    }

    public Task<JsonObject> GetAsync(GraphApi api, string path, CancellationToken ct)
    {
        Reads.Add(path);
        var (basePath, id, sub) = Resolve(path);
        if (_singles.TryGetValue(basePath, out var single)) return Task.FromResult((JsonObject)single.DeepClone());
        if (_collections.TryGetValue(basePath, out var list))
        {
            if (id is null) return Task.FromResult(new JsonObject { ["value"] = new JsonArray(list.Select(i => (JsonNode?)i.DeepClone()).ToArray()) });
            var obj = list.FirstOrDefault(i => string.Equals(i["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                ?? throw new GraphRequestException(404, "GET", path, "NotFound", "Not found");
            if (sub is null)
            {
                var clone = (JsonObject)obj.DeepClone();
                clone.Remove("_assignments");
                clone.Remove("scheduledActionsForRule");
                return Task.FromResult(MutateReadback is null ? clone : MutateReadback(clone));
            }
            return Task.FromResult(new JsonObject { ["value"] = new JsonArray() });
        }
        throw new GraphRequestException(404, "GET", path, "NotFound", "Unknown path " + path);
    }

    public async Task<IReadOnlyList<JsonObject>> GetAllAsync(GraphApi api, string path, CancellationToken ct)
    {
        Reads.Add(path);
        var (basePath, id, sub) = Resolve(path);
        if (_collections.TryGetValue(basePath, out var list))
        {
            if (id is null) return list.Select(i => (JsonObject)i.DeepClone()).ToList();
            var obj = list.FirstOrDefault(i => string.Equals(i["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                ?? throw new GraphRequestException(404, "GET", path, "NotFound", "Not found");
            if (sub is not null && sub.StartsWith("assignments", StringComparison.OrdinalIgnoreCase))
                return obj["_assignments"] is JsonArray a ? a.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()).ToList() : new List<JsonObject>();
            if (sub is not null && sub.StartsWith("scheduledActionsForRule", StringComparison.OrdinalIgnoreCase))
                return obj["scheduledActionsForRule"] is JsonArray r ? r.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()).ToList() : new List<JsonObject>();
            return new List<JsonObject>();
        }
        var single = await GetAsync(api, path, ct);
        return single["value"] is JsonArray v ? v.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()).ToList() : new List<JsonObject>();
    }

    public async Task<JsonObject> WriteAsync(GraphApi api, GraphWriteMethod method, string path, JsonObject payload, CancellationToken ct)
    {
        if (Mode != SessionMode.Deployment) throw new WriteDeniedException("Write denied: read-only session.");
        if (BeforeWrite is not null) await BeforeWrite(path, payload);
        Writes.Add((method, path, (JsonObject)payload.DeepClone()));
        if (ThrowOnWrite is not null) throw ThrowOnWrite;
        var (basePath, id, _) = Resolve(path);
        var list = _collections[basePath];
        if (method == GraphWriteMethod.Post)
        {
            var created = (JsonObject)payload.DeepClone();
            created["id"] = $"aaaaaaaa-0000-4000-8000-{++_created:D12}";
            created["createdDateTime"] = "2026-09-11T10:00:00Z";
            list.Add(created);
            return (JsonObject)created.DeepClone();
        }
        var existing = list.First(i => string.Equals(i["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
        foreach (var pair in payload) existing[pair.Key] = pair.Value?.DeepClone();
        return new JsonObject();
    }
}
