namespace Bydlo.Platform;

internal static class MacNetworkServices
{
    public static IReadOnlyList<string> Active(string network, string serviceOrder)
    {
        var interfaces = network.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Network interfaces:", StringComparison.Ordinal))
            .SelectMany(line => line["Network interfaces:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        var active = new List<string>();
        string? service = null;
        foreach (var line in serviceOrder.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            const string marker = ", Device: ";
            if (line.StartsWith("(Hardware Port:", StringComparison.Ordinal))
            {
                var offset = line.LastIndexOf(marker, StringComparison.Ordinal);
                if (service is not null && offset >= 0 && interfaces.Contains(line[(offset + marker.Length)..].TrimEnd(')')))
                {
                    active.Add(service);
                }
                service = null;
                continue;
            }

            var closing = line.IndexOf(')');
            service = line.StartsWith('(') && closing > 1 && int.TryParse(line.AsSpan(1, closing - 1), out _)
                ? line[(closing + 1)..].Trim() : null;
        }
        return active;
    }
}
