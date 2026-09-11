using System.Text.RegularExpressions;

namespace BDIT.TenantToolkit.Core.Diagnostics;

/// <summary>Removes tokens, secrets and credential-like material from any text before it reaches a log, journal or report.</summary>
public static partial class SensitiveDataScrubber
{
    private static readonly Regex[] Patterns =
    {
        BearerPattern(),
        JwtPattern(),
        JsonSecretPattern(),
        QuerySecretPattern()
    };

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        var text = input;
        text = BearerPattern().Replace(text, "Bearer [redacted]");
        text = JwtPattern().Replace(text, "[redacted token]");
        text = JsonSecretPattern().Replace(text, m => m.Groups[1].Value + "\"[redacted]\"");
        text = QuerySecretPattern().Replace(text, m => m.Groups[1].Value + "[redacted]");
        return text;
    }

    public static bool LooksSensitive(string? input) => !string.IsNullOrEmpty(input) && Patterns.Any(p => p.IsMatch(input));

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtPattern();

    [GeneratedRegex("(?i)(\"(?:access_token|refresh_token|id_token|client_secret|clientSecret|password|secret|recoveryKey|recoveryPassword|privateKey|certificatePassword)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex JsonSecretPattern();

    [GeneratedRegex(@"(?i)((?:code|access_token|refresh_token|client_secret|password)=)[^&\s]+")]
    private static partial Regex QuerySecretPattern();
}
