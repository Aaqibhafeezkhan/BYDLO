using System.Text;
using Avalonia.Platform.Storage;
using Bydlo.Core.Backup;
using Bydlo.Core.Connection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _backupPassword = "";
    [ObservableProperty] private string _backupPasswordConfirmation = "";
    [ObservableProperty] private bool _backupBusy;
    [ObservableProperty] private bool _backupReplace;
    [ObservableProperty] private string _backupPreviewText = "";
    [ObservableProperty] private bool _hasBackupPreview;
    private BackupData? _pendingBackup;

    partial void OnBackupReplaceChanged(bool value) => UpdateBackupPreview();

    private BackupImportMode ImportMode => BackupReplace ? BackupImportMode.Replace : BackupImportMode.Merge;

    public void PreviewBackup(BackupData data)
    {
        _ = BackupArchive.Preview(data, Services.Profiles, ImportMode);
        _pendingBackup = data;
        UpdateBackupPreview();
        HasBackupPreview = true;
    }

    private void UpdateBackupPreview()
    {
        if (_pendingBackup is null) { return; }
        var p = BackupArchive.Preview(_pendingBackup, Services.Profiles, ImportMode);
        var text = new StringBuilder();
        text.AppendLine(string.Format(L["backup.preview.profiles"], p.AddedProfiles, p.ReplacedProfiles, p.RemovedProfiles));
        text.AppendLine(string.Format(L["backup.preview.subscriptions"], p.AddedSubscriptions, p.ReplacedSubscriptions, p.RemovedSubscriptions));
        text.AppendLine(L[BackupReplace ? "backup.replace.hint" : "backup.merge.hint"]);
        foreach (var item in _pendingBackup.Profiles)
        { text.AppendLine($"{L[Services.Profiles.Profiles.Any(p => p.Id == item.Profile.Id) ? "backup.overwrite" : "backup.add"]}: {item.Profile.Name}"); }
        foreach (var sub in _pendingBackup.Subscriptions)
        { text.AppendLine($"{L[Services.Profiles.Subscriptions.Any(s => s.Id == sub.Subscription.Id) ? "backup.overwrite" : "backup.add"]}: {sub.Subscription.Name}"); }
        if (BackupReplace)
        {
            foreach (var profile in Services.Profiles.Profiles.Where(p => _pendingBackup.Profiles.All(i => i.Profile.Id != p.Id)))
            { text.AppendLine($"{L["backup.remove"]}: {profile.Name}"); }
            foreach (var sub in Services.Profiles.Subscriptions.Where(s => _pendingBackup.Subscriptions.All(i => i.Subscription.Id != s.Id)))
            { text.AppendLine($"{L["backup.remove"]}: {sub.Name}"); }
        }
        BackupPreviewText = text.ToString();
    }

    [RelayCommand]
    private async Task ExportBackup()
    {
        if (BackupBusy) { return; }
        if (BackupPassword.Length < 12 || BackupPassword != BackupPasswordConfirmation)
        { Shell.ShowToast(L["backup.password.invalid"], isError: true); return; }
        BackupBusy = true;
        try
        {
            var data = await Services.Profiles.CaptureBackupAsync(Services.Settings);
            var password = BackupPassword;
            var encrypted = await Task.Run(() => BackupArchive.Encrypt(data, password));
            if (Services.TopLevel()?.StorageProvider is not { } storage) { return; }
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L["backup.export"], SuggestedFileName = "BYDLO.bydlobackup", DefaultExtension = "bydlobackup",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("BYDLO") { Patterns = ["*.bydlobackup"] }],
            });
            if (file is null) { return; }
            if (file.TryGetLocalPath() is { } localPath)
            {
                // Atomic replacement (issue #334): the previous backup stays
                // readable until the new payload is fully flushed to disk.
                await BackupExport.WriteAtomicAsync(localPath, encrypted);
            }
            else
            {
                // Providers without a local path have no safe replacement primitive.
                // Never overwrite: a partial write (or a truncate-on-open provider)
                // would destroy the previous backup (issue #334).
                if (!await BackupExport.IsRemoteTargetMissingAsync(() => file.OpenReadAsync()))
                {
                    Shell.ShowToast(L["backup.export.exists"], isError: true);
                    return;
                }
                await BackupExport.WriteRemoteAsync(() => file.OpenWriteAsync(), encrypted);
            }
            Shell.ShowToast(L["backup.exported"]);
        }
        catch (Exception) { Shell.ShowToast(L["backup.error"], isError: true); }
        finally { BackupBusy = false; BackupPassword = BackupPasswordConfirmation = ""; }
    }

    [RelayCommand]
    private async Task OpenBackup()
    {
        if (BackupBusy) { return; }
        BackupBusy = true;
        CancelBackup();
        try
        {
            if (Services.TopLevel()?.StorageProvider is not { } storage) { return; }
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L["backup.import"], AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("BYDLO") { Patterns = ["*.bydlobackup"] }],
            });
            if (files.Count == 0) { return; }
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > BackupArchive.MaximumFileSize) { throw new InvalidDataException(); }
                buffer.Write(chunk, 0, count);
            }
            var password = BackupPassword;
            PreviewBackup(await Task.Run(() => BackupArchive.Decrypt(buffer.ToArray(), password)));
        }
        catch (Exception) { Shell.ShowToast(L["backup.error"], isError: true); }
        finally { BackupBusy = false; BackupPassword = BackupPasswordConfirmation = ""; }
    }

    [RelayCommand]
    private async Task ApplyBackup()
    {
        if (BackupBusy || _pendingBackup is null) { return; }
        var sessions = new[] { Services.Connections.Primary, Services.Connections.Zapret, Services.Connections.TgWsProxy };
        if (sessions.Any(s => s.Status is not (ConnectionStatus.Disconnected or ConnectionStatus.Error) || s.CleanupPending))
        { Shell.ShowToast(L["backup.disconnect"], isError: true); return; }
        BackupBusy = true;
        try
        {
            await Services.RestoreBackupAsync(_pendingBackup, ImportMode);
            Shell.Tools.ReloadSettings();
            Services.Theme.ApplySaved(Services.Settings, Services.Paths.DataDirectory);
            Bydlo.App.Services.Loc.Instance.SetLanguage(Services.Settings.Language);
            Shell.ReloadProfiles();
            Shell.RefreshMock();
            Shell.Home.RefreshAll();
            Shell.Routing.OnShown();
            Shell.Connections.OnShown();
            OnShown();
            CancelBackup();
            Shell.ShowToast(L["backup.imported"]);
        }
        catch (Exception) { Shell.ShowToast(L["backup.error"], isError: true); }
        finally { BackupBusy = false; }
    }

    [RelayCommand]
    private void CancelBackup()
    {
        _pendingBackup = null;
        HasBackupPreview = false;
        BackupPreviewText = "";
    }
}
