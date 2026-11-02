using Bydlo.Core.Backup;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;

namespace Bydlo.Core.Storage;

public sealed partial class ProfileStore
{
    public async Task<BackupData> CaptureBackupAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = new BackupData { Settings = BackupArchive.Clone(settings), ToolSecrets = await CaptureToolSecretsAsync(ct).ConfigureAwait(false) };
            data.Settings.EnginePaths.Clear();
            data.Settings.Zapret.InstallPath = null;
            foreach (var profile in _doc.Profiles)
            { data.Profiles.Add(new(BackupArchive.Clone(profile), await secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false))); }
            foreach (var sub in _doc.Subscriptions)
            { data.Subscriptions.Add(new(BackupArchive.Clone(sub), await GetSubscriptionUrlAsync(sub.Id, ct).ConfigureAwait(false) ?? "")); }
            return data;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restores credentials and the document together; rolls back on a storage failure.</summary>
    public async Task RestoreBackupAsync(BackupData data, BackupImportMode mode, CancellationToken ct = default)
    {
        data = BackupArchive.Clone(data);
        BackupArchive.Validate(data);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var incoming = new ProfileDocument { Profiles = data.Profiles.Select(p => p.Profile).ToList(), Subscriptions = data.Subscriptions.Select(s => s.Subscription).ToList() };
            if (mode == BackupImportMode.Merge)
            {
                incoming.Profiles.InsertRange(0, _doc.Profiles.Where(p => incoming.Profiles.All(i => i.Id != p.Id)));
                incoming.Subscriptions.InsertRange(0, _doc.Subscriptions.Where(s => incoming.Subscriptions.All(i => i.Id != s.Id)));
            }
            var writes = data.Profiles.ToDictionary(p => p.Profile.Id.ToString(), p => p.Secrets);
            foreach (var sub in data.Subscriptions)
            { writes.Add(SubscriptionSecretId(sub.Subscription.Id), new ProfileSecrets { Values = { ["url"] = sub.Url } }); }
            if (mode == BackupImportMode.Replace || data.ToolSecrets.Values.Count > 0)
            { RestoreToolSecrets(writes, data.ToolSecrets); }
            var obsolete = mode == BackupImportMode.Replace
                ? _doc.Profiles.Select(p => p.Id.ToString()).Concat(_doc.Subscriptions.Select(s => SubscriptionSecretId(s.Id))).Where(id => !writes.ContainsKey(id)).ToList()
                : [];
            var original = new Dictionary<string, ProfileSecrets>();
            foreach (var id in writes.Keys.Concat(obsolete))
            { original.Add(id, await secrets.LoadAsync(id, ct).ConfigureAwait(false)); }
            try
            {
                foreach (var (id, value) in writes) { await secrets.SaveAsync(id, value, ct).ConfigureAwait(false); }
                foreach (var id in obsolete) { await secrets.DeleteAsync(id, ct).ConfigureAwait(false); }
                await JsonDefaults.WriteAtomicAsync(path, incoming, ct).ConfigureAwait(false);
                _doc = incoming;
            }
            catch
            {
                // A cancelled import must still restore credentials, using an uncancelled token.
                foreach (var (id, value) in original)
                {
                    if (value.Values.Count == 0) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                    else { await secrets.SaveAsync(id, value, CancellationToken.None).ConfigureAwait(false); }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Merges device-local tool secrets into the single backup bag without changing its format.</summary>
    private async Task<ProfileSecrets> CaptureToolSecretsAsync(CancellationToken ct)
    {
        var bag = new ProfileSecrets();
        var tg = await secrets.LoadAsync(ConnectionManager.TgWsProxySecretId, ct).ConfigureAwait(false);
        if (tg["secret"] is { } secret) { bag.Values["secret"] = secret; }
        var lan = await secrets.LoadAsync(ConnectionManager.LocalProxySecretId, ct).ConfigureAwait(false);
        if (lan["password"] is { } password) { bag.Values["localproxy-password"] = password; }
        return bag;
    }

    /// <summary>Splits the backup bag back into per-tool secret records.</summary>
    private static void RestoreToolSecrets(Dictionary<string, ProfileSecrets> writes, ProfileSecrets bag)
    {
        var tg = new ProfileSecrets();
        if (bag["secret"] is { } secret) { tg.Values["secret"] = secret; }
        writes.Add(ConnectionManager.TgWsProxySecretId, tg);
        var lan = new ProfileSecrets();
        if (bag["localproxy-password"] is { } password) { lan.Values["password"] = password; }
        writes.Add(ConnectionManager.LocalProxySecretId, lan);
    }
}
