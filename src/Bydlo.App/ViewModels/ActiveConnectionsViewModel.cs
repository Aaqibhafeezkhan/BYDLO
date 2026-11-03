using System.ComponentModel;
using Avalonia.Threading;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Routing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ActiveConnectionsViewModel : PageViewModel
{
    public ActiveConnectionsViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        shell.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ShowAdvanced)) { UpdateConnectionPolling(); }
        };
        _connectionTimer.Tick += (_, _) => _ = RefreshConnectionsAsync();
    }

    private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _connectionPage;
    private IReadOnlyList<ActiveConnection> _connections = [];
    private bool _refreshing;

    [ObservableProperty] private bool _activeConnectionsExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredConnections)), NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _connectionFilter = "";
    [ObservableProperty] private bool _connectionListError;

    public bool SupportsActiveConnections => Shell.Primary.Status == ConnectionStatus.Connected
        && (Shell.Primary.IsMock || Shell.Primary.Engine == EngineKind.SingBox);

    public bool IsEmpty => SupportsActiveConnections && !ConnectionListError && !FilteredConnections.Any();

    partial void OnConnectionListErrorChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public IEnumerable<ActiveConnection> FilteredConnections => _connections.Where(connection =>
        string.IsNullOrWhiteSpace(ConnectionFilter) ||
        connection.Host.Contains(ConnectionFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
        connection.Process.Contains(ConnectionFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
        connection.Rule.Contains(ConnectionFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
        connection.Outbound.Contains(ConnectionFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    partial void OnActiveConnectionsExpandedChanged(bool value) => UpdateConnectionPolling();

    private void UpdateConnectionPolling()
    {
        if (!ActiveConnectionsExpanded || (!ReferenceEquals(Shell.CurrentPage, Shell.Settings) || !Shell.Settings.ShowAdvanced) || !SupportsActiveConnections)
        {
            _connectionTimer.Stop();
            _connectionPage?.Cancel();
            _connectionPage?.Dispose();
            _connectionPage = null;
            _connections = [];
            ConnectionListError = false;
            OnPropertyChanged(nameof(FilteredConnections));
            OnPropertyChanged(nameof(IsEmpty));
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
            if (ReferenceEquals(_connectionPage, page) && !page.IsCancellationRequested)
            {
                _connections = connections ?? [];
                ConnectionListError = false;
                OnPropertyChanged(nameof(FilteredConnections));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            if (ReferenceEquals(_connectionPage, page) && !page.IsCancellationRequested)
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
            if (ReferenceEquals(_connectionPage, page) && !page.IsCancellationRequested)
            {
                ConnectionListError = true;
            }
        }
    }

    private static bool CanAddConnectionRule(ActiveConnection? connection) =>
        connection is not null && Uri.CheckHostName(connection.Host) == UriHostNameType.Dns;

    [RelayCommand(CanExecute = nameof(CanAddConnectionRule))]
    private void AddConnectionRule(ActiveConnection connection)
    {
        if (CanAddConnectionRule(connection))
        {
            Shell.Navigate("routing");
            Shell.Routing.RuleConditionIndex = (int)RoutingCondition.Domain;
            Shell.Routing.RuleValue = connection.Host;
            Shell.Routing.RuleActionIndex = (int)RoutingAction.Proxy;
            Shell.Routing.ShowRuleEditor = true;
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Primary) or nameof(MainViewModel.CurrentPage))
        {
            OnPropertyChanged(nameof(SupportsActiveConnections));
            UpdateConnectionPolling();
        }
    }
}
