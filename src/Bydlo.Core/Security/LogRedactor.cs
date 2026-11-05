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

        // Credential blobs stored as JSON (e.g. XHTTP headers, raw configs) would
        // otherwise mask only the whole blob: register every nested string value
        // too, so a separately logged Cookie or token is still redacted.
        if ((value[0] is '{' or '[') && value.Length <= 16384)
        {
            try
            {
                foreach (var leaf in JsonStringLeaves(value, depth: 0))
                {
                    if (leaf.Length >= 4)
                    {
                        lock (_gate)
                        {
                            _known.Add(leaf);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
            {
            }
        }
    }

    private static IEnumerable<string> JsonStringLeaves(string json, int depth)
    {
        if (depth > 8)
        {
            yield break;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var stack = new Stack<(System.Text.Json.JsonElement Element, int Depth)>();
        stack.Push((doc.RootElement, depth));
        while (stack.Count > 0)
        {
            var (element, current) = stack.Pop();
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    yield return element.GetString() ?? "";
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    if (current < 8)
                    {
                        foreach (var property in element.EnumerateObject())
                        {
                            stack.Push((property.Value, current + 1));
                        }
                    }
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    if (current < 8)
                    {
                        foreach (var item in element.EnumerateArray())
                        {
                            stack.Push((item, current + 1));
                        }
                    }
                    break;
            }
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
        // Whole bodies without userinfo (ss/vmess base64 blobs may embed host and port).
        text = BareSchemeBody().Replace(text, m => $"{m.Groups["scheme"].Value}://{Mask}");
        text = AuthHeader().Replace(text, m => m.Groups["prefix"].Value + Mask);
        text = KeyValueSecret().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
        text = Uuid().Replace(text, Mask);
        text = WireGuardKey().Replace(text, m => m.Groups["prefix"].Value + Mask);
        text = TelegramSecret().Replace(text, m => m.Groups["prefix"].Value + Mask);
        return text;
    }

    // vless://uuid@host, ss://base64@host (base64 may contain '/'), socks5://user:pass@host
    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]{1,15})://[^\s@?]{3,}@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentials();

    // Whole vmess:// and ss:// bodies without '@': base64 JSON or method:password@host:port blobs.
    [GeneratedRegex(@"(?<scheme>vmess|ss)://[^\s#@]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareSchemeBody();

    [GeneratedRegex(@"(?<key>\b(?:password|ss_password|shadow_password|private_key_passphrase|passwd|pass|secret|private[_\- ]?key|presharedkey|pre_shared_key|psk|token|auth|auth_str|uuid|id)\b[""']?)(?<sep>\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s""',;&}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    // Authorization: Bearer/Basic/Token <credentials> (or a bare token after the colon).
    [GeneratedRegex(@"(?<prefix>\bAuthorization\s*[:=]\s*(?:(?:Bearer|Basic|Token)\s+)?)[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Uuid();

    // 44-char base64 keys (WireGuard) when prefixed by a key-ish word
    [GeneratedRegex(@"(?<prefix>(?:key|PrivateKey|PresharedKey)\s*[=:]?\s*)[A-Za-z0-9+/]{42,43}=", RegexOptions.IgnoreCase)]
    private static partial Regex WireGuardKey();

    // MTProto secrets: dd/ee + 32 hex, "Secret: <hex>"
    [GeneratedRegex(@"(?<prefix>secret(?:=|:\s*)(?:dd|ee)?)[0-9a-fA-F]{32,}", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramSecret();
}
