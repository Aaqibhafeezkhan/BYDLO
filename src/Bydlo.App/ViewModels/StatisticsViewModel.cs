using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Routing;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class StatisticsViewModel : PageViewModel
{
    public StatisticsViewModel(MainViewModel shell)
        : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        _connectionTimer.Tick += (_, _) => _ = RefreshConnectionsAsync();
        foreach (var e in Services.Log.Snapshot().TakeLast(300))
        {
            Log.Add(e.ToString());
        }

        Services.Log.EntryAdded += (_, e) => Dispatcher.UIThread.Post(() => AddLog(e));
    }

    private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _connectionPage;
    private IReadOnlyList<ActiveConnection> _connections = [];
    private bool _refreshing;

    [ObservableProperty] private bool _activeConnectionsExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredConnections))]
    private string _connectionFilter = "";
    [ObservableProperty] private bool _connectionListError;

    public bool SupportsActiveConnections => Shell.Primary.Status == ConnectionStatus.Connected
        && Shell.Primary.Engine is EngineKind.SingBox or EngineKind.Mock;

    public IEnumerable<ActiveConnection> FilteredConnections => _connections.Where(connection =>
        string.IsNullOrWhiteSpace(ConnectionFilter) ||
        connection.Host.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase) ||
        connection.Process.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase) ||
        connection.Rule.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase) ||
        connection.Outbound.Contains(ConnectionFilter, StringComparison.OrdinalIgnoreCase));

    public override void OnShown() => UpdateConnectionPolling();

    partial void OnActiveConnectionsExpandedChanged(bool value) => UpdateConnectionPolling();

    private void UpdateConnectionPolling()
    {
        if (!ActiveConnectionsExpanded || !ReferenceEquals(Shell.CurrentPage, this) || !SupportsActiveConnections)
        {
            _connectionTimer.Stop();
            _connectionPage?.Cancel();
            _connectionPage?.Dispose();
            _connectionPage = null;
            _connections = [];
            ConnectionListError = false;
            OnPropertyChanged(nameof(FilteredConnections));
        }
        else if (_connectionPage is null)
        {
            _connectionPage = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
            _connectionTimer.Start();
            _ = RefreshConnectionsAsync();
        }
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_refreshing || _connectionPage is not { } page)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var connections = await Services.Connections.ListActiveConnectionsAsync(page.Token);
            if (ReferenceEquals(_connectionPage, page))
            {
                _connections = connections ?? [];
                ConnectionListError = false;
                OnPropertyChanged(nameof(FilteredConnections));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            if (ReferenceEquals(_connectionPage, page))
            {
                ConnectionListError = true;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task CloseConnection(ActiveConnection connection)
    {
        if (_connectionPage is not { } page)
        {
            return;
        }
        try
        {
            await Services.Connections.CloseActiveConnectionAsync(connection.Id, page.Token);
            await RefreshConnectionsAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            if (ReferenceEquals(_connectionPage, page))
            {
                ConnectionListError = true;
            }
        }
    }

    [RelayCommand]
    private void AddConnectionRule(ActiveConnection connection)
    {
        if (Uri.CheckHostName(connection.Host) == UriHostNameType.Dns)
        {
            Shell.Navigate("routing");
            Shell.Routing.RuleConditionIndex = (int)RoutingCondition.Domain;
            Shell.Routing.RuleValue = connection.Host;
            Shell.Routing.RuleActionIndex = (int)RoutingAction.Proxy;
            Shell.Routing.ShowRuleEditor = true;
        }
    }

    public ObservableCollection<string> Log { get; } = [];

    public string Down => Format.Speed(Shell.Primary.LastTraffic?.DownloadBytesPerSecond ?? 0);

    public string Up => Format.Speed(Shell.Primary.LastTraffic?.UploadBytesPerSecond ?? 0);

    public string TotalDown => Format.Bytes(Shell.Primary.LastTraffic?.TotalDownload ?? 0);

    public string TotalUp => Format.Bytes(Shell.Primary.LastTraffic?.TotalUpload ?? 0);

    public string Engine => Labels.Engine(Shell.Primary.Engine);

    public string Server => Shell.Primary.Profile?.Endpoint is { Length: > 0 } e ? e : "—";

    public bool HasTraffic => Shell.Primary.LastTraffic is not null;

    public bool NoStatsEngine => Shell.Primary.Status == ConnectionStatus.Connected && Shell.Primary.LastTraffic is null;

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        Services.Log.Clear();
    }

    [RelayCommand]
    private async Task CopyLog()
    {
        if (Services.TopLevel()?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(string.Join(Environment.NewLine, Log));
            Shell.ShowToast(L["toast.copied"]);
        }
    }

    private void AddLog(LogEntry e)
    {
        Log.Add(e.ToString());
        while (Log.Count > 500)
        {
            Log.RemoveAt(0);
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            UpdateConnectionPolling();
        }
        if (e.PropertyName != nameof(MainViewModel.Primary))
        {
            return;
        }

        OnPropertyChanged(nameof(SupportsActiveConnections));
        UpdateConnectionPolling();
        foreach (var n in new[] { nameof(Down), nameof(Up), nameof(TotalDown), nameof(TotalUp), nameof(Engine), nameof(Server), nameof(HasTraffic), nameof(NoStatsEngine) })
        {
            OnPropertyChanged(n);
        }
    }
}
