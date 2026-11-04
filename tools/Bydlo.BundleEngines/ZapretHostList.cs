namespace Bydlo.BundleEngines;

internal static class ZapretHostList
{
    public static void Apply(string executable, string additionsPath)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(executable))!;
        var hostList = Path.Combine(root, "lists", "list-general.txt");
        var original = File.ReadAllText(hostList);
        var existing = original.Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<string>();
        foreach (var line in File.ReadLines(additionsPath))
        {
            var domain = line.Trim();
            if (domain.Length == 0 || domain.StartsWith('#')) { continue; }
            if (!domain.Contains('.') || Uri.CheckHostName(domain) != UriHostNameType.Dns ||
                domain.EndsWith('.') || domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            {
                throw new InvalidDataException($"Invalid domain in the BYDLO Zapret list: {domain}");
            }

            if (existing.Add(domain)) { additions.Add(domain.ToLowerInvariant()); }
        }

        if (additions.Count == 0) { return; }
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = original.Length == 0 || original.EndsWith('\n') ? "" : newline;
        File.AppendAllText(hostList, separator + string.Join(newline, additions) + newline);
    }
}
