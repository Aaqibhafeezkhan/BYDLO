using Bydlo.Core.Backup;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Isolated tests for issue #80 (Allow LAN). No sockets are opened: builders and fakes only.</summary>
public sealed class LanProxyTests
{
    private static (Profile Profile, ProfileSecrets Secrets) Vless(string host = "example.com") =>
        (new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = host, Port = 443 },
            new ProfileSecrets { Values = { [Keys.Uuid] = "11111111-2222-3333-4444-555555555555" } });

    private static AppSettings LanSettings() => new()
    {
        AllowLan = true,
        LocalProxyUsername = "lan-user",
    };

    [Fact]
    public void SingBox_loopback_without_auth_stays_default()
    {
        var (profile, secrets) = Vless();
        var inbound = new SingBoxConfigBuilder().Build(profile, secrets, new BuildContext(new AppSettings()))["inbounds"]![0]!;
        Assert.Equal("127.0.0.1", (string?)inbound["listen"]);
        Assert.Null(inbound["users"]);
    }

    [Fact]
    public void SingBox_lan_listens_everywhere_with_auth()
    {
        var (profile, secrets) = Vless();
        var ctx = new BuildContext(LanSettings(), LocalProxyAuth: new LocalProxyAuth("lan-user", "lan-pass"));
        var inbound = new SingBoxConfigBuilder().Build(profile, secrets, ctx)["inbounds"]![0]!;
        Assert.Equal("0.0.0.0", (string?)inbound["listen"]);
        var user = Assert.Single(inbound["users"]!.AsArray());
        Assert.Equal("lan-user", (string?)user!["username"]);
        Assert.Equal("lan-pass", (string?)user!["password"]);
    }

    [Fact]
    public void Xray_loopback_without_auth_stays_default()
    {
        var (profile, secrets) = Vless();
        var inbounds = new XrayConfigBuilder().Build(profile, secrets, new BuildContext(new AppSettings()))["inbounds"]!.AsArray();
        Assert.Equal("127.0.0.1", (string?)inbounds[0]!["listen"]);
        Assert.Equal("127.0.0.1", (string?)inbounds[1]!["listen"]);
    }

    [Fact]
    public void Xray_lan_listens_everywhere_with_auth_on_both_inbounds()
    {
        var (profile, secrets) = Vless();
        var ctx = new BuildContext(LanSettings(), LocalProxyAuth: new LocalProxyAuth("lan-user", "lan-pass"));
        var inbounds = new XrayConfigBuilder().Build(profile, secrets, ctx)["inbounds"]!.AsArray();
        // SOCKS defaults to NO_AUTH; accounts alone would leave the port open.
        Assert.Equal("password", (string?)inbounds[0]!["settings"]!["auth"]);
        foreach (var inbound in inbounds)
        {
            Assert.Equal("0.0.0.0", (string?)inbound!["listen"]);
            var account = Assert.Single(inbound!["settings"]!["accounts"]!.AsArray());
            Assert.Equal("lan-user", (string?)account!["user"]);
            Assert.Equal("lan-pass", (string?)account!["pass"]);
        }
    }

    [Fact]
    public void Validator_requires_login_then_password_only_when_enabled()
    {
        Assert.Null(LocalProxyAuthValidator.Validate(new AppSettings(), null));
        Assert.Equal(LocalProxyAuthValidator.LoginRequiredKey,
            LocalProxyAuthValidator.Validate(new AppSettings { AllowLan = true }, "pw"));
        Assert.Equal(LocalProxyAuthValidator.PasswordRequiredKey,
            LocalProxyAuthValidator.Validate(new AppSettings { AllowLan = true, LocalProxyUsername = "u" }, null));
        Assert.Equal(LocalProxyAuthValidator.PasswordRequiredKey,
            LocalProxyAuthValidator.Validate(new AppSettings { AllowLan = true, LocalProxyUsername = "u" }, " "));
        Assert.Null(LocalProxyAuthValidator.Validate(
            new AppSettings { AllowLan = true, LocalProxyUsername = "u" }, "pw"));
    }

    [Fact]
    public void Conflicts_block_lan_without_login()
    {
        var detector = new ConflictDetector(new FakePorts());
        var profile = new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = "h", Port = 443 };
        var blocking = detector.CheckPrimary(profile, new AppSettings { AllowLan = true }, false, false)
            .Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        Assert.Contains(blocking, i => i.Code == "lan.login");
    }

    [Fact]
    public void Conflicts_block_lan_together_with_system_proxy()
    {
        var detector = new ConflictDetector(new FakePorts());
        var profile = new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = "h", Port = 443 };
        var settings = new AppSettings
        {
            Mode = ConnectionMode.LocalProxy,
            AllowLan = true,
            LocalProxyUsername = "lan-user",
            SetSystemProxy = true,
        };
        var blocking = detector.CheckPrimary(profile, settings, false, false)
            .Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        Assert.Contains(blocking, i => i.Code == "lan.sysproxy");
    }

    [Fact]
    public void Conflicts_allow_lan_with_system_proxy_in_tun_mode()
    {
        var detector = new ConflictDetector(new FakePorts());
        var profile = new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = "h", Port = 443 };
        var settings = new AppSettings
        {
            Mode = ConnectionMode.Tun,
            AllowLan = true,
            LocalProxyUsername = "lan-user",
            SetSystemProxy = true,
        };
        var blocking = detector.CheckPrimary(profile, settings, false, false)
            .Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        Assert.DoesNotContain(blocking, i => i.Code == "lan.sysproxy");
    }

    [Fact]
    public async Task Connect_refuses_lan_without_password_with_localized_key()
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1));
        var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("lan-cm")), new ConflictDetector(new FakePorts()), new LogHub(new LogRedactor()),
            os: OsFamily.Windows);
        var profile = new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = "example.com", Port = 443 };
        var error = await Assert.ThrowsAsync<EngineException>(
            () => manager.ConnectAsync(profile, LanSettings()));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LocalProxyAuthValidator.PasswordRequiredKey, error.LocalizationKey);
    }

    [Fact]
    public async Task Connect_blocks_lan_without_login_before_starting()
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1));
        var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("lan-cm")), new ConflictDetector(new FakePorts()), new LogHub(new LogRedactor()),
            os: OsFamily.Windows);
        var events = new List<SessionSnapshot>();
        manager.SessionChanged += (_, s) => { lock (events) { events.Add(s); } };
        var profile = new Profile { Name = "s", Protocol = ProtocolKind.Vless, Host = "example.com", Port = 443 };
        var error = await Assert.ThrowsAsync<EngineException>(
            () => manager.ConnectAsync(profile, new AppSettings { AllowLan = true }));
        Assert.Contains("login", error.Message, StringComparison.OrdinalIgnoreCase);
        var snapshot = Assert.Single(events, s => s.Status == ConnectionStatus.Error);
        Assert.Contains("login", snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tool_secrets_roundtrip_keeps_tg_and_lan_secrets_separate()
    {
        var first = new ProfileStore(Path.Combine(TestDirs.New("lan"), "profiles.json"), new InMemorySecretStore());
        await first.Secrets.SaveAsync(ConnectionManager.TgWsProxySecretId, new ProfileSecrets { Values = { ["secret"] = "tg-secret" } });
        await first.Secrets.SaveAsync(ConnectionManager.LocalProxySecretId, new ProfileSecrets { Values = { ["password"] = "lan-secret" } });
        var captured = await first.CaptureBackupAsync(new AppSettings());
        Assert.Equal("tg-secret", captured.ToolSecrets["secret"]);
        Assert.Equal("lan-secret", captured.ToolSecrets["localproxy-password"]);

        var second = new ProfileStore(Path.Combine(TestDirs.New("lan"), "profiles.json"), new InMemorySecretStore());
        await second.RestoreBackupAsync(captured, BackupImportMode.Replace);
        Assert.Equal("tg-secret", (await second.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Equal("lan-secret", (await second.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId))["password"]);
    }
}
