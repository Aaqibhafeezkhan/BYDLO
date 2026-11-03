using Bydlo.Core.Models;

namespace Bydlo.Core.Platform;

/// <summary>Lexical target paths. Never resolves a Windows path against a Unix working directory.</summary>
public sealed class TargetPathRules(OsFamily os)
{
    public char Separator => os == OsFamily.Windows ? '\\' : '/';
    public StringComparer Comparer => os == OsFamily.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public string FileName(string path) => os == OsFamily.Windows
        ? path.Replace('\\', '/').Split('/')[^1] : path.Split('/')[^1];

    public bool IsAbsolute(string path) => os == OsFamily.Windows
        ? path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            || path.StartsWith(@"\\", StringComparison.Ordinal) && path[2..].Split('\\').Length >= 3
        : path.StartsWith('/');

    public string Normalize(string path)
    {
        if (!IsAbsolute(path)) { throw new ArgumentException("An absolute target path is required.", nameof(path)); }
        return os == OsFamily.Windows ? path.Replace('/', '\\').TrimEnd('\\') : path.TrimEnd('/');
    }

    public string Combine(string root, params string[] parts) =>
        Normalize(root) + Separator + string.Join(Separator, parts.Select(p => p.Replace('\\', Separator).Replace('/', Separator).Trim(Separator)));
}
