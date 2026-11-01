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
        foreach (var e in Services.Log.Snapshot().TakeLast(300))
        {
            Log.Add(e.ToString());
        }

        Services.Log.EntryAdded += (_, e) => Dispatcher.UIThread.Post(() => AddLog(e));
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
        if (e.PropertyName != nameof(MainViewModel.Primary))
        {
            return;
        }

        foreach (var n in new[] { nameof(Down), nameof(Up), nameof(TotalDown), nameof(TotalUp), nameof(Engine), nameof(Server), nameof(HasTraffic), nameof(NoStatsEngine) })
        {
            OnPropertyChanged(n);
        }
    }
}
