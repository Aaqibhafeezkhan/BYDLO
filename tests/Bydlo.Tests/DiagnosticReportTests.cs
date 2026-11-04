using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue #213: the one-click bug report must not leak secrets or server addresses.</summary>
public class DiagnosticReportTests
{
    private static readonly string Uuid = "11111111-2222-3333-4444-555555555555";

    private static DiagnosticReport.DiagnosticEngineInfo Engine() => new("sing-box", "1.12.0",
        new Dictionary<string, string> { ["win-x64"] = new string('a', 64) });

    private static string BuildAdversarial()
    {
        var redactor = new LogRedactor();
        redactor.RegisterSecret("supersecret-auth-token-xyz");
        redactor.RegisterSecret("correct horse battery staple");
        var settings = new AppSettings { Mode = ConnectionMode.Tun, LocalProxyPort = 2080 };
        var lines = DiagnosticReport.SummarizeSettings(
            settings,
            new Dictionary<ProtocolKind, int> { [ProtocolKind.Vless] = 2 },
            subscriptionCount: 1);
        var logs = new[]
        {
            $"connecting with vless://{Uuid}@203.0.113.7:443?security=reality",
            "Authorization: Bearer supersecret-auth-token-xyz",
            "\"password\": \"correct horse battery staple\"",
            "ss://YWVzLTI1Ni1nY206cGFzcy9zZWNyZXQ/Pw==@198.51.100.9:8388#x",
            "server example.com resolved to 2001:db8::1",
            "update available at https://github.com/endercodezz/BYDLO/releases",
        };
        return DiagnosticReport.Build("0.3.1", "Microsoft Windows 10.0.26100", "win-x64",
            [Engine()], lines, logs, $"crash with {Uuid} at 192.0.2.1", redactor);
    }

    [Fact]
    public void Report_hides_secrets_uuids_links_and_server_addresses()
    {
        var report = BuildAdversarial();
        Assert.DoesNotContain("supersecret-auth-token-xyz", report, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", report, StringComparison.Ordinal);
        Assert.DoesNotContain(Uuid, report, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", report, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.9", report, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", report, StringComparison.Ordinal);
        Assert.DoesNotContain("cGFzcy9zZWNyZXQ", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_keeps_version_engines_and_safe_settings()
    {
        var report = BuildAdversarial();
        Assert.Contains("0.3.1", report, StringComparison.Ordinal);
        Assert.Contains("win-x64", report, StringComparison.Ordinal);
        Assert.Contains("sing-box 1.12.0", report, StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), report, StringComparison.Ordinal);
        Assert.Contains("Mode: Tun", report, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:2080", report, StringComparison.Ordinal);
        Assert.Contains("Vless: 2", report, StringComparison.Ordinal);
        Assert.Contains("subscriptions: 1", report, StringComparison.Ordinal);
        Assert.Contains("Logs (last 30):", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_summary_excludes_identifiers()
    {
        var settings = new AppSettings
        {
            LocalProxyUsername = "lan-user",
            CustomThemeId = "my-theme",
            LastProfileId = Guid.NewGuid(),
        };
        settings.EnginePaths["sing-box"] = "C:\\Users\\name\\custom\\sing-box.exe";
        settings.Routing.OrderedRules.Add(new Bydlo.Core.Routing.OrderedRoutingRule(Bydlo.Core.Routing.RoutingCondition.Domain, "example.org", Bydlo.Core.Routing.RoutingAction.Proxy));
        var lines = string.Join('\n', DiagnosticReport.SummarizeSettings(settings, new Dictionary<ProtocolKind, int>(), 0));
        Assert.DoesNotContain("lan-user", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("my-theme", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\name", lines, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.LastProfileId.ToString()!, lines, StringComparison.Ordinal);
        Assert.Contains("sing-box", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_address_masking_keeps_loopback_and_clock_times()
    {
        var masked = DiagnosticReport.MaskServerAddresses(
            "ping 127.0.0.1:2080 ok 2001:db8::1 bad ::1 bad 12:34:56 full 2001:0db8:85a3:08d3:1319:8a2e:0370:7334 end");
        Assert.Contains("127.0.0.1:2080", masked, StringComparison.Ordinal);
        Assert.Contains("12:34:56", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::1", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:0db8:85a3:08d3:1319:8a2e:0370:7334", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Bug_template_url_points_to_bug_issue_template()
    {
        Assert.Contains("template=bug.yml", DiagnosticReport.BugTemplateUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_strings_exist_in_both_languages()
    {
        foreach (var file in new[] { "en.json", "ru.json" })
        {
            var path = Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Assets", "Strings", file);
            var json = File.ReadAllText(path);
            Assert.Contains("\"report.copy\"", json, StringComparison.Ordinal);
            Assert.Contains("\"report.open\"", json, StringComparison.Ordinal);
            Assert.Contains("\"report.hint\"", json, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
