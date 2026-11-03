using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Bydlo.Core.Models;

namespace Bydlo.App.Services;

internal sealed class DpiEditorPanel : StackPanel
{
    private static readonly string[] Fragments = ["off", "tls", "record"];
    private static readonly string[] Muxes = ["off", "smux", "yamux", "h2mux", "xray", "xudp"];

    private readonly ComboBox _fragment;
    private readonly ComboBox _mux;
    private readonly TextBox _size;
    private readonly TextBox _interval;
    private readonly CheckBox _padding;

    public DpiEditorPanel(DpiSettings initial)
    {
        var loc = Loc.Instance;
        Spacing = 8;
        _fragment = Choice("profile.dpi.fragment", Fragments, initial.Fragment);
        _size = Text("profile.dpi.size", initial.Size);
        _interval = Text("profile.dpi.interval", initial.Interval);
        _mux = Choice("profile.dpi.mux", Muxes, initial.Mux);
        _padding = new CheckBox { Content = loc["profile.dpi.padding"], IsChecked = initial.Padding };
        Children.Add(_padding);
        Children.Add(new TextBlock { Text = loc["profile.dpi.hint"], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
    }

    public DpiSettings Read() => new()
    {
        Fragment = Fragments[Math.Max(0, _fragment.SelectedIndex)],
        Mux = Muxes[Math.Max(0, _mux.SelectedIndex)],
        Size = _size.Text?.Trim() ?? "",
        Interval = _interval.Text?.Trim() ?? "",
        Padding = _padding.IsChecked == true,
    };

    private ComboBox Choice(string key, string[] values, string selected)
    {
        Children.Add(new TextBlock { Text = Loc.Instance[key] });
        var combo = new ComboBox
        {
            ItemsSource = values.Select(value => Loc.Instance["profile.dpi.value." + value]).ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(values, selected)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Children.Add(combo);
        return combo;
    }

    private TextBox Text(string key, string value)
    {
        Children.Add(new TextBlock { Text = Loc.Instance[key] });
        var field = new TextBox { Text = value };
        Children.Add(field);
        return field;
    }

    public static async Task<DpiSettings?> ShowDefaultsAsync(Window owner, DpiSettings current)
    {
        var loc = Loc.Instance;
        var editor = new DpiEditorPanel(current);
        var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = loc["common.save"] };
        var dialog = new Window
        {
            Title = loc["profile.dpi.defaults"],
            Width = 460, Height = 550,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = new StackPanel { Spacing = 10, Margin = new Thickness(20), Children = { editor, message, save } },
            },
        };
        save.Click += (_, _) =>
        {
            var result = editor.Read();
            try
            {
                result.ValidateFormat();
                dialog.Close(result);
            }
            catch (Bydlo.Core.Engines.EngineException ex)
            {
                message.Text = loc[ex.LocalizationKey ?? "profile.dpi.invalid"];
            }
        };
        return await dialog.ShowDialog<DpiSettings?>(owner);
    }
}
