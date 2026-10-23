using System.Collections.Immutable;
using GameGround.Core;

namespace GameGround.Intelligence;

public readonly record struct FactId(int Value);
public readonly record struct ControlSequenceId(int Value);
public readonly record struct MomentId(int Value);

public sealed record AnalysisScope(
    DatasetManifest Manifest, string MatchId, int ThroughSequence, int RulesVersion);

public sealed record Evidence(AnalysisScope Scope, ImmutableArray<EventId> SourceEventIds);

public enum FactUnit
{
    Count,
    Seconds,
    Percent
}

public enum FactKind
{
    Events,
    PeriodsStarted,
    PeriodsEnded,
    Goals,
    Fouls,
    Shots,
    SavedShots,
    OffTargetShots,
    BlockedShots,
    ShotsOnTarget,
    ShotConversion,
    Passes,
    CompletedPasses,
    PassCompletion,
    Tackles,
    PressureEvents,
    ExplicitControlChanges,
    ControlSequences,
    ControlDuration,
    ContestedDuration,
    DeadBallDuration,
    ObservedDuration,
    GoalOccurrence,
    ControlTransitionOccurrence,
    FoulInterruptionOccurrence
}

public sealed record CalculatedFact(
    FactId Id,
    FactKind Kind,
    TeamId? Team,
    decimal? Value,
    FactUnit Unit,
    int? Numerator,
    int? Denominator,
    Evidence Evidence);

public static class EvidenceResolver
{
    public static ImmutableArray<MatchEvent> Resolve(MatchIntelligence analysis, Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Scope != analysis.Scope || evidence.SourceEventIds.IsDefault)
        {
            throw new ArgumentException("Evidence must belong to this analysis scope.", nameof(evidence));
        }

        var sources = analysis.SourceEvents.ToDictionary(item => item.Id);
        var result = ImmutableArray.CreateBuilder<MatchEvent>();
        var lastSequence = 0;
        foreach (var id in evidence.SourceEventIds)
        {
            if (!sources.TryGetValue(id, out var item)
                || item.Sequence <= lastSequence || item.Sequence > analysis.Scope.ThroughSequence)
            {
                throw new ArgumentException(
                    "Evidence sources must exist in the scope and be unique in event order.", nameof(evidence));
            }

            result.Add(item);
            lastSequence = item.Sequence;
        }

        return result.ToImmutable();
    }
}
