using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Graph.Auth;

namespace BDIT.TenantToolkit.Graph;

/// <summary>A live, verified tenant connection: the session facts, the guarded client and the authenticator that backs it.</summary>
public sealed class ConnectedTenant : IAsyncDisposable
{
    public TenantSession Session { get; }
    public IGraphClient Graph { get; }
    private readonly MsalAuthenticator? _authenticator;

    public ConnectedTenant(TenantSession session, IGraphClient graph, MsalAuthenticator? authenticator)
    {
        Session = session;
        Graph = graph;
        _authenticator = authenticator;
    }

    public async ValueTask DisposeAsync()
    {
        if (_authenticator is not null) await _authenticator.DisconnectAsync();
    }
}

/// <summary>
/// Establishes tenant connections. The flow is: resolve the application for the requested mode, interactive sign-in
/// pinned to the tenant, verify the organisation returned by Graph, resolve and verify the operator, then report the
/// effective scopes. Nothing here writes to the tenant.
/// </summary>
public sealed class TenantConnectionService
{
    public static readonly string[] DiagnosticScopes = { "User.Read", "Organization.Read.All", "RoleManagement.Read.Directory" };
    private const string GlobalAdministratorTemplateId = "62e90394-69f5-4237-9190-012177145e10";

    private readonly ToolkitSettings _settings;
    private readonly ToolkitPaths _paths;
    private readonly IToolkitLog _log;
    private readonly HttpClient _http;
    private readonly string _toolkitVersion;

    public TenantConnectionService(ToolkitSettings settings, ToolkitPaths paths, HttpClient http, IToolkitLog log, string toolkitVersion)
    {
        _settings = settings;
        _paths = paths;
        _http = http;
        _log = log;
        _toolkitVersion = toolkitVersion;
    }

    public IReadOnlyList<string> ScopesFor(SessionMode mode, StandardCatalogue standard)
    {
        var scopes = new List<string>(DiagnosticScopes);
        scopes.AddRange(standard.ReadScopes());
        if (mode == SessionMode.Deployment) scopes.AddRange(standard.WriteScopes());
        return scopes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ConnectedTenant> ConnectAsync(TenantProfile profile, SessionMode mode, StandardCatalogue standard, IProgress<string>? progress, CancellationToken ct)
    {
        var client = _settings.ResolveClient(mode, profile)
            ?? throw new ConfigurationException(mode == SessionMode.Deployment
                ? "No deployment application is configured. Register the BDIT Tenant Deployment application and record its client ID in config/toolkit.settings.json (or on the tenant profile) before deployment can be enabled."
                : "No assessment application is configured and the Microsoft Graph PowerShell fallback is disabled. Record an assessment client ID in config/toolkit.settings.json.");

        var scopes = ScopesFor(mode, standard);
        var tenantDir = _paths.TenantDirectory(profile.TenantId);
        Directory.CreateDirectory(tenantDir);
        var cacheFile = Path.Combine(tenantDir, $"msal-{mode.ToString().ToLowerInvariant()}.cache");

        progress?.Report($"Opening Microsoft sign-in for {profile.Company} ({mode}, {client.Label}).");
        var authenticator = await MsalAuthenticator.SignInAsync(new SignInRequest
        {
            TenantId = profile.TenantId,
            ClientId = client.ClientId,
            ClientLabel = client.Label,
            Mode = mode,
            Scopes = scopes,
            CacheFile = cacheFile,
            Timeout = TimeSpan.FromMinutes(_settings.SignInTimeoutMinutes),
            ClientVersion = _toolkitVersion
        }, _log, ct);

        var routes = GraphRouteAllowList.FromStandard(standard);
        var graph = new GraphClient(_http, authenticator, profile.TenantId, mode, routes, new GraphClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(_settings.GraphReadTimeoutSeconds),
            WriteTimeout = TimeSpan.FromSeconds(_settings.GraphWriteTimeoutSeconds),
            MaxRetryAfter = TimeSpan.FromSeconds(_settings.MaxRetryAfterSeconds)
        }, _log);

        var session = new TenantSession
        {
            TenantId = profile.TenantId,
            Account = authenticator.Outcome.Account,
            AccountObjectId = authenticator.Outcome.AccountObjectId,
            ClientId = client.ClientId,
            ClientLabel = client.Label,
            Mode = mode,
            Scopes = authenticator.Outcome.Scopes.ToList(),
            ConnectedAt = Timestamps.Format(DateTimeOffset.UtcNow),
            TokenExpiresAt = Timestamps.Format(authenticator.Outcome.ExpiresOn)
        };

        try
        {
            progress?.Report("Verifying the authenticated tenant with Microsoft Graph.");
            var org = await graph.GetAsync(GraphApi.V1, "/organization?$select=id,displayName,verifiedDomains", ct);
            var orgs = org["value"] as JsonArray;
            if (orgs is null || orgs.Count != 1 || orgs[0] is not JsonObject organisation)
                throw new TenantMismatchException("Organisation verification failed: Microsoft Graph did not return exactly one organisation.");
            var orgId = organisation["id"]?.GetValue<string>() ?? "";
            if (!string.Equals(orgId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new TenantMismatchException($"Microsoft Graph reports organisation {orgId}, not the requested tenant {profile.TenantId}. Connection rejected.");
            session.TenantVerified = true;
            session.TenantName = organisation["displayName"]?.GetValue<string>() ?? "";
            if (organisation["verifiedDomains"] is JsonArray domains)
                foreach (var d in domains)
                    if (d is JsonObject dom && dom["isDefault"]?.GetValue<bool>() == true)
                        session.PrimaryDomain = dom["name"]?.GetValue<string>() ?? "";

            progress?.Report("Resolving the signed-in operator.");
            var me = await graph.GetAsync(GraphApi.V1, "/me?$select=id,displayName,userPrincipalName", ct);
            var meId = me["id"]?.GetValue<string>() ?? "";
            if (ProfileValidator.IsGuid(meId))
            {
                session.OperatorObjectId = meId.ToLowerInvariant();
                session.OperatorDisplayName = me["displayName"]?.GetValue<string>() ?? "";
                session.OperatorUpn = me["userPrincipalName"]?.GetValue<string>() ?? "";
                session.OperatorVerified = string.IsNullOrEmpty(session.AccountObjectId)
                    || string.Equals(session.AccountObjectId, meId, StringComparison.OrdinalIgnoreCase);
                if (!session.OperatorVerified)
                    session.Notices.Add("The token subject and the /me object ID differ; operator identity is not verified. Conditional Access creation is blocked until this is resolved.");
            }
            else
            {
                session.Notices.Add("The signed-in user's object ID could not be resolved from /me. Conditional Access creation is blocked.");
            }

            if (mode == SessionMode.Assessment && session.HasWriteScopes)
            {
                if (!client.IsSharedFallback)
                    throw new ConfigurationException("The assessment application's token contains write permissions. Assessment must use a registration with read scopes only; remove the write permissions from that registration or use a separate deployment registration.");
                session.Notices.Add("The shared Microsoft Graph PowerShell application returned previously consented write scopes. The toolkit blocks every write in assessment mode, but the token itself is not read-only. Register a dedicated BDIT Tenant Assessment application for token-level isolation.");
            }
            if (client.IsSharedFallback)
                session.Notices.Add("Connected through the shared Microsoft Graph PowerShell application. This is acceptable for read-only assessment only.");

            _log.Info("Connect", $"Connected to verified tenant {session.TenantId} ({session.TenantName}) as {session.Account} in {mode} mode via {client.Label}.", session.TenantId);
            return new ConnectedTenant(session, graph, authenticator);
        }
        catch
        {
            await authenticator.DisconnectAsync();
            throw;
        }
    }

    /// <summary>Read-only access verification: role observations and a $top=1 probe per collection. Creates nothing.</summary>
    public async Task<AccessReport> CheckAccessAsync(ConnectedTenant connected, StandardCatalogue standard, IProgress<string>? progress, CancellationToken ct)
    {
        var session = connected.Session;
        var graph = connected.Graph;
        var report = new AccessReport
        {
            At = Timestamps.Format(DateTimeOffset.UtcNow),
            TenantId = session.TenantId,
            Account = session.Account,
            Mode = session.Mode.ToString(),
            Scopes = session.Scopes.ToList(),
            CandidateRecipes = standard.Controls.Count(c => c.HasRecipe),
            ManualControls = standard.Controls.Count(c => !c.HasRecipe)
        };
        report.Notes.Add("No test objects were created or changed. Scope and role observations do not prove that every write would be authorised.");

        if (!string.IsNullOrEmpty(session.OperatorObjectId))
        {
            try
            {
                var assignments = await graph.GetAllAsync(GraphApi.V1,
                    $"/roleManagement/directory/roleAssignments?$filter=principalId eq '{session.OperatorObjectId}'&$expand=roleDefinition", ct);
                foreach (var a in assignments)
                {
                    var def = a["roleDefinition"] as JsonObject;
                    report.Roles.Add(new RoleObservation
                    {
                        Name = def?["displayName"]?.GetValue<string>() ?? a["roleDefinitionId"]?.GetValue<string>() ?? "",
                        TemplateId = def?["templateId"]?.GetValue<string>() ?? a["roleDefinitionId"]?.GetValue<string>() ?? "",
                        Scope = a["directoryScopeId"]?.GetValue<string>() ?? ""
                    });
                }
                report.RoleStatus = "Direct active assignments read";
                report.Notes.Add("Role inspection covers direct active Entra assignments only. Group-based roles, eligible PIM roles, custom-role effectiveness and Intune RBAC scope are not evaluated.");
            }
            catch (ToolkitException ex)
            {
                report.RoleError = ex.Message;
            }
        }
        var globalAdmin = report.Roles.Any(r => string.Equals(r.TemplateId, GlobalAdministratorTemplateId, StringComparison.OrdinalIgnoreCase) && r.Scope == "/");
        report.GlobalAdministrator = globalAdmin ? "Observed direct active tenant-wide role" : "Not confirmed";

        foreach (var (key, def) in standard.Collections)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Testing read access: {def.Label}");
            var check = new AccessCheck { Collection = key, Label = def.Label, Scope = def.Scope };
            try
            {
                if (def.Singleton)
                {
                    await graph.GetAsync(def.ApiVersion, def.Path, ct);
                }
                else
                {
                    // Collections that reject OData parameters are read whole; $top would fail as UnsupportedQuery and
                    // be misread as a permission problem.
                    var probe = def.SupportsQuery
                        ? def.Path + (def.Path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "$top=1"
                        : def.Path;
                    var page = await graph.GetAsync(def.ApiVersion, probe, ct);
                    if (page["value"] is not JsonArray) throw new GraphRequestException(200, "GET", def.Path, null, "Unexpected collection response.");
                }
                check.Status = "Pass";
                check.Detail = "Read request succeeded; this is not a full configuration assessment.";
            }
            catch (ToolkitException ex)
            {
                check.Status = "Error";
                check.Detail = ex.Message;
            }
            report.Reads.Add(check);
        }

        foreach (var (key, def) in standard.Collections.Where(c => c.Value.Writable))
        {
            var observed = session.Scopes.Any(s => string.Equals(s, def.Write, StringComparison.OrdinalIgnoreCase));
            report.Writes.Add(new AccessCheck
            {
                Collection = key,
                Label = def.Label,
                Scope = def.Write ?? "",
                Status = session.Mode != SessionMode.Deployment ? "Not requested" : observed ? "Scope observed" : "Missing scope",
                Detail = session.Mode != SessionMode.Deployment
                    ? "Enable deployment access to request write scopes. No write is performed by this check."
                    : observed
                        ? "Write scope observed" + (globalAdmin ? " and Global Administrator role observed" : "") + ". API acceptance, licensing and resource restrictions remain untested until a reviewed plan is executed."
                        : "The required delegated write scope was not returned in the token. Review the deployment application's consent."
            });
        }
        return report;
    }
}
