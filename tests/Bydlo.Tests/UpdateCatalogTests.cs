using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bydlo.App.Services;
using Bydlo.Core.Updates;

namespace Bydlo.Tests;

/// <summary>
/// Issue #411: the signed update catalog is the only source of update metadata. Every case uses
/// in-memory bytes or a fake <see cref="HttpMessageHandler"/>; no updater, engine or network call runs.
/// </summary>
public sealed class UpdateCatalogTests
{
    private const string Rid = "win-x64";
    private const string Channel = "stable";
    private const string Installed = "0.4.2";
    private const string Next = "0.4.3";
    private const long PackageSize = 4096;

    private static readonly string PackageHash = new('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri CatalogUrl = new("https://example.invalid/releases-catalog.json");

    [Fact]
    public void Signed_catalog_yields_the_trusted_entry()
    {
        using var key = new SigningKey("test-current");
        var trusted = Verifier(key).Verify(Envelope(key, PayloadJson()), Channel, Rid, Installed);
        Assert.Equal(Next, trusted.Version);
        Assert.Equal(Channel, trusted.Channel);
        Assert.Equal(Rid, trusted.Rid);
        Assert.Equal($"BydloDesktop-{Next}-{Rid}-full.nupkg", trusted.FileName);
        Assert.Equal(PackageSize, trusted.Size);
        Assert.Equal(PackageHash, trusted.Sha256);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var original = PayloadJson();
        var envelope = Envelope(key, original);
        // The classic attack: keep the signature, swap the metadata it was supposed to cover.
        var tampered = envelope.Replace(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(original)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(PayloadJson(version: "9.9.9"))),
            StringComparison.Ordinal);
        Assert.NotEqual(envelope, tampered);
        var error = Assert.Throws<UpdateTrustException>(() => Verifier(key).Verify(tampered, Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.InvalidSignature, error.Failure);
    }

    [Fact]
    public void Signature_from_another_key_with_the_same_id_is_rejected()
    {
        using var trusted = new SigningKey("test-current");
        using var attacker = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(trusted).Verify(Envelope(attacker, PayloadJson()), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.InvalidSignature, error.Failure);
    }

    [Fact]
    public void Unknown_key_is_rejected()
    {
        using var trusted = new SigningKey("test-current");
        using var other = new SigningKey("test-other");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(trusted).Verify(Envelope(other, PayloadJson()), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.UnknownKey, error.Failure);
    }

    [Fact]
    public void Revoked_key_is_rejected_even_with_a_valid_signature()
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier([key], [key.KeyId]).Verify(Envelope(key, PayloadJson()), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.RevokedKey, error.Failure);
    }

    [Fact]
    public void Rotation_to_the_next_key_is_accepted()
    {
        using var current = new SigningKey("test-current");
        using var next = new SigningKey("test-next");
        var trusted = Verifier([current, next], []).Verify(Envelope(next, PayloadJson()), Channel, Rid, Installed);
        Assert.Equal(Next, trusted.Version);
    }

    [Fact]
    public void Compiled_trust_root_never_accepts_a_foreign_key()
    {
        using var key = new SigningKey(UpdateTrustRoots.CurrentKeyId);
        var error = Assert.Throws<UpdateTrustException>(() =>
            new UpdateCatalogVerifier().Verify(Envelope(key, PayloadJson()), Channel, Rid, Installed));
        Assert.True(error.Failure is UpdateTrustFailure.UnknownKey or UpdateTrustFailure.InvalidSignature);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("android-arm64")]
    public void Wrong_or_unsupported_rid_is_rejected(string rid)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson()), Channel, rid, Installed));
        Assert.Equal(UpdateTrustFailure.UnsupportedRid, error.Failure);
    }

    [Fact]
    public void Wrong_channel_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(channel: "beta")), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.WrongChannel, error.Failure);
    }

    [Theory]
    [InlineData("0.4.2")]
    [InlineData("0.4.1")]
    public void Equal_or_older_version_is_rejected(string offered)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(version: offered)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.NotNewer, error.Failure);
    }

    [Fact]
    public void Expired_catalog_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(expiresAt: Now.AddMinutes(-11))), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.Expired, error.Failure);
    }

    [Fact]
    public void Small_clock_skew_is_tolerated()
    {
        using var key = new SigningKey("test-current");
        var trusted = Verifier(key).Verify(Envelope(key, PayloadJson(expiresAt: Now.AddMinutes(-5))), Channel, Rid, Installed);
        Assert.Equal(Next, trusted.Version);
    }

    [Fact]
    public void Catalog_issued_in_the_future_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(issuedAt: Now.AddHours(1))), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.NotYetValid, error.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"payload":"e30=","keyId":"test-current"}""")]
    [InlineData("""{"payload":"!!!","keyId":"test-current","signature":"e30="}""")]
    public void Malformed_envelope_is_rejected(string json)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() => Verifier(key).Verify(json, Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedEnvelope, error.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"issuedAt":"2026-03-01T00:00:00Z","expiresAt":"2026-04-01T00:00:00Z","channel":"stable","version":"0.4.3"}""")]
    public void Malformed_payload_is_rejected(string payload)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, payload), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedPayload, error.Failure);
    }

    [Fact]
    public void Unsupported_schema_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(schemaVersion: 2)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.UnsupportedSchema, error.Failure);
    }

    [Theory]
    [InlineData("BydloDesktop-0.4.3-win-x64-delta.nupkg")]
    [InlineData("../BydloDesktop-0.4.3-win-x64-full.nupkg")]
    public void Unexpected_package_name_is_rejected(string fileName)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(fileName: fileName)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedPayload, error.Failure);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Non_positive_size_is_rejected(long size)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(size: size)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedPayload, error.Failure);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Malformed_sha256_is_rejected(string sha256)
    {
        using var key = new SigningKey("test-current");
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(sha256: sha256)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedPayload, error.Failure);
    }

    [Fact]
    public void Duplicate_rid_entry_is_rejected()
    {
        using var key = new SigningKey("test-current");
        var duplicate = $$"""
        {"rid":"{{Rid}}","fileName":"BydloDesktop-{{Next}}-{{Rid}}-full.nupkg","size":{{PackageSize}},"sha256":"{{PackageHash}}"}
        """;
        var error = Assert.Throws<UpdateTrustException>(() =>
            Verifier(key).Verify(Envelope(key, PayloadJson(extraPackage: duplicate)), Channel, Rid, Installed));
        Assert.Equal(UpdateTrustFailure.MalformedPayload, error.Failure);
    }

    [Fact]
    public void Matching_asset_is_accepted()
    {
        using var key = new SigningKey("test-current");
        var trusted = Verifier(key).Verify(Envelope(key, PayloadJson()), Channel, Rid, Installed);
        trusted.EnsureMatches(Next, trusted.FileName, PackageSize, PackageHash.ToUpperInvariant());
    }

    [Fact]
    public void Asset_with_a_different_version_is_rejected() =>
        AssertMismatch(t => t.EnsureMatches("0.4.4", t.FileName, t.Size, t.Sha256));

    [Fact]
    public void Asset_with_a_different_file_name_is_rejected() =>
        AssertMismatch(t => t.EnsureMatches(t.Version, "other.nupkg", t.Size, t.Sha256));

    [Fact]
    public void Asset_with_a_different_size_is_rejected() =>
        AssertMismatch(t => t.EnsureMatches(t.Version, t.FileName, t.Size + 1, t.Sha256));

    [Fact]
    public void Asset_with_a_different_hash_is_rejected() =>
        AssertMismatch(t => t.EnsureMatches(t.Version, t.FileName, t.Size, new string('b', 64)));

    [Fact]
    public void Asset_without_a_hash_is_rejected() =>
        AssertMismatch(t => t.EnsureMatches(t.Version, t.FileName, t.Size, null));

    [Fact]
    public async Task Catalog_download_reads_the_envelope()
    {
        using var key = new SigningKey("test-current");
        var json = Envelope(key, PayloadJson());
        using var http = new HttpClient(new StubHandler(json));
        var body = await new UpdateCatalogClient(http, CatalogUrl).DownloadAsync(CancellationToken.None);
        Assert.Equal(json, body);
        Assert.Equal(Next, Verifier(key).Verify(body, Channel, Rid, Installed).Version);
    }

    [Fact]
    public async Task Oversized_catalog_is_rejected_from_the_content_length()
    {
        using var http = new HttpClient(new StubHandler(new string('x', UpdateCatalogClient.MaxBytes + 1)));
        var error = await Assert.ThrowsAsync<UpdateTrustException>(() =>
            new UpdateCatalogClient(http, CatalogUrl).DownloadAsync(CancellationToken.None));
        Assert.Equal(UpdateTrustFailure.CatalogTooLarge, error.Failure);
    }

    [Fact]
    public async Task Oversized_catalog_is_rejected_without_a_content_length()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', UpdateCatalogClient.MaxBytes + 1));
        using var http = new HttpClient(new StreamingHandler(body));
        var error = await Assert.ThrowsAsync<UpdateTrustException>(() =>
            new UpdateCatalogClient(http, CatalogUrl).DownloadAsync(CancellationToken.None));
        Assert.Equal(UpdateTrustFailure.CatalogTooLarge, error.Failure);
    }

    [Fact]
    public async Task Catalog_http_failure_is_reported()
    {
        using var http = new HttpClient(new StubHandler("", HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<UpdateTrustException>(() =>
            new UpdateCatalogClient(http, CatalogUrl).DownloadAsync(CancellationToken.None));
        Assert.Equal(UpdateTrustFailure.CatalogUnavailable, error.Failure);
    }

    [Fact]
    public async Task Catalog_timeout_is_reported_as_unavailable()
    {
        using var http = new HttpClient(new BlockingHandler());
        var error = await Assert.ThrowsAsync<UpdateTrustException>(() =>
            new UpdateCatalogClient(http, CatalogUrl, TimeSpan.FromMilliseconds(50)).DownloadAsync(CancellationToken.None));
        Assert.Equal(UpdateTrustFailure.CatalogUnavailable, error.Failure);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_reported_as_a_trust_failure()
    {
        using var http = new HttpClient(new BlockingHandler());
        using var cancellation = new CancellationTokenSource();
        var download = new UpdateCatalogClient(http, CatalogUrl).DownloadAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
    }

    [Fact]
    public void Every_refusal_has_an_english_and_russian_message()
    {
        var en = Strings("en");
        var ru = Strings("ru");
        foreach (var failure in Enum.GetValues<UpdateTrustFailure>())
        {
            var key = UpdateTrustMessages.Key(failure);
            Assert.True(en.ContainsKey(key), $"Missing en.json key '{key}' for {failure}.");
            Assert.True(ru.ContainsKey(key), $"Missing ru.json key '{key}' for {failure}.");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"Empty en.json value for '{key}'.");
            Assert.False(string.IsNullOrWhiteSpace(ru[key]), $"Empty ru.json value for '{key}'.");
        }
    }

    [Fact]
    public void Release_pipeline_signs_the_catalog_after_the_final_packages()
    {
        var root = RepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var stage = ci.IndexOf("stage-release.ps1 -InputDirectory dist -OutputDirectory release-staging", StringComparison.Ordinal);
        var sign = ci.IndexOf("scripts/sign-update-catalog.ps1", StringComparison.Ordinal);
        Assert.True(stage >= 0, "The package job must stage the final release set.");
        Assert.True(sign > stage, "The catalog must be signed after the final packages are staged.");
        Assert.Contains("secrets.BYDLO_UPDATE_SIGNING_KEY", ci, StringComparison.Ordinal);
        Assert.Contains("releases-catalog.json",
            File.ReadAllText(Path.Combine(root, "scripts", "stage-release.ps1")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "scripts", "new-update-signing-key.ps1")));
    }

    [Fact]
    public void Signing_script_refuses_to_publish_without_a_key()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "sign-update-catalog.ps1"));
        Assert.Contains("BYDLO_UPDATE_SIGNING_KEY is missing", script, StringComparison.Ordinal);
        Assert.Contains("refusing to publish an unsigned update catalog", script, StringComparison.Ordinal);
    }

    private static void AssertMismatch(Action<TrustedUpdate> check)
    {
        using var key = new SigningKey("test-current");
        var trusted = Verifier(key).Verify(Envelope(key, PayloadJson()), Channel, Rid, Installed);
        var error = Assert.Throws<UpdateTrustException>(() => check(trusted));
        Assert.Equal(UpdateTrustFailure.AssetMismatch, error.Failure);
    }

    private static UpdateCatalogVerifier Verifier(params SigningKey[] keys) => Verifier(keys, []);

    private static UpdateCatalogVerifier Verifier(SigningKey[] keys, string[] revoked) =>
        new(keys.ToDictionary(k => k.KeyId, k => k.Spki, StringComparer.Ordinal), revoked, new FixedClock(Now));

    private static string Envelope(SigningKey key, string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = key.Sign(bytes);
        return $$"""
        {"payload":"{{Convert.ToBase64String(bytes)}}","keyId":"{{key.KeyId}}","signature":"{{Convert.ToBase64String(signature)}}"}
        """;
    }

    private static string PayloadJson(
        string version = Next,
        string channel = Channel,
        string rid = Rid,
        string? fileName = null,
        long size = PackageSize,
        string? sha256 = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        int schemaVersion = 1,
        string? extraPackage = null)
    {
        var name = fileName ?? $"BydloDesktop-{version}-{rid}-full.nupkg";
        var hash = sha256 ?? PackageHash;
        var issued = (issuedAt ?? Now.AddDays(-1)).ToString("O", CultureInfo.InvariantCulture);
        var expires = (expiresAt ?? Now.AddDays(30)).ToString("O", CultureInfo.InvariantCulture);
        var entry = $$"""
        {"rid":"{{rid}}","fileName":"{{name}}","size":{{size}},"sha256":"{{hash}}"}
        """;
        var packages = extraPackage is null ? entry : entry + "," + extraPackage;
        return $$"""
        {"schemaVersion":{{schemaVersion}},"issuedAt":"{{issued}}","expiresAt":"{{expires}}","channel":"{{channel}}","version":"{{version}}","packages":[{{packages}}]}
        """;
    }

    private static Dictionary<string, string> Strings(string language) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Assets", "Strings", language + ".json")))!;

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class SigningKey : IDisposable
    {
        private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public SigningKey(string keyId) => KeyId = keyId;

        public string KeyId { get; }

        public string Spki => Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());

        public byte[] Sign(byte[] payload) => _ecdsa.SignData(payload, HashAlgorithmName.SHA256);

        public void Dispose() => _ecdsa.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Body without a Content-Length header, so the size limit must be enforced while reading.</summary>
    private sealed class StreamingHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(body) });
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Never answers, so only the client's own timeout or the caller's token can end the request.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
