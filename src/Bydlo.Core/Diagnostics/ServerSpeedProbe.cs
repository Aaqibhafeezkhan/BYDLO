using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Diagnostics;

public sealed class ServerSpeedProbe(
    Func<Profile, Uri, CancellationToken, Task<long>> measure)
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    public async Task<IReadOnlyDictionary<Guid, long>> RunAsync(
        IReadOnlyList<Profile> profiles, Uri target, CancellationToken ct = default)
    {
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
        {
            throw new ArgumentException("An HTTPS test URL without credentials is required.", nameof(target));
        }

        var results = new ConcurrentDictionary<Guid, long>();
        await Parallel.ForEachAsync(profiles, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (profile, token) =>
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(Timeout);
                try
                {
                    results[profile.Id] = await measure(profile, target, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    results[profile.Id] = 0;
                }
                catch (Exception ex) when (ex is EngineException or HttpRequestException or IOException or TimeoutException)
                {
                    results[profile.Id] = 0;
                }
            }).ConfigureAwait(false);
        return results;
    }

    public static async Task<long> MeasureThroughEngineAsync(IEngine engine, EngineStartRequest start, HttpClient http,
        Uri target, CancellationToken ct = default)
    {
        await using (engine.ConfigureAwait(false))
        {
            await engine.StartAsync(start, ct).ConfigureAwait(false);
            return await DownloadAsync(http, target, ct).ConfigureAwait(false);
        }
    }

    public static async Task<long> DownloadAsync(HttpClient http, Uri target, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, MaximumBytes - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            return 0;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var buffer = new byte[65536];
        var total = 0;
        while (total < MaximumBytes)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes - total)), ct)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            total += count;
        }
        return (long)(total / Math.Max(watch.Elapsed.TotalSeconds, 0.001));
    }
}
