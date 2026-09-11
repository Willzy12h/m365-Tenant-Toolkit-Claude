using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Core.Graph;

public enum GraphWriteMethod { Post, Patch }

/// <summary>
/// Minimal Microsoft Graph contract used by the engine. Paths are relative to the API version root, for example
/// "/identity/conditionalAccess/policies?$select=id". Implementations enforce session mode, route allow-lists and
/// payload safety; callers must still treat every write as a deliberate, reviewed action.
/// </summary>
public interface IGraphClient
{
    string TenantId { get; }
    SessionMode Mode { get; }

    Task<JsonObject> GetAsync(GraphApi api, string path, CancellationToken ct);

    /// <summary>Follows @odata.nextLink pages. Throws if a page cannot be read; never returns a partial list silently.</summary>
    Task<IReadOnlyList<JsonObject>> GetAllAsync(GraphApi api, string path, CancellationToken ct);

    /// <summary>Creates or updates an object. Never retried. Throws <see cref="AmbiguousWriteException"/> when the outcome is unknown.</summary>
    Task<JsonObject> WriteAsync(GraphApi api, GraphWriteMethod method, string path, JsonObject payload, CancellationToken ct);
}
