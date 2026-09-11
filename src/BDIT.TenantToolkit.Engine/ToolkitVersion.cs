using System.Reflection;

namespace BDIT.TenantToolkit.Engine;

public static class ToolkitVersion
{
    public static string Current { get; } =
        typeof(ToolkitVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ToolkitVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
