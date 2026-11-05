using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Serialization;

namespace Bydlo.Tests;

public class AutomaticAppUpdaterTests
{
    [Fact]
    public void Data_directory_must_be_outside_replaceable_payload_including_exact_path()
    {
        var application = Path.Combine(TestDirs.New("updater-paths"), "app");
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application + Path.DirectorySeparatorChar, application));
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application, application + Path.DirectorySeparatorChar));
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application, Path.Combine(application, "data")));
        Assert.True(AutomaticAppUpdater.HasSeparateDataDirectory(application, application + "-data"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_mock_never_constructs_backend(bool enabled, bool blocked)
    {
        var policy = new AutomaticAppUpdater(() => throw new InvalidOperationException("No I/O allowed"));
        var settings = new AppSettings { AutoUpdateApp = enabled };
        Assert.False(await policy.DownloadIfDueAsync(settings, blocked));
        policy.ApplyOnExit(settings, blocked);
        Assert.Null(settings.LastAutomaticUpdateCheck);
    }

    [Fact]
    public async Task Applies_only_at_exit_with_current_opt_in_and_preserves_user_data()
    {
        var backend = new FakeBackend();
        var policy = new AutomaticAppUpdater(() => backend);
        var settings = new AppSettings { AutoUpdateApp = true, LastProfileId = Guid.NewGuid(), SubscriptionAutoUpdate = true };
        var original = settings.LastProfileId;
        Assert.True(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(0, backend.Applied);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(1, backend.Downloads);
        policy.ApplyOnExit(settings, true);
        Assert.Equal(0, backend.Applied);
        settings.AutoUpdateApp = false;
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
        settings.AutoUpdateApp = true;
        policy.ApplyOnExit(settings, false);
        policy.ApplyOnExit(settings, false);
        Assert.Equal(1, backend.Applied);
        var restored = BydloJson.DeepClone(settings);
        Assert.True(restored.AutoUpdateApp);
        Assert.Equal(settings.LastAutomaticUpdateCheck, restored.LastAutomaticUpdateCheck);
        Assert.Equal(original, restored.LastProfileId);
        Assert.True(restored.SubscriptionAutoUpdate);
    }

    [Fact]
    public async Task Turning_off_during_download_prevents_installation()
    {
        var backend = new FakeBackend();
        var settings = new AppSettings { AutoUpdateApp = true };
        backend.Download = () => { settings.AutoUpdateApp = false; return Task.FromResult(true); };
        var policy = new AutomaticAppUpdater(() => backend);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        settings.AutoUpdateApp = true;
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Failed_check_is_throttled_and_never_installs()
    {
        var backend = new FakeBackend { Download = () => throw new IOException("offline") };
        var policy = new AutomaticAppUpdater(() => backend);
        var settings = new AppSettings { AutoUpdateApp = true };
        await Assert.ThrowsAsync<IOException>(() => policy.DownloadIfDueAsync(settings, false));
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Unpackaged_build_never_downloads_or_applies()
    {
        var backend = new FakeBackend { IsInstalled = false };
        var policy = new AutomaticAppUpdater(() => backend);
        var settings = new AppSettings { AutoUpdateApp = true };
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.Applied);
        Assert.False(new AppSettings().AutoUpdateApp);
    }

    private sealed class FakeBackend : IAppUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public int Downloads { get; private set; }
        public int Applied { get; private set; }
        public Func<Task<bool>> Download { get; set; } = () => Task.FromResult(true);
        public Task<bool> DownloadLatestAsync(CancellationToken ct) { Downloads++; return Download(); }
        public void ApplyOnExit() => Applied++;
    }
}
