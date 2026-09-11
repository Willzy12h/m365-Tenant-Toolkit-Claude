using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Core.Safety;

/// <summary>
/// Pure safety rules applied twice: by the planner when a payload is built and by the Graph client immediately before
/// a write. Catalogue content can never relax them because they run after template resolution.
/// </summary>
public static class ConditionalAccessSafety
{
    public const string SafeState = "disabled";
    public const string ConditionalAccessPolicyPath = "/identity/conditionalAccess/policies";

    public static bool IsConditionalAccessPath(string basePath) =>
        basePath.TrimEnd('/').EndsWith(ConditionalAccessPolicyPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsConditionalAccess(CollectionDefinition definition) => IsConditionalAccessPath(definition.BasePath);

    /// <summary>Forces the candidate into the only state the toolkit may create.</summary>
    public static void EnforceSafeState(JsonObject payload)
    {
        payload["state"] = SafeState;
    }

    /// <summary>Adds user object IDs to conditions.users.excludeUsers, creating the structure if needed and never duplicating.</summary>
    public static IReadOnlyList<string> InjectUserExclusions(JsonObject payload, IEnumerable<string> userObjectIds)
    {
        if (payload["conditions"] is not JsonObject conditions)
        {
            conditions = new JsonObject();
            payload["conditions"] = conditions;
        }
        if (conditions["users"] is not JsonObject users)
        {
            users = new JsonObject();
            conditions["users"] = users;
        }
        var existing = new List<string>();
        if (users["excludeUsers"] is JsonArray current)
            foreach (var item in current)
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) existing.Add(s);

        foreach (var id in userObjectIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!existing.Any(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase))) existing.Add(id);
        }
        var array = new JsonArray();
        foreach (var e in existing) array.Add(e);
        users["excludeUsers"] = array;
        return existing;
    }

    public static IReadOnlyList<string> ExcludedUsers(JsonObject payload)
    {
        var list = new List<string>();
        if (payload["conditions"] is JsonObject c && c["users"] is JsonObject u && u["excludeUsers"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        return list;
    }

    public static string? State(JsonObject payload) =>
        payload["state"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Throws unless the payload is a disabled policy with no assignment keys.</summary>
    public static void AssertSafeCandidate(JsonObject payload)
    {
        var state = State(payload);
        if (!string.Equals(state, SafeState, StringComparison.Ordinal))
            throw new SafetyViolationException($"Conditional Access candidates must be created with state '{SafeState}'. The payload requested '{state ?? "(none)"}'. Write refused.");
        if (payload.ContainsKey("assignments"))
            throw new SafetyViolationException("Conditional Access payloads must not carry an 'assignments' element. Write refused.");
    }
}

/// <summary>Generic payload rules that apply to every write regardless of object type.</summary>
public static class WritePayloadGuard
{
    public static void Assert(CollectionDefinition definition, JsonObject payload)
    {
        if (payload.ContainsKey("assignments"))
            throw new SafetyViolationException("Assignment writes are not supported. Objects are created unassigned; assignment is an explicit post-creation engineering step.");
        if (ConditionalAccessSafety.IsConditionalAccess(definition))
            ConditionalAccessSafety.AssertSafeCandidate(payload);
    }
}
