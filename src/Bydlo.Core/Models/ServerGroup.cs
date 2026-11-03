using Bydlo.Core.Engines;

namespace Bydlo.Core.Models;

public enum ServerGroupMode { Manual, UrlTest, Fallback }

/// <summary>References only; credentials remain under each member's id in ISecretStore.</summary>
public sealed class ServerGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> ProfileIds { get; set; } = [];
    public Guid? SubscriptionId { get; set; }
    public Guid? SelectedProfileId { get; set; }
    public EngineKind Engine { get; set; } = EngineKind.SingBox;
    public ServerGroupMode Mode { get; set; }
    public string ProbeUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int IntervalSeconds { get; set; } = 180;
    public int ToleranceMs { get; set; } = 50;
    public override string ToString() => Name;

    public IReadOnlyList<Profile> Resolve(IReadOnlyList<Profile> profiles) => SubscriptionId is { } id
        ? profiles.Where(p => p.SubscriptionId == id).ToList()
        : ProfileIds.Select(id => profiles.FirstOrDefault(p => p.Id == id)
            ?? throw Invalid()).ToList();

    public void Validate(IReadOnlyList<Profile> members)
    {
        if (string.IsNullOrWhiteSpace(Name) || !Enum.IsDefined(Mode)
            || Engine is not (EngineKind.SingBox or EngineKind.Xray)
            || members.Count is < 1 or > 100 || members.Select(p => p.Id).Distinct().Count() != members.Count
            || IntervalSeconds is < 10 or > 86400 || ToleranceMs is < 0 or > 65535
            || !Uri.TryCreate(ProbeUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment)
            || SubscriptionId is null && SelectedProfileId is { } selected && !members.Any(p => p.Id == selected))
        {
            throw Invalid();
        }
    }

    public static EngineException Invalid() => new("Invalid server group.", localizationKey: "group.invalid");
    public Guid InitialProfileId(IReadOnlyList<ChainHop> members) =>
        members.FirstOrDefault(m => m.Profile.Id == SelectedProfileId)?.Profile.Id ?? members[0].Profile.Id;
    public static string Tag(Guid id) => "member-" + id.ToString("N");
}

public sealed record GroupProbe(Guid ProfileId, bool Alive, long DelayMs);

/// <summary>Sticky failover and latency hysteresis, shared by the controller and isolated tests.</summary>
public static class GroupSelection
{
    public static Guid? Pick(ServerGroup group, Guid? current, IReadOnlyList<GroupProbe> probes)
    {
        if (group.Mode == ServerGroupMode.Manual) { return group.SelectedProfileId ?? current; }
        var alive = probes.Where(p => p.Alive && p.DelayMs >= 0).ToList();
        var previous = alive.FirstOrDefault(p => p.ProfileId == current);
        if (group.Mode == ServerGroupMode.Fallback && previous is not null) { return previous.ProfileId; }
        var best = alive.OrderBy(p => p.DelayMs).FirstOrDefault();
        return previous is not null && best is not null && previous.DelayMs - best.DelayMs <= group.ToleranceMs
            ? previous.ProfileId : best?.ProfileId;
    }
}
