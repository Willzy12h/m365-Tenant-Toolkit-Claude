using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Engine.Assessment;

/// <summary>Turns object IDs found in policies and assignments into readable names using the captured directory collections.</summary>
public sealed class NameResolver
{
    private static readonly IReadOnlyDictionary<string, string> WellKnown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = "All",
        ["None"] = "None",
        ["GuestsOrExternalUsers"] = "Guests or external users",
        ["Office365"] = "Office 365 (all)",
        ["MicrosoftAdminPortals"] = "Microsoft Admin Portals",
        ["AllTrusted"] = "All trusted locations",
        ["00000002-0000-0ff1-ce00-000000000000"] = "Exchange Online",
        ["00000003-0000-0ff1-ce00-000000000000"] = "SharePoint Online",
        ["00000003-0000-0000-c000-000000000000"] = "Microsoft Graph"
    };

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public static NameResolver FromSnapshot(TenantSnapshot? snapshot)
    {
        var resolver = new NameResolver();
        if (snapshot is null) return resolver;
        foreach (var key in new[] { "groups", "users", "namedLocations", "apps", "licences" })
        {
            if (!snapshot.Collections.TryGetValue(key, out var capture)) continue;
            foreach (var item in capture.Items)
            {
                var id = item["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                var name = item["displayName"]?.GetValue<string>() ?? item["userPrincipalName"]?.GetValue<string>() ?? item["skuPartNumber"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name)) resolver._names[id] = name;
            }
        }
        return resolver;
    }

    public void Add(string id, string name)
    {
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) _names[id] = name;
    }

    public bool Knows(string id) => _names.ContainsKey(id) || WellKnown.ContainsKey(id);

    public string Display(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (_names.TryGetValue(id, out var name)) return $"{name} [{id}]";
        if (WellKnown.TryGetValue(id, out var known)) return known;
        return ProfileValidator.IsGuid(id) ? $"Unresolved [{id}]" : id;
    }

    /// <summary>Renders a node as compact text with known IDs replaced by names. Used for report cells.</summary>
    public string Render(JsonNode? node)
    {
        switch (node)
        {
            case null: return "";
            case JsonValue v when v.TryGetValue<string>(out var s): return Knows(s) ? Display(s) : s;
            case JsonValue v: return CanonicalJson.ScalarText(v);
            case JsonArray arr:
                return arr.Count == 0 ? "[]" : string.Join("; ", arr.Select(Render));
            case JsonObject obj:
                return obj.Count == 0 ? "{}" : string.Join("; ", obj.Select(p => p.Key + "=" + Render(p.Value)));
            default: return node.ToJsonString();
        }
    }

    /// <summary>Summarises Conditional Access user targeting or Intune assignments in one line.</summary>
    public string AssignmentSummary(JsonObject item)
    {
        if (item["conditions"] is JsonObject conditions && conditions["users"] is JsonObject users)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "includeUsers", "includeGroups", "includeRoles", "excludeUsers", "excludeGroups", "excludeRoles" })
                if (users[key] is JsonArray arr && arr.Count > 0)
                    parts.Add(key + ": " + string.Join(", ", arr.Select(Render)));
            return parts.Count == 0 ? "No user targeting" : string.Join(" | ", parts);
        }
        if (item[Collection.TenantCollector.AssignmentsUnknownKey] is not null) return "Assignments could not be read";
        if (item[Collection.TenantCollector.AssignmentsKey] is JsonArray assignments)
        {
            if (assignments.Count == 0) return "Unassigned";
            var targets = new List<string>();
            foreach (var a in assignments)
            {
                if (a is not JsonObject ao || ao["target"] is not JsonObject target) continue;
                var type = target["@odata.type"]?.GetValue<string>() ?? "";
                var groupId = target["groupId"]?.GetValue<string>();
                var label = type.Contains("allLicensedUsers", StringComparison.OrdinalIgnoreCase) ? "All users"
                    : type.Contains("allDevices", StringComparison.OrdinalIgnoreCase) ? "All devices"
                    : type.Contains("exclusion", StringComparison.OrdinalIgnoreCase) ? "Exclude " + Display(groupId)
                    : groupId is not null ? Display(groupId) : type;
                targets.Add(label);
            }
            return targets.Count == 0 ? "Assigned (targets unreadable)" : string.Join(", ", targets);
        }
        return "";
    }
}
