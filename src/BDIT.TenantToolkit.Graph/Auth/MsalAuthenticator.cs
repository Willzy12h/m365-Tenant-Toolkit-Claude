using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;
using Microsoft.Identity.Client;

namespace BDIT.TenantToolkit.Graph.Auth;

/// <summary>Supplies bearer tokens to the Graph client. Silent renewal only; interactive prompts happen solely at connect time.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

public sealed class SignInRequest
{
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientLabel { get; init; } = "";
    public SessionMode Mode { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public string CacheFile { get; init; } = "";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public string ClientName { get; init; } = "BDIT Tenant Toolkit";
    public string ClientVersion { get; init; } = "1.0.0";
}

public sealed class SignInOutcome
{
    public string TenantId { get; init; } = "";
    public string Account { get; init; } = "";
    public string AccountObjectId { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ExpiresOn { get; init; }
}

/// <summary>
/// Interactive delegated authentication through the system browser (no embedded web view). The authority is pinned to
/// the requested tenant and the returned tenant is verified before any Graph call is made. Mid-operation token renewal is
/// silent only; if Microsoft requires interaction again the current operation fails and the engineer reconnects deliberately.
/// </summary>
public sealed class MsalAuthenticator : IAccessTokenProvider
{
    private const string GraphResourcePrefix = "https://graph.microsoft.com/";

    private readonly IPublicClientApplication _pca;
    private readonly string[] _scopes;
    private readonly string _tenantId;
    private readonly string _cacheFile;
    private readonly IToolkitLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccount? _account;
    private string? _accountIdentifier;
    private string? _accessToken;
    private DateTimeOffset _expiresOn;
    private bool _disconnected;

    public SignInOutcome Outcome { get; }
    public string ClientId { get; }

    private MsalAuthenticator(IPublicClientApplication pca, string[] scopes, string tenantId, string cacheFile, IToolkitLog log, AuthenticationResult result, string clientId)
    {
        _pca = pca;
        _scopes = scopes;
        _tenantId = tenantId;
        _cacheFile = cacheFile;
        _log = log;
        _account = result.Account;
        _accountIdentifier = result.Account?.HomeAccountId?.Identifier;
        _accessToken = result.AccessToken;
        _expiresOn = result.ExpiresOn;
        ClientId = clientId;
        Outcome = new SignInOutcome
        {
            TenantId = result.TenantId ?? "",
            Account = result.Account?.Username ?? "",
            AccountObjectId = result.UniqueId ?? "",
            Scopes = NormaliseScopes(result.Scopes),
            ExpiresOn = result.ExpiresOn
        };
    }

    public static async Task<MsalAuthenticator> SignInAsync(SignInRequest request, IToolkitLog log, CancellationToken ct)
    {
        if (!ProfileValidator.IsGuid(request.TenantId)) throw new ConfigurationException("Sign-in requires a tenant ID GUID.");
        if (!ProfileValidator.IsGuid(request.ClientId)) throw new ConfigurationException("Sign-in requires an application (client) ID GUID.");
        if (request.Scopes.Count == 0) throw new ConfigurationException("Sign-in requires at least one scope.");

        var pca = PublicClientApplicationBuilder.Create(request.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, request.TenantId)
            .WithRedirectUri("http://localhost")
            .WithClientName(request.ClientName)
            .WithClientVersion(request.ClientVersion)
            .Build();
        TokenCacheProtection.Attach(pca.UserTokenCache, request.CacheFile, log);

        var scopes = request.Scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        AuthenticationResult result;
        try
        {
            log.Info("Auth", $"Opening Microsoft sign-in in the system browser for tenant {request.TenantId} ({request.ClientLabel}, {request.Mode}).", request.TenantId);
            result = await pca.AcquireTokenInteractive(scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = "<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Sign-in complete</h2><p>You can close this tab and return to the BDIT Tenant Toolkit.</p></body></html>",
                    HtmlMessageError = "<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Sign-in failed</h2><p>Return to the BDIT Tenant Toolkit and try again.</p></body></html>"
                })
                .ExecuteAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthenticationRequiredException("Microsoft sign-in did not complete within the allowed time. Try again.");
        }
        catch (MsalException ex)
        {
            throw new AuthenticationRequiredException($"Microsoft sign-in failed ({ex.ErrorCode}). {ex.Message}", ex);
        }

        if (!string.Equals(result.TenantId, request.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            try { await pca.RemoveAsync(result.Account); } catch (MsalException) { }
            TokenCacheProtection.Delete(request.CacheFile, log);
            throw new TenantMismatchException($"Microsoft returned a token for tenant {result.TenantId}, not the requested tenant {request.TenantId}. Connection rejected.");
        }

        return new MsalAuthenticator(pca, scopes, request.TenantId, request.CacheFile, log, result, request.ClientId);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disconnected || _account is null) throw new AuthenticationRequiredException("Not connected. Sign in again.");
            if (_accessToken is not null && _expiresOn > DateTimeOffset.UtcNow.AddMinutes(5)) return _accessToken;
            try
            {
                var result = await _pca.AcquireTokenSilent(_scopes, _account).ExecuteAsync(ct);
                if (!string.Equals(result.TenantId, _tenantId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(result.Account?.HomeAccountId?.Identifier, _accountIdentifier, StringComparison.Ordinal))
                    throw new AuthenticationRequiredException("The authenticated identity changed during silent renewal. Disconnect and reconnect.");
                _account = result.Account;
                _accessToken = result.AccessToken;
                _expiresOn = result.ExpiresOn;
                return _accessToken;
            }
            catch (MsalUiRequiredException ex)
            {
                _accessToken = null;
                throw new AuthenticationRequiredException("Microsoft requires a fresh interactive sign-in. Disconnect, reconnect, then capture and review a new plan. No automatic interactive retry was performed.", ex);
            }
            catch (MsalException ex)
            {
                _accessToken = null;
                throw new AuthenticationRequiredException($"Token renewal failed ({ex.ErrorCode}). Reconnect.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disconnected = true;
            _accessToken = null;
            try
            {
                foreach (var account in await _pca.GetAccountsAsync()) await _pca.RemoveAsync(account);
            }
            catch (MsalException ex)
            {
                _log.Warn("Auth", $"Account removal reported {ex.ErrorCode}; the cache file is deleted regardless.");
            }
            _account = null;
            TokenCacheProtection.Delete(_cacheFile, _log);
            _log.Info("Auth", "Disconnected; cached tokens removed.", _tenantId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static IReadOnlyList<string> NormaliseScopes(IEnumerable<string>? scopes) =>
        (scopes ?? Array.Empty<string>())
            .Select(s => s.StartsWith(GraphResourcePrefix, StringComparison.OrdinalIgnoreCase) ? s[GraphResourcePrefix.Length..] : s)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
