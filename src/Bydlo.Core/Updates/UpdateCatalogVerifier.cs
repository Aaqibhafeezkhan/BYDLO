using System.Globalization;
using System.Security.Cryptography;

namespace Bydlo.Core.Updates;

/// <summary>Why a catalog or package was refused. Every value keeps the installed version.</summary>
public enum UpdateTrustFailure
{
    CatalogUnavailable,
    CatalogTooLarge,
    MalformedEnvelope,
    UnknownKey,
    RevokedKey,
    InvalidSignature,
    MalformedPayload,
    UnsupportedSchema,
    Expired,
    NotYetValid,
    WrongChannel,
    UnsupportedRid,
    NotNewer,
    AssetMismatch,
}

/// <summary>A refused update. The caller keeps the installed version and reports a localized message.</summary>
public sealed class UpdateTrustException(UpdateTrustFailure failure, string message) : Exception(message)
{
    public UpdateTrustFailure Failure { get; } = failure;
}

/// <summary>A catalog entry that passed every check and may be handed to the updater backend.</summary>
public sealed record TrustedUpdate(string Channel, string Version, string Rid, string FileName, long Size, string Sha256)
{
    /// <summary>
    /// Compares what the updater backend found or downloaded with the signed entry. A mismatch means the
    /// feed or the staged package changed after signing, so nothing may be installed.
    /// </summary>
    public void EnsureMatches(string? version, string? fileName, long size, string? sha256)
    {
        if (!string.Equals(version, Version, StringComparison.Ordinal) ||
            !string.Equals(fileName, FileName, StringComparison.Ordinal) ||
            size != Size ||
            !string.Equals(sha256, Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateTrustException(UpdateTrustFailure.AssetMismatch,
                $"Update asset does not match the signed catalog: expected {FileName}, {Size} bytes, SHA-256 {Sha256}.");
        }
    }
}

/// <summary>
/// Pure verification policy for the signed update catalog (issue #411). It authenticates the version,
/// channel, RID, package name, size and SHA-256 before anything reaches Velopack, and refuses
/// downgrades, replays, expired metadata and unknown or revoked keys.
/// </summary>
public sealed class UpdateCatalogVerifier
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>Only the stable channel is published today.</summary>
    public const string StableChannel = "stable";

    /// <summary>Velopack package id of the desktop application.</summary>
    public const string PackageId = "BydloDesktop";

    /// <summary>Upper bound for a full package; a larger catalog entry is treated as malformed.</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>Tolerated clock drift when checking <c>issuedAt</c> and <c>expiresAt</c>.</summary>
    public static TimeSpan ClockSkew { get; } = TimeSpan.FromMinutes(10);

    /// <summary>RIDs the release pipeline publishes. Anything else is refused.</summary>
    public static IReadOnlyList<string> SupportedRids { get; } = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"];

    /// <summary>
    /// Reserved hook for the trusted recovery rollback of #412. An ordinary update never downgrades,
    /// so this stays false until #412 defines its own trusted source and UI states.
    /// </summary>
    public const bool AllowRecoveryRollback = false;

    private readonly IReadOnlyDictionary<string, string> _keys;
    private readonly HashSet<string> _revoked;
    private readonly TimeProvider _time;

    /// <summary>Defaults to the compiled-in trust root; tests inject their own keys and clock.</summary>
    public UpdateCatalogVerifier(IReadOnlyDictionary<string, string>? keys = null,
        IEnumerable<string>? revokedKeyIds = null, TimeProvider? timeProvider = null)
    {
        _keys = keys ?? UpdateTrustRoots.Keys;
        _revoked = new HashSet<string>(revokedKeyIds ?? UpdateTrustRoots.RevokedKeyIds, StringComparer.Ordinal);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Verifies the envelope and returns the trusted entry for <paramref name="rid"/>. Throws
    /// <see cref="UpdateTrustException"/> on any failure; the caller must not touch the updater backend then.
    /// </summary>
    public TrustedUpdate Verify(string envelopeJson, string channel, string rid, string installedVersion)
    {
        var envelope = UpdateCatalogJson.ParseEnvelope(envelopeJson)
            ?? throw new UpdateTrustException(UpdateTrustFailure.MalformedEnvelope, "The update catalog envelope is malformed.");
        if (_revoked.Contains(envelope.KeyId))
        {
            throw new UpdateTrustException(UpdateTrustFailure.RevokedKey, $"Update signing key '{envelope.KeyId}' is revoked.");
        }

        if (!_keys.TryGetValue(envelope.KeyId, out var spki) || string.IsNullOrWhiteSpace(spki))
        {
            throw new UpdateTrustException(UpdateTrustFailure.UnknownKey, $"Update signing key '{envelope.KeyId}' is not trusted.");
        }

        var payloadBytes = DecodeBase64(envelope.Payload);
        var signature = DecodeBase64(envelope.Signature);
        VerifySignature(spki, payloadBytes, signature, envelope.KeyId);
        var payload = UpdateCatalogJson.ParsePayload(payloadBytes)
            ?? throw new UpdateTrustException(UpdateTrustFailure.MalformedPayload, "The update catalog payload is malformed.");
        if (payload.SchemaVersion != SupportedSchemaVersion)
        {
            throw new UpdateTrustException(UpdateTrustFailure.UnsupportedSchema,
                $"Unsupported update catalog schema {payload.SchemaVersion}.");
        }

        var now = _time.GetUtcNow();
        if (payload.ExpiresAt + ClockSkew < now)
        {
            throw new UpdateTrustException(UpdateTrustFailure.Expired, $"The update catalog expired at {payload.ExpiresAt:O}.");
        }

        if (payload.IssuedAt - ClockSkew > now)
        {
            throw new UpdateTrustException(UpdateTrustFailure.NotYetValid,
                $"The update catalog is issued in the future ({payload.IssuedAt:O}).");
        }

        if (!string.Equals(payload.Channel, channel, StringComparison.Ordinal))
        {
            throw new UpdateTrustException(UpdateTrustFailure.WrongChannel,
                $"The update catalog is for channel '{payload.Channel}', not '{channel}'.");
        }

        if (!SupportedRids.Contains(rid, StringComparer.Ordinal))
        {
            throw new UpdateTrustException(UpdateTrustFailure.UnsupportedRid, $"Updates are not published for '{rid}'.");
        }

        // Downgrade and replay protection: a signed but older catalog must never replace a newer install.
        // A recovery rollback (#412) is a separate, explicitly trusted flow and is not implemented here.
        if (!AllowRecoveryRollback && !IsNewer(installedVersion, payload.Version))
        {
            throw new UpdateTrustException(UpdateTrustFailure.NotNewer,
                $"The update catalog offers {payload.Version}, which is not newer than {installedVersion}.");
        }

        var entry = FindEntry(payload, rid);
        var expectedName = $"{PackageId}-{payload.Version}-{rid}-full.nupkg";
        if (!string.Equals(entry.FileName, expectedName, StringComparison.Ordinal))
        {
            throw new UpdateTrustException(UpdateTrustFailure.MalformedPayload,
                $"The update catalog names '{entry.FileName}', expected '{expectedName}'.");
        }

        if (entry.Size <= 0 || entry.Size > MaxPackageBytes)
        {
            throw new UpdateTrustException(UpdateTrustFailure.MalformedPayload,
                $"The update catalog size for '{rid}' is out of range.");
        }

        if (entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
        {
            throw new UpdateTrustException(UpdateTrustFailure.MalformedPayload,
                $"The update catalog SHA-256 for '{rid}' is malformed.");
        }

        return new TrustedUpdate(payload.Channel, payload.Version, rid, entry.FileName, entry.Size, entry.Sha256.ToLowerInvariant());
    }

    private static UpdateCatalogPackage FindEntry(UpdateCatalogPayload payload, string rid)
    {
        UpdateCatalogPackage? found = null;
        foreach (var package in payload.Packages)
        {
            if (!string.Equals(package.Rid, rid, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new UpdateTrustException(UpdateTrustFailure.MalformedPayload,
                    $"Duplicate update catalog entry for '{rid}'.");
            }

            found = package;
        }

        return found ?? throw new UpdateTrustException(UpdateTrustFailure.UnsupportedRid,
            $"The update catalog has no package for '{rid}'.");
    }

    private static byte[] DecodeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new UpdateTrustException(UpdateTrustFailure.MalformedEnvelope,
                "The update catalog envelope is not valid base64.");
        }
    }

    private static void VerifySignature(string spki, byte[] payload, byte[] signature, string keyId)
    {
        bool valid;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            // ECDSA P-256 / SHA-256; the default signature format is IEEE P1363 (r||s), which is what
            // scripts/sign-update-catalog.ps1 produces.
            valid = ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new UpdateTrustException(UpdateTrustFailure.UnknownKey, $"Update signing key '{keyId}' is unusable.");
        }

        if (!valid)
        {
            throw new UpdateTrustException(UpdateTrustFailure.InvalidSignature, "The update catalog signature is invalid.");
        }
    }

    private static bool IsNewer(string installed, string candidate)
    {
        if (!TryParseVersion(candidate, out var next) || !TryParseVersion(installed, out var current))
        {
            return false;
        }

        var length = Math.Max(next.Length, current.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < current.Length ? current[i] : 0;
            var right = i < next.Length ? next[i] : 0;
            if (right != left)
            {
                return right > left;
            }
        }

        return false;
    }

    private static bool TryParseVersion(string value, out int[] parts)
    {
        parts = [];
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var segments = text.Split('.');
        if (segments.Length is < 1 or > 4)
        {
            return false;
        }

        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || !segments[i].All(char.IsAsciiDigit) ||
                !int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        parts = parsed;
        return true;
    }
}
