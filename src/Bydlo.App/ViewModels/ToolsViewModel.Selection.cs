using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Security;
using Bydlo.Engines.Zapret;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed record ZapretResultRow(string Strategy, string Successes, string Errors, string Timeouts, string Latency, string Verdict);

public sealed partial class ToolsViewModel
{
    private static readonly JsonSerializerOptions SelectionJsonOptions = new() { WriteIndented = true };
    [ObservableProperty] private bool _isSelecting;
    [ObservableProperty] private string _testTargets = "";
    [ObservableProperty] private string _selectionStatus = "";
    [ObservableProperty] private string _selectionLog = "";
    [ObservableProperty] private bool _canApplySelection;
    public ObservableCollection<ZapretResultRow> SelectionResults { get; } = [];
    private CancellationTokenSource? _selectionCancellation;
    private ZapretSelectionReport? _selectionReport;
    private string? _selectionContext;
    private string SelectionFile => Path.Combine(Services.Paths.DataDirectory, "zapret-selection.json");
    private string SelectionContext
    {
        get
        {
            var install = Services.Zapret.ResolveInstallation(Services.Settings);
            var files = install is null ? [] : install.Strategies.Where(s => !s.FileName.StartsWith("native-", StringComparison.Ordinal))
                .Select(s => Path.Combine(install.Root, s.FileName)).Append(install.ExecutablePath);
            var hashes = files.Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            return JsonSerializer.Serialize(Services.Settings.Zapret) + install?.Root + install?.Version + string.Join(':', hashes);
        }
    }

    private void LoadSelection()
    {
        TestTargets = string.Join('\n', Services.Settings.Zapret.TestTargets);
        try
        {
            if (File.Exists(SelectionFile))
            {
                _selectionReport = JsonSerializer.Deserialize<ZapretSelectionReport>(File.ReadAllText(SelectionFile));
                if (_selectionReport is not null) { ShowSelectionReport(_selectionReport); }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            SelectionStatus = L["tools.select.readfailed"];
        }
    }

    partial void OnIsSelectingChanged(bool value)
    {
        SelectZapretCommand.NotifyCanExecuteChanged();
        RecheckStartupCommand.NotifyCanExecuteChanged();
    }
    private bool CanSelectZapret() => !IsSelecting && !IsCheckingStartup;

    [RelayCommand(CanExecute = nameof(CanSelectZapret))]
    private async Task SelectZapret()
    {
        IsSelecting = true;
        CanApplySelection = false;
        _selectionReport = null;
        SelectionResults.Clear();
        SelectionLog = "";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _selectionCancellation = cancellation;
        try
        {
            await Shell.Guard(async () =>
            {
                if (Services.IsMock || NetworkSafetyGuard.IsLocked)
                {
                    throw new EngineException("Real checks are disabled in Mock Mode / safe development.", localizationKey: "tools.select.locked");
                }
                if (!Services.Privileges.IsElevated)
                {
                    throw new EngineException("Zapret selection needs administrator / root rights.", localizationKey: "tools.select.admin");
                }
                IReadOnlyList<string> targets;
                try { targets = TestZapretProbe.ParseTargets(TestTargets); }
                catch (FormatException) { throw new EngineException("Invalid test URLs.", localizationKey: "tools.select.badtargets"); }
                var install = Services.Zapret.ResolveInstallation(Services.Settings);
                if (install is not { IsValid: true })
                {
                    throw new EngineException("Zapret is missing.", localizationKey: "error.engine.zapret.missing");
                }
                if (IsWindows && !File.Exists(Path.Combine(install.Root, "utils", "test zapret.ps1")))
                {
                    throw new EngineException("Test Zapret is missing.", localizationKey: "tools.select.missing");
                }
                var strategies = install.Strategies.Select(s => s.FileName).ToArray();
                if (strategies.Length == 0)
                {
                    throw new EngineException("No strategies found.", localizationKey: "error.engine.zapret.strategies");
                }
                var probe = new TestZapretProbe(Services.Runner);
                Services.Settings.Zapret.TestTargets = [.. targets];
                await Services.SaveSettingsAsync();
                _selectionContext = SelectionContext;
                var current = Services.Settings.Zapret.Strategy ?? ZapretEngine.DefaultStrategy(install);
                var progress = new Progress<ZapretSelectionProgress>(p =>
                {
                    SelectionStatus = p.Total > 0
                        ? Loc.T("tools.select.progress", L["tools.select." + p.Stage], p.Strategy ?? "", p.Completed, p.Total)
                        : L["tools.select." + p.Stage];
                    if (p.Stage == "checked")
                    {
                        SelectionStatus += " · " + Loc.T("tools.select.brief", p.Successes, p.Errors, p.Timeouts, p.Unknown);
                        SelectionLog += SelectionStatus + "\n";
                    }
                });
                var report = await Services.Connections.SelectZapretStrategyAsync(Services.EffectiveSettings, strategies, targets, current,
                    probe, progress, cancellation.Token);
                // Flush queued progress before displaying the final state.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                _selectionReport = report;
                ShowSelectionReport(report);
                await SaveSelectionReportAsync(report);
                CanApplySelection = report.RecommendedStrategy is not null && report.RestoreFailure is null && !report.Cancelled && report.Failure is null;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SelectionStatus = L["tools.select.cancelled"];
        }
        finally
        {
            _selectionCancellation = null;
            IsSelecting = false;
        }
    }

    [RelayCommand]
    private void CancelSelection() => _selectionCancellation?.Cancel();

    [RelayCommand]
    private async Task ApplySelection()
    {
        if (IsSelecting || !CanApplySelection || _selectionReport?.RecommendedStrategy is not { } strategy) { return; }
        IsSelecting = true;
        CanApplySelection = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _selectionCancellation = cancellation;
        try
        {
            await Shell.Guard(async () =>
            {
                if (Services.IsMock || NetworkSafetyGuard.IsLocked)
                {
                    throw new EngineException("Real checks are disabled.", localizationKey: "tools.select.locked");
                }
                if (_selectionContext != SelectionContext)
                {
                    throw new EngineException("Settings changed. Run selection again.", localizationKey: "tools.select.stale");
                }
                var previous = Services.Settings.Zapret.Strategy;
                try
                {
                    await Services.Connections.ApplyZapretStrategyAsync(Services.EffectiveSettings, strategy, async () =>
                    {
                        Services.Settings.Zapret.Strategy = strategy;
                        await Services.SaveSettingsAsync();
                    }, cancellation.Token);
                }
                catch
                {
                    Services.Settings.Zapret.Strategy = previous;
                    throw;
                }
                _selectionReport.AppliedStrategy = strategy;
                await SaveSelectionReportAsync(_selectionReport);
                RefreshZapret();
                SelectionStatus = Loc.T("tools.select.applied", strategy);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SelectionStatus = L["tools.select.cancelled"];
        }
        finally { _selectionCancellation = null; IsSelecting = false; }
    }

    private async Task SaveSelectionReportAsync(ZapretSelectionReport report)
    {
        var temporary = SelectionFile + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, SelectionJsonOptions));
        File.Move(temporary, SelectionFile, overwrite: true);
    }

    private void ShowSelectionReport(ZapretSelectionReport report)
    {
        SelectionResults.ReplaceWith(ZapretSelectionService.Rank(report.Candidates, report.OriginalStrategy).Select(row => new ZapretResultRow(
            row.Strategy, row.Successes.ToString(), row.Errors.ToString(), $"{row.Timeouts} / {row.Unknown}",
            row.Milliseconds is { } ms ? Loc.T("tools.select.ms", ms.ToString("0")) : "—",
            row.Strategy == report.RecommendedStrategy ? L["tools.select.recommended"] : "")));
        SelectionLog = string.Join('\n', report.Log);
        SelectionStatus = report.RestoreFailure is not null ? L["tools.select.restorefailed"]
            : report.Cancelled ? L["tools.select.cancelled"]
            : report.Failure is not null ? L["tools.select.failed"]
            : report.RecommendedStrategy is null ? L["tools.select.none"]
            : Loc.T("tools.select.result", report.TestedAt.ToLocalTime().ToString("g"), report.RecommendedStrategy);
        if (report.Failure is not null) { SelectionLog += "\n" + report.Failure; }
        if (report.RestoreFailure is not null) { SelectionLog += "\n" + report.RestoreFailure; }
        if (report.AppliedStrategy is not null && report.RestoreFailure is null && report.Failure is null && !report.Cancelled)
        {
            SelectionStatus = Loc.T("tools.select.applied", report.AppliedStrategy) + " · " + report.TestedAt.ToLocalTime().ToString("g");
        }
    }
}
