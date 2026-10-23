using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameGround.Core;
using GameGround.Intelligence;

namespace GameGround.Agents;

public static class AgentContextFactory
{
    public static AgentContext Create(MatchDataset dataset, AnalysisWindow window)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(window);
        if (dataset.Events.IsDefault || window.FromSequence < 1
            || window.ThroughSequence < window.FromSequence - 1
            || window.ThroughSequence > dataset.Events.Length
            || (window.ThroughSequence < window.FromSequence && window != new AnalysisWindow(1, 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be an available ordered sequence range.");
        }

        var prefix = dataset with { Events = dataset.Events[..window.ThroughSequence] };
        var analysis = FootballIntelligenceEngine.Analyze(prefix);
        var positions = prefix.Events.ToDictionary(item => item.Id,
            item => new EventPosition(item.Sequence, item.Period, item.Clock));
        bool Within(Evidence evidence) => evidence.SourceEventIds.All(id =>
            positions[id].Sequence >= window.FromSequence
            && positions[id].Sequence <= window.ThroughSequence);
        var facts = analysis.Facts.Where(fact => Within(fact.Evidence)
            && (window.FromSequence == 1 || IsOccurrence(fact.Kind))).ToImmutableArray();
        var factIds = facts.Select(fact => fact.Id).ToHashSet();
        var sequences = analysis.ControlSequences
            .Where(sequence => sequence.Start.Sequence >= window.FromSequence && Within(sequence.Evidence))
            .ToImmutableArray();
        var sequenceIds = sequences.Select(sequence => sequence.Id).ToHashSet();
        var moments = analysis.Moments.Where(moment => Within(moment.Evidence)
            && moment.FactIds.All(factIds.Contains)).Select(moment => moment with
            {
                ControlSequence = moment.ControlSequence is { } id && sequenceIds.Contains(id) ? id : null
            }).ToImmutableArray();
        var momentIds = moments.Select(moment => moment.Id).ToHashSet();
        var candidates = analysis.Candidates.Where(candidate => momentIds.Contains(candidate.MomentId))
            .ToImmutableArray();
        var teams = ImmutableArray.Create(
            new TeamLabel(prefix.Match.HomeTeam.Id, prefix.Match.HomeTeam.Name),
            new TeamLabel(prefix.Match.AwayTeam.Id, prefix.Match.AwayTeam.Name));
        // Bind even equal ordinal IDs to the exact visible source content, never to a future suffix.
        var content = JsonSerializer.Serialize(new
        {
            analysis.Scope,
            Window = window,
            Teams = teams,
            Events = prefix.Events.Select(item => new
            {
                Type = item.GetType().Name,
                Data = JsonSerializer.SerializeToElement(item, item.GetType())
            }).ToArray()
        });
        var identity = new ContextIdentity(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), analysis.Scope, window);
        var evidenceEntries = facts.Select(fact => new AvailableEvidence(
            new EvidenceReference(new EvidenceId(fact.Id.Value), identity), fact.Id, fact.Evidence)).ToImmutableArray();
        var visible = prefix.Events.Where(item => item.Sequence >= window.FromSequence).ToArray();
        return new AgentContext(identity, teams, facts, evidenceEntries, sequences, moments, candidates,
            visible.Length == 0 ? null : positions[visible[0].Id],
            visible.Length == 0 ? null : positions[visible[^1].Id]);
    }

    public static AgentContext Through(MatchDataset dataset, int throughSequence) =>
        Create(dataset, new AnalysisWindow(1, throughSequence));

    public static AgentContext Before(MatchDataset dataset, EventPosition moment)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(moment);
        if (dataset.Events.IsDefault || moment.Sequence < 1 || moment.Sequence > dataset.Events.Length
            || dataset.Events[moment.Sequence - 1].Sequence != moment.Sequence
            || dataset.Events[moment.Sequence - 1].Period != moment.Period
            || dataset.Events[moment.Sequence - 1].Clock != moment.Clock)
        {
            throw new ArgumentException("Moment must identify an available source event position.", nameof(moment));
        }

        return Through(dataset, moment.Sequence - 1);
    }

    public static AgentContext Period(MatchDataset dataset, int period, int throughSequence) =>
        TimeWindow(dataset, period, TimeSpan.Zero, TimeSpan.MaxValue, throughSequence);

    public static AgentContext TimeWindow(
        MatchDataset dataset, int period, TimeSpan from, TimeSpan through, int throughSequence)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (from < TimeSpan.Zero || through < from || period < 1
            || throughSequence < 0 || dataset.Events.IsDefault || throughSequence > dataset.Events.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(throughSequence), "Invalid time window or cutoff.");
        }

        var prefix = dataset.Events[..throughSequence];
        var selected = prefix.Where(item => item.Period == period && item.Clock >= from && item.Clock <= through)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException("No observed event belongs to the requested time window.");
        }

        return Create(dataset, new AnalysisWindow(selected[0].Sequence, selected[^1].Sequence));
    }

    private static bool IsOccurrence(FactKind kind) => kind is FactKind.GoalOccurrence
        or FactKind.ControlTransitionOccurrence or FactKind.FoulInterruptionOccurrence;
}
