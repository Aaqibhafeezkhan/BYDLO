using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bydlo.App.Services;
using Avalonia.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using Bydlo.Core.Routing;
using Bydlo.Core.Security;
using Bydlo.Platform;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Split tunneling, DNS and rules. Applied on the next connect.</summary>
public sealed partial class RoutingViewModel : PageViewModel
{
    public RoutingViewModel(MainViewModel shell)
        : base(shell)
    {
        OrderedRules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanDownloadPreset));
        Load();
    }

    [ObservableProperty]
    private bool _preserveMesh;

    [ObservableProperty]
    private bool _bypassPrivate;

    [ObservableProperty]
    private bool _proxyOnlyListed;

    [ObservableProperty]
    private string _remoteDns = "";

    [ObservableProperty]
    private string _directDns = "";

    public IReadOnlyList<string> DnsTransports => [L["routing.dns.system"], "UDP", "TCP", "DoH", "DoT", "DoQ"];

    [ObservableProperty] private int _remoteDnsTransport;
    [ObservableProperty] private int _directDnsTransport;
    [ObservableProperty] private bool _remoteDnsThroughProxy;
    [ObservableProperty] private bool _directDnsThroughProxy;
    [ObservableProperty] private bool _fakeIp;
    [ObservableProperty] private string _fakeIpExceptions = "";
    [ObservableProperty] private string _remoteDnsDomains = "";
    [ObservableProperty] private string _directDnsDomains = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownloadPreset))] private string _remoteDnsRuleSets = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownloadPreset))] private string _directDnsRuleSets = "";

    [ObservableProperty]
    private string _directDomains = "";

    [ObservableProperty]
    private string _proxyDomains = "";

    [ObservableProperty]
    private string _blockDomains = "";

    [ObservableProperty]
    private string _directCidrs = "";

    [ObservableProperty]
    private string _directProcesses = "";

    [ObservableProperty]
    private bool _proxyOnlyListedProcesses;

    private IReadOnlyList<ApplicationRow> _applicationRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingApplications))]
    private string _applicationSearch = "";

    [ObservableProperty]
    private ApplicationRow? _selectedApplication;

    public bool HasApplicationPicker => OperatingSystem.IsWindows();
    public IEnumerable<ApplicationRow> MatchingApplications =>
        _applicationRows.Where(app => app.Name.Contains(ApplicationSearch, StringComparison.CurrentCultureIgnoreCase));

    [RelayCommand]
    private async Task FindApplications()
    {
        if (OperatingSystem.IsWindows())
        {
            await Shell.Guard(async () =>
            {
                var applications = await Task.Run(() => OperatingSystem.IsWindows()
                    ? WindowsApplicationCatalog.ListApplications() : Array.Empty<WindowsApplication>());
                var oldRows = _applicationRows;
                _applicationRows = applications.Select(app => new ApplicationRow(app.Name, CreateIcon(app.Icon))).ToArray();
                SelectedApplication = null;
                foreach (var old in oldRows) { old.Icon?.Dispose(); }
                OnPropertyChanged(nameof(MatchingApplications));
            });
        }
    }

    [RelayCommand]
    private void AddApplication()
    {
        if (SelectedApplication is { } application)
        {
            DirectProcesses = string.Join('\n', Split(DirectProcesses + "\n" + application.Name));
        }
    }

    public IReadOnlyList<string> PresetOptions => [L["routing.preset.all"], L["routing.preset.blocked"], L["routing.preset.local"]];
    public IReadOnlyList<string> Countries => [L["routing.preset.ru"], L["routing.preset.cn"]];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalPreset))]
    [NotifyPropertyChangedFor(nameof(CanDownloadPreset))]
    private int _presetIndex;

    [ObservableProperty]
    private int _countryIndex;

    public bool IsLocalPreset => PresetIndex == (int)RoutingPreset.ExceptLocal;
    public bool CanDownloadPreset => (PresetIndex > 0 || OrderedRules.Count > 0 || RemoteDnsRuleSets.Length > 0 || DirectDnsRuleSets.Length > 0) && !Services.IsMock && !NetworkSafetyGuard.IsLocked;
    private string Country => CountryIndex == 1 ? "cn" : "ru";

    [RelayCommand]
    private Task<bool> DownloadPreset() => Shell.Guard(async () =>
    {
        if (!CanDownloadPreset)
        {
            return;
        }
        var cache = new RuleSetCache(RoutingPresets.CacheDirectory(Services.Paths.DataDirectory), Services.Http);
        foreach (var source in RoutingPresets.Sources((RoutingPreset)PresetIndex, Country)
            .Concat(OrderedRules.Where(rule => rule.Condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp)
                .Select(rule => RoutingPresets.Find(rule.Value)).OfType<RuleSetSource>())
            .Concat(OrderedRules
                .Select(rule => RoutingPresets.XraySource(rule.Condition)).OfType<RuleSetSource>())
            .Concat(Split(RemoteDnsRuleSets).Concat(Split(DirectDnsRuleSets)).Select(RoutingPresets.Find).OfType<RuleSetSource>())
            .DistinctBy(source => source.Tag))
        {
            await cache.UpdateAsync(source, Services.ShutdownToken);
        }
        Shell.ShowToast(L["routing.preset.updated"]);
    });

    private static readonly JsonSerializerOptions RulesJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<RoutingCondition>(), new JsonStringEnumConverter<RoutingAction>() },
    };

    public ObservableCollection<OrderedRoutingRule> OrderedRules { get; } = [];
    public IReadOnlyList<string> RuleConditions => Enum.GetValues<RoutingCondition>()
        .Select(condition => L["routing.rules.condition." + condition]).ToArray();
    public IReadOnlyList<string> RuleActions => Enum.GetValues<RoutingAction>()
        .Select(action => L["routing.rules.action." + action]).ToArray();

    [ObservableProperty] private bool _showRuleEditor;
    [ObservableProperty] private int _ruleConditionIndex;
    [ObservableProperty] private int _ruleActionIndex;
    [ObservableProperty] private string _ruleValue = "";
    [ObservableProperty] private OrderedRoutingRule? _selectedRule;

    [RelayCommand]
    private void AddRule()
    {
        if (string.IsNullOrWhiteSpace(RuleValue) || RuleConditionIndex < 0 || RuleActionIndex < 0)
        {
            return;
        }
        var rule = new OrderedRoutingRule((RoutingCondition)RuleConditionIndex, RuleValue.Trim(), (RoutingAction)RuleActionIndex);
        OrderedRules.Add(rule);
        SelectedRule = rule;
        RuleValue = "";
    }

    partial void OnSelectedRuleChanged(OrderedRoutingRule? value)
    {
        if (value is not null)
        {
            RuleConditionIndex = (int)value.Condition;
            RuleActionIndex = (int)value.Action;
            RuleValue = value.Value;
        }
    }

    [RelayCommand]
    private void UpdateRule()
    {
        if (SelectedRule is not { } selected || string.IsNullOrWhiteSpace(RuleValue)
            || RuleConditionIndex < 0 || RuleActionIndex < 0)
        {
            return;
        }
        var index = IndexOfRule(selected);
        if (index >= 0)
        {
            var rule = new OrderedRoutingRule((RoutingCondition)RuleConditionIndex, RuleValue.Trim(), (RoutingAction)RuleActionIndex);
            OrderedRules[index] = rule;
            SelectedRule = rule;
        }
    }

    public void MoveRuleTo(OrderedRoutingRule source, OrderedRoutingRule destination)
    {
        var from = IndexOfRule(source);
        var to = IndexOfRule(destination);
        if (from >= 0 && to >= 0 && from != to)
        {
            OrderedRules.Move(from, to);
            SelectedRule = source;
        }
    }

    private int IndexOfRule(OrderedRoutingRule rule)
    {
        for (var i = 0; i < OrderedRules.Count; i++)
        {
            if (ReferenceEquals(OrderedRules[i], rule))
            {
                return i;
            }
        }
        return -1;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is { } rule)
        {
            var index = IndexOfRule(rule);
            if (index >= 0) { OrderedRules.RemoveAt(index); }
        }
    }

    [RelayCommand]
    private void MoveRuleUp() => MoveRule(-1);

    [RelayCommand]
    private void MoveRuleDown() => MoveRule(1);

    private void MoveRule(int step)
    {
        if (SelectedRule is not { } rule)
        {
            return;
        }
        var index = IndexOfRule(rule);
        var next = index + step;
        if (index >= 0 && next >= 0 && next < OrderedRules.Count)
        {
            OrderedRules.Move(index, next);
        }
    }

    [RelayCommand]
    private Task<bool> ExportRules() => Shell.Guard(async () =>
    {
        if (Services.TopLevel()?.StorageProvider is not { } storage)
        {
            return;
        }
        using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L["routing.rules.export"], SuggestedFileName = "routing.json", DefaultExtension = "json",
        });
        if (file is not null)
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, OrderedRules, RulesJson);
        }
    });

    [RelayCommand]
    private Task<bool> ImportRules() => Shell.Guard(async () =>
    {
        if (Services.TopLevel()?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L["routing.rules.import"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        using var file = files[0];
        await using var stream = await file.OpenReadAsync();
        var buffer = new byte[1024 * 1024 + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false);
        if (length == buffer.Length)
        {
            throw new InvalidDataException(L["routing.rules.invalid"]);
        }
        var rules = JsonSerializer.Deserialize<List<OrderedRoutingRule>>(buffer.AsSpan(0, length), RulesJson);
        if (rules is null || rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Value)
            || !Enum.IsDefined(rule.Condition) || !Enum.IsDefined(rule.Action)))
        {
            throw new InvalidDataException(L["routing.rules.invalid"]);
        }
        OrderedRules.Clear();
        foreach (var rule in rules)
        {
            OrderedRules.Add(rule);
        }
    });

    [RelayCommand]
    private Task<bool> EditDpiDefaults() => Shell.Guard(async () =>
    {
        if (Services.TopLevel() is Window owner &&
            await DpiEditorPanel.ShowDefaultsAsync(owner, Services.Settings.DpiDefaults) is { } updated)
        {
            Services.Settings.DpiDefaults = updated;
            await Services.SaveSettingsAsync();
        }
    });

    public bool IsTunMode => Services.Settings.Mode == ConnectionMode.Tun;

    public override void OnShown()
    {
        Load();
        OnPropertyChanged(nameof(IsTunMode));
    }

    [RelayCommand]
    private async Task Save()
    {
        var r = Services.Settings.Routing;
        r.OrderedRules = [.. OrderedRules];
        r.Preset = (RoutingPreset)Math.Max(PresetIndex, 0);
        r.PresetCountry = Country;
        r.PreserveMeshVpnRanges = PreserveMesh;
        r.BypassPrivateNetworks = BypassPrivate;
        r.ProxyOnlyListed = ProxyOnlyListed;
        r.RemoteDns = string.IsNullOrWhiteSpace(RemoteDns) ? "1.1.1.1" : RemoteDns.Trim();
        r.DirectDns = string.IsNullOrWhiteSpace(DirectDns) ? "local" : DirectDns.Trim();
        r.RemoteDnsTransport = (DnsTransport)Math.Clamp(RemoteDnsTransport, 0, 5);
        r.DirectDnsTransport = (DnsTransport)Math.Clamp(DirectDnsTransport, 0, 5);
        r.RemoteDnsThroughProxy = RemoteDnsThroughProxy;
        r.DirectDnsThroughProxy = DirectDnsThroughProxy;
        r.FakeIp = FakeIp;
        r.FakeIpExceptions = Split(FakeIpExceptions);
        r.RemoteDnsDomains = Split(RemoteDnsDomains);
        r.DirectDnsDomains = Split(DirectDnsDomains);
        r.RemoteDnsRuleSets = Split(RemoteDnsRuleSets);
        r.DirectDnsRuleSets = Split(DirectDnsRuleSets);
        r.DirectDomains = Split(DirectDomains);
        r.ProxyDomains = Split(ProxyDomains);
        r.BlockDomains = Split(BlockDomains);
        r.DirectCidrs = Split(DirectCidrs);
        r.DirectProcesses = Split(DirectProcesses);
        r.ProxyOnlyListedProcesses = ProxyOnlyListedProcesses;
        await Services.SaveSettingsAsync();
        Shell.ShowToast(Shell.Home.IsConnected ? L["routing.saved.reconnect"] : L["toast.saved"]);
    }

    private void Load()
    {
        var r = Services.Settings.Routing;
        OrderedRules.Clear();
        foreach (var rule in r.OrderedRules)
        {
            OrderedRules.Add(rule);
        }
        PresetIndex = (int)r.Preset;
        CountryIndex = r.PresetCountry == "cn" ? 1 : 0;
        PreserveMesh = r.PreserveMeshVpnRanges;
        BypassPrivate = r.BypassPrivateNetworks;
        ProxyOnlyListed = r.ProxyOnlyListed;
        RemoteDns = r.RemoteDns;
        DirectDns = r.DirectDns;
        RemoteDnsTransport = (int)r.RemoteDnsTransport;
        DirectDnsTransport = (int)r.DirectDnsTransport;
        RemoteDnsThroughProxy = r.RemoteDnsThroughProxy;
        DirectDnsThroughProxy = r.DirectDnsThroughProxy;
        FakeIp = r.FakeIp;
        FakeIpExceptions = string.Join('\n', r.FakeIpExceptions);
        RemoteDnsDomains = string.Join('\n', r.RemoteDnsDomains);
        DirectDnsDomains = string.Join('\n', r.DirectDnsDomains);
        RemoteDnsRuleSets = string.Join('\n', r.RemoteDnsRuleSets);
        DirectDnsRuleSets = string.Join('\n', r.DirectDnsRuleSets);
        DirectDomains = string.Join('\n', r.DirectDomains);
        ProxyDomains = string.Join('\n', r.ProxyDomains);
        BlockDomains = string.Join('\n', r.BlockDomains);
        DirectCidrs = string.Join('\n', r.DirectCidrs);
        DirectProcesses = string.Join('\n', r.DirectProcesses);
        ProxyOnlyListedProcesses = r.ProxyOnlyListedProcesses;
    }

    private static List<string> Split(string text) =>
        [.. text.Split('\n', ',', ';').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase)];
    public sealed record ApplicationRow(string Name, WriteableBitmap? Icon);

    private static WriteableBitmap? CreateIcon(byte[]? pixels)
    {
        if (pixels is null)
        {
            return null;
        }
        const int size = WindowsApplicationCatalog.IconSize;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < size; y++)
        {
            Marshal.Copy(pixels, y * size * 4, buffer.Address + y * buffer.RowBytes, size * 4);
        }
        return bitmap;
    }
}
