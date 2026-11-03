using System.Text;
using System.Text.RegularExpressions;
using Bydlo.Core.Platform;
using Bydlo.Core.Models;
using Bydlo.Core.Engines;

namespace Bydlo.Engines.Zapret;

/// <summary>Game filter port ranges, mirroring <c>service.bat :game_switch_status</c> from Flowseal.</summary>
public sealed record GameFilterValues(string GameFilter, string Tcp, string Udp)
{
    public const string DefaultRange = "1024-65535";

    /// <summary>"12" is Flowseal's placeholder port that effectively disables the game filter.</summary>
    public static GameFilterValues From(string mode, string tcpRange = DefaultRange, string udpRange = DefaultRange) =>
        mode.ToLowerInvariant() switch
        {
            "all" => new(tcpRange, tcpRange, udpRange),
            "tcp" => new(tcpRange, tcpRange, "12"),
            "udp" => new(udpRange, "12", udpRange),
            _ => new("12", "12", "12"),
        };
}

public sealed record ZapretStrategy(string FileName, string DisplayName);

public sealed record ParsedStrategy(IReadOnlyList<string> Arguments, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a Flowseal strategy (<c>general*.bat</c>) and extracts the <c>winws.exe</c> argument list, substituting
/// the same variables the batch file would (<c>%BIN%</c>, <c>%LISTS%</c>, game filter). This lets BYDLO start
/// winws.exe directly (no console window, captured output) while using upstream strategies unchanged.
/// </summary>
public static partial class ZapretStrategyParser
{
    public static IReadOnlyList<ZapretStrategy> Discover(string installDir)
    {
        if (!Directory.Exists(installDir))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(installDir, "*.bat")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase))
            .OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase)
            .Select(n => new ZapretStrategy(n, Path.GetFileNameWithoutExtension(n)))];
    }

    public static ParsedStrategy Parse(string batchText, string installDir, GameFilterValues gameFilter, string? userListsDirectory = null, PlatformInfo? platform = null)
    {
        var warnings = new List<string>();
        var paths = new TargetPathRules((platform ?? PlatformInfo.Current).Os);
        var root = paths.Normalize(paths.IsAbsolute(installDir) ? installDir : Path.GetFullPath(installDir)) + paths.Separator;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = root + "bin" + paths.Separator,
            ["LISTS"] = root + "lists" + paths.Separator,
            ["~dp0"] = root,
            ["GameFilter"] = gameFilter.GameFilter,
            ["GameFilterTCP"] = gameFilter.Tcp,
            ["GameFilterUDP"] = gameFilter.Udp,
        };

        var line = JoinContinuations(batchText).FirstOrDefault(l => l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new EngineException("Strategy file does not start winws.exe.", localizationKey: "error.engine.zapret.strategy.executable");

        var idx = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + "winws.exe".Length;
        if (idx < line.Length && line[idx] == '"')
        {
            idx++;
        }

        var args = new List<string>();
        foreach (var token in Tokenize(line[idx..]))
        {
            var value = Variable().Replace(token, m =>
            {
                var name = m.Groups["name"].Value;
                if (vars.TryGetValue(name, out var v))
                {
                    return v;
                }

                warnings.Add($"Unknown variable %{name}% left as is.");
                return m.Value;
            });
            if (value.Length > 0)
            {
                if (userListsDirectory is not null)
                {
                    foreach (var name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
                    {
                        value = value.Replace(paths.Combine(root, "lists", name), paths.Combine(userListsDirectory, name), StringComparison.OrdinalIgnoreCase);
                    }
                }

                args.Add(value);
            }
        }

        if (args.Count == 0)
        {
            throw new EngineException("Strategy has no winws.exe arguments.", localizationKey: "error.engine.zapret.strategy.arguments");
        }

        return new ParsedStrategy(args, warnings);
    }

    /// <summary>Joins lines ending with the batch continuation character <c>^</c>.</summary>
    internal static IEnumerable<string> JoinContinuations(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('^'))
            {
                sb.Append(line[..^1]).Append(' ');
                continue;
            }

            sb.Append(line);
            yield return sb.ToString();
            sb.Clear();
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>Splits on whitespace; double quotes group and are removed (cmd.exe semantics for these files).</summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }

                continue;
            }

            if (c == '^' && !inQuotes)
            {
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static string NaturalKey(string name) => Digits().Replace(name, m => m.Value.PadLeft(8, '0'));

    [GeneratedRegex(@"%(?<name>~dp0|[A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex Variable();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
