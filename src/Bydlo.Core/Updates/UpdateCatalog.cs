using System.Text.Json;

namespace Bydlo.Core.Updates;

/// <summary>
/// Signed envelope published as the <c>releases-catalog.json</c> release asset (issue #411).
/// <see cref="Payload"/> is base64 of the exact UTF-8 bytes the signature covers.
/// </summary>
public sealed record UpdateCatalogEnvelope(string Payload, string KeyId, string Signature);

/// <summary>Verified catalog payload: one release, one package per RID.</summary>
public sealed record UpdateCatalogPayload(
    int SchemaVersion,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Channel,
    string Version,
    IReadOnlyList<UpdateCatalogPackage> Packages);

/// <summary>One RID entry of the signed catalog.</summary>
public sealed record UpdateCatalogPackage(string Rid, string FileName, long Size, string Sha256);

/// <summary>
/// Parses the catalog envelope and payload. Parsing trusts nothing: the verifier checks the signature
/// over the exact payload bytes before any field is used, so a tampered payload never reaches policy.
/// </summary>
public static class UpdateCatalogJson
{
    public static UpdateCatalogEnvelope? ParseEnvelope(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "payload", out var payload) ||
                !TryString(root, "keyId", out var keyId) ||
                !TryString(root, "signature", out var signature))
            {
                return null;
            }

            return new UpdateCatalogEnvelope(payload, keyId, signature);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static UpdateCatalogPayload? ParsePayload(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                !TryTimestamp(root, "issuedAt", out var issuedAt) ||
                !TryTimestamp(root, "expiresAt", out var expiresAt) ||
                !TryString(root, "channel", out var channel) ||
                !TryString(root, "version", out var version) ||
                !root.TryGetProperty("packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<UpdateCatalogPackage>();
            foreach (var item in packages.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryString(item, "rid", out var rid) ||
                    !TryString(item, "fileName", out var fileName) ||
                    !TryString(item, "sha256", out var sha256) ||
                    !item.TryGetProperty("size", out var size) ||
                    size.ValueKind != JsonValueKind.Number ||
                    !size.TryGetInt64(out var bytes))
                {
                    return null;
                }

                entries.Add(new UpdateCatalogPackage(rid, fileName, bytes, sha256));
            }

            return new UpdateCatalogPayload(schemaVersion, issuedAt, expiresAt, channel, version, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return value.Length > 0;
    }

    private static bool TryTimestamp(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            element.TryGetDateTimeOffset(out value);
    }
}
