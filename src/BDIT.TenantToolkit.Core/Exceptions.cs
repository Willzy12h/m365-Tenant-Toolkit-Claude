namespace BDIT.TenantToolkit.Core;

/// <summary>Base class for every deliberate toolkit failure. Messages are safe to show to an engineer.</summary>
public class ToolkitException : Exception
{
    public ToolkitException(string message) : base(message) { }
    public ToolkitException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Configuration files, settings or catalogue content are invalid.</summary>
public sealed class ConfigurationException : ToolkitException
{
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>An integrity digest did not match the content it protects.</summary>
public sealed class IntegrityException : ToolkitException
{
    public IntegrityException(string message) : base(message) { }
}

/// <summary>A tenant-bound artefact was used against a different tenant.</summary>
public sealed class TenantMismatchException : ToolkitException
{
    public TenantMismatchException(string message) : base(message) { }
}

/// <summary>A write was attempted that the current session or safety rules do not permit.</summary>
public sealed class WriteDeniedException : ToolkitException
{
    public WriteDeniedException(string message) : base(message) { }
}

/// <summary>A plan failed validation and must be rebuilt.</summary>
public sealed class PlanValidationException : ToolkitException
{
    public PlanValidationException(string message) : base(message) { }
}

/// <summary>A safety invariant (for example Conditional Access state) would have been violated.</summary>
public sealed class SafetyViolationException : ToolkitException
{
    public SafetyViolationException(string message) : base(message) { }
}

/// <summary>A catalogue template referenced a client parameter that the profile does not provide.</summary>
public sealed class MissingParameterException : ToolkitException
{
    public string ParameterName { get; }
    public MissingParameterException(string parameterName)
        : base($"Missing client value: {parameterName}. Record it in the tenant profile before this control can be assessed or deployed.")
    {
        ParameterName = parameterName;
    }
}

/// <summary>Authentication is required again; the toolkit never performs silent interactive retries mid-operation.</summary>
public sealed class AuthenticationRequiredException : ToolkitException
{
    public AuthenticationRequiredException(string message) : base(message) { }
    public AuthenticationRequiredException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Microsoft Graph returned an error response.</summary>
public class GraphRequestException : ToolkitException
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }
    public string Method { get; }
    public string Path { get; }

    public GraphRequestException(int statusCode, string method, string path, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        ErrorCode = errorCode;
    }
}

/// <summary>Microsoft Graph returned 403. Carries the best available hint about the missing permission.</summary>
public sealed class PermissionException : GraphRequestException
{
    public string? RequiredScopeHint { get; }

    public PermissionException(string method, string path, string? errorCode, string message, string? requiredScopeHint)
        : base(403, method, path, errorCode, message)
    {
        RequiredScopeHint = requiredScopeHint;
    }
}

/// <summary>A write request ended without a definitive answer (timeout, connection reset). The outcome is unknown.</summary>
public sealed class AmbiguousWriteException : ToolkitException
{
    public AmbiguousWriteException(string message, Exception? inner) : base(message, inner) { }
}
