using System.IO.Compression;
using System.Text;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Installation;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Platform;

namespace Bydlo.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("vless://11111111-2222-3333-4444-555555555555@host:443", "11111111")]
    [InlineData("socks5://user:hunter2@host:1080", "hunter2")]
    [InlineData("password=hunter2 next", "hunter2")]
    [InlineData("  Secret:        0123456789abcdef0123456789abcdef", "0123456789abcdef")]
    [InlineData("PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=", "yAnz5TF")]
    [InlineData("tg://proxy?server=127.0.0.1&port=1443&secret=dd0123456789abcdef0123456789abcdef", "0123456789abcdef")]
    public void Redactor_masks_credentials(string input, string leaked)
    {
        var redacted = new LogRedactor().Redact(input);
        Assert.DoesNotContain(leaked, redacted, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_masks_registered_secrets_and_keeps_normal_text()
    {
        var r = new LogRedactor();
        r.RegisterSecret("my-very-own-token");
        Assert.Equal("started on 127.0.0.1:2080 with ***", r.Redact("started on 127.0.0.1:2080 with my-very-own-token"));
    }

    [Fact]
    public async Task Integrity_verifier_accepts_match_and_rejects_mismatch()
    {
        var dir = TestDirs.New("integrity");
        var file = Path.Combine(dir, "a.bin");
        await File.WriteAllTextAsync(file, "hello");
        const string sha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        await IntegrityVerifier.VerifyFileAsync(file, "sha256:" + sha.ToUpperInvariant());
        await Assert.ThrowsAsync<IntegrityException>(() => IntegrityVerifier.VerifyFileAsync(file, new string('0', 64)));
        Assert.Throws<FormatException>(() => IntegrityVerifier.NormalizeDigest("abc"));
    }

    [Fact]
    public void Engine_lock_is_well_formed()
    {
        var engineLock = EngineLock.LoadEmbedded();
        Assert.NotNull(engineLock.Find("sing-box"));
        Assert.NotNull(engineLock.Find("xray"));
        Assert.NotNull(engineLock.Find("zapret"));
        Assert.Equal(TgWsProxyEngine.UpstreamVersion, engineLock.Find("tg-ws-proxy")?.Version);
        foreach (var asset in engineLock.Engines.SelectMany(e => e.Assets.Values))
        {
            Assert.StartsWith("https://github.com/", asset.Url, StringComparison.Ordinal);
            Assert.Equal(64, IntegrityVerifier.NormalizeDigest(asset.Sha256).Length);
        }
    }

    [Fact]
    public async Task Installer_blocks_zip_slip()
    {
        var dir = TestDirs.New("zipslip");
        var zipPath = Path.Combine(dir, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("../../escape.txt");
            await using var s = e.Open();
            await s.WriteAsync(Encoding.UTF8.GetBytes("x"));
        }

        var target = Path.Combine(dir, "out");
        Directory.CreateDirectory(target);
        await Assert.ThrowsAsync<InvalidDataException>(() => EngineInstaller.ExtractAsync(zipPath, target, CancellationToken.None));
    }

    [Fact]
    public void Safety_lock_refuses_network_processes()
    {
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var runner = new SystemProcessRunner();
            Assert.Throws<NetworkSafetyException>(() => runner.Start(new ProcessSpec { FileName = "does-not-matter", DisplayName = "sing-box" }));
        }
        finally
        {
            NetworkSafetyGuard.OverrideForTests(null);
        }
    }

    [Fact]
    public async Task Aes_secret_store_round_trip_and_ciphertext_hides_values()
    {
        var dir = TestDirs.New("secrets");
        var store = new AesFileSecretStore(dir);
        await store.SaveAsync("p1", new ProfileSecrets { Values = { ["password"] = "hunter2-very-secret" } });
        Assert.Equal("hunter2-very-secret", (await store.LoadAsync("p1"))["password"]);
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
        {
            Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(f)), StringComparison.Ordinal);
        }

        await store.DeleteAsync("p1");
        Assert.Null((await store.LoadAsync("p1"))["password"]);
    }

    [Fact]
    public async Task Dpapi_secret_store_round_trip_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiSecretStore(TestDirs.New("dpapi"));
        await store.SaveAsync("p", new ProfileSecrets { Values = { ["uuid"] = "abc-123" } });
        Assert.Equal("abc-123", (await store.LoadAsync("p"))["uuid"]);
    }
}
