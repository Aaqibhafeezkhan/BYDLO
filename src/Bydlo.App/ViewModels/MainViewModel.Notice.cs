using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Maintainer announcement from <c>notice.json</c> in the repository (issue #193).</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    [NotifyPropertyChangedFor(nameof(IsNoticeCritical))]
    [NotifyPropertyChangedFor(nameof(HasNoticeUrl))]
    [NotifyPropertyChangedFor(nameof(NoticeTitle))]
    [NotifyPropertyChangedFor(nameof(HasNoticeTitle))]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    private ProjectNotice? _notice;

    public bool HasNotice => Notice is not null;
    public bool IsNoticeCritical => Notice?.Level == ProjectNoticeLevel.Critical;
    public bool HasNoticeUrl => Notice?.Url is not null;
    public string NoticeTitle => Notice?.LocalizedTitle(Loc.Instance.Language) ?? "";
    public bool HasNoticeTitle => NoticeTitle.Length > 0;
    public string NoticeText => Notice?.LocalizedText(Loc.Instance.Language) ?? "";

    private void OnNoticeLanguageChanged()
    {
        OnPropertyChanged(nameof(NoticeTitle));
        OnPropertyChanged(nameof(HasNoticeTitle));
        OnPropertyChanged(nameof(NoticeText));
    }

    private async Task RefreshNoticeAsync()
    {
        try
        {
            Notice = await new ProjectNoticeChecker(new ProjectNoticeClient()).CheckAsync(
                Services.Settings, Bydlo.Core.AppVersion.Current, Services.IsMock || NetworkSafetyGuard.IsLocked,
                Services.ShutdownToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Services.Log.Warn("notice", ex.GetType().Name);
        }
    }

    [RelayCommand]
    private async Task DismissNotice()
    {
        if (Notice is not { } notice)
        {
            return;
        }
        Notice = null;
        Services.Settings.DismissedNoticeId = notice.Id;
        await Guard(() => Services.SaveSettingsAsync());
    }

    [RelayCommand]
    private async Task OpenNotice()
    {
        // Url is already restricted to the BYDLO repository by the parser.
        if (Notice?.Url is not { } url || Services.TopLevel()?.Launcher is not { } launcher)
        {
            return;
        }
        await Guard(() => launcher.LaunchUriAsync(new Uri(url)));
    }
}
