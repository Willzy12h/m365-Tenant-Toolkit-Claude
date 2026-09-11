using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;
using BDIT.TenantToolkit.Graph.Auth;

namespace BDIT.TenantToolkit.Graph;

public sealed class GraphClientOptions
{
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(100);
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(300);
    public int MaxReadAttempts { get; init; } = 5;
    public int MaxPages { get; init; } = 1000;
    public int MaxItems { get; init; } = 50000;
    /// <summary>Base delay for exponential back-off when no Retry-After header is present.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Test hook: when false, back-off delays are skipped.</summary>
    public bool Sleep { get; init; } = true;
}

/// <summary>
/// Guarded Microsoft Graph client.
///  - v1.0 and beta are separate roots chosen per call; nothing ever falls back from one to the other.
///  - Reads may retry on 429/5xx/transient failures, honouring Retry-After (capped); writes are never retried.
///  - Every route must be on the allow-list derived from the loaded standard.
///  - Writes require a deployment session and a payload that passes the safety guard.
///  - Bodies and tokens are never logged.
/// </summary>
public sealed class GraphClient : IGraphClient
{
    public const string Host = "https://graph.microsoft.com";

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly GraphRouteAllowList _routes;
    private readonly GraphClientOptions _options;
    private readonly IToolkitLog _log;

    public string TenantId { get; }
    public SessionMode Mode { get; }

    public GraphClient(HttpClient http, IAccessTokenProvider tokens, string tenantId, SessionMode mode, GraphRouteAllowList routes, GraphClientOptions options, IToolkitLog log)
    {
        _http = http;
        _tokens = tokens;
        _routes = routes;
        _options = options;
        _log = log;
        TenantId = tenantId;
        Mode = mode;
    }

    public static string Root(GraphApi api) => api == GraphApi.Beta ? Host + "/beta" : Host + "/v1.0";

    public async Task<JsonObject> GetAsync(GraphApi api, string path, CancellationToken ct)
    {
        GraphRouteAllowList.ValidatePathSyntax(path);
        var route = _routes.MatchRead(api, path)
            ?? throw new WriteDeniedException($"Graph route is outside the allow-list for this standard: {api} {GraphRouteAllowList.BasePathOf(path)}");
        var node = await SendReadAsync(api, Root(api) + path, route, ct);
        return node as JsonObject ?? throw new GraphRequestException(200, "GET", path, null, "Graph returned a non-object body.");
    }

    public async Task<IReadOnlyList<JsonObject>> GetAllAsync(GraphApi api, string path, CancellationToken ct)
    {
        GraphRouteAllowList.ValidatePathSyntax(path);
        var route = _routes.MatchRead(api, path)
            ?? throw new WriteDeniedException($"Graph route is outside the allow-list for this standard: {api} {GraphRouteAllowList.BasePathOf(path)}");

        var items = new List<JsonObject>();
        var url = Root(api) + path;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        while (true)
        {
            if (!seen.Add(url) || ++pages > _options.MaxPages)
                throw new GraphRequestException(0, "GET", path, "pagination", "Pagination did not complete (loop or page limit). Collection marked incomplete.");
            var page = await SendReadAsync(api, url, route, ct) as JsonObject
                ?? throw new GraphRequestException(200, "GET", path, null, "Graph returned a non-object page.");
            if (page["value"] is not JsonArray value)
                throw new GraphRequestException(200, "GET", path, null, "Expected a Graph collection ('value' array) but none was returned.");
            foreach (var item in value)
            {
                if (item is JsonObject obj) items.Add((JsonObject)obj.DeepClone());
            }
            if (items.Count > _options.MaxItems)
                throw new GraphRequestException(0, "GET", path, "limit", "Collection safety limit exceeded; refusing to continue.");
            var next = page["@odata.nextLink"]?.GetValue<string>();
            if (string.IsNullOrEmpty(next)) break;
            url = ValidateNextLink(api, next, route);
        }
        return items;
    }

    public async Task<JsonObject> WriteAsync(GraphApi api, GraphWriteMethod method, string path, JsonObject payload, CancellationToken ct)
    {
        GraphRouteAllowList.ValidatePathSyntax(path);
        if (Mode != SessionMode.Deployment)
            throw new WriteDeniedException("Write denied: this session is read-only. Connect with deployment access, capture a fresh snapshot and review a new plan.");
        var route = _routes.MatchWrite(api, path, out var existing)
            ?? throw new WriteDeniedException($"Write route is not permitted: {api} {path}. Only collection roots and single objects of writable collections are accepted.");
        if (method == GraphWriteMethod.Patch && !existing)
            throw new WriteDeniedException("PATCH requires a single object path ending in the object ID.");
        if (method == GraphWriteMethod.Post && existing)
            throw new WriteDeniedException("POST must target the collection root, not an existing object.");

        var definition = new CollectionDefinition { Api = api == GraphApi.Beta ? "beta" : "v1.0", Path = route.BasePath, Write = route.WriteScope };
        WritePayloadGuard.Assert(definition, payload);

        var url = Root(api) + path;
        var body = payload.ToJsonString(ToolkitJson.Compact);
        var httpMethod = method == GraphWriteMethod.Post ? HttpMethod.Post : HttpMethod.Patch;
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            var token = await _tokens.GetAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(httpMethod, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.WriteTimeout);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Error("Graph", $"{httpMethod.Method} {Describe(path)} timed out after {LogFormat.Ms(sw.ElapsedMilliseconds)}; outcome unknown.", null, TenantId);
            throw new AmbiguousWriteException($"The write to {GraphRouteAllowList.BasePathOf(path)} timed out. Its outcome is unknown; reassess the tenant and reconcile before retrying.", null);
        }
        catch (HttpRequestException ex)
        {
            _log.Error("Graph", $"{httpMethod.Method} {Describe(path)} failed at transport level; outcome unknown.", ex, TenantId);
            throw new AmbiguousWriteException($"The write to {GraphRouteAllowList.BasePathOf(path)} failed at the network level ({ex.Message}). Its outcome is unknown; reassess before retrying.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            _log.Info("Graph", $"{httpMethod.Method} {Describe(path)} -> {status} in {LogFormat.Ms(sw.ElapsedMilliseconds)}", TenantId);
            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
                return ToolkitJson.ParseNode(text) as JsonObject ?? new JsonObject();
            }
            if (status is 502 or 503 or 504)
                throw new AmbiguousWriteException($"Graph returned HTTP {status} for the write to {GraphRouteAllowList.BasePathOf(path)}. The gateway response does not prove the write was rejected; reconcile before retrying.", null);
            throw BuildError(status, httpMethod.Method, path, text, route);
        }
    }

    private async Task<JsonNode?> SendReadAsync(GraphApi api, string url, GraphRoute route, CancellationToken ct)
    {
        var path = url.Length > Root(api).Length ? url[Root(api).Length..] : url;
        var attempt = 0;
        var refreshedToken = false;
        while (true)
        {
            attempt++;
            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                var token = await _tokens.GetAccessTokenAsync(ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.ReadTimeout);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < _options.MaxReadAttempts)
            {
                _log.Warn("Graph", $"GET {Describe(path)} timed out (attempt {attempt}); retrying.", TenantId);
                await BackoffAsync(attempt, null, ct);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GraphRequestException(0, "GET", path, "timeout", $"GET {GraphRouteAllowList.BasePathOf(path)} timed out after {attempt} attempts.");
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxReadAttempts)
            {
                _log.Warn("Graph", $"GET {Describe(path)} transport failure (attempt {attempt}): {ex.Message}; retrying.", TenantId);
                await BackoffAsync(attempt, null, ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new GraphRequestException(0, "GET", path, "network", $"GET {GraphRouteAllowList.BasePathOf(path)} failed: {ex.Message}");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(ct);
                _log.Debug("Graph", $"GET {Describe(path)} -> {status} in {LogFormat.Ms(sw.ElapsedMilliseconds)}", TenantId);

                if (response.IsSuccessStatusCode)
                {
                    if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
                    try { return ToolkitJson.ParseNode(text); }
                    catch (JsonException ex) { throw new GraphRequestException(status, "GET", path, "parse", $"Graph response could not be parsed: {ex.Message}"); }
                }

                if (status == 401 && !refreshedToken)
                {
                    refreshedToken = true;
                    _log.Warn("Graph", $"GET {Describe(path)} returned 401; renewing the token silently once.", TenantId);
                    continue;
                }
                if (status == 401)
                    throw new AuthenticationRequiredException("Microsoft Graph rejected the access token twice. Disconnect and reconnect.");

                if (status == 429 && attempt < _options.MaxReadAttempts)
                {
                    var wait = RetryAfter(response);
                    _log.Warn("Graph", $"GET {Describe(path)} throttled (429); waiting {wait.TotalSeconds:0}s before attempt {attempt + 1}.", TenantId);
                    await BackoffAsync(attempt, wait, ct);
                    continue;
                }
                if (status is 500 or 502 or 503 or 504 && attempt < _options.MaxReadAttempts)
                {
                    var wait = RetryAfter(response);
                    _log.Warn("Graph", $"GET {Describe(path)} returned {status}; retrying after {wait.TotalSeconds:0}s.", TenantId);
                    await BackoffAsync(attempt, wait, ct);
                    continue;
                }
                throw BuildError(status, "GET", path, text, route);
            }
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? wait = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta) wait = delta;
        else if (header?.Date is DateTimeOffset date) wait = date - DateTimeOffset.UtcNow;
        else if (response.Headers.TryGetValues("Retry-After", out var values)
                 && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        if (wait is null || wait.Value <= TimeSpan.Zero) wait = TimeSpan.FromSeconds(5);
        return wait.Value > _options.MaxRetryAfter ? _options.MaxRetryAfter : wait.Value;
    }

    private async Task BackoffAsync(int attempt, TimeSpan? explicitWait, CancellationToken ct)
    {
        var wait = explicitWait ?? TimeSpan.FromMilliseconds(_options.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        if (wait > _options.MaxRetryAfter) wait = _options.MaxRetryAfter;
        if (_options.Sleep) await Task.Delay(wait, ct);
    }

    private string ValidateNextLink(GraphApi api, string next, GraphRoute route)
    {
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new GraphRequestException(0, "GET", route.BasePath, "pagination", "Unexpected pagination origin; refusing to follow @odata.nextLink.");
        var expectedPrefix = api == GraphApi.Beta ? "/beta/" : "/v1.0/";
        if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new GraphRequestException(0, "GET", route.BasePath, "pagination", $"@odata.nextLink changed API version ({uri.AbsolutePath}); refusing to follow.");
        var relative = uri.AbsolutePath[(expectedPrefix.Length - 1)..] + uri.Query;
        var match = _routes.MatchRead(api, relative);
        if (match is null || !string.Equals(match.BasePath, route.BasePath, StringComparison.OrdinalIgnoreCase))
            throw new GraphRequestException(0, "GET", route.BasePath, "pagination", "@odata.nextLink pointed at a different route; refusing to follow.");
        return Root(api) + relative;
    }

    private static GraphRequestException BuildError(int status, string method, string path, string body, GraphRoute route)
    {
        string? code = null, message = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && ToolkitJson.ParseNode(body) is JsonObject obj && obj["error"] is JsonObject err)
            {
                code = err["code"]?.GetValue<string>();
                message = err["message"]?.GetValue<string>();
            }
        }
        catch (JsonException) { }
        var basePath = GraphRouteAllowList.BasePathOf(path);
        var summary = SensitiveDataScrubber.Scrub(message ?? "").Trim();
        if (summary.Length > 300) summary = summary[..300] + "…";
        if (status == 403)
        {
            var hint = route.Scope;
            return new PermissionException(method, path, code,
                $"Microsoft Graph refused {method} {basePath} (403 {code ?? "Forbidden"}). The signed-in account lacks a required permission or role. Expected delegated scope: {hint}. {summary}",
                hint);
        }
        if (status == 404)
            return new GraphRequestException(404, method, path, code, $"{method} {basePath} returned 404 {code ?? "NotFound"}. {summary}");
        if (status == 429)
            return new GraphRequestException(429, method, path, code, $"{method} {basePath} was throttled (429) and was not retried because writes are never retried automatically. {summary}");
        return new GraphRequestException(status, method, path, code, $"{method} {basePath} returned HTTP {status} {code ?? ""}. {summary}".Trim());
    }

    private static string Describe(string path)
    {
        var basePath = GraphRouteAllowList.BasePathOf(path);
        return path.Contains('?', StringComparison.Ordinal) ? basePath + "?…" : basePath;
    }
}
