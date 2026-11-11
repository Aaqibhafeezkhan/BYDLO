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
    private const int MaxLogLines = 500;

    public StatisticsViewModel(MainViewModel shell)
        : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        foreach (var e in Services.Log.Snapshot().TakeLast(300))
        {
            Log.Add(e.ToString());
        }

        Services.Log.EntriesAdded += (_, batch) => Dispatcher.UIThread.Post(() => AddLogs(batch));
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

    private void AddLogs(IReadOnlyList<LogEntry> batch)
    {
        // Only the tail can survive the cap; adding the rest would churn the list for nothing.
        var start = Math.Max(0, batch.Count - MaxLogLines);
        for (var i = start; i < batch.Count; i++)
        {
            Log.Add(batch[i].ToString());
        }

        var excess = Log.Count - MaxLogLines;
        for (var i = 0; i < excess; i++)
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
