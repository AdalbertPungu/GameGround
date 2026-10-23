using System.Collections.Immutable;
using GameGround.Core;

namespace GameGround.Intelligence;

public static class FootballIntelligenceEngine
{
    public const int RulesVersion = 1;

    public static MatchIntelligence Analyze(MatchDataset dataset)
    {
        var timeline = MatchReducer.ReplayTimeline(dataset);
        var snapshot = timeline[^1];
        var scope = new AnalysisScope(
            dataset.Manifest, dataset.Match.MatchId, snapshot.LastSequence, RulesVersion);
        var sequences = ControlAnalyzer.Analyze(dataset, timeline, scope);
        var teams = new[] { dataset.Match.HomeTeam.Id, dataset.Match.AwayTeam.Id };
        var eventsByPeriod = dataset.Events.GroupBy(item => item.Period)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var shotIds = dataset.Events.OfType<Shot>().Select(shot => shot.Id).ToHashSet();
        var shotSequences = sequences.Where(sequence => sequence.Control.Kind == BallControlKind.Team)
            .SelectMany(sequence => sequence.Evidence.SourceEventIds.Where(shotIds.Contains)
                .Select(id => (Event: id, Sequence: sequence)))
            .ToDictionary(item => item.Event, item => item.Sequence);
        var closedTeamSequences = sequences
            .Where(sequence => sequence.IsClosed && sequence.Control.Kind == BallControlKind.Team)
            .ToDictionary(sequence => sequence.End.Sequence);
        var teamSummaries = teams.Select(team =>
        {
            var passes = dataset.Events.OfType<Pass>().Where(item => item.Team == team).ToArray();
            var completed = passes.Count(item => item.Outcome == PassOutcome.Completed);
            return new TeamSummary(
                team, dataset.Events.OfType<Shot>().Count(item => item.Team == team && item.Outcome == ShotOutcome.Goal),
                passes.Length, completed, Percentage(completed, passes.Length),
                dataset.Events.OfType<Foul>().Count(item => item.Team == team),
                dataset.Events.OfType<Tackle>().Count(item => item.Team == team),
                dataset.Events.OfType<Pressure>().Count(item => item.Team == team));
        }).ToImmutableArray();
        var periods = dataset.Events.OfType<PeriodStarted>().Select(start =>
        {
            var events = eventsByPeriod[start.Period];
            return new PeriodSummary(start.Period, events[^1].Clock,
                events[^1] is PeriodEnded, EvidenceFor(events));
        }).ToImmutableArray();
        var shooting = teams.Select(team =>
        {
            var shots = dataset.Events.OfType<Shot>().Where(item => item.Team == team).ToArray();
            var goals = shots.Count(item => item.Outcome == ShotOutcome.Goal);
            var saved = shots.Count(item => item.Outcome == ShotOutcome.Saved);
            return new ShootingSummary(team, shots.Length, goals, saved,
                shots.Count(item => item.Outcome == ShotOutcome.OffTarget),
                shots.Count(item => item.Outcome == ShotOutcome.Blocked),
                goals + saved, Percentage(goals, shots.Length), EvidenceFor(shots));
        }).ToImmutableArray();
        var controlTeams = teams.Select(team =>
        {
            var owned = sequences.Where(sequence => sequence.Control == BallControl.By(team)).ToArray();
            return new TeamControlSummary(team, owned.Length, ControlAnalyzer.Duration(owned),
                EvidenceForSequences(owned));
        }).ToImmutableArray();
        var possession = new PossessionSummary(
            snapshot.Control, dataset.Events.OfType<ControlChanged>().Count(),
            ControlAnalyzer.Duration(sequences),
            ControlAnalyzer.Duration(sequences.Where(sequence => sequence.Control == BallControl.Contested)),
            ControlAnalyzer.Duration(sequences.Where(sequence => sequence.Control == BallControl.DeadBall)),
            controlTeams);
        var summary = new MatchSummary(snapshot, dataset.Events.Length,
            teamSummaries.Sum(team => team.Goals), shooting.Sum(team => team.Shots),
            teamSummaries.Sum(team => team.Passes), teamSummaries.Sum(team => team.Fouls),
            periods, teamSummaries);
        var shotsTimeline = dataset.Events.OfType<Shot>().Select(shot => new ShotObservation(
            shot.Id, shot.Team, shot.Player, ControlAnalyzer.Position(shot), shot.Outcome,
            shotSequences[shot.Id].Id, EvidenceFor([shot]))).ToImmutableArray();
        var facts = ImmutableArray.CreateBuilder<CalculatedFact>();
        AddFact(FactKind.Events, null, dataset.Events.Length, FactUnit.Count, EvidenceFor(dataset.Events));
        AddFact(FactKind.PeriodsStarted, null, periods.Length, FactUnit.Count,
            EvidenceFor(dataset.Events.OfType<PeriodStarted>()));
        AddFact(FactKind.PeriodsEnded, null, periods.Count(period => period.HasEnded), FactUnit.Count,
            EvidenceFor(dataset.Events.OfType<PeriodEnded>()));
        AddFact(FactKind.ExplicitControlChanges, null, possession.ExplicitTransitionCount, FactUnit.Count,
            EvidenceFor(dataset.Events.OfType<ControlChanged>()));
        AddFact(FactKind.ObservedDuration, null, Seconds(possession.ObservedDuration), FactUnit.Seconds,
            EvidenceForSequences(sequences));
        AddFact(FactKind.ContestedDuration, null, Seconds(possession.ContestedDuration), FactUnit.Seconds,
            EvidenceForSequences(sequences.Where(sequence => sequence.Control == BallControl.Contested)));
        AddFact(FactKind.DeadBallDuration, null, Seconds(possession.DeadBallDuration), FactUnit.Seconds,
            EvidenceForSequences(sequences.Where(sequence => sequence.Control == BallControl.DeadBall)));

        foreach (var team in teamSummaries)
        {
            var teamShots = shooting.Single(item => item.Team == team.Team);
            var teamControl = controlTeams.Single(item => item.Team == team.Team);
            var shots = dataset.Events.OfType<Shot>().Where(item => item.Team == team.Team).ToArray();
            var passes = dataset.Events.OfType<Pass>().Where(item => item.Team == team.Team).ToArray();
            AddCount(FactKind.Goals, team.Team, shots.Where(item => item.Outcome == ShotOutcome.Goal));
            AddCount(FactKind.Shots, team.Team, shots);
            AddCount(FactKind.SavedShots, team.Team, shots.Where(item => item.Outcome == ShotOutcome.Saved));
            AddCount(FactKind.OffTargetShots, team.Team, shots.Where(item => item.Outcome == ShotOutcome.OffTarget));
            AddCount(FactKind.BlockedShots, team.Team, shots.Where(item => item.Outcome == ShotOutcome.Blocked));
            AddCount(FactKind.ShotsOnTarget, team.Team,
                shots.Where(item => item.Outcome is ShotOutcome.Goal or ShotOutcome.Saved));
            AddFact(FactKind.ShotConversion, team.Team, teamShots.ConversionPercent, FactUnit.Percent,
                teamShots.Evidence, teamShots.Goals, teamShots.Shots);
            AddCount(FactKind.Passes, team.Team, passes);
            AddCount(FactKind.CompletedPasses, team.Team,
                passes.Where(item => item.Outcome == PassOutcome.Completed));
            AddFact(FactKind.PassCompletion, team.Team, team.PassCompletionPercent, FactUnit.Percent,
                EvidenceFor(passes), team.CompletedPasses, team.Passes);
            AddCount(FactKind.Fouls, team.Team,
                dataset.Events.OfType<Foul>().Where(item => item.Team == team.Team));
            AddCount(FactKind.Tackles, team.Team,
                dataset.Events.OfType<Tackle>().Where(item => item.Team == team.Team));
            AddCount(FactKind.PressureEvents, team.Team,
                dataset.Events.OfType<Pressure>().Where(item => item.Team == team.Team));
            AddFact(FactKind.ControlSequences, team.Team, teamControl.SequenceCount,
                FactUnit.Count, teamControl.Evidence);
            AddFact(FactKind.ControlDuration, team.Team, Seconds(teamControl.ObservedDuration),
                FactUnit.Seconds, teamControl.Evidence);
        }

        var moments = ImmutableArray.CreateBuilder<MatchMoment>();
        var candidates = ImmutableArray.CreateBuilder<InsightCandidate>();
        for (var index = 0; index < dataset.Events.Length; index++)
        {
            var item = dataset.Events[index];
            switch (item)
            {
                case Shot { Outcome: ShotOutcome.Goal } goal:
                    AddMoment(MomentKind.Goal, FactKind.GoalOccurrence, goal.Team,
                        shotSequences[goal.Id].Id, EvidenceFor([goal]),
                        FormattableString.Invariant($"Team {goal.Team.Value} scored"));
                    break;
                case ControlChanged change:
                    AddMoment(MomentKind.ControlTransition, FactKind.ControlTransitionOccurrence,
                        change.To.Team, null, EvidenceFor([change]),
                        $"Control changed from {ControlName(change.From)} to {ControlName(change.To)}");
                    break;
                case Foul foul when timeline[index].Control.Kind == BallControlKind.Team:
                    var interrupted = closedTeamSequences[foul.Sequence];
                    AddMoment(MomentKind.FoulInterruption, FactKind.FoulInterruptionOccurrence,
                        foul.Team, interrupted.Id, interrupted.Evidence,
                        FormattableString.Invariant(
                            $"A foul by team {foul.Team.Value} interrupted {ControlName(interrupted.Control)} control"));
                    break;
            }

            void AddMoment(
                MomentKind kind, FactKind factKind, TeamId? team, ControlSequenceId? sequence,
                Evidence evidence, string statement)
            {
                var id = new MomentId(moments.Count + 1);
                var fact = AddFact(factKind, team, 1, FactUnit.Count, evidence);
                var position = ControlAnalyzer.Position(item);
                var factIds = ImmutableArray.Create(fact);
                moments.Add(new MatchMoment(id, kind, position, team, sequence, factIds, evidence));
                candidates.Add(new InsightCandidate(id, kind, team, position,
                    kind == MomentKind.Goal ? CandidatePriority.ScoreUpdate : CandidatePriority.ContextUpdate,
                    FormattableString.Invariant($"{statement} at period {item.Period}, {item.Clock:c}."),
                    factIds, evidence));
            }
        }

        return new MatchIntelligence(scope, summary, possession, shooting, sequences,
            shotsTimeline, facts.ToImmutable(), moments.ToImmutable(), candidates.ToImmutable(), dataset.Events);

        Evidence EvidenceFor(IEnumerable<MatchEvent> events) =>
            new(scope, events.Select(item => item.Id).ToImmutableArray());

        Evidence EvidenceForSequences(IEnumerable<ControlSequence> selected)
        {
            var ids = selected.SelectMany(sequence => sequence.Evidence.SourceEventIds).ToHashSet();
            return EvidenceFor(dataset.Events.Where(item => ids.Contains(item.Id)));
        }

        void AddCount(FactKind kind, TeamId team, IEnumerable<MatchEvent> events)
        {
            var sources = events.ToArray();
            AddFact(kind, team, sources.Length, FactUnit.Count, EvidenceFor(sources));
        }

        FactId AddFact(
            FactKind kind, TeamId? team, decimal? value, FactUnit unit, Evidence evidence,
            int? numerator = null, int? denominator = null)
        {
            var id = new FactId(facts.Count + 1);
            facts.Add(new CalculatedFact(id, kind, team, value, unit, numerator, denominator, evidence));
            return id;
        }
    }

    private static decimal? Percentage(int numerator, int denominator) =>
        denominator == 0 ? null : 100m * numerator / denominator;

    private static decimal Seconds(TimeSpan duration) => (decimal)duration.Ticks / TimeSpan.TicksPerSecond;

    private static string ControlName(BallControl control) => control.Kind switch
    {
        BallControlKind.Team when control.Team is { } team => FormattableString.Invariant($"team {team.Value}"),
        BallControlKind.Contested => "contested",
        BallControlKind.DeadBall => "dead ball",
        _ => throw new InvalidOperationException("Unknown validated control state.")
    };
}
