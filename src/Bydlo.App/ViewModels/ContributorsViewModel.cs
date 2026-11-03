using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Row for the contributors page; the commit label follows the UI language.</summary>
public sealed class ContributorRow(Contributor contributor)
{
    public Contributor Contributor { get; } = contributor;

    public string Login => Contributor.Login;

    public string CommitsText => Loc.T("contributors.commits", Contributor.Contributions);
}

/// <summary>Contributors page (issue #168): the GitHub list cached in settings, never hardcoded.</summary>
public sealed partial class ContributorsViewModel : PageViewModel
{
    public ContributorsViewModel(MainViewModel shell)
        : base(shell)
    {
        Loc.Instance.PropertyChanged += (_, _) => Items = Items.Select(r => new ContributorRow(r.Contributor)).ToArray();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContributors))]
    private IReadOnlyList<ContributorRow> _items = [];

    public bool HasContributors => Items.Count > 0;

    /// <summary>Shows the cached list; only valid logins survive (settings may come from a backup file).</summary>
    public void Show(IEnumerable<Contributor> contributors) =>
        Items = contributors.Where(c => GitHubContributorsClient.IsValidLogin(c.Login))
            .DistinctBy(c => c.Login, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ContributorRow(c))
            .ToArray();

    [RelayCommand]
    private async Task OpenContributor(ContributorRow? row)
    {
        if (row is not null && GitHubContributorsClient.IsValidLogin(row.Login))
        {
            await OpenExternal(row.Contributor.ProfileUrl);
        }
    }

    [RelayCommand]
    private Task OpenAll() => OpenExternal(GitHubContributorsClient.ContributorsPageUrl);

    private async Task OpenExternal(string url)
    {
        if (Services.TopLevel()?.Launcher is not { } launcher)
        {
            return;
        }
        await Shell.Guard(async () => await launcher.LaunchUriAsync(new Uri(url)));
    }
}
