using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>Writes redacted crash reports for unhandled exceptions (issue #205).</summary>
public static class CrashReporter
{
    public const int MaxReports = 5;

    public const string FilePrefix = "crash-";

    /// <summary>Installs process-wide handlers. Dispatcher handler is installed separately in the UI head.</summary>
    public static void Install(string logsDirectory, LogRedactor? redactor = null)
    {
        redactor ??= LogRedactor.Shared;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                WriteReport(logsDirectory, redactor, ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteReport(logsDirectory, redactor, e.Exception);
            e.SetObserved();
        };
    }

    public static string WriteReport(string logsDirectory, Exception exception) =>
        WriteReport(logsDirectory, LogRedactor.Shared, exception);

    public static string WriteReport(string logsDirectory, LogRedactor redactor, Exception exception)
    {
        Directory.CreateDirectory(logsDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(logsDirectory, $"{FilePrefix}{stamp}-{Guid.NewGuid().ToString("N")[..8]}.log");
        var body = redactor.Redact(BuildBody(exception));
        try
        {
            File.WriteAllText(path, body);
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }

        TrimOldReports(logsDirectory);
        return path;
    }

    public static IReadOnlyList<string> ListReports(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(logsDirectory, FilePrefix + "*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void TrimOldReports(string logsDirectory)
    {
        var reports = ListReports(logsDirectory);
        foreach (var extra in reports.Skip(MaxReports))
        {
            try
            {
                File.Delete(extra);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string BuildBody(Exception exception)
    {
        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : Environment.OSVersion.ToString();
        return $"BYDLO crash report {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"Version: {AppVersion.Current}{Environment.NewLine}" +
            $"OS: {os}{Environment.NewLine}" +
            $"{exception}";
    }
}
