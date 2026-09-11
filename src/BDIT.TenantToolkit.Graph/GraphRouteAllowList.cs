using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Graph;

public sealed record GraphRoute(GraphApi Api, string BasePath, string Scope, string? WriteScope, string CollectionKey)
{
    public bool Writable => !string.IsNullOrWhiteSpace(WriteScope);
}

/// <summary>
/// The Graph client only calls routes derived from the loaded standard plus a fixed set of diagnostic reads.
/// Writes are further restricted to collections that declare a write scope, and only to the collection root (create)
/// or a single object by GUID (update).
/// </summary>
public sealed class GraphRouteAllowList
{
    public static readonly IReadOnlyList<GraphRoute> DiagnosticRoutes = new[]
    {
        new GraphRoute(GraphApi.V1, "/organization", "Organization.Read.All", null, "organization"),
        new GraphRoute(GraphApi.V1, "/me", "User.Read", null, "me"),
        new GraphRoute(GraphApi.V1, "/roleManagement/directory/roleAssignments", "RoleManagement.Read.Directory", null, "roleAssignments"),
        new GraphRoute(GraphApi.V1, "/subscribedSkus", "Organization.Read.All", null, "subscribedSkus"),
        new GraphRoute(GraphApi.V1, "/users", "User.Read.All", null, "users"),
        new GraphRoute(GraphApi.V1, "/groups", "Group.Read.All", null, "groups")
    };

    private readonly List<GraphRoute> _routes = new();

    public IReadOnlyList<GraphRoute> Routes => _routes;

    public static GraphRouteAllowList FromStandard(StandardCatalogue standard)
    {
        var list = new GraphRouteAllowList();
        foreach (var route in DiagnosticRoutes) list._routes.Add(route);
        foreach (var (key, def) in standard.Collections)
        {
            if (!def.BasePath.StartsWith("/", StringComparison.Ordinal))
                throw new ConfigurationException($"Collection '{key}' path must start with '/'.");
            list._routes.Add(new GraphRoute(def.ApiVersion, def.BasePath.TrimEnd('/'), def.Scope, def.Write, key));
        }
        return list;
    }

    public static GraphRouteAllowList Only(IEnumerable<GraphRoute> routes)
    {
        var list = new GraphRouteAllowList();
        list._routes.AddRange(routes);
        return list;
    }

    public GraphRoute? MatchRead(GraphApi api, string path)
    {
        var basePath = BasePathOf(path);
        return _routes
            .Where(r => r.Api == api && (string.Equals(basePath, r.BasePath, StringComparison.OrdinalIgnoreCase)
                                          || basePath.StartsWith(r.BasePath + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.BasePath.Length)
            .FirstOrDefault();
    }

    /// <summary>Returns the writable route when the path is exactly the collection root or root/{guid}.</summary>
    public GraphRoute? MatchWrite(GraphApi api, string path, out bool targetsExistingObject)
    {
        targetsExistingObject = false;
        var basePath = BasePathOf(path);
        if (path.Contains('?', StringComparison.Ordinal)) return null;
        foreach (var route in _routes.Where(r => r.Writable && r.Api == api))
        {
            if (string.Equals(basePath, route.BasePath, StringComparison.OrdinalIgnoreCase)) return route;
            if (basePath.StartsWith(route.BasePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = basePath[(route.BasePath.Length + 1)..];
                if (ProfileValidator.IsGuid(remainder))
                {
                    targetsExistingObject = true;
                    return route;
                }
            }
        }
        return null;
    }

    public static void ValidatePathSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            throw new ConfigurationException($"Graph path must be relative to the API root and start with '/': '{path}'.");
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new ConfigurationException($"Graph path contains disallowed characters: '{path}'.");
    }

    public static string BasePathOf(string path)
    {
        var q = path.IndexOf('?', StringComparison.Ordinal);
        var basePath = q >= 0 ? path[..q] : path;
        return basePath.Length > 1 ? basePath.TrimEnd('/') : basePath;
    }
}
