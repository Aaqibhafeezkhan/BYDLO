using Bydlo.Core.Models;
using Bydlo.Engines.Installation;
using Bydlo.Core.Security;
using System.Text.Json;

// Packaging only: never executes an engine, strategy or privileged helper.
if (args.Length != 3)
{
    throw new ArgumentException("Usage: Bydlo.BundleEngines <RID> <publish-directory> <repository-root>");
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
if (!installer.HasAsset(id, platform))
{
    // ARM64 Windows has no compatible pinned kernel driver. Do not package x64 as ARM64.
    Directory.CreateDirectory(output);
    await File.WriteAllTextAsync(Path.Combine(output, "ZAPRET-UNAVAILABLE.txt"),
        "Zapret is unavailable on Windows ARM64: the pinned Flowseal release contains an x64 WinDivert driver only.\n");
    return;
}

try
{
    var executable = await installer.InstallAsync(id, platform);
    var entry = engineLock.Find(id)!;
    var source = installer.InstallDirectory(id, entry.Version);
    var destination = Path.Combine(output, "engines", id, entry.Version);
    if (os == OsFamily.Windows)
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

    var licenses = Path.Combine(output, "engines", "licenses");
    Directory.CreateDirectory(licenses);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "engines", "licenses")))
    {
        File.Copy(file, Path.Combine(licenses, Path.GetFileName(file)), overwrite: true);
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

    Console.WriteLine($"Bundled SHA-256 verified {id} {entry.Version} for {platform.Rid}.");
}
finally
{
    if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
}
