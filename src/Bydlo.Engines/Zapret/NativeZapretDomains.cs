namespace Bydlo.Engines.Zapret;

internal static class NativeZapretDomains
{
    internal static string Merge(string defaults, string additions) =>
        string.Join('\n', (defaults + "\n" + additions).Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)) + "\n";

    internal static string Load()
    {
        using var stream = typeof(NativeZapretDomains).Assembly.GetManifestResourceStream("bydlo-zapret-list.txt")
            ?? throw new InvalidOperationException("Missing bundled BYDLO Zapret domain list.");
        using var reader = new StreamReader(stream);
        return Merge(NativeZapretStrategy.DefaultHosts, reader.ReadToEnd());
    }
}
