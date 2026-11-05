using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bydlo.Engines.Installation;

public sealed record EngineAsset(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("executable")] string Executable);

public sealed record EngineLockEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("assets")] Dictionary<string, EngineAsset> Assets)
{
    private readonly Dictionary<string, string> _bundledExecutables = [];

    // Constructor-based source-generated deserialization passes null for a missing init-only property.
    [JsonPropertyName("bundledExecutables")]
    public Dictionary<string, string> BundledExecutables { get => _bundledExecutables; init => _bundledExecutables = value ?? []; }
}

/// <summary>Pinned engine versions and digests from <c>engines/engines.lock.json</c> (embedded at build time).</summary>
public sealed class EngineLock
{
    public static string BundleVersion { get; } = ReadBundleVersion();

    private static string ReadBundleVersion()
    {
        using var stream = typeof(EngineLock).Assembly.GetManifestResourceStream("engines.lock.json")
            ?? throw new InvalidDataException("engines.lock.json resource missing.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [JsonPropertyName("engines")]
    public List<EngineLockEntry> Engines { get; init; } = [];

    public EngineLockEntry? Find(string id) => Engines.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static EngineLock Parse(string json) =>
        JsonSerializer.Deserialize(json, EnginesJsonContext.Default.EngineLock) ?? throw new InvalidDataException("engines.lock.json is empty.");

    public static EngineLock LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("engines.lock.json")
            ?? throw new InvalidDataException("engines.lock.json resource missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
