using System.Collections.Immutable;
using GameGround.Intelligence;

namespace GameGround.Agents;

public static class ClaimText
{
    public static string RenderMetric(AgentContext context, MetricAssertion metric)
    {
        var label = MetricLabel(metric.Kind);
        var subject = metric.Team is { } team
            ? (context.Teams.SingleOrDefault(item => item.Id == team)
                ?? throw new ArgumentException("Team is unavailable in this context.", nameof(metric))).Name
            : "Match";
        var unit = metric.Unit switch
        {
            FactUnit.Count => "",
            FactUnit.Seconds => " s",
            FactUnit.Percent => "%",
            _ => throw new ArgumentOutOfRangeException(nameof(metric), "Unsupported fact unit.")
        };
        var ratio = metric.Numerator is { } numerator && metric.Denominator is { } denominator
            ? FormattableString.Invariant($" ({numerator}/{denominator})")
            : "";
        return FormattableString.Invariant(
            $"{subject}: recorded {label} = {metric.Value}{unit}{ratio}, within sequences {context.Identity.Window.FromSequence}-{context.Identity.Window.ThroughSequence}.");
    }

    public static Claim ForFact(AgentContext context, FactId factId, ClaimId id, AgentIdentity producer)
    {
        var fact = context.Facts.SingleOrDefault(item => item.Id == factId)
            ?? throw new ArgumentException("Fact is unavailable in this context.", nameof(factId));
        if (fact.Value is not { } value)
        {
            throw new ArgumentException("An unavailable metric cannot be asserted.", nameof(factId));
        }

        var assertion = new MetricAssertion(fact.Kind, fact.Team, value, fact.Unit, fact.Numerator, fact.Denominator);
        return new Claim(id, producer, RenderMetric(context, assertion), ClaimType.Metric,
            context.Identity, [factId], new EvidenceQuery(context).GetEvidence([factId])
                .Select(item => item.Reference).ToImmutableArray(),
            fact.Evidence.SourceEventIds, assertion, null);
    }

    public static Claim ForMoment(AgentContext context, MomentId momentId, ClaimId id, AgentIdentity producer)
    {
        var candidate = context.Candidates.SingleOrDefault(item => item.MomentId == momentId)
            ?? throw new ArgumentException("Moment candidate is unavailable in this context.", nameof(momentId));
        return new Claim(id, producer, candidate.Statement, ClaimType.RecordedMoment,
            context.Identity, candidate.FactIds,
            new EvidenceQuery(context).GetEvidence(candidate.FactIds).Select(item => item.Reference).ToImmutableArray(),
            candidate.Evidence.SourceEventIds, null, momentId);
    }

    internal static string MetricLabel(FactKind kind) => kind switch
    {
        FactKind.Events => "events",
        FactKind.PeriodsStarted => "period starts",
        FactKind.PeriodsEnded => "period ends",
        FactKind.Goals => "goals",
        FactKind.Fouls => "fouls",
        FactKind.Shots => "shots",
        FactKind.SavedShots => "saved shots",
        FactKind.OffTargetShots => "off-target shots",
        FactKind.BlockedShots => "blocked shots",
        FactKind.ShotsOnTarget => "shots on target (goals + saved)",
        FactKind.ShotConversion => "goal conversion",
        FactKind.Passes => "pass attempts",
        FactKind.CompletedPasses => "completed passes",
        FactKind.PassCompletion => "pass completion",
        FactKind.Tackles => "tackle events",
        FactKind.PressureEvents => "pressure events",
        FactKind.ExplicitControlChanges => "explicit control transitions",
        FactKind.ControlSequences => "team control sequences (including restarts)",
        FactKind.ControlDuration => "modelled observed team control",
        FactKind.ContestedDuration => "modelled observed contested control",
        FactKind.DeadBallDuration => "modelled observed dead-ball control",
        FactKind.ObservedDuration => "modelled observed duration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "Occurrence facts require a moment claim.")
    };

    internal static bool IsMetric(FactKind kind) => Enum.IsDefined(kind) && kind is not
        (FactKind.GoalOccurrence or FactKind.ControlTransitionOccurrence or FactKind.FoulInterruptionOccurrence);
}
