using System.Collections.Immutable;
using GameGround.Agents;

namespace GameGround.AgentFramework;

public sealed record NarrativeRequest(
    ImmutableArray<Claim> ApprovedObservations,
    ImmutableArray<Claim> PreviousClaims,
    ImmutableArray<VerificationResult> Feedback,
    ImmutableArray<string> RevisionReasons,
    int Attempt);

public interface INarrativeAgent
{
    AgentIdentity Identity { get; }
    Task<AgentOutput> GenerateAsync(NarrativeRequest request, CancellationToken cancellationToken);
}

public sealed class SimulatedNarrativeAgent : INarrativeAgent
{
    public AgentIdentity Identity { get; } = new("local-narrator", AgentRole.Narrator);

    public Task<AgentOutput> GenerateAsync(NarrativeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentOutput(request.ApprovedObservations.Select((claim, index) => claim with
        {
            Id = new ClaimId(index + 1),
            Producer = Identity,
            Status = ClaimStatus.Proposed
        }).ToImmutableArray()));
    }
}
