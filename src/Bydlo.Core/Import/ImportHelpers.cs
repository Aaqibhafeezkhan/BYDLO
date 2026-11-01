using System.Text;
using System.Web;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

public interface IProfileParser
{
    bool CanParse(string input);

    /// <summary>Parses one import unit (a link or a config file). Throws <see cref="ImportException"/> on invalid input.</summary>
    ImportedProfile Parse(string input);
}

public sealed class ImportException(string message) : FormatException(message);

internal static class ImportHelpers
{
    /// <summary>Decodes standard or URL-safe base64, with or without padding.</summary>
    public static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = "";
        var s = text.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
        if (s.Length == 0 || s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
        {
            return false;
        }

        s = s.TrimEnd('=');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "" };
        if (s.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Parses the query of a URI into a case-insensitive dictionary (first value wins).</summary>
    public static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in q.AllKeys)
        {
            if (key is not null && q[key] is { } v && !result.ContainsKey(key))
            {
                result[key] = v;
            }
        }

        return result;
    }

    public static string Fragment(Uri uri, string fallback)
    {
        var f = uri.Fragment.TrimStart('#');
        return string.IsNullOrWhiteSpace(f) ? fallback : Uri.UnescapeDataString(f).Trim();
    }

    public static Uri ParseUri(string input, string scheme)
    {
        // Uri cannot parse IPv6 hosts without brackets nor some exotic userinfo; normalize the most common issues.
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException($"Not a valid {scheme}:// link.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new ImportException($"The {scheme}:// link has no server address.");
        }

        return uri;
    }

    public static string Host(Uri uri) => uri.Host.Trim('[', ']');

    public static int Port(Uri uri, int defaultPort)
    {
        var p = uri.IsDefaultPort || uri.Port <= 0 ? defaultPort : uri.Port;
        return p is > 0 and <= 65535 ? p : throw new ImportException("Invalid port.");
    }

    public static void Copy(Dictionary<string, string> query, Profile profile, params (string From, string To)[] map)
    {
        foreach (var (from, to) in map)
        {
            if (query.TryGetValue(from, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                profile.Options[to] = v;
            }
        }
    }
}
