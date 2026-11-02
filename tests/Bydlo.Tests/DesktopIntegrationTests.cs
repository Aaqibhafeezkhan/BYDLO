using System.Text.Json;
using System.Xml.Linq;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class DesktopIntegrationTests
{
    private static SessionSnapshot Snapshot(ConnectionStatus status, string key = ConnectionManager.PrimaryKey) =>
        new(key, status, null, null, true, null, null, null);

    [Fact]
    public void LoginAndConnectionRequireOptInAndSettingsSurviveRoundTrip()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(settings.StartWithSystem);
        Assert.False(settings.ConnectOnStartup);
        Assert.False(settings.MinimizeToTray);
        Assert.True(settings.ShowNotifications);
        settings.StartWithSystem = settings.ConnectOnStartup = settings.MinimizeToTray = true;
        settings.ShowNotifications = false;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(restored.StartWithSystem && restored.ConnectOnStartup && restored.MinimizeToTray);
        Assert.False(restored.ShowNotifications);
    }

    [Fact]
    public async Task RapidPreferenceChangesSaveWithoutTemporaryFileCollisions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bydlo-settings-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            var settings = new AppSettings { ConnectOnStartup = true, MinimizeToTray = true, ShowNotifications = false };
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => store.SaveAsync(settings)));
            var loaded = await store.LoadAsync();
            Assert.True(loaded.ConnectOnStartup && loaded.MinimizeToTray);
            Assert.False(loaded.ShowNotifications);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    }

    [Fact]
    public void NotificationsIgnoreTrafficAndIntentionalDisconnects()
    {
        var tracker = new SessionNotificationTracker();
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connecting)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connected)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connected)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Disconnecting)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Disconnected)));
    }

    [Fact]
    public void ReconnectNotificationsAreDeduplicatedAndSessionsAreIndependent()
    {
        var tracker = new SessionNotificationTracker();
        tracker.Update(Snapshot(ConnectionStatus.Connected));
        Assert.Equal("notification.lost", tracker.Update(Snapshot(ConnectionStatus.Reconnecting)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Reconnecting)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connected, ConnectionManager.ZapretKey)));
        Assert.Equal("notification.restored", tracker.Update(Snapshot(ConnectionStatus.Connected)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connected)));
        Assert.Equal("notification.error", tracker.Update(Snapshot(ConnectionStatus.Error, ConnectionManager.TgWsProxyKey)));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Error, ConnectionManager.TgWsProxyKey)));
    }

    [Fact]
    public void FailedReconnectThenUserStopDoesNotProduceFalseRestoredNotification()
    {
        var tracker = new SessionNotificationTracker();
        tracker.Update(Snapshot(ConnectionStatus.Connected));
        tracker.Update(Snapshot(ConnectionStatus.Reconnecting));
        Assert.Equal("notification.error", tracker.Update(Snapshot(ConnectionStatus.Error)));
        tracker.Update(Snapshot(ConnectionStatus.Disconnecting));
        tracker.Update(Snapshot(ConnectionStatus.Disconnected));
        Assert.Null(tracker.Update(Snapshot(ConnectionStatus.Connected)));
    }

    [Fact]
    public void MacLoginRegistrationKeepsPathAsSingleXmlArgument()
    {
        const string executable = "/Users/Test & user/BYDLO <app>/Bydlo.Desktop";
        var doc = XDocument.Parse(AutostartService.MacRegistration(executable));
        var arguments = doc.Descendants("array").Single().Elements("string").Select(e => e.Value).ToArray();
        Assert.Equal(new[] { executable, "--autostart" }, arguments);
    }

    [Fact]
    public void LinuxLoginRegistrationEscapesFieldCodesAndShellMetacharacters()
    {
        var entry = AutostartService.LinuxRegistration("/home/Test User/$app`%f\"/bydlo");
        Assert.Contains("Exec=\"/home/Test User/\\\\$app\\\\`%%f\\\\\"/bydlo\" --autostart\n", entry, StringComparison.Ordinal);
        Assert.Contains("Terminal=false\n", entry, StringComparison.Ordinal);
    }
}
