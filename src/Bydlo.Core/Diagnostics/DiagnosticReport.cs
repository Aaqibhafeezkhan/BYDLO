using System.Text.RegularExpressions;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>
/// One-click bug-report content (issue #213): BYDLO version, OS and RID, pinned engine
/// versions and hashes, non-secret settings, a redacted log tail and the latest crash report.
/// Profile names, hosts, subscription URLs and secrets never enter the report; server
/// addresses are masked at the report level. The whole text goes through
/// <see cref="LogRedactor"/> last as defense in depth.
/// </summary>
public static partial class DiagnosticReport
{
    public const int MaxLogLines = 30;

    public const string BugTemplateUrl = "https://github.com/endercodezz/BYDLO/issues/new?template=bug.yml";

    public sealed record DiagnosticEngineInfo(string Id, string Version, IReadOnlyDictionary<string, string> AssetHashes);

    /// <summary>
    /// Allowlist snapshot of settings: modes, ports, toggles and counts only.
    /// Paths, names, hosts, URLs, DNS values, rule values and identifiers are excluded.
    /// </summary>
    public static IReadOnlyList<string> SummarizeSettings(
        AppSettings settings,
        IReadOnlyDictionary<ProtocolKind, int> profilesByProtocol,
        int subscriptionCount)
    {
        var total = profilesByProtocol.Values.Sum();
        var byProtocol = total == 0
            ? "none"
            : string.Join(", ", profilesByProtocol.OrderBy(p => p.Key.ToString()).Select(p => $"{p.Key}: {p.Value}"));
        return
        [
            $"Mode: {settings.Mode}",
            $"Local proxy: 127.0.0.1:{settings.LocalProxyPort} (LAN: {OnOff(settings.AllowLan)}, system proxy: {OnOff(settings.SetSystemProxy)})",
            $"Kill switch: {OnOff(settings.KillSwitch)}, auto-reconnect: {OnOff(settings.AutoReconnect)}",
            $"Theme: {settings.Theme}, language: {settings.Language}, mock: {OnOff(settings.MockMode)}",
            $"Routing preset: {settings.Routing.Preset} (rules: {settings.Routing.OrderedRules.Count}, DNS: {settings.Routing.RemoteDnsTransport}/{settings.Routing.DirectDnsTransport}, FakeIP: {OnOff(settings.Routing.FakeIp)})",
            $"DPI: fragment={settings.DpiDefaults.Fragment} {settings.DpiDefaults.Size}/{settings.DpiDefaults.Interval}, mux={settings.DpiDefaults.Mux}, padding: {OnOff(settings.DpiDefaults.Padding)}",
            $"Zapret: strategy={settings.Zapret.Strategy ?? "default"}, game filter={settings.Zapret.GameFilter}",
            $"Custom engine paths: {(settings.EnginePaths.Count == 0 ? "none" : string.Join(", ", settings.EnginePaths.Keys.OrderBy(k => k, StringComparer.Ordinal)))}",
            $"Profiles: {total} ({byProtocol}), subscriptions: {subscriptionCount}",
        ];
    }

    public static string Build(
        string appVersion,
        string osDescription,
        string runtimeId,
        IReadOnlyList<DiagnosticEngineInfo> engines,
        IReadOnlyList<string> settingsLines,
        IReadOnlyList<string> logLines,
        string? crashReport,
        LogRedactor redactor)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine("BYDLO diagnostic report");
        body.AppendLine($"Version: {appVersion}");
        body.AppendLine($"OS: {osDescription} (RID: {runtimeId})");
        body.AppendLine("Engines:");
        foreach (var engine in engines)
        {
            body.AppendLine($"- {engine.Id} {engine.Version}");
            foreach (var asset in engine.AssetHashes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                body.AppendLine($"  {asset.Key} sha256:{asset.Value}");
            }
        }
        body.AppendLine("Settings:");
        foreach (var line in settingsLines)
        {
            body.AppendLine($"- {line}");
        }
        body.AppendLine($"Logs (last {MaxLogLines}):");
        foreach (var line in logLines.TakeLast(MaxLogLines))
        {
            body.AppendLine(line);
        }
        body.AppendLine("Crash report (latest):");
        body.AppendLine(string.IsNullOrWhiteSpace(crashReport) ? "none" : crashReport.Trim());
        return MaskServerAddresses(redactor.Redact(body.ToString()));
    }

    /// <summary>
    /// The log redactor keeps hosts for debugging; a bug report must not carry server
    /// addresses, so IPv4/IPv6 literals (except loopback) and dotted hostnames are masked here.
    /// </summary>
    internal static string MaskServerAddresses(string text)
    {
        text = Ipv4().Replace(text, m => m.Value.StartsWith("127.", StringComparison.Ordinal) ? m.Value : "***.***.***.***");
        text = Ipv6().Replace(text, "***");
        text = Hostname().Replace(text, "***");
        return text;
    }

    // Full and ::-compressed literals, but not clock times (needs '::' or 4+ groups).
    [GeneratedRegex(@"(?<![0-9a-fA-F:.])(?:[0-9a-fA-F:.]{0,20}::[0-9a-fA-F:.]{1,45}|(?:[0-9a-fA-F]{1,4}:){4,}[0-9a-fA-F:.]+)")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+(?:[a-z]{2,}|xn--[a-z0-9]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hostname();

    private static string OnOff(bool value) => value ? "on" : "off";
}
