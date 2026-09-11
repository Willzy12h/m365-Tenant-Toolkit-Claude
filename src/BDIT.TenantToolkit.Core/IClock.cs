namespace BDIT.TenantToolkit.Core;

/// <summary>Abstracts time so plan expiry and snapshot age rules can be tested deterministically.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class Timestamps
{
    /// <summary>ISO-8601 UTC with millisecond precision; the only timestamp format written to evidence.</summary>
    public static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out value);
}
