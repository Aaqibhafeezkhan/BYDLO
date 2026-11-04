using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ConnectionsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<SubscriptionRow> Subscriptions { get; } = [];

    [ObservableProperty] private bool _isWorking;

    [ObservableProperty] private bool _isAutoRefreshing;

    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(CanRegisterWarp));

    private bool _autoRefreshInProgress;
    private readonly SubscriptionAutoRefresh _autoRefresh = new(shell.Services.Profiles, shell.Services.Subscriptions);

    public Task RunAutoRefreshAsync(CancellationToken ct) => _autoRefresh.RunAsync(async token =>
    {
        await Dispatcher.UIThread.InvokeAsync(() => AutoRefreshIfDueAsync(token));
    }, ct);

    [ObservableProperty] private bool _isCheckingLatency;

    /// <summary>User-initiated latency probe only; never runs in Mock Mode or under the safety lock.</summary>
    public bool CanCheckLatency => !IsCheckingLatency && !Services.IsMock && !NetworkSafetyGuard.IsLocked;

    public string? CheckLatencyDisabledReason => IsCheckingLatency ? null
        : Services.IsMock ? L["connections.latency.mock"]
        : NetworkSafetyGuard.IsLocked ? L["connections.latency.locked"] : null;

    partial void OnIsCheckingLatencyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckLatency));
        OnPropertyChanged(nameof(CheckLatencyDisabledReason));
    }

    [RelayCommand]
    private async Task CheckLatency()
    {
        if (IsCheckingLatency || Services.IsMock || NetworkSafetyGuard.IsLocked)
        {
            return;
        }

        IsCheckingLatency = true;
        try
        {
            IReadOnlyDictionary<Guid, ServerLatencyResult>? results = null;
            await Shell.Guard(async () =>
            {
                results = await Services.ServerLatency.ProbeAsync(Services.Profiles.Profiles).ConfigureAwait(false);
            });
            if (results is not null)
            {
                Shell.SetLatencies(results);
            }
        }
        finally
        {
            IsCheckingLatency = false;
        }
    }

    public bool IsEmpty => Shell.Profiles.Count == 0;

    [ObservableProperty] private string? _profileSearch;

    [ObservableProperty] private bool _onlyWorking;

    public IEnumerable<ProfileItem> VisibleProfiles
    {
        get
        {
            IEnumerable<ProfileItem> items = Shell.Profiles;
            if (OnlyWorking)
            {
                items = items.Where(profile =>
                    Shell.Latencies.TryGetValue(profile.Id, out var probe) && probe.Reachable);
            }
            var query = ProfileSearch?.Trim();
            return string.IsNullOrEmpty(query) ? items : items.Where(profile =>
                profile.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                profile.Endpoint.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                profile.ProtocolLabel.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool HasNoMatchingProfiles => !IsEmpty && !VisibleProfiles.Any();

    partial void OnProfileSearchChanged(string? value) => RefreshProfileSearch();

    partial void OnOnlyWorkingChanged(bool value) => RefreshProfileSearch();

    internal void RefreshProfileSearch()
    {
        OnPropertyChanged(nameof(VisibleProfiles));
        OnPropertyChanged(nameof(HasNoMatchingProfiles));
    }

    public override void OnShown()
    {
        Subscriptions.ReplaceWith(Services.Profiles.Subscriptions.Select(s => new SubscriptionRow(s)));
        OnPropertyChanged(nameof(IsEmpty));
        _ = AutoRefreshIfDueAsync(Services.ShutdownToken);
        OnPropertyChanged(nameof(CanMeasureSpeed));
        OnPropertyChanged(nameof(SpeedTestDisabledReason));
        OnPropertyChanged(nameof(CanCheckLatency));
        OnPropertyChanged(nameof(CheckLatencyDisabledReason));
    }

    public Task AddImportedAsync(ImportResult result) => AddAsync(result, null);

    public async Task ImportExternalSubscriptionAsync(string url)
    {
        var preview = await Services.Subscriptions.FetchPreviewAsync(url);
        if (preview.Import.IsEmpty)
        {
            throw new InvalidOperationException(L["import.none"]);
        }

        var sub = await Services.Profiles.AddSubscriptionWithProfilesAsync(new Uri(url).Host, url, preview.Import.Profiles, preview.Metadata);
        Shell.ReloadProfiles();
        Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, preview.Import.Profiles.Count));
        OnShown();
    }

    [RelayCommand]
    private async Task RefreshSubscription(SubscriptionRow row)
    {
        if (IsWorking) { return; }
        if (Services.IsMock || NetworkSafetyGuard.IsLocked)
        {
            Shell.ShowToast(L["catalog.locked"], isError: true);
            return;
        }
        IsWorking = true;
        try
        {
            await Shell.Guard(async () =>
            {
                var url = await Services.Profiles.GetSubscriptionUrlAsync(row.Id) ?? throw new InvalidOperationException("Subscription URL missing.");
                var preview = await Services.Subscriptions.FetchRefreshAsync(url, row.Snapshot.UserAgent, Services.ShutdownToken);
                if (preview.Import.IsEmpty)
                {
                    Shell.ShowToast(L["wizard.error.subscription"], isError: true);
                    return;
                }

                await Services.Profiles.ReplaceSubscriptionProfilesAsync(row.Id, preview.Import.Profiles, preview.Metadata, Services.ShutdownToken);
                Shell.ReloadProfiles();

                Shell.ShowToast(Loc.T("import.sub.updated", row.Snapshot.Name, preview.Import.Profiles.Count));
            });
        }
        finally
        {
            IsWorking = false;
            OnShown();
        }
    }

    [RelayCommand]
    private async Task ToggleSubscriptionMode(SubscriptionRow row)
    {
        var next = row.Snapshot.Mode == SubscriptionMode.SelectedOnly
            ? SubscriptionMode.FollowAll
            : SubscriptionMode.SelectedOnly;
        await Shell.Guard(async () =>
        {
            await Services.Profiles.SetSubscriptionModeAsync(row.Id, next);
            OnShown();
            Shell.ShowToast(L["toast.saved"]);
        });
    }

    [RelayCommand]
    private async Task RenameSubscription(SubscriptionRow row)
    {
        var name = (row.Name ?? "").Trim();
        if (name.Length == 0 || name == row.Snapshot.Name)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            await Services.Profiles.RenameSubscriptionAsync(row.Id, name);
            OnShown();
            Shell.ShowToast(L["toast.saved"]);
        });
    }

    [RelayCommand]
    private async Task SaveSubscriptionUserAgent(SubscriptionRow row)
    {
        await Shell.Guard(async () =>
        {
            await Services.Profiles.SetSubscriptionUserAgentAsync(row.Id, row.UserAgent);
            OnShown();
            Shell.ShowToast(L["toast.saved"]);
        });
    }

    [RelayCommand]
    private async Task DeleteSubscription(SubscriptionRow row)
    {
        var session = Services.Connections.Primary;
        if (session.HoldsResources && (session.Profile?.SubscriptionId == row.Id ||
            Services.Profiles.Profiles.Any(p => p.SubscriptionId == row.Id && session.Chain?.ProfileIds.Contains(p.Id) == true)))
        {
            Shell.ShowToast(L["connections.delete.active"], isError: true);
            return;
        }
        await Shell.Guard(async () =>
        {
            await Services.Profiles.RemoveSubscriptionAsync(row.Id);
            Shell.ReloadProfiles();
            OnShown();
        });
    }

    /// <summary>Refreshes due subscriptions on the UI thread, including while another page is visible.</summary>
    public async Task AutoRefreshIfDueAsync(CancellationToken ct = default)
    {
        if (_autoRefreshInProgress || IsWorking || IsAutoRefreshing || ct.IsCancellationRequested) { return; }
        _autoRefreshInProgress = true;
        IsAutoRefreshing = true;
        try
        {
            var refreshed = await _autoRefresh.RefreshIfDueAsync(Services.Settings,
                Services.IsMock || NetworkSafetyGuard.IsLocked,
                ex => Services.Log.Warn("subs", $"Auto-update failed: {Services.Log.Redactor.Redact(ex.Message)} ({ex.GetType().Name})"), ct);
            if (refreshed > 0)
            {
                Shell.ReloadProfiles();
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            IsAutoRefreshing = false;
            _autoRefreshInProgress = false;
            Subscriptions.ReplaceWith(Services.Profiles.Subscriptions.Select(s => new SubscriptionRow(s)));
        }
    }

    [RelayCommand]
    private async Task Select(ProfileItem item)
    {
        Shell.SelectedProfile = item;
        Shell.SelectedChain = null;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConnectTo(ProfileItem item)
    {
        // MainViewModel.Primary is a snapshot refreshed on the UI thread, so ToggleConnection
        // could misread it mid-switch. Disconnect and connect explicitly instead (issue #141).
        var alreadyActive = Shell.Home.IsConnected
            && Shell.Primary.Chain is null
            && Shell.Primary.Group is null
            && Shell.Primary.Profile?.Id == item.Id;
        Shell.Home.UseGroup = false;
        Shell.SelectedProfile = item;
        Shell.SelectedChain = null;
        Shell.Navigate("home");
        if (alreadyActive)
        {
            return;
        }
        await Shell.Guard(() => Services.Connections.DisconnectAsync());
        foreach (var w in Services.Connections.PreviewConflicts(item.Profile, Services.EffectiveSettings).Where(i => i.Severity == ConflictSeverity.Warning))
        {
            Shell.ShowToast(w.Message);
        }
        await Shell.Guard(() => Services.Connections.ConnectAsync(item.Profile, Services.EffectiveSettings));
    }

    [RelayCommand]
    private async Task ToggleFavorite(ProfileItem item)
    {
        item.Profile.IsFavorite = !item.Profile.IsFavorite;
        await Services.Profiles.UpdateAsync(item.Profile);
        Shell.ReloadProfiles();
    }

    [RelayCommand]
    private Task CopyLink(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.copy");

    [RelayCommand]
    private Task ShowQr(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.qr");

    [RelayCommand]
    private Task ExportConfig(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.file");

    [RelayCommand]
    private Task<bool> EditProfile(ProfileItem item) => Shell.Guard(async () =>
    {
        if (await ProfileEditorDialog.ShowAsync(Services, item.Profile) is { } edited)
        {
            await Services.Profiles.UpdateAsync(edited.Profile, edited.Secrets);
            Shell.ReloadProfiles();
            OnShown();
        }
    });

    [RelayCommand]
    private Task<bool> DuplicateProfile(ProfileItem item) => Shell.Guard(async () =>
    {
        var source = item.Profile;
        var copy = new Profile
        {
            Name = Loc.T("profile.edit.copyName", source.Name),
            Protocol = source.Protocol,
            Host = source.Host,
            Port = source.Port,
            Options = new Dictionary<string, string>(source.Options, StringComparer.OrdinalIgnoreCase),
            PreferredEngine = source.PreferredEngine,
        };
        var secrets = await Services.Secrets.LoadAsync(source.Id.ToString());
        await Services.Profiles.AddAsync([new ImportedProfile(copy, secrets)]);
        Shell.ReloadProfiles();
        OnShown();
    });

    [RelayCommand]
    private async Task Delete(ProfileItem item)
    {
        var session = Services.Connections.Primary;
        if (session.HoldsResources && session.Profile?.Id == item.Id)
        {
            Shell.ShowToast(L["connections.delete.active"], isError: true);
            return;
        }

        if (session.HoldsResources && session.Chain?.ProfileIds.Contains(item.Id) == true)
        {
            Shell.ShowToast(L["chain.delete.active"], isError: true);
            return;
        }

        await Services.Profiles.RemoveAsync(item.Id);
        Shell.ReloadProfiles();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task AddDemoProfiles()
    {
        // Mock Mode helper: fake, clearly labelled profiles for UI testing. They never reach a real engine.
        var demo = new ImportResult(
        [
            Demo("Demo · Amsterdam", ProtocolKind.Vless, "nl.demo.invalid"),
            Demo("Demo · Frankfurt", ProtocolKind.Hysteria2, "de.demo.invalid"),
            Demo("Demo · Helsinki", ProtocolKind.WireGuard, "fi.demo.invalid"),
        ], []);
        await AddAsync(demo, null);

        static ImportedProfile Demo(string name, ProtocolKind kind, string host) =>
            new(new Profile { Name = name, Protocol = kind, Host = host, Port = 443, Options = { ["demo"] = "true" } }, new ProfileSecrets());
    }

    /// <summary>Imports supplied text for internal callers; interactive entry points use the wizard.</summary>
    public Task ImportTextAsync(string text) => ImportAsync(text, null);

    private async Task ImportAsync(string text, Guid? subscription)
    {
        if (ProfileImporter.LooksLikeSubscriptionUrl(text))
        {
            await ImportExternalSubscriptionAsync(text.Trim());
            return;
        }
        await AddAsync(Services.Importer.ImportText(text), subscription);
    }

    private async Task AddAsync(ImportResult result, Guid? subscription)
    {
        if (result.Profiles.Count > 0)
        {
            var added = await Services.Profiles.AddAsync(result.Profiles, subscription);
            Shell.ReloadProfiles();
            Shell.SelectedProfile = Shell.Profiles.FirstOrDefault(p => p.Id == added[0].Id) ?? Shell.SelectedProfile;

            Shell.ShowToast(Loc.T("import.added", added.Count) + (result.Errors.Count > 0 ? " " + Loc.T("import.skipped", result.Errors.Count) : ""));
        }
        else
        {
            var error = result.Errors.Count > 0 ? result.Errors[0] : null;
            Shell.ShowToast(error == "import.openvpn.unsafe" ? L[error] : error ?? L["import.none"], isError: true);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
