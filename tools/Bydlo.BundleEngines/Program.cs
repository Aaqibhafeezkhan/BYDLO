using Bydlo.Core.Models;
using Bydlo.Engines.Installation;
using Bydlo.Core.Security;
using Bydlo.BundleEngines;
using System.Text.Json;
using System.Security.Cryptography;

// Packaging only: never executes an engine, strategy or privileged helper.
if (args.Length != 3)
{
    throw new ArgumentException("Usage: Bydlo.BundleEngines <RID> <publish-directory> <repository-root>");
}

if (args[0] is not ("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-arm64"))
{
    throw new ArgumentException("Unsupported RID. macOS packages support Apple Silicon only.");
}

var parts = args[0].Split('-');
var os = parts[0] switch { "win" => OsFamily.Windows, "linux" => OsFamily.Linux, "osx" => OsFamily.MacOS, _ => throw new ArgumentException("Unsupported RID") };
var arch = parts[1] switch { "x64" => CpuArch.X64, "arm64" => CpuArch.Arm64, _ => throw new ArgumentException("Unsupported architecture") };
var platform = new PlatformInfo(os, arch);
var output = Path.GetFullPath(args[1]);
var repo = Path.GetFullPath(args[2]);
var engineLock = EngineLock.LoadEmbedded();
var id = os == OsFamily.Windows ? "zapret" : "zapret-native";
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var staging = Path.Combine(repo, ".tmp", "bundle-" + args[0] + "-" + Guid.NewGuid().ToString("N"));
var installer = new EngineInstaller(http, engineLock, staging);
try
{
    foreach (var engineId in new[] { "sing-box", "xray", id })
    {
        if (!installer.HasAsset(engineId, platform))
        {
            // ARM64 Windows has no compatible pinned kernel driver. Do not package x64 as ARM64.
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "ZAPRET-UNAVAILABLE.txt"),
                "Zapret is unavailable on Windows ARM64: the pinned Flowseal release contains an x64 WinDivert driver only.\n");
            continue;
        }

        var executable = await installer.InstallAsync(engineId, platform);
        var entry = engineLock.Find(engineId)!;
        var source = installer.InstallDirectory(engineId, entry.Version);
        var destination = Path.Combine(output, "engines", engineId, entry.Version);
        if (engineId == "zapret")
        {
            ZapretHostList.Apply(executable, Path.Combine(repo, "engines", "bydlo-zapret-list.txt"));
        }
        if (os == OsFamily.Windows || engineId != "zapret-native")
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        else
        {
            // Keep only the selected executable, not unrelated platforms' binaries or install scripts.
            var target = Path.Combine(destination, Path.GetRelativePath(source, executable));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(executable, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            var targetExecutable = Path.Combine(destination, Path.GetRelativePath(source, executable));
            File.SetUnixFileMode(targetExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        Console.WriteLine($"Bundled SHA-256 verified {engineId} {entry.Version} for {platform.Rid}.");
    }

    var licenses = Path.Combine(output, "engines", "licenses");
    Directory.CreateDirectory(licenses);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "engines", "licenses")))
    {
        File.Copy(file, Path.Combine(licenses, Path.GetFileName(file)), overwrite: true);
    }

    // Package corresponding sources on the cross-build host too.
    using var inputs = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "engines", "bundle-inputs.lock.json")));
    foreach (var sourceId in new[] { "sing-box-source", "xray-source" })
    {
        var asset = inputs.RootElement.GetProperty(sourceId);
        var archive = Path.Combine(staging, sourceId + ".tar.gz");
        await using (var sourceStream = await http.GetStreamAsync(asset.GetProperty("url").GetString()!))
        await using (var targetStream = File.Create(archive))
        {
            await sourceStream.CopyToAsync(targetStream);
        }
        await IntegrityVerifier.VerifyFileAsync(archive, asset.GetProperty("sha256").GetString()!);
        var targetDirectory = Path.Combine(output, "engines", "sources");
        Directory.CreateDirectory(targetDirectory);
        File.Copy(archive, Path.Combine(targetDirectory, sourceId + ".tar.gz"), overwrite: true);
    }

    // Corresponding source is distributed alongside LGPL libraries, never just an unversioned link.
    using var sources = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "engines", "zapret-sources.lock.json")));
    if (sources.RootElement.TryGetProperty(platform.Rid, out var sourceAssets))
    {
        foreach (var asset in sourceAssets.EnumerateArray())
        {
            var archive = Path.Combine(staging, asset.GetProperty("fileName").GetString()!);
            await using (var sourceStream = await http.GetStreamAsync(asset.GetProperty("url").GetString()!))
            await using (var targetStream = File.Create(archive))
            {
                await sourceStream.CopyToAsync(targetStream);
            }

            await IntegrityVerifier.VerifyFileAsync(archive, asset.GetProperty("sha256").GetString()!);
            var targetDirectory = Path.Combine(output, "engines", "sources");
            Directory.CreateDirectory(targetDirectory);
            File.Copy(archive, Path.Combine(targetDirectory, Path.GetFileName(archive)), overwrite: true);
        }
    }
    var manifest = new List<string>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(output, "engines"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        manifest.Add($"{hash}  {Path.GetRelativePath(output, file).Replace('\\', '/')}");
    }
    await File.WriteAllLinesAsync(Path.Combine(output, "ENGINES-SHA256SUMS.txt"), manifest);
}
finally
{
    if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
}
