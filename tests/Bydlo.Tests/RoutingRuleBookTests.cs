using System.Text;
using System.Security.Cryptography;
using Bydlo.Core.Backup;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Routing;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;
using Bydlo.Engines.Xray;

namespace Bydlo.Tests;

public sealed class RoutingRuleBookTests
{
    private static readonly RuleSetSource Source = new("custom", new Uri("https://example.test/pinned/rules.srs"), new string('a', 64), 1024);

    [Fact]
    public void Round_trip_preserves_order_actions_and_sources()
    {
        var book = new RoutingRuleBook([
            new(RoutingCondition.GeoSite, "custom", RoutingAction.Direct),
            new(RoutingCondition.Suffix, "example.test", RoutingAction.Block),
            new(RoutingCondition.Port, "443", RoutingAction.Proxy),
        ], [Source]);
        var imported = RoutingRuleBook.Import(Encoding.UTF8.GetBytes(book.Export()));
        Assert.Equal(book.Rules, imported.Rules);
        Assert.Equal(book.Sources, imported.Sources);
    }

    [Fact]
    public void Accepts_legacy_rules_only_arrays()
    {
        var book = RoutingRuleBook.Import("""[{"Condition":"Domain","Value":"example.test","Action":"Block"}]"""u8);
        Assert.Equal(new(RoutingCondition.Domain, "example.test", RoutingAction.Block), Assert.Single(book.Rules));
        Assert.Empty(book.Sources);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Rules\":[],\"Sources\":null}")]
    public void Rejects_incomplete_documents(string json) =>
        Assert.Throws<InvalidDataException>(() => RoutingRuleBook.Import(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData(RoutingCondition.Port, "0")]
    [InlineData(RoutingCondition.Port, "65536")]
    [InlineData(RoutingCondition.Port, "invalid")]
    [InlineData(RoutingCondition.Cidr, "192.0.2.0/33")]
    [InlineData(RoutingCondition.Domain, "bad\nvalue")]
    public void Rejects_invalid_rules_before_import_changes_anything(RoutingCondition condition, string value) =>
        Assert.Equal("routing.rules.invalid", Assert.Throws<EngineException>(() =>
            new RoutingRuleBook([new(condition, value, RoutingAction.Proxy)], []).Validate()).LocalizationKey);

    [Theory]
    [InlineData("http://example.test/rules")]
    [InlineData("https://user:pass@example.test/rules")]
    [InlineData("https://example.test/rules#fragment")]
    public void Rejects_unpinned_or_credentialed_urls(string url) =>
        Assert.Throws<InvalidDataException>(() => RoutingRuleBook.ValidateSource(Source with { Url = new Uri(url) }));

    [Fact]
    public void Rejects_duplicates_reserved_tags_bad_hashes_and_sizes()
    {
        Assert.Throws<InvalidDataException>(() => new RoutingRuleBook([], [Source, Source]).Validate());
        Assert.Throws<InvalidDataException>(() => new RoutingRuleBook([], [Source with { Tag = "geosite-ru" }]).Validate());
        foreach (var tag in new[] { "../geosite.dat", "", "bad/tag" })
        {
            Assert.Throws<InvalidDataException>(() => RoutingRuleBook.ValidateSource(Source with { Tag = tag }));
        }
        Assert.Throws<InvalidDataException>(() => RoutingRuleBook.ValidateSource(Source with { Sha256 = "bad" }));
        Assert.Throws<InvalidDataException>(() => RoutingRuleBook.ValidateSource(Source with { MaxBytes = 0 }));
        Assert.Throws<InvalidDataException>(() => RoutingRuleBook.ValidateSource(Source with { MaxBytes = 64 * 1024 * 1024 + 1 }));
    }

    [Fact]
    public void Source_resolution_includes_custom_dns_and_xray_private_dependency()
    {
        var geoSite = Source with { Tag = "geosite.dat" };
        var settings = new RoutingSettings
        {
            RuleSetSources = [Source, geoSite], RemoteDnsRuleSets = ["geoip-cn", "custom"],
            OrderedRules = [new(RoutingCondition.GeoSite, "cn", RoutingAction.Direct)],
        };
        Assert.Equal(Source, RoutingPresets.Find(settings, "custom"));
        Assert.Equal(geoSite, RoutingPresets.XraySource(settings, RoutingCondition.GeoSite));
        Assert.Equal(2, RoutingPresets.XraySources(settings).Count);
        var downloads = RoutingPresets.DownloadSources(settings);
        Assert.Equal(5, downloads.Count);
        Assert.Contains(downloads, source => source.Tag == "geosite-cn");
        Assert.Contains(downloads, source => source.Tag == "geoip.dat");
        Assert.Contains(downloads, source => source.Tag == "geoip-cn");
    }

    [Fact]
    public void Backup_merge_retains_current_source_pins()
    {
        var current = new AppSettings { Routing = { RuleSetSources = [Source] } };
        var incoming = new BackupData { Settings = new AppSettings
        {
            Routing = { RuleSetSources = [Source with { Sha256 = new string('b', 64) }, Source with { Tag = "another" }] },
        } };
        BackupArchive.Validate(incoming);
        var merged = BackupArchive.ImportSettings(incoming, current, BackupImportMode.Merge);
        Assert.Equal(Source, merged.Routing.RuleSetSources[0]);
        Assert.Equal(2, merged.Routing.RuleSetSources.Count);
        incoming.Settings.Routing.RuleSetSources = [null!];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(incoming));
    }

    [Fact]
    public async Task Unsupported_process_rules_report_not_implemented_without_starting_a_process()
    {
        var settings = new AppSettings { Routing = { OrderedRules = [new(RoutingCondition.Process, "browser", RoutingAction.Proxy)] } };
        var runner = new FakeProcessRunner();
        await using var singBox = new SingBoxEngine(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(false));
        Assert.Equal(AvailabilityStatus.NotImplemented, (await singBox.CheckAvailabilityAsync(settings)).Status);
        await using var xray = new XrayEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.MacOS, CpuArch.Arm64)));
        Assert.Equal(AvailabilityStatus.NotImplemented, (await xray.CheckAvailabilityAsync(settings)).Status);
        Assert.Null(runner.Last);
    }

    [Fact]
    public async Task Xray_rejects_srs_instead_of_silently_compiling_it_as_a_geosite_code()
    {
        var settings = new AppSettings { Routing =
        {
            RuleSetSources = [Source], OrderedRules = [new(RoutingCondition.GeoSite, Source.Tag, RoutingAction.Proxy)],
        } };
        await using var engine = new XrayEngine(new FakeProcessRunner(), new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        Assert.Equal(AvailabilityStatus.NotImplemented, (await engine.CheckAvailabilityAsync(settings)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xray_stages_only_verified_custom_geo_files(bool corrupt)
    {
        byte[] bytes = [1, 2, 3];
        var source = Source with { Tag = "geosite.dat", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        var settings = new AppSettings { Routing =
        {
            BypassPrivateNetworks = false, RuleSetSources = [source],
            OrderedRules = [new(RoutingCondition.GeoSite, "cn", RoutingAction.Direct)],
        } };
        var directory = TestDirs.New("xray-rule-cache");
        await File.WriteAllBytesAsync(RoutingPresets.CachedPath(directory, source), corrupt ? [9] : bytes);
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        await using var engine = new XrayEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        var request = new EngineStartRequest
        {
            Settings = settings, WorkDirectory = TestDirs.New("xray-rule-work"), RuleSetDirectory = directory,
            Profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "example.test", Port = 443 },
            Secrets = new ProfileSecrets { Values = { [Keys.Password] = "fake" } },
        };
        if (corrupt)
        {
            Assert.Equal("routing.preset.missing", (await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request))).LocalizationKey);
            Assert.Empty(runner.Started);
        }
        else
        {
            await engine.StartAsync(request);
            Assert.Equal(request.WorkDirectory, Assert.Single(runner.Started).Environment["XRAY_LOCATION_ASSET"]);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(request.WorkDirectory, "geosite.dat")));
            await engine.StopAsync();
        }
    }
}
