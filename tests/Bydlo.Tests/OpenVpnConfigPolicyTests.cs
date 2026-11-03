using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.OpenVpn;

namespace Bydlo.Tests;

public sealed class OpenVpnConfigPolicyTests
{
    private const string Client = "client\ndev tun\nremote vpn.example.org 1194\n";

    [Theory]
    [InlineData("up external-handler")]
    [InlineData("down external-handler")]
    [InlineData("route-up external-handler")]
    [InlineData("route-pre-down external-handler")]
    [InlineData("ipchange external-handler")]
    [InlineData("tls-verify external-handler")]
    [InlineData("auth-user-pass-verify external-handler via-file")]
    [InlineData("client-connect external-handler")]
    [InlineData("client-disconnect external-handler")]
    [InlineData("learn-address external-handler")]
    [InlineData("iproute external-handler")]
    [InlineData("plugin external-library")]
    [InlineData("engine external-library")]
    [InlineData("providers external-library")]
    [InlineData("pkcs11-providers external-library")]
    [InlineData("script-security 2")]
    [InlineData("config nested.ovpn")]
    [InlineData("--config nested.ovpn")]
    [InlineData("'config' nested.ovpn")]
    [InlineData("\"config\" nested.ovpn")]
    [InlineData("con\\fig nested.ovpn")]
    [InlineData("<connection>\nremote vpn.example.org\nconfig nested.ovpn\n</connection>")]
    [InlineData("<key>\n</key> trailing\nup external-handler\n</key>")]
    [InlineData("ca external.pem")]
    [InlineData("auth-user-pass external.txt")]
    [InlineData("log external.txt")]
    [InlineData("management 127.0.0.1 1234")]
    public async Task Unsafe_configs_are_rejected_by_import_helper_and_direct_launch(string directive)
    {
        var config = Client + directive;
        var imported = new ProfileImporter().ImportText(config);
        Assert.Empty(imported.Profiles);
        Assert.Equal("import.openvpn.unsafe", Assert.Single(imported.Errors));
        var request = new EngineStartRequest
        {
            Profile = new Profile { Protocol = ProtocolKind.OpenVpn },
            Secrets = new ProfileSecrets { Values = { [Keys.RawConfig] = config } },
            Settings = new(), WorkDirectory = TestDirs.New("unsafe-openvpn"),
        };
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(new HelperRequest
        {
            Engine = EngineKind.OpenVpn, Operation = HelperOperation.Start, Start = request, Settings = new(),
        }));

        // Simulate an existing profile that predates import validation. Never start a real engine.
        var runner = new FakeProcessRunner();
        await using var engine = new OpenVpnEngine(runner, new LogRedactor(),
            new FakeLocator(new(OsFamily.Linux, CpuArch.X64)), new FakePrivileges(true));
        var error = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request));
        Assert.Equal("import.openvpn.unsafe", error.LocalizationKey);
        Assert.Empty(runner.Started);
        Assert.Empty(runner.Completed);
        Assert.False(File.Exists(Path.Combine(request.WorkDirectory, "profile.ovpn")));
        Assert.False(File.Exists(Path.Combine(request.WorkDirectory, "auth.txt")));
    }

    [Fact]
    public async Task Ordinary_client_with_inline_keys_reaches_fake_runner_unchanged()
    {
        var config = Client + "dev tun # tunnel\nport 1194\nproto udp\nnobind\npersist-key\npersist-tun\nremote-cert-tls server\n"
            + "data-ciphers AES-256-GCM:AES-128-GCM\nauth SHA256\nverb 3\n"
            + "# up ignored-comment\n; config ignored-comment\n"
            + "<ca>\n-----BEGIN CERTIFICATE-----\nYWJj\n-----END CERTIFICATE-----\n</ca>\n"
            + "<key>\n-----BEGIN PRIVATE KEY-----\nYWJj\n-----END PRIVATE KEY-----\n</key>\n"
            + "<tls-crypt>\nYWJj\n</tls-crypt>\n";
        var imported = Assert.Single(new ProfileImporter().ImportText(config).Profiles);
        HelperRequestPolicy.ValidateOpenVpn(config);
        var runner = new FakeProcessRunner { StartupOutput = { "Initialization Sequence Completed" } };
        await using var engine = new OpenVpnEngine(runner, new LogRedactor(),
            new FakeLocator(new(OsFamily.Linux, CpuArch.X64)), new FakePrivileges(true));
        await engine.StartAsync(new()
        {
            Profile = imported.Profile, Secrets = imported.Secrets, Settings = new(),
            WorkDirectory = TestDirs.New("safe-openvpn"),
        });
        var spec = Assert.Single(runner.Started);
        var path = spec.Arguments[spec.Arguments.ToList().IndexOf("--config") + 1];
        Assert.Equal(config.TrimEnd(), (await File.ReadAllTextAsync(path)).TrimEnd());
        await engine.StopAsync();
        Assert.False(File.Exists(path));
    }
}
