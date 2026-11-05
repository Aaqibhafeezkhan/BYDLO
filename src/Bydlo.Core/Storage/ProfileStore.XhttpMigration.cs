using Bydlo.Core.Models;

namespace Bydlo.Core.Storage;

public sealed partial class ProfileStore
{
    /// <summary>
    /// One-time migration for profiles saved before XHTTP headers moved to the
    /// secret store (issue #338). Moves <c>headers</c> out of plain
    /// <c>extra</c> options (and subscription baselines) into per-profile
    /// secrets, then rewrites the document without the tokens.
    /// </summary>
    private async Task MigrateXhttpSecretHeadersAsync(CancellationToken ct)
    {
        if (IsNewerVersion)
        {
            return;
        }
        var changed = false;
        foreach (var profile in _doc.Profiles)
        {
            if (!profile.Options.TryGetValue(Keys.XhttpExtra, out var raw))
            {
                continue;
            }
            if (!Bydlo.Core.Import.Parsers.XhttpSecretHeaders.TryExtractHeaders(raw, out var publicExtra, out var headersJson) || headersJson is null)
            {
                continue;
            }
            try
            {
                var current = await secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false);
                current.Values[Keys.XhttpHeaders] = headersJson;
                await secrets.SaveAsync(profile.Id.ToString(), current, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the profile unmigrated for the next launch rather than
                // aborting startup: secret stores can fail independently.
                continue;
            }
            if (publicExtra is null) { profile.Options.Remove(Keys.XhttpExtra); }
            else { profile.Options[Keys.XhttpExtra] = publicExtra; }
            changed = true;
        }
        foreach (var sub in _doc.Subscriptions)
        {
            if (sub.SourceOptions is null)
            {
                continue;
            }
            foreach (var options in sub.SourceOptions.Values)
            {
                if (Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Strip(options))
                {
                    changed = true;
                }
            }
        }
        if (changed)
        {
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
    }
}
