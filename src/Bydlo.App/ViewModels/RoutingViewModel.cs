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

    public override void OnShown() => Load();

    [RelayCommand]
    private async Task Save()
    {
        var r = Services.Settings.Routing;
        r.PreserveMeshVpnRanges = PreserveMesh;
        r.BypassPrivateNetworks = BypassPrivate;
        r.ProxyOnlyListed = ProxyOnlyListed;
        r.RemoteDns = string.IsNullOrWhiteSpace(RemoteDns) ? "1.1.1.1" : RemoteDns.Trim();
        r.DirectDns = string.IsNullOrWhiteSpace(DirectDns) ? "local" : DirectDns.Trim();
        r.DirectDomains = Split(DirectDomains);
        r.ProxyDomains = Split(ProxyDomains);
        r.BlockDomains = Split(BlockDomains);
        r.DirectCidrs = Split(DirectCidrs);
        r.DirectProcesses = Split(DirectProcesses);
        await Services.SaveSettingsAsync();
        Shell.ShowToast(Shell.Home.IsConnected ? L["routing.saved.reconnect"] : L["toast.saved"]);
    }

    private void Load()
    {
        var r = Services.Settings.Routing;
        PreserveMesh = r.PreserveMeshVpnRanges;
        BypassPrivate = r.BypassPrivateNetworks;
        ProxyOnlyListed = r.ProxyOnlyListed;
        RemoteDns = r.RemoteDns;
        DirectDns = r.DirectDns;
        DirectDomains = string.Join('\n', r.DirectDomains);
        ProxyDomains = string.Join('\n', r.ProxyDomains);
        BlockDomains = string.Join('\n', r.BlockDomains);
        DirectCidrs = string.Join('\n', r.DirectCidrs);
        DirectProcesses = string.Join('\n', r.DirectProcesses);
    }

    private static List<string> Split(string text) =>
        [.. text.Split('\n', ',', ';').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase)];
}
