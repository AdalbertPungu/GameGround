using System.Collections.Immutable;
using GameGround.Core;

namespace GameGround.Intelligence;

public sealed record EventPosition(int Sequence, int Period, TimeSpan Clock);

public sealed record PeriodSummary(
    int Number, TimeSpan ObservedUntil, bool HasEnded, Evidence Evidence);

public sealed record TeamSummary(
    TeamId Team, int Goals, int Passes, int CompletedPasses, decimal? PassCompletionPercent,
    int Fouls, int Tackles, int PressureEvents);

public sealed record MatchSummary(
    MatchSnapshot Snapshot,
    int EventCount,
    int Goals,
    int Shots,
    int Passes,
    int Fouls,
    ImmutableArray<PeriodSummary> Periods,
    ImmutableArray<TeamSummary> Teams);

public sealed record TeamControlSummary(
    TeamId Team, int SequenceCount, TimeSpan ObservedDuration, Evidence Evidence);

public sealed record PossessionSummary(
    BallControl CurrentControl,
    int ExplicitTransitionCount,
    TimeSpan ObservedDuration,
    TimeSpan ContestedDuration,
    TimeSpan DeadBallDuration,
    ImmutableArray<TeamControlSummary> Teams);

public sealed record ControlSequence(
    ControlSequenceId Id,
    BallControl Control,
    EventPosition Start,
    EventPosition End,
    bool IsClosed,
    Evidence Evidence)
{
    public TimeSpan ObservedDuration => End.Clock - Start.Clock;
}

public sealed record ShootingSummary(
    TeamId Team, int Shots, int Goals, int Saved, int OffTarget, int Blocked,
    int OnTarget, decimal? ConversionPercent, Evidence Evidence);

public sealed record ShotObservation(
    EventId EventId, TeamId Team, PlayerId Player, EventPosition Position,
    ShotOutcome Outcome, ControlSequenceId ControlSequence, Evidence Evidence);

public enum MomentKind
{
    Goal,
    ControlTransition,
    FoulInterruption
}

public sealed record MatchMoment(
    MomentId Id,
    MomentKind Kind,
    EventPosition Position,
    TeamId? Team,
    ControlSequenceId? ControlSequence,
    ImmutableArray<FactId> FactIds,
    Evidence Evidence);

public enum CandidatePriority
{
    ScoreUpdate,
    ContextUpdate
}

public sealed record InsightCandidate(
    MomentId MomentId,
    MomentKind Type,
    TeamId? Team,
    EventPosition Position,
    CandidatePriority Priority,
    string Statement,
    ImmutableArray<FactId> FactIds,
    Evidence Evidence);

public sealed record MatchIntelligence(
    AnalysisScope Scope,
    MatchSummary Summary,
    PossessionSummary Possession,
    ImmutableArray<ShootingSummary> Shooting,
    ImmutableArray<ControlSequence> ControlSequences,
    ImmutableArray<ShotObservation> ShotTimeline,
    ImmutableArray<CalculatedFact> Facts,
    ImmutableArray<MatchMoment> Moments,
    ImmutableArray<InsightCandidate> Candidates,
    ImmutableArray<MatchEvent> SourceEvents);
