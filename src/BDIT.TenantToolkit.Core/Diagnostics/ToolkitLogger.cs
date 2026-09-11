using System.Globalization;
using System.Text.Json;
using BDIT.TenantToolkit.Core.Json;

namespace BDIT.TenantToolkit.Core.Diagnostics;

public enum LogLevel { Debug, Information, Warning, Error }

public sealed class LogEntry
{
    public long Sequence { get; set; }
    public string At { get; set; } = "";
    public LogLevel Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? TenantId { get; set; }
    public string? ControlId { get; set; }
    public string? Exception { get; set; }
}

public interface IToolkitLog
{
    void Log(LogLevel level, string category, string message, string? tenantId = null, string? controlId = null, Exception? exception = null);
}

public static class ToolkitLogExtensions
{
    public static void Debug(this IToolkitLog log, string category, string message, string? tenantId = null, string? controlId = null) => log.Log(LogLevel.Debug, category, message, tenantId, controlId);
    public static void Info(this IToolkitLog log, string category, string message, string? tenantId = null, string? controlId = null) => log.Log(LogLevel.Information, category, message, tenantId, controlId);
    public static void Warn(this IToolkitLog log, string category, string message, string? tenantId = null, string? controlId = null) => log.Log(LogLevel.Warning, category, message, tenantId, controlId);
    public static void Error(this IToolkitLog log, string category, string message, Exception? exception = null, string? tenantId = null, string? controlId = null) => log.Log(LogLevel.Error, category, message, tenantId, controlId, exception);
}

/// <summary>A log that discards everything; used by tests and by tooling that has no log directory.</summary>
public sealed class NullLog : IToolkitLog
{
    public static readonly NullLog Instance = new();
    public void Log(LogLevel level, string category, string message, string? tenantId = null, string? controlId = null, Exception? exception = null) { }
}

/// <summary>
/// Structured JSONL logger. Writes synchronously under a lock so that nothing is lost at shutdown, keeps a bounded
/// in-memory ring for the UI, and scrubs every message before it is stored. Never logs request or response bodies.
/// </summary>
public sealed class ToolkitLogger : IToolkitLog, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly LogLevel _minimum;
    private readonly Queue<LogEntry> _recent = new();
    private long _sequence;
    private bool _disposed;

    public string? FilePath { get; }
    public int RingCapacity { get; }

    public event Action<LogEntry>? EntryWritten;

    public ToolkitLogger(string? logDirectory, LogLevel minimum = LogLevel.Information, int ringCapacity = 2000)
    {
        _minimum = minimum;
        RingCapacity = ringCapacity;
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
            FilePath = Path.Combine(logDirectory, $"toolkit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream) { AutoFlush = true };
        }
    }

    public static LogLevel ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information
    };

    public void Log(LogLevel level, string category, string message, string? tenantId = null, string? controlId = null, Exception? exception = null)
    {
        if (level < _minimum) return;
        LogEntry entry;
        lock (_gate)
        {
            if (_disposed) return;
            entry = new LogEntry
            {
                Sequence = ++_sequence,
                At = Timestamps.Format(DateTimeOffset.UtcNow),
                Level = level,
                Category = category,
                Message = SensitiveDataScrubber.Scrub(message),
                TenantId = tenantId,
                ControlId = controlId,
                Exception = exception is null ? null : SensitiveDataScrubber.Scrub(exception.GetType().Name + ": " + exception.Message)
            };
            _recent.Enqueue(entry);
            while (_recent.Count > RingCapacity) _recent.Dequeue();
            try { _writer?.WriteLine(JsonSerializer.Serialize(entry, ToolkitJson.Compact)); }
            catch (IOException) { }
        }
        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    public void Flush()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Flush(); _writer?.Dispose(); } catch (IOException) { }
        }
    }
}

/// <summary>Formats numbers and durations consistently for messages.</summary>
public static class LogFormat
{
    public static string Ms(long milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
}
