using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bydlo.Core.Backup;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Routing;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;
using Bydlo.Engines;
using Bydlo.Engines.Installation;

namespace Bydlo.Tests;

/// <summary>
/// Trimmed / Native AOT builds use source-generated JSON. These tests pin the wire format to what reflection-based
/// System.Text.Json produced before, so existing settings, profiles, backups and helper messages stay readable.
/// </summary>
public sealed class SourceGeneratedJsonTests
{
    private static readonly JsonSerializerOptions ReflectionDefault = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static readonly JsonSerializerOptions ReflectionStore = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSerializerOptions ReflectionRouting = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<RoutingCondition>(), new JsonStringEnumConverter<RoutingAction>() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static void AssertWireCompatible<T>(T value, JsonSerializerOptions reflection, JsonTypeInfo<T> generated)
    {
        var expected = JsonSerializer.Serialize(value, reflection);
        Assert.Equal(expected, JsonSerializer.Serialize(value, generated));
        var readBack = JsonSerializer.Deserialize(expected, generated);
        Assert.Equal(expected, JsonSerializer.Serialize(readBack, reflection));
    }

    private static Profile SampleProfile() => new()
    {
        Name = "Server", Protocol = ProtocolKind.Vless, Host = "example.org", Port = 443,
        Options = new(StringComparer.OrdinalIgnoreCase) { ["sni"] = "example.org", ["transport"] = "ws" },
        PreferredEngine = EngineKind.Xray, SubscriptionId = Guid.NewGuid(), IsFavorite = true,
    };

    private static AppSettings SampleSettings()
    {
        var settings = new AppSettings { Theme = ThemePreference.Oled };
        settings.EnginePaths["sing-box"] = "engines/sing-box";
        settings.Routing.OrderedRules.Add(new OrderedRoutingRule(RoutingCondition.Suffix, "example.org", RoutingAction.Direct));
        return settings;
    }

    [Fact]
    public void Settings_store_format_is_unchanged() =>
        AssertWireCompatible(SampleSettings(), ReflectionStore, JsonDefaults.Options.Info<AppSettings>());

    [Fact]
    public void Default_settings_round_trip_is_unchanged() =>
        AssertWireCompatible(SampleSettings(), ReflectionDefault, BydloJson.Core.Info<AppSettings>());

    [Fact]
    public void Profile_document_format_is_unchanged()
    {
        var profile = SampleProfile();
        var document = new ProfileDocument
        {
            Profiles = [profile],
            Subscriptions = [new Subscription { Name = "Provider", Upload = 1, Download = 2, Total = 3 }],
            Chains = [new ProxyChain { Name = "Chain", ProfileIds = [profile.Id], PreferredEngine = EngineKind.SingBox }],
            PendingSecretDeletes = ["gone"],
        };
        AssertWireCompatible(document, ReflectionStore, JsonDefaults.Options.Info<ProfileDocument>());
    }

    [Fact]
    public void Backup_format_is_unchanged()
    {
        var secrets = new ProfileSecrets();
        secrets.Values["uuid"] = "00000000-0000-0000-0000-000000000001";
        var data = new BackupData
        {
            Settings = SampleSettings(),
            Profiles = [new ImportedProfile(SampleProfile(), secrets)],
            Subscriptions = [new BackupSubscription(new Subscription { Name = "Provider" }, "https://example.org/sub")],
            Chains = [new ProxyChain { Name = "Chain" }],
        };
        AssertWireCompatible(data, ReflectionStore, JsonDefaults.Options.Info<BackupData>());
    }

    [Fact]
    public void Helper_messages_are_unchanged()
    {
        AssertWireCompatible(new HelperRequest
        {
            Operation = HelperOperation.Start, Engine = EngineKind.SingBox, Settings = SampleSettings(),
            ConnectionId = "abc", ExpectedBuild = new HelperBuild("0.3.1", "ABCDEF"),
        }, ReflectionDefault, BydloJson.Core.Info<HelperRequest>());
        AssertWireCompatible(new HelperReply
        {
            Build = new HelperBuild("0.3.1", "ABCDEF"), State = EngineState.Running, Error = "helper.error",
            Logs = ["line"], SelectedGroupProfileId = Guid.NewGuid(),
        }, ReflectionDefault, BydloJson.Core.Info<HelperReply>());
    }

    [Fact]
    public void Routing_rule_book_export_is_unchanged()
    {
        var book = new RoutingRuleBook([new OrderedRoutingRule(RoutingCondition.Domain, "example.org", RoutingAction.Block)], []);
        Assert.Equal(JsonSerializer.Serialize(book, ReflectionRouting), book.Export());
        Assert.Equal(book.Export(), RoutingRuleBook.Import(System.Text.Encoding.UTF8.GetBytes(book.Export())).Export());
    }

    [Fact]
    public void Embedded_engine_lock_parses_like_reflection()
    {
        using var stream = typeof(EngineLock).Assembly.GetManifestResourceStream("engines.lock.json")!;
        var json = new StreamReader(stream).ReadToEnd();
        var expected = JsonSerializer.Serialize(JsonSerializer.Deserialize<EngineLock>(json, ReflectionDefault), ReflectionDefault);
        Assert.Equal(expected, JsonSerializer.Serialize(EngineLock.Parse(json), ReflectionDefault));
    }

    [Fact]
    public void Missing_bundled_executables_stay_empty()
    {
        var entry = EngineLock.Parse("""{"engines":[{"id":"x","version":"1","license":"MIT","upstream":"u","assets":{}}]}""").Engines.Single();
        Assert.NotNull(entry.BundledExecutables);
        Assert.Empty(entry.BundledExecutables);
    }

    [Fact]
    public void Amnezia_host_config_keeps_its_field_names() =>
        Assert.Equal("""{"Name":"awg0","MTU":1280,"UAPI":"private_key=x"}""",
            JsonSerializer.Serialize(new AmneziaHostConfig("awg0", 1280, "private_key=x"), EnginesJsonContext.Default.AmneziaHostConfig));

    [Fact]
    public void Store_defaults_match_a_fresh_install() =>
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<AppSettings>("{}", ReflectionStore), ReflectionStore),
            JsonSerializer.Serialize(JsonSerializer.Deserialize("{}", JsonDefaults.Options.Info<AppSettings>()), ReflectionStore));
}
