using System.Collections.Immutable;
using GameGround.Core;
using GameGround.Intelligence;

namespace GameGround.Agents;

public readonly record struct ClaimId(int Value);

public enum AgentRole
{
    Analyst,
    Narrator
}

public sealed record AgentIdentity(string Id, AgentRole Role);

public enum ClaimType
{
    Metric,
    RecordedMoment
}

public enum ClaimStatus
{
    Proposed,
    Verified,
    Rejected
}

public sealed record MetricAssertion(
    FactKind Kind, TeamId? Team, decimal Value, FactUnit Unit, int? Numerator, int? Denominator);

public sealed record Claim(
    ClaimId Id,
    AgentIdentity Producer,
    string Text,
    ClaimType Type,
    ContextIdentity Context,
    ImmutableArray<FactId> FactIds,
    ImmutableArray<EvidenceReference> Evidence,
    ImmutableArray<EventId> CoveredSourceEvents,
    MetricAssertion? Metric,
    MomentId? Moment,
    ClaimStatus Status = ClaimStatus.Proposed);
