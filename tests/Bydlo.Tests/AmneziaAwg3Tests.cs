using System.Net;
using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.Amnezia;

namespace Bydlo.Tests;

public sealed class AmneziaAwg3Tests
{
    private static string HeaderKey => Convert.ToBase64String(Enumerable.Repeat((byte)123, 32).ToArray());
    private static string HeaderHex => string.Concat(Enumerable.Repeat("7b", 32));
    private static string Config(string option) => $"""
        [Interface]
        PrivateKey = {Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())}
        Address = 10.5.0.2/24
        S1 = 12
        S2 = 16
        S3 = 20
        S4 = 24
        {option}
        [Peer]
        PublicKey = {Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray())}
        AllowedIPs = 0.0.0.0/0
        Endpoint = 203.0.113.20:51820
        """;

    public static TheoryData<string, string, string> Options => new()
    {
        { "HeaderProtectionKey", HeaderKey, "header_protection_key=" + HeaderHex },
        { "ContentPaddingAddition", "16-64", "content_padding_addition=16-64" },
        { "RekeyAfterTime", "120-180", "rekey_after_time=120-180" },
        { "RekeyTimeout", "5", "rekey_timeout=5" },
        { "RejectAfterTime", "180-240", "reject_after_time=180-240" },
        { "KeepaliveTimeout", "10-15", "keepalive_timeout=10-15" },
        { "MaxHandshakeAttempts", "5-20", "max_handshake_attempts=5-20" },
        { "RandomTrailers", "ON", "random_trailers=true" },
        { "DisableCookies", "0", "disable_cookies=false" },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public async Task Awg3_options_are_recognized_and_mapped_case_insensitively(string key, string value, string uapi)
    {
        // Remove all legacy AWG keys: each new key alone must select AmneziaWG.
        var raw = Config(key.ToLowerInvariant() + " = " + value);
        var importOnly = string.Join('\n', raw.Split('\n').Where(line => !line.TrimStart().StartsWith('S')));
        var result = new ProfileImporter().ImportText(importOnly);
        Assert.Empty(result.Errors);
        var imported = Assert.Single(result.Profiles);
        Assert.Equal(ProtocolKind.AmneziaWG, imported.Profile.Protocol);
        if (key == "HeaderProtectionKey")
        {
            Assert.Equal(value, imported.Secrets["headerProtectionKey"]);
            Assert.DoesNotContain(value, JsonSerializer.Serialize(imported.Profile), StringComparison.Ordinal);
            Assert.False(imported.Profile.Options.ContainsKey("awg.HeaderProtectionKey"));
        }
        else { Assert.Equal(value, imported.Profile.Options["awg." + key]); }
        Assert.Equal(importOnly, ProfileExporter.ExportConfig(imported.Profile, imported.Secrets));
        var config = await ParseAsync(raw);
        Assert.Contains(uapi, config.Uapi.Split('\n').Select(line => line.TrimEnd('\r')));
        Assert.Contains("private_key=" + string.Concat(Enumerable.Repeat("01", 32)), config.Uapi, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ContentPaddingAddition", "0")]
    [InlineData("ContentPaddingAddition", "4294967295")]
    [InlineData("RekeyAfterTime", "0-4294967295")]
    [InlineData("RekeyTimeout", "5-5")]
    [InlineData("RejectAfterTime", "180")]
    [InlineData("KeepaliveTimeout", "0-0")]
    [InlineData("MaxHandshakeAttempts", "1-4294967295")]
    public async Task Numeric_boundaries_are_preserved(string key, string value)
    {
        var config = await ParseAsync(Config(key + " = " + value));
        Assert.Contains("=" + value + "\n", config.Uapi.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRUE", "true")]
    [InlineData("true", "true")]
    [InlineData("t", "true")]
    [InlineData("ON", "true")]
    [InlineData("1", "true")]
    [InlineData("false", "false")]
    [InlineData("F", "false")]
    [InlineData("off", "false")]
    [InlineData("0", "false")]
    public async Task Boolean_config_spellings_map_to_uapi(string value, string expected)
    {
        var config = await ParseAsync(Config($"RandomTrailers = {value}\nDisableCookies = {value}"));
        Assert.Contains("random_trailers=" + expected, config.Uapi, StringComparison.Ordinal);
        Assert.Contains("disable_cookies=" + expected, config.Uapi, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InvalidRanges =>
        from key in new[] { "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts" }
        from value in new[] { "", "-1", "4294967296", "2-1", "1-2-3", "1-", "+1", "1, 2", "x", "1 - 2" }
        select new object[] { key, value };

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public async Task Invalid_numeric_options_fail_before_endpoint_resolution(string key, string value) =>
        await Assert.ThrowsAsync<FormatException>(() => AmneziaConfig.ParseAsync(
            Config(key + " = " + value).Replace("203.0.113.20", "must-not-resolve.invalid", StringComparison.Ordinal),
            (_, _) => throw new InvalidOperationException("Must fail before DNS"), default));

    [Theory]
    [InlineData("HeaderProtectionKey = invalid")]
    [InlineData("HeaderProtectionKey = AQID")]
    [InlineData("RandomTrailers = 2")]
    [InlineData("DisableCookies = maybe")]
    [InlineData("PostUp = command")]
    [InlineData("PreDown = command")]
    [InlineData("Table = off")]
    [InlineData("SaveConfig = true")]
    [InlineData("UnknownAwgKey = 1")]
    public async Task Invalid_keys_flags_and_unsafe_directives_stay_rejected(string option) =>
        await Assert.ThrowsAsync<FormatException>(() => ParseAsync(Config(option)));

    [Theory]
    [InlineData("")]
    [InlineData("11")]
    [InlineData("65536")]
    public async Task Nonzero_header_key_requires_nonce_space_in_every_padding(string padding)
    {
        foreach (var key in new[] { "S1", "S2", "S3", "S4" })
        {
            var raw = Config("HeaderProtectionKey = " + HeaderKey);
            var lines = raw.Split('\n').Select(line => line.TrimStart().StartsWith(key + " =", StringComparison.Ordinal) ? (padding.Length == 0 ? "" : key + " = " + padding) : line);
            await Assert.ThrowsAsync<FormatException>(() => ParseAsync(string.Join('\n', lines)));
        }
    }

    [Fact]
    public async Task Disabled_header_protection_does_not_require_padding()
    {
        var raw = Config("HeaderProtectionKey = " + Convert.ToBase64String(new byte[32]));
        raw = string.Join('\n', raw.Split('\n').Where(line => !line.TrimStart().StartsWith('S')));
        var config = await ParseAsync(raw);
        Assert.Contains("header_protection_key=" + new string('0', 64), config.Uapi, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_only_profile_registers_base64_and_hex_header_keys_for_redaction()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "BYDLO_AMNEZIA_READY" } };
        var redactor = new LogRedactor();
        await using var engine = new AmneziaEngine(runner, redactor, new FakeLocator(new(OsFamily.Linux, CpuArch.X64)),
            new FakePrivileges(true), new NoNetwork());
        await engine.StartAsync(new()
        {
            Settings = new(), WorkDirectory = TestDirs.New("awg3-redaction"),
            Secrets = new() { Values = { [Keys.RawConfig] = Config("HeaderProtectionKey = " + HeaderKey) } },
        });
        var log = redactor.Redact($"base64 {HeaderKey} hex {HeaderHex}");
        Assert.DoesNotContain(HeaderKey, log, StringComparison.Ordinal);
        Assert.DoesNotContain(HeaderHex, log, StringComparison.Ordinal);
        Assert.Empty(runner.Completed);
    }

    private static Task<AmneziaConfig> ParseAsync(string raw) => AmneziaConfig.ParseAsync(raw,
        (_, _) => throw new InvalidOperationException("Literal endpoints must not resolve DNS"), default);

    private sealed class NoNetwork : IAmneziaNetwork
    {
        public bool HasPendingRecovery => false;
        public Task PrepareAsync(IReadOnlyList<string> endpoints, CancellationToken ct) => Task.CompletedTask;
        public Task ApplyAsync(IReadOnlyList<string> addresses, IReadOnlyList<string> dns, IReadOnlyList<string> routes, int mtu, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
