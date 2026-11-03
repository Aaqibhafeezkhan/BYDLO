using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.App.Services;

public static class ServerGroupDialog
{
    public static async Task<ServerGroup?> ShowAsync(AppServices services, ServerGroup? source)
    {
        if (services.TopLevel() is not Window owner) { return null; }
        var loc = Loc.Instance;
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        var name = Field("group.name", source?.Name ?? "");
        var engine = new ComboBox { ItemsSource = new[] { "sing-box", "Xray" }, SelectedIndex = source?.Engine == EngineKind.Xray ? 1 : 0 };
        Label("profile.edit.engine"); panel.Children.Add(engine);
        var subscription = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        subscription.Items.Add(new ComboBoxItem { Content = loc["group.manual.members"] });
        foreach (var sub in services.Profiles.Subscriptions)
        {
            subscription.Items.Add(new ComboBoxItem { Content = sub.Name, Tag = sub.Id });
        }
        subscription.SelectedItem = subscription.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, source?.SubscriptionId)) ?? subscription.Items[0];
        Label("group.source"); panel.Children.Add(subscription);
        var mode = new ComboBox
        {
            ItemsSource = new[] { loc["group.mode.manual"], loc["group.mode.auto"], loc["group.mode.fallback"] },
            SelectedIndex = (int)(source?.Mode ?? ServerGroupMode.Manual),
        };
        Label("group.mode"); panel.Children.Add(mode);
        var checks = new StackPanel { Spacing = 4 };
        Label("group.members"); panel.Children.Add(checks);
        var manual = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Label("group.selected"); panel.Children.Add(manual);
        var url = Field("group.url", source?.ProbeUrl ?? new ServerGroup().ProbeUrl);
        var interval = new NumericUpDown { Minimum = 10, Maximum = 86400, Value = source?.IntervalSeconds ?? 180 };
        Label("group.interval"); panel.Children.Add(interval);
        var tolerance = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = source?.ToleranceMs ?? 50 };
        Label("group.tolerance"); panel.Children.Add(tolerance);
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(error);
        var save = new Button { Content = loc["common.save"] };
        panel.Children.Add(save);
        var dialog = new Window
        {
            Title = loc["group.title"], Width = 480, Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel },
        };
        var boxes = new List<(Profile Profile, CheckBox Box)>();
        void RefreshMembers()
        {
            var selected = manual.SelectedItem is ComboBoxItem item ? item.Tag as Guid? : source?.SelectedProfileId;
            manual.Items.Clear();
            foreach (var (profile, box) in boxes.Where(pair => pair.Box.IsChecked == true))
            {
                manual.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            }
            manual.SelectedItem = manual.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected)) ?? (manual.Items.Count > 0 ? manual.Items[0] : null);
        }
        void Refresh()
        {
            var kind = engine.SelectedIndex == 1 ? EngineKind.Xray : EngineKind.SingBox;
            var subId = (subscription.SelectedItem as ComboBoxItem)?.Tag as Guid?;
            boxes.Clear(); checks.Children.Clear();
            foreach (var profile in services.Profiles.Profiles.Where(p => ServerGroupSupport.Supports(kind, p)))
            {
                var box = new CheckBox
                {
                    Content = profile.Name,
                    IsChecked = subId is { } id ? profile.SubscriptionId == id : source?.ProfileIds.Contains(profile.Id) == true,
                    IsEnabled = subId is null,
                };
                boxes.Add((profile, box)); checks.Children.Add(box);
                box.IsCheckedChanged += (_, _) => RefreshMembers();
            }
            RefreshMembers();
        }
        engine.SelectionChanged += (_, _) => Refresh();
        subscription.SelectionChanged += (_, _) => Refresh();
        Refresh();
        save.Click += (_, _) =>
        {
            try
            {
                var group = new ServerGroup
                {
                    Id = source?.Id ?? Guid.NewGuid(), Name = name.Text?.Trim() ?? "",
                    Engine = engine.SelectedIndex == 1 ? EngineKind.Xray : EngineKind.SingBox,
                    Mode = (ServerGroupMode)mode.SelectedIndex,
                    SubscriptionId = (subscription.SelectedItem as ComboBoxItem)?.Tag as Guid?,
                    ProfileIds = boxes.Where(p => p.Box.IsChecked == true).Select(p => p.Profile.Id).ToList(),
                    SelectedProfileId = (manual.SelectedItem as ComboBoxItem)?.Tag as Guid?,
                    ProbeUrl = url.Text?.Trim() ?? "", IntervalSeconds = (int)(interval.Value ?? 180), ToleranceMs = (int)(tolerance.Value ?? 50),
                };
                var members = group.Resolve(services.Profiles.Profiles);
                group.Validate(members);
                if (members.Any(p => !ServerGroupSupport.Supports(group.Engine, p))
                    || !services.Registry.CandidatesForChain(members, PlatformInfo.Current.Os).Contains(group.Engine))
                { throw ServerGroup.Invalid(); }
                dialog.Close(group);
            }
            catch (EngineException ex) { error.Text = loc[ex.LocalizationKey ?? "group.invalid"]; }
        };
        return await dialog.ShowDialog<ServerGroup?>(owner);

        void Label(string key) => panel.Children.Add(new TextBlock { Text = loc[key], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        TextBox Field(string key, string text)
        {
            Label(key); var field = new TextBox { Text = text }; panel.Children.Add(field); return field;
        }
    }
}
