using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Backup;
using Bydlo.Core.Models;

internal static class BackupVerification
{
    // Isolated app data, fake credentials, forced Mock Mode. No engine starts.
    public static void Run(MainViewModel shell, string output)
    {
        var services = shell.Services;
        var original = new Profile { Name = "Локальный сервер", Protocol = ProtocolKind.Vless };
        Wait(services.Profiles.AddAsync([new(original, new ProfileSecrets())]));
        shell.ReloadProfiles();
        var data = new BackupData
        {
            Settings = new AppSettings
            {
                Language = "ru", Theme = ThemePreference.Purple,
                TgWsProxy = new TgWsProxySettings { Host = "localhost", Port = 2443 },
                Zapret = new ZapretSettings { GameFilter = "tcp" },
                Routing = new RoutingSettings { DirectDomains = ["example.invalid"] },
            },
            Profiles = [new(new Profile { Name = "Сервер из резервной копии", Protocol = ProtocolKind.Trojan, Host = "example.invalid", Port = 443 },
                new ProfileSecrets { Values = { [Keys.Password] = "fake-backup-credential" } })],
        };
        var encrypted = BackupArchive.Encrypt(data, "fake backup password");
        var initialSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(initialSession);
        Require(services.Profiles.Profiles.Count == 1 && shell.Settings.HasBackupPreview, "Preview does not change data");
        var mergeText = shell.Settings.BackupPreviewText;
        shell.Settings.BackupReplace = true;
        Require(shell.Settings.BackupPreviewText != mergeText, "Changing mode updates preview");
        Require(shell.Settings.BackupPreviewText.Contains(original.Name, StringComparison.Ordinal), "Preview names removed records");
        Require(!shell.Settings.BackupPreviewText.Contains("fake-backup-credential", StringComparison.Ordinal), "Preview excludes credentials");
        var window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        var backupSection = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "BackupSettings");
        backupSection.IsExpanded = true;
        Pump();
        backupSection.BringIntoView();
        Pump();
        var backupPasswords = window.GetVisualDescendants().OfType<TextBox>().Where(t =>
            Equals(t.Watermark, Loc.Instance["backup.password"]) || Equals(t.Watermark, Loc.Instance["backup.password.confirm"])).ToArray();
        Require(backupPasswords.Length == 2 && backupPasswords.All(t => t.PasswordChar != default), "Both backup password fields are masked");
        Require(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, shell.Settings.ApplyBackupCommand)), "Apply button is bound");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "backup-preview.png")); }
        window.Width = 400;
        window.Height = 860;
        Pump();
        backupSection.BringIntoView();
        Pump();
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "backup-preview-narrow.png")); }
        shell.Settings.CancelBackupCommand.Execute(null);
        RequireDisposed(initialSession);
        Require(!shell.Settings.HasBackupPreview && services.Profiles.Profiles.Count == 1, "Cancel does not change data");
        var replacedSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(replacedSession);
        var navigationSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(navigationSession);
        RequireDisposed(replacedSession);
        shell.Navigate("settings");
        shell.Navigate("home");
        RequireDisposed(navigationSession);
        Require(!shell.Settings.HasBackupPreview, "Leaving settings closes preview");
        var appliedSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(appliedSession);
        Wait(shell.Settings.ApplyBackupCommand.ExecuteAsync(null));
        RequireDisposed(appliedSession);
        Require(!shell.Settings.HasBackupPreview, "Successful import closes preview");
        Require(services.Profiles.Profiles.Count == 1 && services.Profiles.Profiles[0].Name == data.Profiles[0].Profile.Name, "Replacement is applied");
        Require(shell.Tools.TgPort == 2443 && shell.Tools.GameFilter == "tcp", "Tools refresh restored settings");
        Require(shell.Routing.DirectDomains == "example.invalid", "Routing refreshes restored settings");
        Require(services.IsMock, "Forced Mock Mode remains active");
        // Force a document-write failure after secret writes. The old store must survive,
        // and the failed attempt must release the key rather than retain a retry session.
        var failedSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(failedSession);
        var blockedPath = Path.Combine(services.Paths.DataDirectory, "profiles.json.tmp");
        Directory.CreateDirectory(blockedPath);
        try
        {
            Wait(shell.Settings.ApplyBackupCommand.ExecuteAsync(null));
            RequireDisposed(failedSession);
            Require(!shell.Settings.HasBackupPreview, "Failed apply closes preview");
            Require(services.Profiles.Profiles.Count == 1, "Failed apply retains profiles");
        }
        finally { Directory.Delete(blockedPath); }
        var shutdownSession = BackupArchive.OpenImport(encrypted, "fake backup password");
        shell.Settings.PreviewBackup(shutdownSession);
        Wait(services.ShutdownAsync());
        RequireDisposed(shutdownSession);
        window.Close();
        Console.WriteLine("Backup verification passed: masked inputs, preview, modes, cancellation, replacement, navigation, import, failed apply, shutdown.");
    }

    private static void RequireDisposed(BackupImportSession session)
    {
        try { _ = session.Materialize(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Backup session was not disposed.");
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
