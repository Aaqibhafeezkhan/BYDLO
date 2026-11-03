using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Covers issue #205: crash reports never contain secrets and rotation is bounded.</summary>
public sealed class CrashReporterTests
{
    [Fact]
    public void Crash_report_redacts_secrets_and_credentials()
    {
        var dir = TestDirs.New("crash");
        var redactor = new LogRedactor();
        var uuid = Guid.NewGuid().ToString();
        var password = "super-secret-pw-123";
        redactor.RegisterSecret(uuid);
        redactor.RegisterSecret(password);
        var ex = new InvalidOperationException($"Failed for vless://{uuid}@host:443 with password={password}");
        var path = CrashReporter.WriteReport(dir, redactor, ex);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(uuid, text, StringComparison.Ordinal);
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Crash_reports_are_rotated()
    {
        var dir = TestDirs.New("crash-rotate");
        var redactor = new LogRedactor();
        for (var i = 0; i < CrashReporter.MaxReports + 3; i++)
        {
            CrashReporter.WriteReport(dir, redactor, new InvalidOperationException($"boom {i}"));
        }
        var reports = CrashReporter.ListReports(dir);
        Assert.Equal(CrashReporter.MaxReports, reports.Count);
    }
}
