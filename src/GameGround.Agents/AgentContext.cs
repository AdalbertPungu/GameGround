using System.Collections.Immutable;
using GameGround.Core;
using GameGround.Intelligence;

namespace GameGround.Agents;

public sealed record AnalysisWindow(int FromSequence, int ThroughSequence);

public sealed record ContextIdentity(
    string ContentId, AnalysisScope Scope, AnalysisWindow Window);

public readonly record struct EvidenceId(int Value);

public sealed record EvidenceReference(EvidenceId Id, ContextIdentity Context);

public sealed record AvailableEvidence(
    EvidenceReference Reference, FactId FactId, Evidence Evidence);

public sealed record TeamLabel(TeamId Id, string Name);

public sealed class AgentContext
{
    internal AgentContext(
        ContextIdentity identity,
        ImmutableArray<TeamLabel> teams,
        ImmutableArray<CalculatedFact> facts,
        ImmutableArray<AvailableEvidence> evidence,
        ImmutableArray<ControlSequence> sequences,
        ImmutableArray<MatchMoment> moments,
        ImmutableArray<InsightCandidate> candidates,
        EventPosition? from,
        EventPosition? through)
    {
        Identity = identity;
        Teams = teams;
        Facts = facts;
        Evidence = evidence;
        Sequences = sequences;
        Moments = moments;
        Candidates = candidates;
        From = from;
        Through = through;
    }

    public ContextIdentity Identity { get; }
    public ImmutableArray<TeamLabel> Teams { get; }
    public ImmutableArray<CalculatedFact> Facts { get; }
    public ImmutableArray<AvailableEvidence> Evidence { get; }
    public ImmutableArray<ControlSequence> Sequences { get; }
    public ImmutableArray<MatchMoment> Moments { get; }
    public ImmutableArray<InsightCandidate> Candidates { get; }
    public EventPosition? From { get; }
    public EventPosition? Through { get; }
}
