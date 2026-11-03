using Bydlo.Core.Security;

namespace Bydlo.Core.Routing;

public sealed record RuleSetSource(string Tag, Uri Url, string Sha256, int MaxBytes);

/// <summary>Stages a download beside its destination and publishes it only after verification.</summary>
public sealed class RuleSetCache(string directory, HttpClient http)
{
    public string GetPath(RuleSetSource source) =>
        RoutingPresets.CachedPath(directory, source);

    public async Task<string> UpdateAsync(RuleSetSource source, CancellationToken ct = default)
    {
        if (!source.Url.IsAbsoluteUri || source.Url.Scheme != Uri.UriSchemeHttps || source.MaxBytes <= 0)
        {
            throw new ArgumentException("Rule sets require an HTTPS source and a positive size limit.", nameof(source));
        }

        var destination = GetPath(source);
        Directory.CreateDirectory(directory);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > source.MaxBytes)
            {
                throw new InvalidDataException("Rule set exceeds its size limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > source.MaxBytes)
                    {
                        throw new InvalidDataException("Rule set exceeds its size limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            await IntegrityVerifier.VerifyFileAsync(staging, source.Sha256, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
            return destination;
        }
        finally
        {
            File.Delete(staging);
        }
    }
}
