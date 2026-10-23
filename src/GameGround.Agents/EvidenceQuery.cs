using System.Collections.Immutable;
using GameGround.Intelligence;

namespace GameGround.Agents;

public sealed class EvidenceQuery
{
    private readonly AgentContext context;

    public EvidenceQuery(AgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public ImmutableArray<CalculatedFact> GetFacts() => context.Facts;

    public ImmutableArray<AvailableEvidence> GetEvidence(ImmutableArray<FactId> factIds)
    {
        if (factIds.IsDefault)
        {
            throw new ArgumentException("Fact references must be initialized.", nameof(factIds));
        }

        return factIds.Select(id => context.Evidence.SingleOrDefault(item => item.FactId == id)
            ?? throw new ArgumentException("Fact is unavailable in this context.", nameof(factIds))).ToImmutableArray();
    }

    public ControlSequence GetSequence(ControlSequenceId id) =>
        context.Sequences.SingleOrDefault(sequence => sequence.Id == id)
        ?? throw new ArgumentException("Sequence is unavailable in this context.", nameof(id));

    public MatchMoment GetMoment(MomentId id) =>
        context.Moments.SingleOrDefault(moment => moment.Id == id)
        ?? throw new ArgumentException("Moment is unavailable in this context.", nameof(id));
}
