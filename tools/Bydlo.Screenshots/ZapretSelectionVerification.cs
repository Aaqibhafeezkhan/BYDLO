using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Diagnostics;

internal static class ZapretSelectionVerification
{
    public static void Run(MainViewModel shell, AppServices services, string output)
    {
        // Explicit fixture data for visual verification only; production never fabricates probe results.
        var report = new ZapretSelectionReport
        {
            OriginalStrategy = "general.bat", RecommendedStrategy = "general (ALT Кириллица).bat",
            Targets = ["https://example.invalid/"],
            Candidates =
            [
                new("general.bat", [[new("https://example.invalid/", "HTTP", ZapretCheckOutcome.Error, null)], [new("https://example.invalid/", "HTTP", ZapretCheckOutcome.Timeout, null)]]),
                new("general (ALT Кириллица).bat", [[new("https://example.invalid/", "HTTP", ZapretCheckOutcome.Success, 125)], [new("https://example.invalid/", "HTTP", ZapretCheckOutcome.Success, 150)]])
            ],
        };
        var reportFile = Path.Combine(services.Paths.DataDirectory, "zapret-selection.json");
        File.WriteAllText(reportFile, JsonSerializer.Serialize(report));
        var tools = new ToolsViewModel(shell);
        if (tools.SelectionResults.Count != 2 || tools.CanApplySelection)
        {
            throw new InvalidOperationException("Persisted report did not load or stale recommendation was enabled.");
        }
        foreach (var (name, width) in new[] { ("selection-desktop", 1000), ("selection-narrow", 400) })
        {
            var window = new Window { Width = width, Height = 900, Content = new ToolsView { DataContext = tools } };
            window.Show();
            Pump(300);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing selection frame.");
            frame.Save(Path.Combine(output, name + ".png"));
            window.Close();
        }
        var command = tools.SelectZapretCommand.ExecuteAsync(null);
        while (!command.IsCompleted) { Pump(10); }
        command.GetAwaiter().GetResult();
        if (tools.IsSelecting || tools.CanApplySelection || tools.SelectionResults.Count != 0 || tools.SelectionLog.Length != 0)
        {
            throw new InvalidOperationException("Mock Mode fabricated results or left the operation active.");
        }
        if (services.Connections.Zapret.Status != Bydlo.Core.Connection.ConnectionStatus.Disconnected)
        {
            throw new InvalidOperationException("Mock selection started an engine.");
        }
        Console.WriteLine("Zapret selection UI: report loading, desktop/narrow rendering, stale-apply guard and Mock Mode refusal passed.");
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
