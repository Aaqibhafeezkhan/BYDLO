using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bydlo.Core.Engines;

namespace Bydlo.Engines.SingBox;

public sealed class ClashConnectionsClient(HttpClient http, int port, string secret) : IActiveConnectionSource
{
    public async Task<IReadOnlyList<ActiveConnection>> ListConnectionsAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "connections");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ConnectionList>(cancellationToken: ct).ConfigureAwait(false);
        return payload?.Connections?.Where(item => !string.IsNullOrEmpty(item.Id)).Select(item =>
            new ActiveConnection(item.Id!, item.Metadata?.Host is { Length: > 0 } host ? host : item.Metadata?.DestinationIp ?? "",
                item.Metadata?.Process ?? item.Metadata?.ProcessPath ?? "", item.RulePayload ?? item.Rule ?? "",
                string.Join(" → ", item.Chains ?? []), item.Download, item.Upload)).ToArray() ?? [];
    }

    public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or ".." || id.Length > 128)
        {
            throw new ArgumentException("Invalid connection ID.", nameof(id));
        }
        using var request = Request(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(id));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }

    private sealed record ConnectionList(ConnectionItem[]? Connections);
    private sealed record ConnectionItem(string? Id, ConnectionMetadata? Metadata, string? Rule, string? RulePayload,
        string[]? Chains, long Download, long Upload);
    private sealed record ConnectionMetadata(string? Host, string? DestinationIp, string? Process, string? ProcessPath);
}
