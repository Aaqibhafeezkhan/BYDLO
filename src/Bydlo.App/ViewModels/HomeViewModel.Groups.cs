using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class HomeViewModel
{
    public IReadOnlyList<ServerGroup> ServerGroups => Services.Settings.ServerGroups;
    [ObservableProperty] private ServerGroup? _selectedGroup;
    [ObservableProperty] private bool _useGroup;
    public bool IsGroupActive => IsConnected && Shell.Primary.Group is not null;
    public string ActiveGroupServer => Shell.Primary.SelectedGroupProfile is { } profile
        ? Loc.T("group.active", Shell.Primary.Group?.Name ?? "", profile.Name) : L["group.waiting"];

    partial void OnUseGroupChanged(bool value)
    {
        if (value) { Shell.SelectedChain = null; }
    }

    [RelayCommand]
    private async Task NewGroup() => await EditGroupCore(null);

    [RelayCommand]
    private async Task EditGroup()
    {
        if (SelectedGroup is { } group) { await EditGroupCore(group); }
    }

    private async Task EditGroupCore(ServerGroup? source)
    {
        await Shell.Guard(async () =>
        {
            if (await ServerGroupDialog.ShowAsync(Services, source) is not { } group) { return; }
            var index = Services.Settings.ServerGroups.FindIndex(g => g.Id == group.Id);
            if (index < 0) { Services.Settings.ServerGroups.Add(group); }
            else { Services.Settings.ServerGroups[index] = group; }
            await Services.SaveSettingsAsync();
            OnPropertyChanged(nameof(ServerGroups));
            SelectedGroup = group;
            UseGroup = true;
        });
    }

    [RelayCommand]
    private async Task DeleteGroup()
    {
        if (SelectedGroup is not { } group) { return; }
        await Shell.Guard(async () =>
        {
            Services.Settings.ServerGroups.RemoveAll(g => g.Id == group.Id);
            await Services.SaveSettingsAsync();
            SelectedGroup = null; UseGroup = false;
            OnPropertyChanged(nameof(ServerGroups));
        });
    }

    private async Task ConnectGroupAsync(ServerGroup group)
    {
        var profiles = group.Resolve(Services.Profiles.Profiles);
        group.Validate(profiles);
        foreach (var warning in Services.Connections.PreviewChainConflicts(profiles, Services.EffectiveSettings)
            .Where(i => i.Severity == ConflictSeverity.Warning))
        { Shell.ShowToast(warning.Message); }
        var members = new List<ChainHop>();
        foreach (var profile in profiles)
        {
            members.Add(new ChainHop(profile, await Services.Profiles.Secrets.LoadAsync(profile.Id.ToString())));
        }
        await Services.Connections.ConnectGroupAsync(group, members, Services.EffectiveSettings);
    }
}
