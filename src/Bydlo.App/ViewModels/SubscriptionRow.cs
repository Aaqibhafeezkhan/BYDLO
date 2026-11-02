using Bydlo.App.Services;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bydlo.App.ViewModels;

/// <summary>Subscription card row with formatted traffic and expiry (issue #11).</summary>
public sealed partial class SubscriptionRow : ObservableObject
{
    private readonly Subscription _subscription;

    public SubscriptionRow(Subscription subscription)
    {
        _subscription = subscription;
        _name = subscription.Name;
        Update();
        Loc.Instance.PropertyChanged += (_, _) => Update();
    }

    public Guid Id => _subscription.Id;

    public DateTimeOffset? LastUpdated => _subscription.LastUpdated;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    private bool _hasDetails;

    [ObservableProperty]
    private double _trafficFraction;

    [ObservableProperty]
    private bool _hasTraffic;

    [ObservableProperty]
    private bool _isWarning;

    public Subscription Snapshot => _subscription;

    private void Update()
    {
        var parts = new List<string>();
        long used = 0;
        bool hasUsage = false;
        if (_subscription.Upload is { } up && _subscription.Download is { } down &&
            up <= long.MaxValue - down)
        {
            used = up + down;
            hasUsage = true;
            parts.Add(Loc.T("subs.used", Format.Bytes(used)));
        }
        if (_subscription.Total is > 0 and { } total)
        {
            parts.Add(Loc.T("subs.limit", Format.Bytes(total)));
            HasTraffic = true;
            TrafficFraction = hasUsage ? Math.Clamp((double)used / total, 0, 1) : 0;
        }
        else
        {
            HasTraffic = hasUsage;
            TrafficFraction = 0;
        }
        if (_subscription.Expires is { } expires)
        {
            parts.Add(Loc.T("subs.expires", expires.ToLocalTime().ToString("g")));
        }
        Details = string.Join(" · ", parts);
        HasDetails = Details.Length > 0;
        IsWarning = IsTrafficWarning(used, _subscription.Total) || IsExpiryWarning(_subscription.Expires);
    }

    private static bool IsTrafficWarning(long used, long? total) =>
        total is > 0 && used >= (double)total * 0.8;

    private static bool IsExpiryWarning(DateTimeOffset? expires) =>
        expires is { } e && e - DateTimeOffset.UtcNow < TimeSpan.FromDays(7);
}
