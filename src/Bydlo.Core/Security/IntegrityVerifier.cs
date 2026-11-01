using System.Security.Cryptography;

namespace Bydlo.Core.Security;

/// <summary>SHA-256 verification for downloaded engines and updates.</summary>
public static class IntegrityVerifier
{
    /// <summary>Accepts "sha256:abc..." or plain hex.</summary>
    public static string NormalizeDigest(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        var d = digest.Trim();
        if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            d = d[7..];
        }

        if (d.Length != 64 || !d.All(Uri.IsHexDigit))
        {
            throw new FormatException("SHA-256 digest must be 64 hex characters.");
        }

        return d.ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return await ComputeSha256Async(fs, ct).ConfigureAwait(false);
    }

    /// <summary>Throws <see cref="IntegrityException"/> when the file does not match.</summary>
    public static async Task VerifyFileAsync(string path, string expectedDigest, CancellationToken ct = default)
    {
        var expected = NormalizeDigest(expectedDigest);
        var actual = await ComputeFileSha256Async(path, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
        {
            throw new IntegrityException($"Integrity check failed for {Path.GetFileName(path)}: expected {expected}, got {actual}.");
        }
    }
}

public sealed class IntegrityException(string message) : Exception(message);
