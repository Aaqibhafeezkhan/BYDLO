using System.Text.RegularExpressions;

namespace Bydlo.Core.Security;

/// <summary>
/// Removes credentials from text before it is shown or written to disk.
/// Also supports explicit registration of known secret values (e.g. the active profile's password).
/// </summary>
public sealed partial class LogRedactor
{
    public const string Mask = "***";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public static LogRedactor Shared { get; } = new();

    /// <summary>Registers a literal value that must never appear in logs (min. 4 chars).</summary>
    public void RegisterSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4)
        {
            return;
        }

        lock (_gate)
        {
            _known.Add(value);
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        string[] known;
        lock (_gate)
        {
            known = [.. _known];
        }

        foreach (var secret in known.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = UriCredentials().Replace(text, m => $"{m.Groups["scheme"].Value}://{Mask}@");
        text = KeyValueSecret().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
        text = Uuid().Replace(text, Mask);
        text = WireGuardKey().Replace(text, m => m.Groups["prefix"].Value + Mask);
        text = TelegramSecret().Replace(text, m => m.Groups["prefix"].Value + Mask);
        return text;
    }

    // vless://uuid@host, ss://base64@host, socks5://user:pass@host
    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]{1,15})://[^\s/@]{3,}@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentials();

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pass|secret|private[_\- ]?key|presharedkey|pre_shared_key|psk|token|auth|uuid|id)\b[""']?)(?<sep>\s*[:=]\s*[""']?)[^\s""',;&}]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Uuid();

    // 44-char base64 keys (WireGuard) when prefixed by a key-ish word
    [GeneratedRegex(@"(?<prefix>(?:key|PrivateKey|PresharedKey)\s*[=:]?\s*)[A-Za-z0-9+/]{42,43}=", RegexOptions.IgnoreCase)]
    private static partial Regex WireGuardKey();

    // MTProto secrets: dd/ee + 32 hex, "Secret: <hex>"
    [GeneratedRegex(@"(?<prefix>secret(?:=|:\s*)(?:dd|ee)?)[0-9a-fA-F]{32,}", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramSecret();
}
