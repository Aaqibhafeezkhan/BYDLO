using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Bydlo.App.Services;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public enum AddServerStep { Source, Link, File, Qr, Manual, Preview }
public sealed record ServerPreviewItem(string Name, string Protocol, string Endpoint);

public sealed partial class AddServerWizardViewModel : PageViewModel
{
    private readonly SubscriptionClient _subscriptions;
    private ImportResult? _pending;
    private string? _pendingUrl;
    private SubscriptionMetadata? _pendingMetadata;
    private AddServerStep _previousStep;
    private CancellationTokenSource? _preparation;

    public AddServerWizardViewModel(MainViewModel shell, SubscriptionClient? subscriptions = null) : base(shell)
    { _subscriptions = subscriptions ?? Services.Subscriptions; }

    [ObservableProperty] private AddServerStep _step;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _input = "";
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

    public ObservableCollection<ServerPreviewItem> PreviewItems { get; } = [];
    public static IReadOnlyList<ProtocolKind> Protocols { get; } = Enum.GetValues<ProtocolKind>();
    public bool IsSource => Step == AddServerStep.Source;
    public bool IsLink => Step == AddServerStep.Link;
    public bool IsFile => Step == AddServerStep.File;
    public bool IsQr => Step == AddServerStep.Qr;
    public bool IsManual => Step == AddServerStep.Manual;
    public bool IsPreview => Step == AddServerStep.Preview;
    public bool HasError => ErrorKey is not null;
    public bool HasSubscriptionDetails => SubscriptionDetails.Length > 0;
    public bool HasSkipped => SkippedCount > 0;
    public string Error => ErrorKey is { } key ? L[key] : "";
    public string Skipped => SkippedCount > 0 ? Loc.T("wizard.skipped", SkippedCount) : "";
    public bool UsesConfiguration => Protocol is ProtocolKind.WireGuard or ProtocolKind.AmneziaWG or ProtocolKind.OpenVpn;
    public bool UsesIdentifier => Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic or ProtocolKind.Http or ProtocolKind.Socks5;
    public bool UsesPassword => !UsesConfiguration && Protocol is not (ProtocolKind.Vless or ProtocolKind.Vmess);
    public bool UsesMethod => Protocol == ProtocolKind.Shadowsocks;
    public bool UsesTls => Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic;

    partial void OnStepChanged(AddServerStep value)
    {
        foreach (var name in new[] { nameof(IsSource), nameof(IsLink), nameof(IsFile), nameof(IsQr), nameof(IsManual), nameof(IsPreview) })
        { OnPropertyChanged(name); }
    }
    partial void OnErrorKeyChanged(string? value) { OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError)); }
    partial void OnSkippedCountChanged(int value) => OnPropertyChanged(nameof(Skipped));
    partial void OnProtocolChanged(ProtocolKind value)
    {
        foreach (var name in new[] { nameof(UsesConfiguration), nameof(UsesIdentifier), nameof(UsesPassword), nameof(UsesMethod), nameof(UsesTls) })
        { OnPropertyChanged(name); }
    }

    public void Reset()
    {
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        Input = Identifier = Password = Configuration = ServerName = Host = Sni = Summary = SubscriptionDetails = "";
        ErrorKey = null;
        SkippedCount = 0;
        Protocol = ProtocolKind.Vless;
        Port = 443;
        Method = "aes-256-gcm";
        PreviewItems.Clear();
        Step = AddServerStep.Source;
    }

    [RelayCommand] private void ChooseLink() => Choose(AddServerStep.Link);
    [RelayCommand] private void ChooseFile() => Choose(AddServerStep.File);
    [RelayCommand] private void ChooseQr() => Choose(AddServerStep.Qr);
    [RelayCommand] private void ChooseManual() => Choose(AddServerStep.Manual);

    private void Choose(AddServerStep step) { if (!Busy) { ErrorKey = null; Step = step; } }

    [RelayCommand]
    private void Back()
    {
        if (Busy) { return; }
        ErrorKey = null;
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        PreviewItems.Clear();
        Step = IsPreview ? _previousStep : AddServerStep.Source;
    }

    [RelayCommand]
    private void Close()
    {
        if (IsSaving) { return; }
        _preparation?.Cancel();
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
        if (ProfileImporter.LooksLikeSubscriptionUrl(text, allowRoot: true))
        {
            try
            {
                var downloaded = await _subscriptions.FetchPreviewAsync(text.Trim(), _preparation?.Token ?? Services.ShutdownToken);
                if (downloaded.Import.IsEmpty) { ErrorKey = "wizard.error.subscription"; return; }
                ShowPreview(downloaded.Import, text.Trim(), downloaded.Metadata);
            }
            catch (OperationCanceledException) when (_preparation?.IsCancellationRequested == true) { }
            catch (Exception) { ErrorKey = "wizard.error.subscription"; }
        }
        else { ShowPreview(Services.Importer.ImportText(text)); }
    }

    private void ShowPreview(ImportResult result, string? url = null, SubscriptionMetadata? metadata = null)
    {
        if (_preparation?.IsCancellationRequested == true) { return; }
        if (result.IsEmpty) { ErrorKey = "wizard.error.input"; return; }
        _pending = result;
        _pendingUrl = url;
        _pendingMetadata = metadata;
        _previousStep = Step;
        PreviewItems.ReplaceWith(result.Profiles.Select(p => new ServerPreviewItem(Safe(p.Profile.Name, p.Secrets), Labels.Protocol(p.Profile.Protocol), Safe(p.Profile.Endpoint, p.Secrets))));
        Summary = Loc.T(url is null ? "wizard.recognized" : "wizard.subscription.recognized", result.Profiles.Count);
        var details = new List<string>();
        if (metadata?.Upload is { } up && metadata.Download is { } down && up <= long.MaxValue - down)
        { details.Add(Loc.T("wizard.traffic", Format.Bytes(up + down))); }
        if (metadata?.Total is > 0) { details.Add(Loc.T("wizard.limit", Format.Bytes(metadata.Total.Value))); }
        if (metadata?.Expires is { } expires) { details.Add(Loc.T("wizard.expires", expires.ToLocalTime().ToString("g"))); }
        SubscriptionDetails = string.Join('\n', details);
        SkippedCount = result.Errors.Count;
        ErrorKey = null;
        Step = AddServerStep.Preview;
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
            FileTypeFilter = image ? [FilePickerFileTypes.ImageAll] : [new FilePickerFileType("VPN") { Patterns = ["*.conf", "*.ovpn", "*.txt"] }],
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _preparation = cancellation;
        ErrorKey = null;
        _pending = null;
        _pendingUrl = null;
        _pendingMetadata = null;
        PreviewItems.Clear();
        if (IsPreview) { Step = _previousStep; }
        try { await action(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { ErrorKey = "wizard.error.file"; }
        finally { _preparation = null; Busy = false; }
    }

    [RelayCommand]
    private async Task Add()
    {
        if (Busy || !IsPreview || _pending is not { IsEmpty: false } pending) { return; }
        Busy = true;
        IsSaving = true;
        try
        {
            if (_pendingUrl is { } url)
            { await Services.Profiles.AddSubscriptionWithProfilesAsync(new Uri(url).Host, url, pending.Profiles, _pendingMetadata); }
            else { await Services.Profiles.AddAsync(pending.Profiles); }
            Shell.ReloadProfiles();
            Shell.SelectedProfile = Shell.Profiles.FirstOrDefault(p => p.Id == pending.Profiles[0].Profile.Id) ?? Shell.SelectedProfile;
            Shell.Connections.OnShown();
            Shell.Navigate("connections");
            Shell.IsAddServerOpen = false;
            Shell.ShowToast(Loc.T("import.added", pending.Profiles.Count));
            Reset();
        }
        catch (Exception) { ErrorKey = "wizard.error.save"; }
        finally { Busy = false; IsSaving = false; }
    }
}
