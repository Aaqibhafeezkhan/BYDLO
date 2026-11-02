using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Security;
using Bydlo.Engines.Zapret;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>
/// Startup check of the remembered Zapret strategy (issue #49).
/// Runs once per session when Tools is first shown, only for the saved strategy;
/// never enumerates other strategies and never auto-applies anything.
/// </summary>
public sealed partial class ToolsViewModel
{
    [ObservableProperty] private bool _isCheckingStartup;
    [ObservableProperty] private string _startupCheckStatus = "";
    [ObservableProperty] private bool _hasStartupCheckStatus;
    [ObservableProperty] private string _startupCheckDetails = "";
    [ObservableProperty] private bool _hasStartupCheckDetails;
    private bool _startupCheckDone;

    partial void OnIsCheckingStartupChanged(bool value)
    {
        SelectZapretCommand.NotifyCanExecuteChanged();
        RecheckStartupCommand.NotifyCanExecuteChanged();
    }

    private bool CanRecheckStartup() => !IsCheckingStartup && !IsSelecting;

    /// <summary>Automatic once-per-session run. Skips silently when a full selection is in progress.</summary>
    private void MaybeRunStartupCheck(string? savedAtOpen)
    {
        if (_startupCheckDone) { return; }
        _startupCheckDone = true;
        if (!IsSelecting) { _ = RunStartupCheckAsync(savedAtOpen); }
    }

    [RelayCommand(CanExecute = nameof(CanRecheckStartup))]
    private async Task RecheckStartup() => await RunStartupCheckAsync(Services.Settings.Zapret.Strategy);

    private async Task RunStartupCheckAsync(string? savedId)
    {
        if (IsCheckingStartup || IsSelecting) { return; }
        IsCheckingStartup = true;
        StartupCheckStatus = "";
        HasStartupCheckStatus = false;
        StartupCheckDetails = "";
        HasStartupCheckDetails = false;
        try
        {
            string saved;
            IReadOnlyList<string> targets;
            try
            {
                if (Services.IsMock || NetworkSafetyGuard.IsLocked)
                {
                    throw new EngineException("Real checks are disabled.", localizationKey: "tools.startup.locked");
                }
                if (!Services.Privileges.IsElevated)
                {
                    throw new EngineException("Startup check needs administrator / root rights.", localizationKey: "tools.startup.admin");
                }
                var install = Services.Zapret.ResolveInstallation(Services.Settings);
                if (install is not { IsValid: true })
                {
                    throw new EngineException("Zapret is missing.", localizationKey: "error.engine.zapret.missing");
                }
                var state = ZapretStartupCheck.Classify(savedId, install.Strategies.Select(s => s.FileName));
                if (state == SavedStrategyState.Empty)
                {
                    throw new EngineException("No saved strategy.", localizationKey: "tools.startup.nosaved");
                }
                if (state == SavedStrategyState.Unavailable)
                {
                    throw new EngineException("Saved strategy is unavailable.", localizationKey: "tools.startup.unavailable");
                }
                saved = savedId!;
                if (Shell.Zapret.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
                {
                    throw new EngineException("Zapret is already running.", localizationKey: "tools.startup.running");
                }
                try { targets = TestZapretProbe.ParseTargets(string.Join('\n', Services.Settings.Zapret.TestTargets)); }
                catch (FormatException) { throw new EngineException("Invalid test URLs.", localizationKey: "tools.select.badtargets"); }
            }
            catch (EngineException ex)
            {
                // Guard skips stay visible on the status line instead of a vanishing toast.
                StartupCheckStatus = Loc.Error(ex.Message, ex.LocalizationKey);
                HasStartupCheckStatus = true;
                return;
            }

            StartupCheckStatus = Loc.T("tools.startup.checking", saved);
            HasStartupCheckStatus = true;
            ZapretSelectionReport report;
            try
            {
                var probe = new TestZapretProbe(Services.Runner);
                report = await Services.Connections.CheckZapretStrategyAsync(Services.EffectiveSettings,
                    saved, targets, probe, null, Services.ShutdownToken);
            }
            catch (EngineException ex)
            {
                StartupCheckStatus = Loc.Error(ex.Message, ex.LocalizationKey);
                return;
            }
            catch (OperationCanceledException)
            {
                StartupCheckStatus = L["tools.select.cancelled"];
                return;
            }

            if (report is { Failure: null, RestoreFailure: null, Cancelled: false } && report.Candidates.FirstOrDefault() is { } row)
            {
                var summary = ZapretStartupCheck.Summarize(row.Rounds.SelectMany(r => r).ToArray());
                var key = summary.Verdict switch
                {
                    StartupCheckVerdict.Works => "tools.startup.works",
                    StartupCheckVerdict.Partial => "tools.startup.partial",
                    StartupCheckVerdict.Unavailable => "tools.startup.unchecked",
                    _ => "tools.startup.failed",
                };
                StartupCheckStatus = Loc.T(key, saved, summary.Successes, summary.Total);
                var lines = summary.Targets.Select(t => Loc.T("tools.startup.target", t.Target, t.Successes, t.Total,
                    t.AverageMilliseconds is { } ms ? Loc.T("tools.select.ms", ms.ToString("0")) : "—"));
                StartupCheckDetails = string.Join('\n', lines.Concat(report.Log));
                HasStartupCheckDetails = true;
            }
            else
            {
                StartupCheckStatus = report.RestoreFailure is not null ? L["tools.select.restorefailed"]
                    : report.Cancelled ? L["tools.select.cancelled"]
                    : L["tools.select.failed"];
                StartupCheckDetails = string.Join('\n', report.Log);
                if (report.Failure is not null) { StartupCheckDetails += "\n" + report.Failure; }
                if (report.RestoreFailure is not null) { StartupCheckDetails += "\n" + report.RestoreFailure; }
                HasStartupCheckDetails = !string.IsNullOrEmpty(StartupCheckDetails);
            }
        }
        finally { IsCheckingStartup = false; }
    }
}
