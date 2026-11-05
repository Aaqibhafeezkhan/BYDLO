using System.Text.Json.Nodes;
using Bydlo.Core.Backup;
using Bydlo.Core.Config;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Issue 338: XHTTP transport credentials must live in the secret store, never in profiles.json.</summary>
public sealed class XhttpSecretsTests
{
    private const string Token = "audit-synthetic-bearer";
    private const string Cookie = "audit-synthetic-cookie";

    private static string Link(string extraJson) =>
        "vless://11111111-2222-3333-4444-555555555555@example.test:443?type=xhttp&path=%2Fx&mode=auto&extra="
        + Uri.EscapeDataString(extraJson) + "#X";

    private static string ExtraWithSecrets(string token = Token, string cookie = Cookie) =>
        "{\"xPaddingBytes\":\"100-1000\",\"headers\":{\"Authorization\":\"Bearer " + token + "\",\"Cookie\":\"" + cookie + "\"}}";

    private static ImportedProfile Import(string text)
    {
        var r = new ProfileImporter().ImportText(text);
        Assert.Empty(r.Errors);
        return Assert.Single(r.Profiles);
    }

    private static JsonObject Xray(ImportedProfile p, AppSettings? s = null) =>
        new XrayConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(s ?? new AppSettings()));

    private static string OptionsText(Profile profile) =>
        string.Join("\n", profile.Options.Select(kv => kv.Key + "=" + kv.Value));

    [Fact]
    public void Import_moves_bearer_headers_to_secrets()
    {
        var p = Import(Link(ExtraWithSecrets()));

        Assert.DoesNotContain(Token, OptionsText(p.Profile), StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, OptionsText(p.Profile), StringComparison.Ordinal);
        Assert.NotNull(p.Secrets[Keys.XhttpHeaders]);
        Assert.Contains(Token, p.Secrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        Assert.Contains(Cookie, p.Secrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        // Tuning fields stay in options.
        Assert.Contains("xPaddingBytes", p.Profile.Option(Keys.XhttpExtra)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stored_profiles_json_never_contains_the_token()
    {
        var p = Import(Link(ExtraWithSecrets()));
        var dir = TestDirs.New("xhttp-secrets-store");
        var store = new ProfileStore(Path.Combine(dir, "profiles.json"), new InMemorySecretStore());
        await store.AddAsync([p]);

        var raw = File.ReadAllText(Path.Combine(dir, "profiles.json"));
        Assert.DoesNotContain(Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Xray_config_reassembles_secret_headers()
    {
        var p = Import(Link(ExtraWithSecrets()));
        var extra = Xray(p)["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"]!;
        Assert.Equal("Bearer " + Token, (string?)extra["headers"]!["Authorization"]);
        Assert.Equal(Cookie, (string?)extra["headers"]!["Cookie"]);
        Assert.Equal("100-1000", (string?)extra["xPaddingBytes"]);
    }

    [Fact]
    public void Export_link_roundtrips_headers_through_secrets()
    {
        var p = Import(Link(ExtraWithSecrets()));
        var exported = ProfileExporter.ExportLink(p.Profile, p.Secrets);
        Assert.Contains("extra=", exported, StringComparison.Ordinal);
        var second = Import(exported);
        Assert.DoesNotContain(Token, OptionsText(second.Profile), StringComparison.Ordinal);
        Assert.NotNull(second.Secrets[Keys.XhttpHeaders]);
        Assert.Contains(Token, second.Secrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var extra = Xray(second)["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"]!;
        Assert.Equal("Bearer " + Token, (string?)extra["headers"]!["Authorization"]);
    }

    [Fact]
    public void Public_extra_without_headers_stays_in_options()
    {
        var p = Import(Link("{\"xPaddingBytes\":\"100-1000\",\"noGRPCHeader\":true}"));
        Assert.Null(p.Secrets[Keys.XhttpHeaders]);
        Assert.Contains("xPaddingBytes", p.Profile.Option(Keys.XhttpExtra)!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"headers\":\"Bearer abc\"}")]
    [InlineData("{\"headers\":[\"Bearer abc\"]}")]
    [InlineData("{\"headers\":42}")]
    public void Non_object_headers_are_rejected(string extra)
    {
        var r = new ProfileImporter().ImportText(Link(extra));
        Assert.Empty(r.Profiles);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public async Task Load_migrates_old_options_and_baselines_to_secrets()
    {
        var dir = TestDirs.New("xhttp-migrate");
        var path = Path.Combine(dir, "profiles.json");
        var profile = new Profile
        {
            Name = "old",
            Protocol = ProtocolKind.Vless,
            Host = "example.test",
            Port = 443,
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Keys.Transport] = "xhttp",
                [Keys.XhttpExtra] = ExtraWithSecrets(),
            },
        };
        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            Name = "sub",
            SourceOptions = new Dictionary<Guid, Dictionary<string, string>>
            {
                [profile.Id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Keys.Transport] = "xhttp",
                    [Keys.XhttpExtra] = ExtraWithSecrets(),
                },
            },
        };
        await JsonDefaults.WriteAtomicAsync(path,
            new ProfileDocument { Version = 1, Profiles = [profile], Subscriptions = [sub], Chains = [] },
            CancellationToken.None);
        Assert.Contains(Token, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        var secrets = new InMemorySecretStore();

        var store = new ProfileStore(path, secrets);
        await store.LoadAsync();

        var migrated = Assert.Single(store.Profiles);
        Assert.DoesNotContain(Token, OptionsText(migrated), StringComparison.Ordinal);
        var migratedSecrets = await secrets.LoadAsync(profile.Id.ToString());
        Assert.NotNull(migratedSecrets[Keys.XhttpHeaders]);
        Assert.Contains(Token, migratedSecrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var baseline = Assert.Single(Assert.Single(store.Subscriptions).SourceOptions!.Values);
        Assert.DoesNotContain(Token, string.Join("\n", baseline.Select(kv => kv.Key + "=" + kv.Value)), StringComparison.Ordinal);
        var raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_splits_old_backup_before_persisting()
    {
        var dir = TestDirs.New("xhttp-restore");
        var store = new ProfileStore(Path.Combine(dir, "profiles.json"), new InMemorySecretStore());
        await store.LoadAsync();
        var profile = new Profile
        {
            Name = "old",
            Protocol = ProtocolKind.Vless,
            Host = "example.test",
            Port = 443,
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Keys.Transport] = "xhttp",
                [Keys.XhttpExtra] = ExtraWithSecrets(),
            },
        };
        var subId = Guid.NewGuid();
        profile.SubscriptionId = subId;
        var data = new BackupData
        {
            Settings = new AppSettings(),
            Profiles = [new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.Uuid] = "11111111-2222-3333-4444-555555555555" } })],
            Subscriptions = [new BackupSubscription(
                new Subscription { Id = subId, Name = "sub", SourceOptions = new Dictionary<Guid, Dictionary<string, string>>
                {
                    [profile.Id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Keys.Transport] = "xhttp",
                        [Keys.XhttpExtra] = ExtraWithSecrets(),
                    },
                } }, "https://example.test/sub")],
        };

        await store.RestoreBackupAsync(data, BackupImportMode.Replace);

        var saved = Assert.Single(store.Profiles);
        Assert.DoesNotContain(Token, OptionsText(saved), StringComparison.Ordinal);
        var savedSecrets = await store.Secrets.LoadAsync(saved.Id.ToString());
        Assert.NotNull(savedSecrets[Keys.XhttpHeaders]);
        Assert.Contains(Token, savedSecrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var raw = await File.ReadAllTextAsync(Path.Combine(dir, "profiles.json"));
        Assert.DoesNotContain(Token, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_normalizes_unsplit_incoming_and_matched_options()
    {
        var file = Path.Combine(TestDirs.New("xhttp-refresh"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var saved = Import(Link(ExtraWithSecrets()));
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [saved]);

        // Simulate an unsplit producer: headers back in options, out of secrets.
        var fresh = Import(Link(ExtraWithSecrets()));
        fresh.Profile.Options[Keys.XhttpExtra] = ExtraWithSecrets();
        fresh.Secrets.Values.Remove(Keys.XhttpHeaders);

        await store.ReplaceSubscriptionProfilesAsync(sub.Id, [fresh]);

        var raw = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Cookie, raw, StringComparison.Ordinal);
        var kept = Assert.Single(store.Profiles);
        Assert.DoesNotContain(Token, OptionsText(kept), StringComparison.Ordinal);
        var keptSecrets = await secrets.LoadAsync(kept.Id.ToString());
        Assert.NotNull(keptSecrets[Keys.XhttpHeaders]);
        Assert.Contains(Token, keptSecrets[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var baseline = Assert.Single(Assert.Single(store.Subscriptions).SourceOptions!.Values);
        Assert.DoesNotContain(Token, string.Join("\n", baseline.Select(kv => kv.Key + "=" + kv.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_start_registration_masks_each_header_value()
    {
        var redactor = new LogRedactor();
        foreach (var value in new ProfileSecrets
        {
            Values = { [Keys.XhttpHeaders] = "{\"Authorization\":\"Bearer " + Token + "\",\"Cookie\":\"" + Cookie + "\"}" },
        }.Values.Values)
        {
            redactor.RegisterSecret(value);
        }
        Assert.DoesNotContain(Cookie, redactor.Redact("Cookie: " + Cookie), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, redactor.Redact("Authorization: Bearer " + Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_without_secrets_restores_credentials_when_document_write_fails()
    {
        var dir = TestDirs.New("xhttp-update-rollback");
        var file = Path.Combine(dir, "profiles.json");
        var secrets = new FailAfterSaveSecrets();
        var store = new ProfileStore(file, secrets);
        var saved = Import(Link(ExtraWithSecrets()));
        await store.AddAsync([saved]);
        var id = saved.Profile.Id.ToString();
        var original = (await secrets.LoadAsync(id))[Keys.XhttpHeaders];
        Assert.NotNull(original);

        // User edit rotates the token straight in options, without secrets.
        var edited = Assert.Single(store.Profiles);
        edited.Options[Keys.XhttpExtra] = ExtraWithSecrets("rotated-token", "rotated-cookie");
        Directory.CreateDirectory(file + ".tmp");
        try
        {
            var error = await Record.ExceptionAsync(() => store.UpdateAsync(edited));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        finally
        {
            Directory.Delete(file + ".tmp");
        }

        Assert.Equal(original, (await secrets.LoadAsync(id))[Keys.XhttpHeaders]);
        Assert.DoesNotContain("rotated-token", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

        // Retry converges: the rotated token moves to secrets, the file stays clean.
        // (A failed attempt strips the in-memory copy, so re-apply the edit.)
        var retry = Assert.Single(store.Profiles);
        retry.Options[Keys.XhttpExtra] = ExtraWithSecrets("rotated-token", "rotated-cookie");
        await store.UpdateAsync(retry);
        Assert.Contains("rotated-token",
            (await secrets.LoadAsync(id))[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var raw = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("rotated-token", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_moves_matched_headers_and_rolls_back_on_failure()
    {
        const string uuid = "33333333-4444-5555-6666-777777777777";
        const string rotated = "{\"xPaddingBytes\":\"100-1000\",\"headers\":{\"Authorization\":\"Bearer rotated-token\"}}";
        const string pub = "{\"xPaddingBytes\":\"100-1000\"}";
        var dir = TestDirs.New("xhttp-refresh-rollback");
        var file = Path.Combine(dir, "profiles.json");
        var secrets = new FailAfterSaveSecrets();
        var profileId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        await JsonDefaults.WriteAtomicAsync(file,
            new ProfileDocument
            {
                Version = 1,
                Profiles = [new Profile
                {
                    Id = profileId,
                    Name = "dirty",
                    Protocol = ProtocolKind.Vless,
                    Host = "example.test",
                    Port = 443,
                    SubscriptionId = subId,
                    Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Keys.Transport] = "xhttp",
                        [Keys.XhttpExtra] = rotated,
                    },
                }],
                Subscriptions = [new Subscription
                {
                    Id = subId,
                    Name = "Sub",
                    SourceOptions = new Dictionary<Guid, Dictionary<string, string>>
                    {
                        [profileId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [Keys.Transport] = "xhttp",
                            [Keys.XhttpExtra] = pub,
                        },
                    },
                }],
                Chains = [],
            },
            CancellationToken.None);
        await secrets.SaveAsync(profileId.ToString(), new ProfileSecrets { Values = { [Keys.Uuid] = uuid } });
        var initialDocument = await File.ReadAllTextAsync(file);

        // Keep the residue past Load: the migration cannot save, so it retries later.
        secrets.FailBeforeSaveIds.Add(profileId.ToString());
        var store = new ProfileStore(file, secrets);
        await store.LoadAsync();
        secrets.FailBeforeSaveIds.Clear();
        Assert.Contains("rotated-token", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

        ImportedProfile CleanIncoming() => new(
            new Profile
            {
                Id = Guid.NewGuid(),
                Name = "dirty",
                Protocol = ProtocolKind.Vless,
                Host = "example.test",
                Port = 443,
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Keys.Transport] = "xhttp",
                    [Keys.XhttpExtra] = pub,
                },
            },
            new ProfileSecrets { Values = { [Keys.Uuid] = uuid } });

        // Matched secret saves fail: the update must roll back.
        secrets.FailNextSaves = 1;
        var error = await Record.ExceptionAsync(() => store.ReplaceSubscriptionProfilesAsync(subId, [CleanIncoming()]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        secrets.FailSaveId = null;
        Assert.Null((await secrets.LoadAsync(profileId.ToString()))[Keys.XhttpHeaders]);
        Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));

        // Retry converges: headers move to secrets and the document is rewritten clean.
        await store.ReplaceSubscriptionProfilesAsync(subId, [CleanIncoming()]);
        Assert.Equal(profileId, Assert.Single(store.Profiles).Id);
        Assert.Contains("rotated-token",
            (await secrets.LoadAsync(profileId.ToString()))[Keys.XhttpHeaders]!, StringComparison.Ordinal);
        var raw = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("rotated-token", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_and_malformed_secrets_keep_working()
    {
        var redactor = new LogRedactor();
        redactor.RegisterSecret("plain-credential");
        redactor.RegisterSecret("not json at all {{{");
        redactor.RegisterSecret("{\"unclosed\": ");
        Assert.Equal("***", redactor.Redact("plain-credential"));
        Assert.Equal("ok", redactor.Redact("ok"));
    }

    private sealed class FailAfterSaveSecrets : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public string? FailSaveId { get; set; }
        public int FailNextSaves { get; set; }
        public HashSet<string> FailBeforeSaveIds { get; } = new(StringComparer.Ordinal);
        public string ProtectionDescription => _inner.ProtectionDescription;
        public async Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
        {
            if (FailBeforeSaveIds.Contains(id)) { throw new IOException("Simulated failure before writing."); }
            await _inner.SaveAsync(id, secrets, ct);
            if (FailNextSaves > 0)
            {
                FailNextSaves--;
                throw new IOException("Simulated failure after writing.");
            }
            if (id == FailSaveId) { throw new IOException("Simulated failure after writing."); }
        }
        public Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) => _inner.LoadAsync(id, ct);
        public Task DeleteAsync(string id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
    }
}
