using Bydlo.Core.Models;
using Bydlo.Engines.Groups.Rpc;
using Grpc.Net.Client;

namespace Bydlo.Engines.Groups;

internal sealed class XrayGroupController(GrpcChannel channel, IReadOnlyList<ChainHop> members) : IGroupController
{
    private readonly RoutingService.RoutingServiceClient _router = new(channel);
    private readonly ObservatoryService.ObservatoryServiceClient _observatory = new(channel);
    private static DateTime Deadline => DateTime.UtcNow.AddSeconds(5);

    public async Task<Guid?> CurrentAsync(CancellationToken ct)
    {
        var response = await _router.GetBalancerInfoAsync(new GetBalancerInfoRequest { Tag = "proxy" }, deadline: Deadline, cancellationToken: ct);
        var tag = response.Balancer?.Override?.Target;
        if (string.IsNullOrEmpty(tag)) { tag = response.Balancer?.PrincipleTarget?.Tag.FirstOrDefault(); }
        return members.FirstOrDefault(m => ServerGroup.Tag(m.Profile.Id) == tag)?.Profile.Id;
    }

    public async Task SelectAsync(Guid? id, CancellationToken ct)
    {
        await _router.OverrideBalancerTargetAsync(new OverrideBalancerTargetRequest
        {
            BalancerTag = "proxy", Target = id is { } value ? ServerGroup.Tag(value) : "group-unavailable",
        }, deadline: Deadline, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<GroupProbe>> ProbeAsync(ServerGroup group, IReadOnlyList<ChainHop> candidates, CancellationToken ct)
    {
        var response = await _observatory.GetOutboundStatusAsync(new GetOutboundStatusRequest(), deadline: Deadline, cancellationToken: ct);
        return candidates.Select(member =>
        {
            var status = response.Status?.Status.FirstOrDefault(s => s.OutboundTag == ServerGroup.Tag(member.Profile.Id));
            var fresh = status is not null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - status.LastTryTime <= group.IntervalSeconds * 2L + 15;
            return new GroupProbe(member.Profile.Id, fresh && status!.Alive, status?.Delay ?? 0);
        }).ToList();
    }
}
