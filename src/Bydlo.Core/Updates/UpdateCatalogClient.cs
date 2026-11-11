using System.Text;

namespace Bydlo.Core.Updates;

/// <summary>
/// Downloads the signed update catalog with a hard size limit, a timeout and cancellation (issue #411).
/// This type only bounds the transfer; <see cref="UpdateCatalogVerifier"/> decides whether the bytes
/// are authentic. Tests inject a fake <see cref="HttpMessageHandler"/> and never touch the network.
/// </summary>
public sealed class UpdateCatalogClient(HttpClient http, Uri? url = null, TimeSpan? timeout = null)
{
    public const string AssetName = "releases-catalog.json";

    /// <summary>Largest accepted catalog. The real file is a few kilobytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Stable URL of the catalog asset of the latest published release.</summary>
    public static Uri DefaultUrl { get; } = new("https://github.com/endercodezz/BYDLO/releases/latest/download/" + AssetName);

    private readonly Uri _url = url ?? DefaultUrl;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    /// <summary>Returns the raw envelope JSON. Throws <see cref="UpdateTrustException"/> on any transport failure.</summary>
    public async Task<string> DownloadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateTrustException(UpdateTrustFailure.CatalogUnavailable,
                    $"The update catalog request failed with HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                throw TooLarge();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            // One byte past the limit is enough to prove the body is oversized.
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxBytes)
            {
                throw TooLarge();
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A timeout is a trust failure, not a user cancellation: the caller keeps the installed version.
            throw new UpdateTrustException(UpdateTrustFailure.CatalogUnavailable, "The update catalog request timed out.");
        }
    }

    private static UpdateTrustException TooLarge() =>
        new(UpdateTrustFailure.CatalogTooLarge, $"The update catalog exceeds {MaxBytes} bytes.");
}
