using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Models;

namespace Bydlo.Engines.Groups;

internal sealed class ClashGroupController(HttpClient http, int port, string secret, IReadOnlyList<ChainHop> members) : IGroupController
{
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }

    public async Task<Guid?> CurrentAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "proxies/proxy");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync(ClashJsonContext.Default.JsonObject, ct).ConfigureAwait(false);
        var tag = (string?)json?["now"];
        return members.FirstOrDefault(m => ServerGroup.Tag(m.Profile.Id) == tag)?.Profile.Id;
    }

    public async Task SelectAsync(Guid? id, CancellationToken ct)
    {
        // With no live member the last dead proxy remains selected; it cannot fall back to direct.
        if (id is null) { return; }
        using var request = Request(HttpMethod.Put, "proxies/proxy");
        request.Content = JsonContent.Create(new JsonObject { ["name"] = ServerGroup.Tag(id.Value) }, ClashJsonContext.Default.JsonObject);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<GroupProbe>> ProbeAsync(ServerGroup group, IReadOnlyList<ChainHop> candidates, CancellationToken ct)
    {
        return await Task.WhenAll(candidates.Select(async member =>
        {
            try
            {
                using var request = Request(HttpMethod.Get, $"proxies/{ServerGroup.Tag(member.Profile.Id)}/delay?timeout=5000&url={Uri.EscapeDataString(group.ProbeUrl)}");
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { return new GroupProbe(member.Profile.Id, false, 0); }
                var json = await response.Content.ReadFromJsonAsync(ClashJsonContext.Default.JsonObject, ct).ConfigureAwait(false);
                return new GroupProbe(member.Profile.Id, json?["delay"] is not null, (long?)json?["delay"] ?? 0);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { return new GroupProbe(member.Profile.Id, false, 0); }
        })).ConfigureAwait(false);
    }
}
