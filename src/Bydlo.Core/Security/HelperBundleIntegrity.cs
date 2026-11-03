using System.Security.Cryptography;

namespace Bydlo.Core.Security;

/// <summary>Checks the complete administrator-approved bundle, including DLLs and strategy data.</summary>
public static class HelperBundleIntegrity
{
    public static void Verify(string root, IReadOnlyDictionary<string, string> hashes)
    {
        if (hashes.Count == 0) { throw new InvalidDataException("Empty bundle hashes."); }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var expected = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var engineRoot = Path.GetFullPath(Path.Combine(root, "engines")) + Path.DirectorySeparatorChar;
        foreach (var (relative, hash) in hashes)
        {
            var file = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathFullyQualified(relative) || !file.StartsWith(engineRoot, comparison))
            { throw new InvalidDataException("Invalid bundle path."); }
            if (!expected.Add(file)) { throw new InvalidDataException("Duplicate bundle path."); }
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), SHA256.HashData(File.ReadAllBytes(file))))
            { throw new InvalidDataException("The installed engine bundle was modified."); }
        }
        var files = EnumerateSafe(Path.Combine(root, "engines")).ToArray();
        if (!expected.SetEquals(files.Select(Path.GetFullPath))) { throw new InvalidDataException("Unexpected files in engine bundle."); }
    }

    private static IEnumerable<string> EnumerateSafe(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked bundle path."); }
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked bundle entry."); }
            if ((attributes & FileAttributes.Directory) != 0)
            { foreach (var file in EnumerateSafe(entry)) { yield return file; } }
            else { yield return entry; }
        }
    }
}
