using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.App.Services;

public static class ProfileEditorDialog
{
    public static async Task<ImportedProfile?> ShowAsync(AppServices services, Profile source)
    {
        if (services.TopLevel() is not Window owner)
        {
            return null;
        }

        var loc = Loc.Instance;
        var secrets = await services.Secrets.LoadAsync(source.Id.ToString());
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        var name = Field("profile.edit.name", source.Name);
        var hasRawConfig = secrets[Keys.RawConfig] is not null;
        var host = hasRawConfig ? null : Field("profile.edit.host", source.Host);
        var port = hasRawConfig ? null : Field("profile.edit.port", source.Port.ToString());
        var fields = new Dictionary<string, TextBox>();
        if (!hasRawConfig)
        {
            foreach (var key in new[] { Keys.Sni, Keys.Transport, Keys.Path, Keys.Fingerprint })
            {
                fields[key] = Field("profile.edit." + key, source.Option(key));
            }
        }

        var hasCredentials = source.Protocol == ProtocolKind.OpenVpn && source.Option("authUserPass") == "true";
        var username = hasCredentials ? Field("profile.edit.username", secrets[Keys.Username]) : null;
        var password = hasCredentials ? Field("profile.edit.password", secrets[Keys.Password]) : null;
        if (password is not null)
        {
            password.PasswordChar = '●';
            username!.PasswordChar = '●';
            var show = new CheckBox { Content = loc["profile.edit.show"] };
            show.IsCheckedChanged += (_, _) =>
            {
                var mask = show.IsChecked == true ? '\0' : '●';
                password.PasswordChar = mask;
                username.PasswordChar = mask;
            };
            panel.Children.Add(show);
        }

        var dpi = new DpiEditorPanel(services.Settings.DpiDefaults.For(source));
        var overrideDpi = new CheckBox
        {
            Content = loc["profile.dpi.override"],
            IsChecked = source.Option(DpiSettings.FragmentKey) is not null,
        };
        dpi.IsEnabled = overrideDpi.IsChecked == true;
        overrideDpi.IsCheckedChanged += (_, _) => dpi.IsEnabled = overrideDpi.IsChecked == true;
        if (!hasRawConfig)
        {
            panel.Children.Add(new Expander
            {
                Header = loc["profile.dpi.title"],
                Content = new StackPanel { Spacing = 8, Children = { overrideDpi, dpi } },
            });
        }

        var engines = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = loc["profile.edit.engine"] });
        panel.Children.Add(engines);
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(error);
        var save = new Button { Content = loc["common.save"] };
        panel.Children.Add(save);

        var dialog = new Window
        {
            Title = loc["profile.edit.title"],
            Width = 460,
            Height = 660,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel },
        };
        RefreshEngines(source);
        if (fields.TryGetValue(Keys.Transport, out var transport))
        {
            transport.TextChanged += (_, _) => RefreshEngines(BuildProfile(source.Port));
        }

        save.Click += (_, _) =>
        {
            var targetPort = source.Port;
            if (string.IsNullOrWhiteSpace(name.Text) ||
                (host is not null && (Uri.CheckHostName(host.Text?.Trim()) == UriHostNameType.Unknown ||
                    !int.TryParse(port?.Text, out targetPort) || targetPort is < 1 or > 65535)) ||
                (hasCredentials && (!ValidCredential(username?.Text) || !ValidCredential(password?.Text))))
            {
                error.Text = loc["profile.edit.invalid"];
                return;
            }

            var updated = BuildProfile(targetPort);
            updated.PreferredEngine = (engines.SelectedItem as ComboBoxItem)?.Tag as EngineKind?;
            if (!hasRawConfig)
            {
                var defaults = services.Settings.DpiDefaults;
                var selected = overrideDpi.IsChecked == true ? dpi.Read() : defaults;
                try
                {
                    var kind = updated.PreferredEngine ?? services.Registry.CandidatesFor(updated, PlatformInfo.Current.Os).FirstOrDefault(EngineKind.Mock);
                    if (kind is EngineKind.SingBox or EngineKind.Xray)
                    {
                        selected.Validate(updated, kind);
                    }
                    else
                    {
                        selected.ValidateFormat();
                    }
                }
                catch (EngineException ex)
                {
                    error.Text = loc[ex.LocalizationKey ?? "profile.dpi.invalid"];
                    return;
                }
                foreach (var key in new[] { DpiSettings.FragmentKey, DpiSettings.SizeKey, DpiSettings.IntervalKey, DpiSettings.MuxKey, DpiSettings.PaddingKey })
                {
                    updated.Options.Remove(key);
                }
                if (overrideDpi.IsChecked == true)
                {
                    updated.Options[DpiSettings.FragmentKey] = selected.Fragment;
                    updated.Options[DpiSettings.SizeKey] = selected.Size;
                    updated.Options[DpiSettings.IntervalKey] = selected.Interval;
                    updated.Options[DpiSettings.MuxKey] = selected.Mux;
                    updated.Options[DpiSettings.PaddingKey] = selected.Padding.ToString();
                }
            }
            if (hasCredentials)
            {
                secrets.Values[Keys.Username] = username!.Text!;
                secrets.Values[Keys.Password] = password!.Text!;
            }
            dialog.Close(new ImportedProfile(updated, secrets));
        };
        return await dialog.ShowDialog<ImportedProfile?>(owner);

        TextBox Field(string key, string? text)
        {
            panel.Children.Add(new TextBlock { Text = loc[key] });
            var field = new TextBox { Text = text ?? "" };
            panel.Children.Add(field);
            return field;
        }

        Profile BuildProfile(int targetPort)
        {
            var options = new Dictionary<string, string>(source.Options, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, field) in fields)
            {
                var value = field.Text?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    options.Remove(key);
                }
                else
                {
                    options[key] = value;
                }
            }

            return new Profile
            {
                Id = source.Id,
                Protocol = source.Protocol,
                Name = name.Text?.Trim() ?? "",
                Host = host?.Text?.Trim() ?? source.Host,
                Port = targetPort,
                Options = options,
                SubscriptionId = source.SubscriptionId,
                AddedAt = source.AddedAt,
                IsFavorite = source.IsFavorite,
            };
        }

        void RefreshEngines(Profile profile)
        {
            var selected = engines.SelectedItem is ComboBoxItem previous ? previous.Tag as EngineKind? : source.PreferredEngine;
            engines.Items.Clear();
            engines.Items.Add(new ComboBoxItem { Content = loc["profile.edit.auto"] });
            foreach (var engine in services.Registry.CandidatesFor(profile, PlatformInfo.Current.Os))
            {
                if (services.Registry.Find(engine)?.SupportOn(PlatformInfo.Current.Os) == SupportLevel.Implemented)
                {
                    engines.Items.Add(new ComboBoxItem { Content = engine.ToString(), Tag = engine });
                }
            }
            engines.SelectedItem = engines.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected))
                ?? engines.Items[0];
        }
    }

    private static bool ValidCredential(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\r', '\n', '\0']) < 0;
}
