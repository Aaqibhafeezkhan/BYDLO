using System.Diagnostics;
using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

// Synthetic local storage only. No engines, HTTP, or system network operations.
if (Environment.GetEnvironmentVariable("BYDLO_SAFE_DEV") != "1")
{ throw new InvalidOperationException("Load scripts/env.ps1 or scripts/env.sh first."); }
var count = args.Length > 0 ? int.Parse(args[0]) : 100;
var secretLength = args.Length > 1 ? int.Parse(args[1]) : 16_384;
var repeats = args.Length > 2 ? int.Parse(args[2]) : 5;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(secretLength);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeats);
var root = Path.Combine(Environment.GetEnvironmentVariable("BYDLO_ROOT")!, ".tmp", "backup-benchmark", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var store = new ProfileStore(Path.Combine(root, "profiles.json"), new InMemorySecretStore());
var data = new BackupData
{
    Profiles = Enumerable.Range(0, count).Select(i => new ImportedProfile(
        new Profile { Name = $"Synthetic {i}", Protocol = ProtocolKind.WireGuard },
        new ProfileSecrets { Values = { [Keys.RawConfig] = new string('k', secretLength) } })).ToList(),
};
const string password = "synthetic benchmark password";
var archive = BackupArchive.Encrypt(data, password);
using var session = BackupArchive.OpenImport(archive, password);
Console.WriteLine($"profiles={count}; secret-chars/profile={secretLength}; encrypted-bytes={archive.Length}; repeats={repeats}");
Console.WriteLine("phase,allocated-bytes/op,elapsed-ms/op");
Measure("legacy-preview-including-KDF", () =>
{
    var decoded = BackupArchive.Decrypt(archive, password);
    _ = BackupArchive.Preview(decoded, store, BackupImportMode.Replace);
});
Measure("session-preview-including-KDF", () =>
{
    using var opened = BackupArchive.OpenImport(archive, password);
    _ = opened.Preview(store, BackupImportMode.Replace);
});
Measure("mode-switch-no-decrypt", () => { _ = session.Preview(store, BackupImportMode.Merge); });
Measure("materialize-repeat-decrypt-no-KDF", () => { _ = session.Materialize(); });
Measure("restore-pre-materialized", () => store.RestoreBackupAsync(data, BackupImportMode.Replace).GetAwaiter().GetResult());
Measure("apply-including-repeat-decrypt", () => store.RestoreBackupAsync(session.Materialize(), BackupImportMode.Replace).GetAwaiter().GetResult());

void Measure(string name, Action action)
{
    action(); // Warm serializers and pools separately for each phase.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();
    for (var i = 0; i < repeats; i++) { action(); }
    watch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Console.WriteLine(FormattableString.Invariant($"{name},{allocated / repeats},{watch.Elapsed.TotalMilliseconds / repeats:F3}"));
}
