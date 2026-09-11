using System.Text.RegularExpressions;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;

namespace BDIT.TenantToolkit.Engine.Standards;

public sealed record StandardRelease(string FileName, string Release, string Status);

/// <summary>Loads and validates BDIT Build Standard releases and enforces manifest integrity before use.</summary>
public sealed partial class StandardsLoader
{
    private static readonly HashSet<string> Severities = new(StringComparer.OrdinalIgnoreCase) { "Critical", "High", "Medium", "Low", "Informational" };
    private readonly ToolkitPaths _paths;
    private readonly IToolkitLog _log;

    public StandardsLoader(ToolkitPaths paths, IToolkitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public IReadOnlyList<StandardRelease> ListReleases()
    {
        var list = new List<StandardRelease>();
        if (!Directory.Exists(_paths.StandardsDirectory)) return list;
        foreach (var file in Directory.EnumerateFiles(_paths.StandardsDirectory, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, StandardsManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var node = ToolkitJson.ParseObject(File.ReadAllText(file));
                list.Add(new StandardRelease(name, node["release"]?.GetValue<string>() ?? name, node["status"]?.GetValue<string>() ?? ""));
            }
            catch (Exception ex) when (ex is ConfigurationException or System.Text.Json.JsonException or IOException)
            {
                _log.Warn("Standards", $"Release file '{name}' could not be read: {ex.Message}");
            }
        }
        return list.OrderByDescending(r => r.Release, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Loads a release after verifying its digest against standards/manifest.json.</summary>
    public StandardCatalogue Load(string fileName)
    {
        if (!FileNamePattern().IsMatch(fileName)) throw new ConfigurationException($"Invalid standard file name '{fileName}'.");
        var path = Path.Combine(_paths.StandardsDirectory, fileName);
        if (!File.Exists(path)) throw new ConfigurationException($"Standard file not found: {fileName}");
        var manifest = StandardsManifest.Load(_paths.StandardsDirectory);
        var digest = manifest.Verify(_paths.StandardsDirectory, fileName);
        var catalogue = Parse(File.ReadAllText(path), fileName);
        catalogue.IntegrityDigest = digest;
        _log.Info("Standards", $"Loaded BDIT Build Standard {catalogue.Release} ({fileName}, digest {digest[..12]}…) with {catalogue.Controls.Count} controls.");
        return catalogue;
    }

    /// <summary>Parses and validates a catalogue document without integrity checks (used by the loader and by tests).</summary>
    public static StandardCatalogue Parse(string json, string fileName)
    {
        StandardCatalogue catalogue;
        try
        {
            catalogue = ToolkitJson.Deserialize<StandardCatalogue>(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ConfigurationException)
        {
            throw new ConfigurationException($"Standard '{fileName}' is not valid JSON for schema {StandardCatalogue.SupportedSchemaVersion}: {ex.Message}", ex);
        }
        catalogue.SourceFileName = fileName;
        Validate(catalogue);
        return catalogue;
    }

    public static void Validate(StandardCatalogue c)
    {
        if (c.SchemaVersion != StandardCatalogue.SupportedSchemaVersion)
            throw new ConfigurationException($"Standard schema version {c.SchemaVersion} is not supported; this build understands version {StandardCatalogue.SupportedSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(c.Release) || c.Release.Length > 40 || !ReleasePattern().IsMatch(c.Release))
            throw new ConfigurationException("Standard release identifier is missing or invalid (letters, digits, dots and dashes only).");
        if (c.Collections.Count == 0) throw new ConfigurationException("Standard defines no collections.");
        foreach (var (key, def) in c.Collections)
        {
            if (!CollectionKeyPattern().IsMatch(key)) throw new ConfigurationException($"Collection key '{key}' is invalid.");
            if (!string.Equals(def.Api, "v1.0", StringComparison.OrdinalIgnoreCase) && !string.Equals(def.Api, "beta", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"Collection '{key}' api must be 'v1.0' or 'beta'.");
            if (string.IsNullOrWhiteSpace(def.Path) || !def.Path.StartsWith("/", StringComparison.Ordinal) || def.Path.Contains("..", StringComparison.Ordinal) || def.Path.Contains("//", StringComparison.Ordinal))
                throw new ConfigurationException($"Collection '{key}' path is invalid.");
            if (def.Path.StartsWith("/v1.0/", StringComparison.OrdinalIgnoreCase) || def.Path.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"Collection '{key}' path must not include the API version; use the 'api' property.");
            if (string.IsNullOrWhiteSpace(def.Scope)) throw new ConfigurationException($"Collection '{key}' must declare a read scope.");
            if (string.IsNullOrWhiteSpace(def.Label)) def.Label = key;
            if (string.IsNullOrWhiteSpace(def.NameProperty)) def.NameProperty = "displayName";
        }
        foreach (var p in c.Parameters)
        {
            if (!ParameterKeyPattern().IsMatch(p.Key)) throw new ConfigurationException($"Parameter key '{p.Key}' is invalid.");
            if (p.Type is not ("guid" or "guidList")) throw new ConfigurationException($"Parameter '{p.Key}' type must be guid or guidList.");
        }
        if (c.Controls.Count == 0) throw new ConfigurationException("Standard defines no controls.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in c.Controls)
        {
            if (!ControlIdPattern().IsMatch(control.Id)) throw new ConfigurationException($"Control ID '{control.Id}' is invalid (expected e.g. CA-001 or CMP-WIN-001).");
            if (!ids.Add(control.Id)) throw new ConfigurationException($"Duplicate control ID '{control.Id}'.");
            if (string.IsNullOrWhiteSpace(control.Name)) throw new ConfigurationException($"Control {control.Id} has no name.");
            if (string.IsNullOrWhiteSpace(control.Category)) throw new ConfigurationException($"Control {control.Id} has no category.");
            if (!Severities.Contains(control.Severity)) throw new ConfigurationException($"Control {control.Id} severity '{control.Severity}' is invalid.");
            CollectionDefinition? def = null;
            if (control.Collection is not null && !c.Collections.TryGetValue(control.Collection, out def))
                throw new ConfigurationException($"Control {control.Id} references unknown collection '{control.Collection}'.");
            if (control.Assessment.Mode == AssessmentMode.Settings)
            {
                if (control.Payload is null) throw new ConfigurationException($"Control {control.Id} uses settings assessment but has no payload recipe.");
                if (def is null) throw new ConfigurationException($"Control {control.Id} uses settings assessment but names no collection.");
                if (control.Assessment.PartialMatchThreshold is < 0.1 or > 1.0) throw new ConfigurationException($"Control {control.Id} partialMatchThreshold must be between 0.1 and 1.0.");
                var nameKey = def.NameProperty;
                if (control.Payload[nameKey] is null) throw new ConfigurationException($"Control {control.Id} payload must define '{nameKey}'.");
                if (control.Payload.ContainsKey("assignments")) throw new ConfigurationException($"Control {control.Id} payload must not contain assignments; assignment is never automated.");
                if (ConditionalAccessSafety.IsConditionalAccess(def))
                {
                    if (!string.Equals(control.SafeDeployment.State, ConditionalAccessSafety.SafeState, StringComparison.Ordinal))
                        throw new ConfigurationException($"Control {control.Id} is a Conditional Access recipe; safeDeployment.state must be '{ConditionalAccessSafety.SafeState}'.");
                    if (control.Payload["state"] is not null && !string.Equals(control.Payload["state"]!.ToString(), ConditionalAccessSafety.SafeState, StringComparison.Ordinal))
                        throw new ConfigurationException($"Control {control.Id} payload requests Conditional Access state '{control.Payload["state"]}'; only '{ConditionalAccessSafety.SafeState}' is permitted.");
                }
                else if (def.Writable && string.IsNullOrWhiteSpace(control.SafeDeployment.State))
                {
                    throw new ConfigurationException($"Control {control.Id} is deployable and must declare safeDeployment.state (for example 'unassigned').");
                }
            }
            else if (control.Payload is not null)
            {
                throw new ConfigurationException($"Control {control.Id} is manual but carries a payload; either make it a settings control or remove the payload.");
            }
            if (control.Equivalence is not null)
            {
                var eq = control.Equivalence;
                var eqCollection = eq.Collection ?? control.Collection;
                if (string.IsNullOrWhiteSpace(eqCollection) || !c.Collections.ContainsKey(eqCollection))
                    throw new ConfigurationException($"Control {control.Id} declares equivalence against unknown collection '{eqCollection}'.");
                if (eq.Signals.Count == 0) throw new ConfigurationException($"Control {control.Id} declares equivalence with no signals.");
                if (!eq.Signals.Any(s => s.Required))
                    throw new ConfigurationException($"Control {control.Id} equivalence has no required signal; coverage would be claimed on optional evidence alone.");
                if (string.IsNullOrWhiteSpace(eq.Note))
                    throw new ConfigurationException($"Control {control.Id} equivalence must carry a note explaining what it does and does not prove.");
                var signalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var signal in eq.Signals.Concat(eq.Caveats))
                {
                    if (string.IsNullOrWhiteSpace(signal.Key)) throw new ConfigurationException($"Control {control.Id} has an equivalence signal with no key.");
                    if (string.IsNullOrWhiteSpace(signal.Label)) throw new ConfigurationException($"Control {control.Id} signal '{signal.Key}' has no label; the label is quoted verbatim in reports.");
                    if (string.IsNullOrWhiteSpace(signal.Path)) throw new ConfigurationException($"Control {control.Id} signal '{signal.Key}' has no path.");
                    if (!signalKeys.Add(signal.Key)) throw new ConfigurationException($"Control {control.Id} has a duplicate equivalence signal key '{signal.Key}'.");
                    var needsValue = signal.Operator is not (SignalOperator.Present or SignalOperator.Absent or SignalOperator.NonEmpty or SignalOperator.Empty);
                    if (needsValue && signal.Value is null)
                        throw new ConfigurationException($"Control {control.Id} signal '{signal.Key}' uses {signal.Operator} but declares no value.");
                }
            }
            foreach (var dep in control.Dependencies)
                if (!c.Controls.Any(x => string.Equals(x.Id, dep, StringComparison.OrdinalIgnoreCase)))
                    throw new ConfigurationException($"Control {control.Id} depends on unknown control '{dep}'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,60}\\.json$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]*$")]
    private static partial Regex ReleasePattern();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex CollectionKeyPattern();

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex ParameterKeyPattern();

    [GeneratedRegex("^[A-Z]+(-[A-Z]+)*-[0-9]{3}$")]
    private static partial Regex ControlIdPattern();
}
