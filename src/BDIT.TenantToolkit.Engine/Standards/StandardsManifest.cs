using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;

namespace BDIT.TenantToolkit.Engine.Standards;

/// <summary>
/// standards/manifest.json records a SHA-256 digest for every standard release file. The loader refuses to load a
/// release whose digest is missing or does not match. This detects modification of the catalogue after release; it is
/// an integrity digest, not a cryptographic signature, and the manifest itself must be protected by release signing
/// or package checksums.
/// </summary>
public sealed class StandardsManifest
{
    public const string FileName = "manifest.json";
    public const string Note = "SHA-256 integrity digests of the BDIT Build Standard release files. Detects modification after release; this is not a cryptographic signature.";

    public string Algorithm { get; set; } = "SHA-256";
    public string GeneratedAt { get; set; } = "";
    public string GeneratedBy { get; set; } = "";
    public string NoteText { get; set; } = Note;
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static StandardsManifest Generate(string standardsDirectory, string generatedBy, DateTimeOffset now)
    {
        var manifest = new StandardsManifest { GeneratedAt = Timestamps.Format(now), GeneratedBy = generatedBy };
        foreach (var file in Directory.EnumerateFiles(standardsDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase)) continue;
            manifest.Files[name] = CanonicalJson.Sha256Hex(File.ReadAllBytes(file));
        }
        return manifest;
    }

    public static void Write(string standardsDirectory, StandardsManifest manifest)
    {
        var path = Path.Combine(standardsDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToolkitJson.Serialize(manifest));
        File.Move(temp, path, overwrite: true);
    }

    public static StandardsManifest Load(string standardsDirectory)
    {
        var path = Path.Combine(standardsDirectory, FileName);
        if (!File.Exists(path))
            throw new IntegrityException("standards/manifest.json is missing. Refusing to load an unverified Build Standard. Use the released package, or run build/Update-StandardsManifest.ps1 after editing a standard.");
        try
        {
            var manifest = ToolkitJson.Deserialize<StandardsManifest>(File.ReadAllText(path));
            if (!string.Equals(manifest.Algorithm, "SHA-256", StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"standards/manifest.json uses unsupported algorithm '{manifest.Algorithm}'.");
            return manifest;
        }
        catch (Exception ex) when (ex is not IntegrityException)
        {
            throw new IntegrityException($"standards/manifest.json could not be read: {ex.Message}");
        }
    }

    /// <summary>Returns the verified digest of the file or throws.</summary>
    public string Verify(string standardsDirectory, string fileName)
    {
        if (!Files.TryGetValue(fileName, out var expected))
            throw new IntegrityException($"'{fileName}' is not listed in standards/manifest.json. Refusing to load an unverified Build Standard.");
        var actual = CanonicalJson.Sha256Hex(File.ReadAllBytes(Path.Combine(standardsDirectory, fileName)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException($"'{fileName}' does not match its recorded digest (expected {expected[..12]}…, found {actual[..12]}…). The Build Standard has been modified since release. Refusing to load.");
        return actual;
    }
}
