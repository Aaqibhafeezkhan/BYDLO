using System.Security.Cryptography;
using System.Text;

namespace Bydlo.Core.Import;

/// <summary>Recognizes external inputs without fetching URLs or persisting credentials.</summary>
public static class ExternalImportInput
{
    public static IReadOnlyList<string> Schemes { get; } = Array.AsReadOnly(new[]
    {
        "vless", "vmess", "trojan", "ss", "hy2", "hysteria2", "tuic", "wg", "wireguard", "socks", "socks5", "http", "https",
    });

    public static string? LinkKind(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || separator + 3 == value.Length)
        {
            return null;
        }

        var scheme = value[..separator].ToLowerInvariant();
        if (!Schemes.Contains(scheme))
        {
            return null;
        }

        if (scheme is "http" or "https")
        {
            return ProfileImporter.LooksLikeSubscriptionUrl(value) ? "subscription" : null;
        }

        return scheme.ToUpperInvariant();
    }

    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";

    public static bool IsFile(string path) => IsImage(path) || Path.GetExtension(path).ToLowerInvariant() is ".conf" or ".ovpn" or ".txt";

    public static IEnumerable<string> LaunchInputs(IEnumerable<string> args) => args
        .Where(a => !a.StartsWith('-') && (LinkKind(a) is not null || IsFile(a)));
}

/// <summary>Remembers only hashes, for the lifetime of this application session.</summary>
public sealed class ClipboardSuggestionHistory
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public bool TryOffer(string? text, bool enabled)
    {
        if (!enabled || ExternalImportInput.LinkKind(text) is null)
        {
            return false;
        }

        return _seen.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text!.Trim()))));
    }
}
