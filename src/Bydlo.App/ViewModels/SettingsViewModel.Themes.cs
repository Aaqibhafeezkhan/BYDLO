using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform.Storage;
using Bydlo.App.Services;
using Bydlo.Core.Models;
using Bydlo.Core.Themes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Theme in the custom group with the active marker (issue #41); built-in Purple cannot be deleted (issue #134).</summary>
public sealed class CustomThemeRow(string id, string name, string author, bool isActive, bool isBuiltIn = false) : ObservableObject
{
    public CustomThemeRow(CustomThemeDocument doc, bool isActive)
        : this(doc.Id, doc.Name, doc.Author, isActive)
    {
    }

    public string Id => id;

    public string Name => name;

    public string Author => author;

    public bool IsActive => isActive;

    public bool CanDelete => !isBuiltIn;
}

/// <summary>One downloadable theme from the repository gallery.</summary>
public sealed class GalleryThemeRow(ThemeGalleryEntry entry)
{
    public string Id => entry.Id;

    public string Name => entry.Name;

    public string Author => entry.Author;

    public string Url => entry.Url;
}

/// <summary>Custom JSON themes: import/export/apply/delete plus the repository gallery (issue #41).</summary>
public sealed partial class SettingsViewModel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    /// <summary>Row id of the built-in Purple theme; the colon keeps it apart from installed theme ids.</summary>
    public const string BuiltInPurpleId = "builtin:purple";

    public bool ThemeCustom => Theme == ThemePreference.Custom;

    /// <summary>Purple and installed JSON themes are listed together, outside the base theme switch.</summary>
    public bool IsCustomGroupActive => Theme is ThemePreference.Purple or ThemePreference.Custom;

    public ObservableCollection<CustomThemeRow> InstalledThemes { get; } = [];

    public ObservableCollection<GalleryThemeRow> GalleryThemes { get; } = [];

    [ObservableProperty] private bool _customThemesBusy;

    [ObservableProperty] private string _customThemeWarning = "";

    [ObservableProperty] private bool _hasCustomThemeWarning;

    private string ThemesDirectory => CustomThemeStore.ThemesDirectory(Services.Paths.DataDirectory);

    public void RefreshCustomThemes()
    {
        var active = Theme == ThemePreference.Custom ? Services.Settings.CustomThemeId : null;
        var purple = new CustomThemeRow(BuiltInPurpleId, L["theme.purple"], L["theme.builtin"], Theme == ThemePreference.Purple, isBuiltIn: true);
        InstalledThemes.ReplaceWith(CustomThemeStore.ListAll(ThemesDirectory).Select(d => new CustomThemeRow(d, d.Id == active)).Prepend(purple));
        UpdateContrastWarning();
    }

    private void UpdateContrastWarning()
    {
        var low = Theme == ThemePreference.Custom && Application.Current is { } app && ThemeService.HasLowContrast(app);
        HasCustomThemeWarning = low;
        CustomThemeWarning = low ? L["theme.custom.lowcontrast"] : "";
    }

    /// <summary>Applies the stored custom theme; falls back to System when the file is gone or unappliable.</summary>
    private bool ApplyStoredCustom()
    {
        var doc = Services.Settings.CustomThemeId is { } id ? CustomThemeStore.TryLoad(ThemesDirectory, id) : null;
        if (doc is null)
        {
            Services.Theme.Apply(ThemePreference.System);
            return false;
        }
        try
        {
            UpdateWarning(Services.Theme.ApplyCustom(doc));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or CustomThemeException)
        {
            Services.Theme.Apply(ThemePreference.System);
            UpdateWarning(false);
            return false;
        }
    }

    private void UpdateWarning(bool low)
    {
        HasCustomThemeWarning = low;
        CustomThemeWarning = low ? L["theme.custom.lowcontrast"] : "";
    }

    [RelayCommand]
    private async Task ImportCustomTheme()
    {
        if (CustomThemesBusy) { return; }
        CustomThemesBusy = true;
        try
        {
            if (Services.TopLevel()?.StorageProvider is not { } storage) { return; }
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L["theme.custom.import"],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) { return; }
            var content = await ReadCappedTextAsync(files[0]);
            if (content is null) { Shell.ShowToast(L["theme.custom.broken"], isError: true); return; }
            CustomThemeDocument doc;
            try { doc = CustomThemeStore.Save(ThemesDirectory, content); }
            catch (CustomThemeException) { Shell.ShowToast(L["theme.custom.broken"], isError: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Shell.ShowToast(L["theme.custom.ioerror"], isError: true);
                return;
            }
            ApplyCustomDocument(doc);
            if (doc.IgnoredKeys.Count > 0)
            {
                Shell.ShowToast(Loc.T("theme.custom.imported.ignored", doc.Name, doc.IgnoredKeys.Count));
            }
            else
            {
                Shell.ShowToast(Loc.T("theme.custom.imported", doc.Name));
            }
        }
        finally { CustomThemesBusy = false; }
    }

    [RelayCommand]
    private void ApplyCustomTheme(string id)
    {
        if (id == BuiltInPurpleId)
        {
            Theme = ThemePreference.Purple;
            return;
        }
        var doc = CustomThemeStore.TryLoad(ThemesDirectory, id);
        if (doc is null) { Shell.ShowToast(L["theme.custom.broken"], isError: true); return; }
        ApplyCustomDocument(doc);
    }

    private void ApplyCustomDocument(CustomThemeDocument doc)
    {
        Services.Settings.Theme = ThemePreference.Custom;
        Services.Settings.CustomThemeId = doc.Id;
        try
        {
            UpdateWarning(Services.Theme.ApplyCustom(doc));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or CustomThemeException)
        {
            Services.Theme.Apply(ThemePreference.System);
            Services.Settings.CustomThemeId = null;
            Shell.ShowToast(L["theme.custom.broken"], isError: true);
            Theme = ThemePreference.System;
            Persist();
            RefreshCustomThemes();
            return;
        }
        Theme = ThemePreference.Custom;
        Persist();
        RefreshCustomThemes();
    }

    [RelayCommand]
    private void DeleteCustomTheme(string id)
    {
        if (id == BuiltInPurpleId) { return; }
        if (!CustomThemeStore.Exists(ThemesDirectory, id))
        {
            RefreshCustomThemes();
            return;
        }
        if (!CustomThemeStore.Delete(ThemesDirectory, id))
        {
            Shell.ShowToast(L["theme.custom.ioerror"], isError: true);
            return;
        }
        if (Services.Settings.CustomThemeId == id && Theme == ThemePreference.Custom)
        {
            Theme = ThemePreference.System;
        }
        Persist();
        RefreshCustomThemes();
    }

    [RelayCommand]
    private async Task ExportCustomTheme()
    {
        if (CustomThemesBusy) { return; }
        CustomThemesBusy = true;
        try
        {
            if (Services.TopLevel()?.StorageProvider is not { } storage) { return; }
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L["theme.custom.export"],
                SuggestedFileName = "bydlo-theme.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (file is null) { return; }
            var palette = ThemeService.ExportCurrentPalette();
            var colors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var full in CustomTheme.ResourceKeys)
            {
                if (palette.TryGetValue(full, out var hex))
                {
                    colors[CustomTheme.ShortKey(full)] = CustomTheme.Shorten(hex);
                }
            }
            var dark = Application.Current?.RequestedThemeVariant != Avalonia.Styling.ThemeVariant.Light;
            var template = new
            {
                id = "my-theme",
                name = "My theme",
                author = "",
                version = CustomTheme.FormatVersion,
                @base = dark ? "dark" : "light",
                colors,
            };
            var json = JsonSerializer.Serialize(template, IndentedJson);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
            await stream.FlushAsync();
            Shell.ShowToast(L["theme.custom.exported"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.ShowToast(L["theme.custom.ioerror"], isError: true);
        }
        finally { CustomThemesBusy = false; }
    }

    [RelayCommand]
    private async Task FetchGallery()
    {
        if (CustomThemesBusy) { return; }
        CustomThemesBusy = true;
        try
        {
            if (Services.IsMock || Bydlo.Core.Security.NetworkSafetyGuard.IsLocked)
            {
                Shell.ShowToast(L["theme.custom.locked"], isError: true);
                return;
            }
            var index = await DownloadCappedTextAsync(CustomTheme.DefaultGalleryIndexUrl);
            if (index is null) { Shell.ShowToast(L["theme.custom.gallery.error"], isError: true); return; }
            IReadOnlyList<ThemeGalleryEntry> entries;
            try { entries = CustomTheme.ParseIndex(index); }
            catch (CustomThemeException) { Shell.ShowToast(L["theme.custom.gallery.error"], isError: true); return; }
            var installed = new HashSet<string>(InstalledThemes.Select(t => t.Id), StringComparer.Ordinal);
            GalleryThemes.ReplaceWith(entries.Where(e => !installed.Contains(e.Id)).Select(e => new GalleryThemeRow(e)));
            if (GalleryThemes.Count == 0) { Shell.ShowToast(L["theme.custom.gallery.empty"]); }
        }
        finally { CustomThemesBusy = false; }
    }

    [RelayCommand]
    private async Task InstallGalleryTheme(string id)
    {
        if (CustomThemesBusy) { return; }
        var entry = GalleryThemes.FirstOrDefault(g => g.Id == id);
        if (entry is null) { return; }
        CustomThemesBusy = true;
        try
        {
            if (Services.IsMock || Bydlo.Core.Security.NetworkSafetyGuard.IsLocked)
            {
                Shell.ShowToast(L["theme.custom.locked"], isError: true);
                return;
            }
            var content = await DownloadCappedTextAsync(entry.Url);
            if (content is null) { Shell.ShowToast(L["theme.custom.gallery.error"], isError: true); return; }
            CustomThemeDocument doc;
            try { doc = CustomThemeStore.Save(ThemesDirectory, content); }
            catch (CustomThemeException) { Shell.ShowToast(L["theme.custom.broken"], isError: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Shell.ShowToast(L["theme.custom.ioerror"], isError: true);
                return;
            }
            ApplyCustomDocument(doc);
            GalleryThemes.Remove(entry);
            Shell.ShowToast(Loc.T("theme.custom.installed", doc.Name));
        }
        finally { CustomThemesBusy = false; }
    }

    private static async Task<string?> ReadCappedTextAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > CustomTheme.MaxFileBytes) { return null; }
                buffer.Write(chunk, 0, count);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> DownloadCappedTextAsync(string url)
    {
        // Stream with an early stop: without Content-Length a full ReadAsStringAsync
        // could allocate an unbounded buffer before the 64 KiB check.
        try
        {
            using var response = await Services.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, Services.ShutdownToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > CustomTheme.MaxFileBytes)
            {
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync(Services.ShutdownToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, Services.ShutdownToken)) > 0)
            {
                if (buffer.Length + count > CustomTheme.MaxFileBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, count);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
