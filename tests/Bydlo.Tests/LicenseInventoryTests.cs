using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Bydlo.App.ViewModels;

namespace Bydlo.Tests;

public sealed class LicenseInventoryTests
{
    [Fact]
    public void Original_notice_files_match_the_catalog_and_pinned_direct_dependencies()
    {
        var root = RepositoryRoot();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "licenses", "manifest.json")));
        var files = catalog.RootElement.GetProperty("files").EnumerateArray().ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in files)
        {
            var name = entry.GetProperty("file").GetString()!;
            Assert.Equal(Path.GetFileName(name), name);
            Assert.True(names.Add(name), name);
            var bytes = File.ReadAllBytes(Path.Combine(root, "licenses", name));
            Assert.NotEmpty(bytes);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.StartsWith("https://", entry.GetProperty("source").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(Directory.GetFiles(Path.Combine(root, "licenses"), "*.txt").Length, names.Count);
        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var versions = props.Descendants("PackageVersion").ToDictionary(p => p.Attribute("Include")!.Value, p => p.Attribute("Version")!.Value);
        versions["Avalonia"] = props.Descendants("AvaloniaVersion").Single().Value;
        foreach (var name in new[] { "Avalonia", "CommunityToolkit.Mvvm", "YamlDotNet", "Google.Protobuf", "ZXing.Net", "Velopack", "System.Security.Cryptography.ProtectedData" })
        {
            Assert.Contains(files, entry => entry.GetProperty("component").GetString() == name && entry.GetProperty("version").GetString() == versions[name]);
        }
        Assert.Contains(files, entry => entry.GetProperty("component").GetString() == "gRPC for .NET" &&
            entry.GetProperty("version").GetString() == versions["Grpc.Net.Client"]);
        Assert.Contains(files, entry => entry.GetProperty("component").GetString() == ".NET" &&
            entry.GetProperty("version").GetString() == versions["System.ServiceProcess.ServiceController"]);
    }

    [Fact]
    public void About_includes_rendering_parsing_and_platform_dependencies_and_both_localizations()
    {
        foreach (var name in new[] { "SkiaSharp / HarfBuzzSharp", "ANGLE", "YamlDotNet", "MicroCom.Runtime", "Tmds.DBus.Protocol", ".NET", "Inter", "Material Design Icons" })
        { Assert.Contains(SettingsViewModel.Credits, credit => credit.Name == name); }
        var root = RepositoryRoot();
        foreach (var language in new[] { "en", "ru" })
        {
            using var strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", language + ".json")));
            Assert.Contains("GPL-3.0-or-later", strings.RootElement.GetProperty("about.licenses").GetString(), StringComparison.Ordinal);
            foreach (var key in new[] { "about.singbox", "about.skia", "about.angle", "about.microcom", "about.dbus", "about.yaml", "about.dotnet" })
            { Assert.False(string.IsNullOrWhiteSpace(strings.RootElement.GetProperty(key).GetString())); }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { if (File.Exists(Path.Combine(directory.FullName, "BYDLO.slnx"))) { return directory.FullName; } }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
