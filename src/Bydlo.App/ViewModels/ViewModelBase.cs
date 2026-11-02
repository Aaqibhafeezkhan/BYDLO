using System.Collections.ObjectModel;
using Bydlo.App.Services;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bydlo.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public static Loc L => Loc.Instance;
}

/// <summary>A page shown in the navigation.</summary>
public abstract class PageViewModel(MainViewModel shell) : ViewModelBase
{
    public MainViewModel Shell { get; } = shell;

    public AppServices Services => Shell.Services;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnShown()
    {
    }
}

public sealed partial class ProfileItem(Profile profile) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string Endpoint => Profile.Endpoint;

    public string ProtocolLabel => Labels.Protocol(Profile.Protocol);

    public bool CanShareLink => Bydlo.Core.Import.ProfileExporter.HasShareLink(Profile.Protocol);

    public bool CanExportConfig => Bydlo.Core.Import.ProfileExporter.HasConfigFile(Profile.Protocol);

    public string Details
    {
        get
        {
            var parts = new List<string> { ProtocolLabel };
            if (Profile.Option(Keys.Security) is { } sec && sec != "none")
            {
                parts.Add(sec.ToUpperInvariant());
            }

            if (Profile.Option(Keys.Transport) is { } t && t is not ("tcp" or "raw"))
            {
                parts.Add(t.ToUpperInvariant());
            }

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    private bool _isFavorite = profile.IsFavorite;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Last measured TCP latency (issue #10). Null when not measured yet.</summary>
    [ObservableProperty]
    private TimeSpan? _latency;

    /// <summary>True when the last latency probe failed to reach the server.</summary>
    [ObservableProperty]
    private bool _latencyFailed;

    public bool HasLatency => Latency is not null || LatencyFailed;

    public string LatencyText => LatencyFailed ? "✕" : Latency is { } t ? Format.Latency(t) : "—";

    public bool LatencyOk => !LatencyFailed && Latency is { } ok && ok < TimeSpan.FromMilliseconds(200);

    public bool LatencyWarn => !LatencyFailed && Latency is { } warn && warn >= TimeSpan.FromMilliseconds(200) && warn < TimeSpan.FromMilliseconds(800);

    public bool LatencyBad => LatencyFailed || (Latency is { } bad && bad >= TimeSpan.FromMilliseconds(800));

    partial void OnLatencyChanged(TimeSpan? value) => RefreshLatency();

    partial void OnLatencyFailedChanged(bool value) => RefreshLatency();

    private void RefreshLatency()
    {
        OnPropertyChanged(nameof(HasLatency));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(LatencyOk));
        OnPropertyChanged(nameof(LatencyWarn));
        OnPropertyChanged(nameof(LatencyBad));
    }
}

public static class Labels
{
    public static string Protocol(ProtocolKind p) => p switch
    {
        ProtocolKind.Vless => "VLESS",
        ProtocolKind.Vmess => "VMess",
        ProtocolKind.Trojan => "Trojan",
        ProtocolKind.Shadowsocks => "Shadowsocks",
        ProtocolKind.Hysteria2 => "Hysteria 2",
        ProtocolKind.Tuic => "TUIC",
        ProtocolKind.Socks5 => "SOCKS5",
        ProtocolKind.Http => "HTTP",
        ProtocolKind.WireGuard => "WireGuard",
        ProtocolKind.AmneziaWG => "AmneziaWG",
        ProtocolKind.OpenVpn => "OpenVPN",
        ProtocolKind.AnyTls => Loc.Instance["protocol.anytls"],
        ProtocolKind.ShadowTls => Loc.Instance["protocol.shadowtls"],
        ProtocolKind.Naive => Loc.Instance["protocol.naive"],
        ProtocolKind.Ssh => Loc.Instance["protocol.ssh"],
        _ => p.ToString(),
    };

    public static string Engine(EngineKind? e) => e switch
    {
        EngineKind.SingBox => "sing-box",
        EngineKind.Xray => "Xray",
        EngineKind.WireGuard => "WireGuard",
        EngineKind.AmneziaWG => "AmneziaWG",
        EngineKind.OpenVpn => "OpenVPN",
        EngineKind.Zapret => "Zapret",
        EngineKind.TgWsProxy => "TgWsProxy",
        EngineKind.Mock => "Mock",
        _ => "—",
    };
}

public static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items)
        {
            target.Add(i);
        }
    }
}
