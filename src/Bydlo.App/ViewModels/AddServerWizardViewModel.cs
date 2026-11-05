using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public enum AddServerStep { Source, Link, File, Qr, Manual, Preview, Catalog }

/// <summary>One previewed server with explicit selection and an optional latency probe result (issue #136).</summary>
public sealed partial class ServerPreviewItem(ImportedProfile item, string displayName, string displayEndpoint) : ObservableObject
{
    /// <summary>Full profile with secrets, used only when the user confirms the selection.</summary>
    public ImportedProfile Item { get; } = item;

    /// <summary>Credential-masked display name.</summary>
    public string Name { get; } = displayName;

    public string Protocol => Labels.Protocol(Item.Profile.Protocol);

    /// <summary>Credential-masked display endpoint.</summary>
    public string Endpoint { get; } = displayEndpoint;

    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>Last measured TCP latency (issue #136). Null when not measured yet.</summary>
    [ObservableProperty]
    private TimeSpan? _latency;

    /// <summary>True when the last latency probe failed to reach the server.</summary>
    [ObservableProperty]
    private bool _latencyFailed;

    public bool HasLatency => Latency is not null || LatencyFailed;

    public bool ShowLatency => Latency is not null;

    public string LatencyText => Latency is { } t ? Format.Latency(t) : "";

    public bool ShowUnreachable => LatencyFailed;

    public bool LatencyOk => !LatencyFailed && Latency is { } ok && ok < TimeSpan.FromMilliseconds(200);

    public bool LatencyWarn => !LatencyFailed && Latency is { } warn && warn >= TimeSpan.FromMilliseconds(200) && warn < TimeSpan.FromMilliseconds(800);

    public bool LatencyBad => LatencyFailed || (Latency is { } bad && bad >= TimeSpan.FromMilliseconds(800));

    partial void OnLatencyChanged(TimeSpan? value) => RefreshLatency();

    partial void OnLatencyFailedChanged(bool value) => RefreshLatency();

    private void RefreshLatency()
    {
        OnPropertyChanged(nameof(HasLatency));
        OnPropertyChanged(nameof(ShowLatency));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(ShowUnreachable));
        OnPropertyChanged(nameof(LatencyOk));
        OnPropertyChanged(nameof(LatencyWarn));
        OnPropertyChanged(nameof(LatencyBad));
    }
}

public sealed partial class AddServerWizardViewModel : PageViewModel
{
    private readonly SubscriptionClient _subscriptions;
    private readonly bool _usesDefaultSubscriptions;
    private readonly ServerLatencyProbe _latency;
    private readonly bool _usesDefaultLatency;
    private readonly Dictionary<Guid, ServerLatencyResult> _previewLatencies = new();
    private string? _pendingName;
    private ImportResult? _pending;
    private string? _pendingUrl;
    private SubscriptionMetadata? _pendingMetadata;
    private string? _pendingUserAgent;
    private List<ImportedProfile>? _previewOriginalOrder;
    private AddServerStep _previousStep;
    private CancellationTokenSource? _preparation;
    private CancellationTokenSource? _latencyCheck;

    public AddServerWizardViewModel(MainViewModel shell, SubscriptionClient? subscriptions = null, ServerLatencyProbe? latency = null) : base(shell)
    {
        _subscriptions = subscriptions ?? Services.Subscriptions;
        _usesDefaultSubscriptions = subscriptions is null;
        _latency = latency ?? Services.ServerLatency;
        _usesDefaultLatency = latency is null;
    }

    [ObservableProperty] private AddServerStep _step;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isCheckingPreviewLatency;
    [ObservableProperty] private bool _sortPreviewByLatency;
    [ObservableProperty] private string _input = "";
    // Matches the localized Auto / Profile / Subscription choices in the link form.
    [ObservableProperty] private int _importTypeIndex;
    // Per-subscription User-Agent override (issue #350). Empty means the default BYDLO/x.y.
    // Entered before the first preview so panels that gate on User-Agent can be fetched.
    [ObservableProperty] private string _userAgent = "";
    [ObservableProperty] private string? _errorKey;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubscriptionDetails))]
    private string _subscriptionDetails = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped))]
    private int _skippedCount;
    [ObservableProperty] private ProtocolKind _protocol = ProtocolKind.Vless;
    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 443;
    [ObservableProperty] private string _identifier = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _method = "aes-256-gcm";
    [ObservableProperty] private string _sni = "";
    [ObservableProperty] private string _configuration = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecretMask))]
    private bool _showSecrets;
    public char SecretMask => ShowSecrets ? '\0' : '●';

    public ObservableCollection<ServerPreviewItem> PreviewItems { get; } = [];
    public bool ShowProgress => Busy || IsCheckingPreviewLatency;

    /// <summary>User-initiated preview latency probe only; never runs in Mock Mode or under the safety lock.</summary>
    public bool CanCheckPreviewLatency => IsPreview && !Busy && !IsCheckingPreviewLatency
        && (!_usesDefaultLatency || (!Services.IsMock && !NetworkSafetyGuard.IsLocked));

    public string? PreviewLatencyDisabledReason => !IsPreview || IsCheckingPreviewLatency ? null
        : _usesDefaultLatency && Services.IsMock ? L["wizard.preview.latency.mock"]
        : _usesDefaultLatency && NetworkSafetyGuard.IsLocked ? L["wizard.preview.latency.locked"] : null;

    public bool HasPreviewLatencies => _previewLatencies.Count > 0;

    public int SelectedPreviewCount => PreviewItems.Count(item => item.IsSelected);

    public string SelectedSummary => Loc.T("wizard.selected", SelectedPreviewCount, PreviewItems.Count);
    public static IReadOnlyList<ProtocolKind> Protocols { get; } = Enum.GetValues<ProtocolKind>()
        .Where(p => p is not (ProtocolKind.AnyTls or ProtocolKind.ShadowTls or ProtocolKind.Naive or ProtocolKind.Ssh or ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig)).ToArray();
    public bool IsSource => Step == AddServerStep.Source;
    public bool IsLink => Step == AddServerStep.Link;
    public bool IsFile => Step == AddServerStep.File;
    public bool IsQr => Step == AddServerStep.Qr;
    public bool IsManual => Step == AddServerStep.Manual;
    public bool IsCatalog => Step == AddServerStep.Catalog;
    public IReadOnlyList<CatalogRow> CatalogSources => PublicSubscriptionCatalog.Sources.Select(source => new CatalogRow(source)).ToArray();
    public bool IsPreview => Step == AddServerStep.Preview;
    public bool HasError => ErrorKey is not null;
    public bool HasSubscriptionDetails => SubscriptionDetails.Length > 0;
    public bool HasSkipped => SkippedCount > 0;
    public string Error => ErrorKey is { } key ? L[key] : "";
    public string Skipped => SkippedCount > 0 ? Loc.T("wizard.skipped", SkippedCount) : "";
    public bool UsesConfiguration => Protocol is ProtocolKind.WireGuard or ProtocolKind.AmneziaWG or ProtocolKind.OpenVpn;
    public bool UsesIdentifier => Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic or ProtocolKind.Http or ProtocolKind.Socks5 or ProtocolKind.OpenVpn;
    public bool UsesPassword => Protocol == ProtocolKind.OpenVpn || !UsesConfiguration && Protocol is not (ProtocolKind.Vless or ProtocolKind.Vmess);
    public bool UsesMethod => Protocol == ProtocolKind.Shadowsocks;
    public bool UsesTls => Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic;

    partial void OnStepChanged(AddServerStep value)
    {
        foreach (var name in new[] { nameof(IsSource), nameof(IsLink), nameof(IsFile), nameof(IsQr), nameof(IsManual), nameof(IsCatalog), nameof(IsPreview),
            nameof(CanCheckPreviewLatency), nameof(PreviewLatencyDisabledReason) })
        { OnPropertyChanged(name); }
    }
    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanCheckPreviewLatency));
        OnPropertyChanged(nameof(PreviewLatencyDisabledReason));
    }
    partial void OnIsCheckingPreviewLatencyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanCheckPreviewLatency));
        OnPropertyChanged(nameof(PreviewLatencyDisabledReason));
    }
    partial void OnSortPreviewByLatencyChanged(bool value) => ApplyPreviewOrder();
    partial void OnErrorKeyChanged(string? value) { OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError)); }
    partial void OnSkippedCountChanged(int value) => OnPropertyChanged(nameof(Skipped));
    partial void OnProtocolChanged(ProtocolKind value)
    {
        foreach (var name in new[] { nameof(UsesConfiguration), nameof(UsesIdentifier), nameof(UsesPassword), nameof(UsesMethod), nameof(UsesTls) })
        { OnPropertyChanged(name); }
    }

    public void Reset()
    {
        _latencyCheck?.Cancel();
        _latencyCheck = null;
        IsCheckingPreviewLatency = false;
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        _pendingUserAgent = null;
        _pendingName = null;
        _previewOriginalOrder = null;
        Input = UserAgent = Identifier = Password = Configuration = ServerName = Host = Sni = Summary = SubscriptionDetails = "";
        ErrorKey = null;
        SkippedCount = 0;
        Protocol = ProtocolKind.Vless;
        ImportTypeIndex = 0;
        ShowSecrets = false;
        Port = 443;
        Method = "aes-256-gcm";
        _previewLatencies.Clear();
        SortPreviewByLatency = false;
        PreviewItems.Clear();
        RefreshPreviewState();
        Step = AddServerStep.Source;
    }

    [RelayCommand] private void ChooseLink() => Choose(AddServerStep.Link);
    [RelayCommand] private void ChooseFile() => Choose(AddServerStep.File);
    [RelayCommand] private void ChooseQr() => Choose(AddServerStep.Qr);
    [RelayCommand]
    private void ChooseCatalog()
    {
        OnPropertyChanged(nameof(CatalogSources));
        Choose(AddServerStep.Catalog);
    }

    [RelayCommand]
    private Task PreviewCatalog(CatalogRow row) => RunPreparation(async () =>
    {
        if (_usesDefaultSubscriptions && (Services.IsMock || NetworkSafetyGuard.IsLocked))
        {
            ErrorKey = "catalog.locked";
            return;
        }

        var (preview, url) = await _subscriptions.FetchFirstAsync(row.Source.Urls, ct: _preparation!.Token);
        if (url is null)
        {
            ErrorKey = "wizard.error.subscription";
            return;
        }

        ShowPreview(preview.Import, url, preview.Metadata, row.Name);
    });

    [RelayCommand] private void ChooseManual() => Choose(AddServerStep.Manual);

    private void Choose(AddServerStep step) { if (!Busy) { ErrorKey = null; Step = step; } }

    [RelayCommand]
    private void Back()
    {
        if (Busy) { return; }
        _latencyCheck?.Cancel();
        ErrorKey = null;
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        _pendingUserAgent = null;
        _pendingName = null;
        _previewOriginalOrder = null;
        _previewLatencies.Clear();
        SortPreviewByLatency = false;
        PreviewItems.Clear();
        RefreshPreviewState();
        Step = IsPreview ? _previousStep : AddServerStep.Source;
    }

    [RelayCommand]
    private void Close()
    {
        if (IsSaving) { return; }
        _preparation?.Cancel();
        _latencyCheck?.Cancel();
        Reset();
        Shell.IsAddServerOpen = false;
    }

    [RelayCommand]
    private Task Continue() => RunPreparation(async () =>
    {
        if (IsManual)
        {
            ImportedProfile item;
            try { item = new ManualProfileDraft { Protocol = Protocol, Name = ServerName, Host = Host, Port = Port, Identifier = Identifier, Password = Password, Method = Method, Sni = Sni, Configuration = Configuration }.Build(); }
            catch (FormatException) { ErrorKey = "wizard.error.manual"; return; }
            ShowPreview(new ImportResult([item], []));
        }
        else { await PrepareTextAsync(Input); }
    });

    private async Task PrepareTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { ErrorKey = "wizard.error.empty"; return; }
        if (text.Length > 2 * 1024 * 1024) { ErrorKey = "wizard.error.large"; return; }
        var subscription = IsLink && ImportTypeIndex != 0
            ? ImportTypeIndex == 2
            : ProfileImporter.LooksLikeSubscriptionUrl(text, allowRoot: true);
        if (subscription)
        {
            if (!TryNormalizeUserAgent(out var userAgent)) { ErrorKey = "wizard.error.input"; return; }
            try
            {
                var downloaded = await _subscriptions.FetchPreviewAsync(text.Trim(), userAgent, ct: _preparation?.Token ?? Services.ShutdownToken);
                if (downloaded.Import.IsEmpty) { ErrorKey = "wizard.error.subscription"; return; }
                ShowPreview(downloaded.Import, text.Trim(), downloaded.Metadata, userAgent: userAgent);
            }
            catch (OperationCanceledException) when (_preparation?.IsCancellationRequested == true) { }
            catch (Exception) { ErrorKey = "wizard.error.subscription"; }
        }
        else { ShowPreview(Services.Importer.ImportText(text)); }
    }

    private bool TryNormalizeUserAgent(out string? userAgent)
    {
        userAgent = UserAgent?.Trim();
        if (string.IsNullOrEmpty(userAgent)) { userAgent = null; return true; }
        if (userAgent.Length > 256 || userAgent.Any(char.IsControl)) { userAgent = null; return false; }
        return true;
    }

    private void ShowPreview(ImportResult result, string? url = null, SubscriptionMetadata? metadata = null, string? name = null, string? userAgent = null)
    {
        if (_preparation?.IsCancellationRequested == true) { return; }
        if (result.IsEmpty)
        {
            ErrorKey = result.Errors.FirstOrDefault(error => error is "import.clash.invalid" or "import.json.unsafe" or "import.openvpn.unsafe")
                ?? "wizard.error.input";
            return;
        }
        _pending = result;
        _pendingUrl = url;
        _pendingMetadata = metadata;
        _pendingUserAgent = url is null ? null : userAgent;
        _pendingName = name;
        _previousStep = Step;
        _previewLatencies.Clear();
        _previewOriginalOrder = result.Profiles.ToList();
        SortPreviewByLatency = false;
        PreviewItems.ReplaceWith(result.Profiles.Select(p => new ServerPreviewItem(p, Safe(p.Profile.Name, p.Secrets), Safe(p.Profile.Endpoint, p.Secrets))));
        foreach (var item in PreviewItems)
        {
            item.PropertyChanged += OnPreviewItemChanged;
        }
        RefreshPreviewState();
        Summary = Loc.T(url is null ? "wizard.recognized" : "wizard.subscription.recognized", result.Profiles.Count);
        var details = new List<string>();
        if (metadata?.Upload is { } up && metadata.Download is { } down && up <= long.MaxValue - down)
        { details.Add(Loc.T("wizard.traffic", Format.Bytes(up + down))); }
        if (metadata?.Total is > 0) { details.Add(Loc.T("wizard.limit", Format.Bytes(metadata.Total.Value))); }
        if (metadata?.Expires is { } expires) { details.Add(Loc.T("wizard.expires", expires.ToLocalTime().ToString("g"))); }
        if (!string.IsNullOrWhiteSpace(metadata?.Title)) { details.Add(Loc.T("wizard.sub.title", metadata.Title)); }
        if (!string.IsNullOrWhiteSpace(metadata?.SupportUrl)) { details.Add(Loc.T("wizard.sub.support", metadata.SupportUrl)); }
        if (!string.IsNullOrWhiteSpace(metadata?.Announce)) { details.Add(Loc.T("wizard.sub.announce", metadata.Announce)); }
        SkippedCount = result.Errors.Count;
        foreach (var error in result.Errors.Where(error => error.StartsWith("import.clash.skipped|", StringComparison.Ordinal)))
        {
            details.Add(Loc.T("import.clash.skipped", error["import.clash.skipped|".Length..]));
        }
        SubscriptionDetails = string.Join('\n', details);
        ErrorKey = null;
        Step = AddServerStep.Preview;
        // Ping immediately after import (issue #242): non-blocking, mock and safety lock stay silent.
        if (CanCheckPreviewLatency)
        {
            SortPreviewByLatency = true;
            _ = CheckPreviewLatency();
        }
    }

    private static string Safe(string text, ProfileSecrets secrets)
    {
        foreach (var value in secrets.Values.Values.Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v.Length))
        { text = text.Replace(value, "•••", StringComparison.Ordinal); }
        return text;
    }

    [RelayCommand]
    private Task SelectFile() => PickFileAsync(false);
    [RelayCommand]
    private Task SelectQr() => PickFileAsync(true);

    private Task PickFileAsync(bool image) => RunPreparation(async () =>
    {
        if (Services.TopLevel()?.StorageProvider is not { } storage) { ErrorKey = "wizard.error.file"; return; }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L[image ? "import.qr" : "import.file"], AllowMultiple = !image,
            FileTypeFilter = image ? [FilePickerFileTypes.ImageAll] : [new FilePickerFileType("VPN") { Patterns = ["*.conf", "*.ovpn", "*.txt", "*.yaml", "*.yml", "*.json"] }],
        });
        if (files.Count == 0) { return; }
        try { await PrepareFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>()); }
        finally { foreach (var file in files) { file.Dispose(); } }
    });

    public Task PreviewFilesAsync(IEnumerable<string> paths) => RunPreparation(() => PrepareFilesAsync(paths));

    private async Task PrepareFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.ToArray();
        var profiles = new List<ImportedProfile>();
        var errors = new List<string>();
        foreach (var path in files)
        {
            ImportResult result;
            if (ExternalImportInput.IsImage(path))
            {
                var payload = QrDecoder.DecodeFile(path);
                if (payload is null) { errors.Add("QR"); continue; }
                if (ProfileImporter.LooksLikeSubscriptionUrl(payload, allowRoot: true))
                {
                    if (files.Length != 1) { ErrorKey = "wizard.error.single.subscription"; return; }
                    await PrepareTextAsync(payload);
                    return;
                }
                result = Services.Importer.ImportText(payload);
            }
            else { result = await Services.Importer.ImportFileAsync(path, _preparation?.Token ?? Services.ShutdownToken); }
            profiles.AddRange(result.Profiles);
            errors.AddRange(result.Errors);
        }
        if (profiles.Count == 0 && files.Any(ExternalImportInput.IsImage)) { ErrorKey = "wizard.error.qr"; return; }
        ShowPreview(new ImportResult(profiles, errors));
    }

    [RelayCommand]
    private Task Paste() => RunPreparation(async () =>
    {
        if (Services.TopLevel()?.Clipboard is not { } clipboard) { ErrorKey = "wizard.error.clipboard"; return; }
        if (IsQr)
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            var payload = bitmap is null ? null : QrDecoder.Decode(bitmap);
            if (payload is null) { ErrorKey = "wizard.error.qr"; return; }
            await PrepareTextAsync(payload);
        }
        else
        {
            if (IsSource) { Step = AddServerStep.Link; }
            Input = await clipboard.TryGetTextAsync() ?? "";
            if (string.IsNullOrWhiteSpace(Input)) { ErrorKey = "wizard.error.empty"; }
        }
    });

    private async Task RunPreparation(Func<Task> action)
    {
        if (Busy) { return; }
        Busy = true;
        _latencyCheck?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _preparation = cancellation;
        ErrorKey = null;
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        _pendingUserAgent = null;
        _pendingName = null;
        PreviewItems.Clear();
        if (IsPreview) { Step = _previousStep; }
        try { await action(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { ErrorKey = "wizard.error.file"; }
        finally { _preparation = null; Busy = false; }
    }

    private void OnPreviewItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerPreviewItem.IsSelected))
        {
            RefreshPreviewState();
        }
    }

    private void RefreshPreviewState()
    {
        OnPropertyChanged(nameof(HasPreviewLatencies));
        OnPropertyChanged(nameof(SelectedPreviewCount));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(CanCheckPreviewLatency));
        OnPropertyChanged(nameof(PreviewLatencyDisabledReason));
    }

    [RelayCommand]
    private void SelectAllPreview()
    {
        foreach (var item in PreviewItems)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNonePreview()
    {
        foreach (var item in PreviewItems)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>Per-server latency probe over the pending preview (issue #136, auto after import in #242). Skipped in Mock Mode and under the safety lock.</summary>
    [RelayCommand]
    private async Task CheckPreviewLatency()
    {
        if (IsCheckingPreviewLatency || !IsPreview || _pending is null)
        {
            return;
        }

        if (_usesDefaultLatency && (Services.IsMock || NetworkSafetyGuard.IsLocked))
        {
            return;
        }

        IsCheckingPreviewLatency = true;
        _latencyCheck?.Cancel();
        var check = _latencyCheck = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        try
        {
            IReadOnlyDictionary<Guid, ServerLatencyResult>? results = null;
            try
            {
                await Shell.Guard(async () =>
                {
                    results = await _latency
                        .ProbeAsync(_pending.Profiles.Select(p => p.Profile).ToList(), ct: check.Token)
                        .ConfigureAwait(false);
                });
            }
            catch (OperationCanceledException) when (check.IsCancellationRequested)
            {
                return;
            }
            if (results is null)
            {
                return;
            }

            _previewLatencies.Clear();
            foreach (var (id, result) in results)
            {
                _previewLatencies[id] = result;
            }

            foreach (var item in PreviewItems)
            {
                if (_previewLatencies.TryGetValue(item.Item.Profile.Id, out var probe))
                {
                    item.Latency = probe.Reachable ? probe.Latency : null;
                    item.LatencyFailed = !probe.Reachable;
                }
                else
                {
                    item.Latency = null;
                    item.LatencyFailed = false;
                }
            }

            RefreshPreviewState();
            if (SortPreviewByLatency)
            {
                ApplyPreviewOrder();
            }
        }
        finally
        {
            IsCheckingPreviewLatency = false;
        }
    }

    private void ApplyPreviewOrder()
    {
        if (!IsPreview || _pending is null || PreviewItems.Count == 0)
        {
            return;
        }

        var items = PreviewItems.Select(item => item.Item).ToList();
        var source = SortPreviewByLatency
            ? CatalogPreview.OrderByLatency(items, _previewLatencies)
            : _previewOriginalOrder ?? items;
        var rows = new Dictionary<Guid, ServerPreviewItem>();
        foreach (var item in PreviewItems)
        {
            rows.TryAdd(item.Item.Profile.Id, item);
        }

        PreviewItems.ReplaceWith(source.Select(profile => rows[profile.Profile.Id]));
    }

    [RelayCommand]
    private async Task Add()
    {
        if (Busy || !IsPreview || _pending is not { IsEmpty: false } pending) { return; }
        var selected = PreviewItems.Where(item => item.IsSelected).Select(item => item.Item).ToList();
        if (selected.Count == 0) { ErrorKey = "wizard.error.selection"; return; }
        Busy = true;
        IsSaving = true;
        try
        {
            if (_pendingUrl is { } url)
            {
                var mode = selected.Count < pending.Profiles.Count
                    ? SubscriptionMode.SelectedOnly
                    : SubscriptionMode.FollowAll;
                await Services.Profiles.AddSubscriptionWithProfilesAsync(_pendingName ?? _pendingMetadata?.Title ?? new Uri(url).Host, url, selected, _pendingMetadata, mode, _pendingUserAgent);
            }
            else { await Services.Profiles.AddAsync(selected); }
            Shell.ReloadProfiles();
            Shell.SelectedProfile = Shell.Profiles.FirstOrDefault(p => p.Id == selected[0].Profile.Id) ?? Shell.SelectedProfile;
            Shell.Connections.OnShown();
            Shell.Navigate("connections");
            Shell.IsAddServerOpen = false;
            Shell.ShowToast(Loc.T("import.added", selected.Count));
            Reset();
        }
        catch (Exception) { ErrorKey = "wizard.error.save"; }
        finally { Busy = false; IsSaving = false; }
    }
}

public sealed class CatalogRow(PublicSubscriptionSource source)
{
    public PublicSubscriptionSource Source => source;
    public string Name => Loc.Instance[source.NameKey];
    public string Description => Loc.Instance[source.DescriptionKey];
}
