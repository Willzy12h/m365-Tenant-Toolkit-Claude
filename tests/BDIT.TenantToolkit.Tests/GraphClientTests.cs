using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Graph;
using BDIT.TenantToolkit.Graph.Auth;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class GraphClientTests
{
    private sealed class StubTokens : IAccessTokenProvider
    {
        public int Calls;
        public Task<string> GetAccessTokenAsync(CancellationToken ct) { Calls++; return Task.FromResult("token-" + Calls); }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public ScriptedHandler Enqueue(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? configure = null)
        {
            _responses.Enqueue(_ =>
            {
                var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                configure?.Invoke(r);
                return r;
            });
            return this;
        }

        public ScriptedHandler EnqueueTimeout()
        {
            _responses.Enqueue(_ => throw new TaskCanceledException("timeout"));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0) throw new InvalidOperationException("No scripted response for " + request.RequestUri);
            return _responses.Dequeue()(request);
        }
    }

    private static (GraphClient Client, ScriptedHandler Handler, StubTokens Tokens) Create(SessionMode mode = SessionMode.Deployment)
    {
        var handler = new ScriptedHandler();
        var tokens = new StubTokens();
        var routes = GraphRouteAllowList.FromStandard(TestData.Standard());
        var client = new GraphClient(new HttpClient(handler), tokens, TestData.TenantA, mode, routes, new GraphClientOptions { Sleep = false, MaxRetryAfter = TimeSpan.FromSeconds(300) }, NullLog.Instance);
        return (client, handler, tokens);
    }

    [Fact]
    public async Task Read_retries_on_429_and_5xx_then_succeeds()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
               .Enqueue(HttpStatusCode.ServiceUnavailable)
               .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"1"}]}""");
        var items = await client.GetAllAsync(GraphApi.V1, "/identity/conditionalAccess/policies", CancellationToken.None);
        Assert.Single(items);
        Assert.Equal(3, handler.Requests.Count);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_gives_up_after_max_attempts_with_a_clear_error()
    {
        var (client, handler, _) = Create();
        for (var i = 0; i < 5; i++) handler.Enqueue(HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => client.GetAsync(GraphApi.V1, "/organization", CancellationToken.None));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Writes_are_never_retried_and_gateway_errors_are_ambiguous()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests);
        var payload = ToolkitJson.ParseObject("""{"displayName":"x","state":"disabled"}""");
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", payload, CancellationToken.None));
        Assert.Equal(429, ex.StatusCode);
        Assert.Single(handler.Requests);

        handler.Enqueue(HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<AmbiguousWriteException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", payload, CancellationToken.None));

        handler.EnqueueTimeout();
        await Assert.ThrowsAsync<AmbiguousWriteException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", payload, CancellationToken.None));
    }

    [Fact]
    public async Task Write_is_denied_in_assessment_mode_before_any_request()
    {
        var (client, handler, _) = Create(SessionMode.Assessment);
        var payload = ToolkitJson.ParseObject("""{"displayName":"x","state":"disabled"}""");
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", payload, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Write_refuses_unsafe_conditional_access_state_and_unlisted_routes()
    {
        var (client, handler, _) = Create();
        var enabled = ToolkitJson.ParseObject("""{"displayName":"x","state":"enabled"}""");
        await Assert.ThrowsAsync<SafetyViolationException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", enabled, CancellationToken.None));
        var reportOnly = ToolkitJson.ParseObject("""{"displayName":"x","state":"enabledForReportingButNotEnforced"}""");
        await Assert.ThrowsAsync<SafetyViolationException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", reportOnly, CancellationToken.None));
        var ok = ToolkitJson.ParseObject("""{"displayName":"x"}""");
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/groups", ok, CancellationToken.None));
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies/not-a-guid", ok, CancellationToken.None));
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.WriteAsync(GraphApi.V1, GraphWriteMethod.Patch, "/identity/conditionalAccess/policies", ok, CancellationToken.None));
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.GetAsync(GraphApi.V1, "/applications", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Successful_write_returns_body_and_sends_json()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.Created, """{"id":"new-id","state":"disabled"}""");
        var payload = ToolkitJson.ParseObject("""{"displayName":"x","state":"disabled"}""");
        var result = await client.WriteAsync(GraphApi.V1, GraphWriteMethod.Post, "/identity/conditionalAccess/policies", payload, CancellationToken.None);
        Assert.Equal("new-id", result["id"]!.GetValue<string>());
        Assert.Contains("\"state\":\"disabled\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Beta_and_v1_roots_are_separate()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");
        await client.GetAllAsync(GraphApi.Beta, "/deviceManagement/configurationPolicies", CancellationToken.None);
        Assert.StartsWith("https://graph.microsoft.com/beta/deviceManagement/configurationPolicies", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<WriteDeniedException>(() => client.GetAllAsync(GraphApi.V1, "/deviceManagement/configurationPolicies", CancellationToken.None));
    }

    [Fact]
    public async Task Pagination_follows_same_origin_links_and_rejects_others()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups?$skiptoken=abc"}""")
               .Enqueue(HttpStatusCode.OK, """{"value":[{"id":"2"}]}""");
        var items = await client.GetAllAsync(GraphApi.V1, "/groups?$select=id,displayName", CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains("skiptoken", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        handler.Enqueue(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://evil.example/v1.0/groups"}""");
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => client.GetAllAsync(GraphApi.V1, "/groups", CancellationToken.None));
        Assert.Contains("origin", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.Requests.Count);

        handler.Enqueue(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/beta/groups?$skiptoken=x"}""");
        await Assert.ThrowsAsync<GraphRequestException>(() => client.GetAllAsync(GraphApi.V1, "/groups", CancellationToken.None));
    }

    [Fact]
    public async Task Forbidden_yields_permission_error_with_scope_hint()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""");
        var ex = await Assert.ThrowsAsync<PermissionException>(() => client.GetAsync(GraphApi.V1, "/identity/conditionalAccess/policies", CancellationToken.None));
        Assert.Equal("Policy.Read.All", ex.RequiredScopeHint);
        Assert.Contains("Authorization_RequestDenied", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Policy.Read.All", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_is_retried_once_with_a_renewed_token_then_fails_clearly()
    {
        var (client, handler, tokens) = Create();
        handler.Enqueue(HttpStatusCode.Unauthorized).Enqueue(HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => client.GetAsync(GraphApi.V1, "/organization", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, tokens.Calls);
    }

    [Fact]
    public void Route_allow_list_matches_objects_and_subresources_but_not_siblings()
    {
        var routes = GraphRouteAllowList.FromStandard(TestData.Standard());
        Assert.NotNull(routes.MatchRead(GraphApi.V1, "/identity/conditionalAccess/policies/abc/assignments"));
        Assert.Null(routes.MatchRead(GraphApi.V1, "/identity/conditionalAccess/policiesX"));
        Assert.Null(routes.MatchRead(GraphApi.Beta, "/identity/conditionalAccess/policies"));
        Assert.NotNull(routes.MatchWrite(GraphApi.V1, "/identity/conditionalAccess/policies/" + TestData.Emergency, out var existing));
        Assert.True(existing);
        Assert.Null(routes.MatchWrite(GraphApi.V1, "/groups", out _));
        Assert.Throws<ConfigurationException>(() => GraphRouteAllowList.ValidatePathSyntax("/groups/../users"));
    }

    [Fact]
    public void Scope_normalisation_strips_graph_prefix_and_orders()
    {
        var scopes = MsalAuthenticator.NormaliseScopes(new[] { "https://graph.microsoft.com/User.Read", "policy.read.all", "User.Read" });
        Assert.Equal(new[] { "policy.read.all", "User.Read" }, scopes);
    }
}
