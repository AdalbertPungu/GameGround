using System.Collections.Immutable;
using GameGround.Intelligence;

namespace GameGround.Agents;

public sealed class SimulatedAnalyst : ILocalAgent
{
    public AgentIdentity Identity { get; } = new("local-analyst", AgentRole.Analyst);

    public Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = request.Context;
        if (context.Through is null)
        {
            return Task.FromResult(new AgentOutput([]));
        }

        var claims = ImmutableArray.CreateBuilder<Claim>();
        foreach (var fact in context.Facts.Where(fact => fact.Team is not null && fact.Value is not null
            && fact.Kind is FactKind.Goals or FactKind.Shots or FactKind.PassCompletion))
        {
            claims.Add(ClaimText.ForFact(context, fact.Id, new ClaimId(claims.Count + 1), Identity));
        }

        foreach (var candidate in context.Candidates)
        {
            claims.Add(ClaimText.ForMoment(context, candidate.MomentId, new ClaimId(claims.Count + 1), Identity));
        }

        return Task.FromResult(new AgentOutput(claims.ToImmutable()));
    }
}

public sealed class SimulatedNarrator : ILocalAgent
{
    public AgentIdentity Identity { get; } = new("local-narrator", AgentRole.Narrator);

    public Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claims = request.ApprovedObservations.Select((claim, index) => claim with
        {
            Id = new ClaimId(index + 1),
            Producer = Identity,
            Status = ClaimStatus.Proposed
        }).ToImmutableArray();
        return Task.FromResult(new AgentOutput(claims));
    }
}
