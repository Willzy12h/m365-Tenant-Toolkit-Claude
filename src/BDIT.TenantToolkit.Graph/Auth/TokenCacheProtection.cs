using System.Security.Cryptography;
using BDIT.TenantToolkit.Core.Diagnostics;
using Microsoft.Identity.Client;

namespace BDIT.TenantToolkit.Graph.Auth;

/// <summary>
/// Persists the MSAL token cache to one file per tenant and mode, encrypted with Windows DPAPI for the current user.
/// The file is removed on disconnect so a later connection always starts with an explicit Microsoft sign-in.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class TokenCacheProtection
{
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("BDIT.TenantToolkit.TokenCache.v1");

    public static void Attach(ITokenCache cache, string cacheFile, IToolkitLog log)
    {
        var gate = new object();
        cache.SetBeforeAccess(args =>
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(cacheFile)) return;
                    var encrypted = File.ReadAllBytes(cacheFile);
                    if (encrypted.Length == 0) return;
                    var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    args.TokenCache.DeserializeMsalV3(plain, shouldClearExistingCache: true);
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    log.Warn("Auth", $"Token cache could not be read ({ex.GetType().Name}); a fresh sign-in will be required.");
                }
            }
        });
        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (gate)
            {
                try
                {
                    var plain = args.TokenCache.SerializeMsalV3();
                    var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    var temp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, encrypted);
                    File.Move(temp, cacheFile, overwrite: true);
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    log.Warn("Auth", $"Token cache could not be written ({ex.GetType().Name}); tokens will not persist beyond this session.");
                }
            }
        });
    }

    public static void Delete(string cacheFile, IToolkitLog log)
    {
        try
        {
            if (File.Exists(cacheFile)) File.Delete(cacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("Auth", $"Token cache file could not be deleted ({ex.GetType().Name}). Delete it manually: {cacheFile}");
        }
    }
}
