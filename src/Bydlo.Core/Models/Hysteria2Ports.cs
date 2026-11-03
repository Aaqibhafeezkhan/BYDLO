using System.Globalization;

namespace Bydlo.Core.Models;

internal static class Hysteria2Ports
{
    public static (int Start, int End)[] Parse(string value) => value.Split(',').Select(item =>
    {
        var bounds = item.Trim().Split(['-', ':']);
        if (bounds.Length is < 1 or > 2 || !TryPort(bounds[0], out var start)
            || !TryPort(bounds[^1], out var end) || start > end)
        {
            throw new FormatException("Invalid Hysteria2 port list.");
        }
        return (Start: start, End: end);
    }).ToArray();

    // Older profiles store only the ports after Profile.Port. Keep those profiles working too.
    public static string[] Ranges(int primary, string additional)
    {
        var ranges = Parse(additional).ToList();
        if (!ranges.Any(range => range.Start <= primary && primary <= range.End))
        {
            ranges.Insert(0, (primary, primary));
        }
        return ranges.Distinct().Select(range => FormattableString.Invariant($"{range.Start}:{range.End}")).ToArray();
    }

    public static string Additional((int Start, int End)[] ranges) => string.Join(',',
        (ranges[0].Start == ranges[0].End ? ranges.Skip(1) : ranges)
        .Select(range => range.Start == range.End
            ? range.Start.ToString(CultureInfo.InvariantCulture)
            : FormattableString.Invariant($"{range.Start}-{range.End}")));

    private static bool TryPort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
