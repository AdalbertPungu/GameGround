using System.Globalization;
using System.Text.Json;
using GameGround.Core;
using GameGround.Intelligence;
using Xunit.Abstractions;
using static GameGround.Tests.Fixtures.SyntheticMatch;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Intelligence;

public sealed class ReferenceAnalysisTests(ITestOutputHelper output)
{
    [Fact]
    public void Reference_summary_matches_the_twenty_events()
    {
        var dataset = Reference();
        var analysis = FootballIntelligenceEngine.Analyze(dataset);
        var summary = analysis.Summary;

        Assert.Equal(MatchReducer.Replay(dataset), summary.Snapshot);
        Assert.Equal((20, 2, 3, 3, 1),
            (summary.EventCount, summary.Goals, summary.Shots, summary.Passes, summary.Fouls));
        Assert.Equal(2, summary.Periods.Length);
        Assert.All(summary.Periods, period =>
        {
            Assert.True(period.HasEnded);
            Assert.Equal(At(60), period.ObservedUntil);
        });
        Assert.Equal(new TeamSummary(Home, 1, 2, 1, 50m, 0, 0, 0), summary.Teams[0]);
        Assert.Equal(new TeamSummary(Away, 1, 1, 1, 100m, 1, 1, 1), summary.Teams[1]);
    }

    [Fact]
    public void Reference_control_durations_partition_observed_period_time()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());

        Assert.Equal(7, analysis.Possession.ExplicitTransitionCount);
        Assert.Equal(BallControl.DeadBall, analysis.Possession.CurrentControl);
        Assert.Equal(At(120), analysis.Possession.ObservedDuration);
        Assert.Equal(At(1), analysis.Possession.ContestedDuration);
        Assert.Equal(At(57), analysis.Possession.DeadBallDuration);
        Assert.Equal((Home, 4, At(15)),
            (analysis.Possession.Teams[0].Team, analysis.Possession.Teams[0].SequenceCount,
                analysis.Possession.Teams[0].ObservedDuration));
        Assert.Equal((Away, 2, At(47)),
            (analysis.Possession.Teams[1].Team, analysis.Possession.Teams[1].SequenceCount,
                analysis.Possession.Teams[1].ObservedDuration));
        Assert.Equal(analysis.Possession.ObservedDuration,
            analysis.Possession.Teams[0].ObservedDuration
            + analysis.Possession.Teams[1].ObservedDuration
            + analysis.Possession.ContestedDuration + analysis.Possession.DeadBallDuration);
    }

    [Fact]
    public void Reference_sequences_have_exact_control_boundaries_and_sources()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        Assert.Equal(12, analysis.ControlSequences.Length);
        var expected = new (BallControl Control, int Start, int End, int Seconds)[]
        {
            (BallControl.DeadBall, 1, 2, 0),
            (BallControl.By(Home), 2, 6, 7),
            (BallControl.Contested, 6, 7, 1),
            (BallControl.By(Away), 7, 9, 4),
            (BallControl.DeadBall, 9, 10, 1),
            (BallControl.By(Home), 10, 11, 1),
            (BallControl.DeadBall, 11, 12, 1),
            (BallControl.By(Home), 12, 14, 2),
            (BallControl.By(Away), 14, 16, 43),
            (BallControl.DeadBall, 17, 18, 0),
            (BallControl.By(Home), 18, 19, 5),
            (BallControl.DeadBall, 19, 20, 55)
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var sequence = analysis.ControlSequences[index];
            var row = expected[index];
            Assert.Equal(new ControlSequenceId(index + 1), sequence.Id);
            Assert.Equal(row.Control, sequence.Control);
            Assert.Equal(row.Start, sequence.Start.Sequence);
            Assert.Equal(row.End, sequence.End.Sequence);
            Assert.Equal(At(row.Seconds), sequence.ObservedDuration);
            Assert.True(sequence.IsClosed);
            Assert.Equal(sequence.Start.Period, sequence.End.Period);
            Assert.Equal(Enumerable.Range(row.Start, row.End - row.Start + 1).Select(id => new EventId(id)),
                sequence.Evidence.SourceEventIds);
        }
    }

    [Fact]
    public void Reference_shooting_and_chronology_are_exact()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var home = analysis.Shooting[0];
        var away = analysis.Shooting[1];

        Assert.Equal((1, 1, 0, 0, 0, 1, 100m),
            (home.Shots, home.Goals, home.Saved, home.OffTarget, home.Blocked, home.OnTarget, home.ConversionPercent));
        Assert.Equal((2, 1, 1, 0, 0, 2, 50m),
            (away.Shots, away.Goals, away.Saved, away.OffTarget, away.Blocked, away.OnTarget, away.ConversionPercent));
        Assert.Equal(new[] { new EventId(9), new EventId(15), new EventId(19) },
            analysis.ShotTimeline.Select(shot => shot.EventId));
        Assert.Equal(new[] { new ControlSequenceId(4), new ControlSequenceId(9), new ControlSequenceId(11) },
            analysis.ShotTimeline.Select(shot => shot.ControlSequence));
        Assert.Equal(new EventPosition(9, 1, At(12)), analysis.ShotTimeline[0].Position);
        Assert.Equal(new EventPosition(19, 2, At(5)), analysis.ShotTimeline[2].Position);
        Assert.Equal(new[] { new EventId(9), new EventId(19) },
            analysis.ShotTimeline.Where(shot => shot.Outcome == ShotOutcome.Goal).Select(shot => shot.EventId));
    }

    [Fact]
    public void Reference_moments_follow_only_the_documented_rules()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());

        Assert.Equal(10, analysis.Moments.Length);
        Assert.Equal(2, analysis.Moments.Count(moment => moment.Kind == MomentKind.Goal));
        Assert.Equal(7, analysis.Moments.Count(moment => moment.Kind == MomentKind.ControlTransition));
        var foul = Assert.Single(analysis.Moments.Where(moment => moment.Kind == MomentKind.FoulInterruption));
        Assert.Equal(Away, foul.Team);
        Assert.Equal(new ControlSequenceId(6), foul.ControlSequence);
        Assert.Equal(new[] { new EventId(10), new EventId(11) }, foul.Evidence.SourceEventIds);
        Assert.Equal(analysis.Moments.Select(moment => moment.Position.Sequence).Order(),
            analysis.Moments.Select(moment => moment.Position.Sequence));
        Assert.DoesNotContain(analysis.Moments, moment => moment.Position.Sequence is 4 or 5 or 15);
    }

    [Fact]
    public void Reference_candidates_are_literal_sourced_statements()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var goal = Assert.Single(analysis.Candidates.Where(candidate => candidate.Position.Sequence == 9));
        Assert.Equal("Team 2 scored at period 1, 00:00:12.", goal.Statement);
        Assert.Equal(CandidatePriority.ScoreUpdate, goal.Priority);
        Assert.Equal(new[] { new EventId(9) }, goal.Evidence.SourceEventIds);
        var change = Assert.Single(analysis.Candidates.Where(candidate => candidate.Position.Sequence == 6));
        Assert.Equal("Control changed from team 1 to contested at period 1, 00:00:07.", change.Statement);
        var foul = Assert.Single(analysis.Candidates.Where(candidate => candidate.Position.Sequence == 11));
        Assert.Equal("A foul by team 2 interrupted team 1 control at period 1, 00:00:14.", foul.Statement);
        Assert.Equal(CandidatePriority.ContextUpdate, foul.Priority);
    }

    [Fact]
    public void Reference_facts_preserve_numerators_denominators_units_and_sources()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        Assert.Equal(47, analysis.Facts.Length);
        var conversion = Assert.Single(analysis.Facts.Where(fact => fact.Kind == FactKind.ShotConversion && fact.Team == Away));
        Assert.Equal(50m, conversion.Value);
        Assert.Equal(FactUnit.Percent, conversion.Unit);
        Assert.Equal((1, 2), (conversion.Numerator, conversion.Denominator));
        Assert.Equal(new[] { new EventId(9), new EventId(15) }, conversion.Evidence.SourceEventIds);
        var duration = Assert.Single(analysis.Facts.Where(fact => fact.Kind == FactKind.ControlDuration && fact.Team == Home));
        Assert.Equal(15m, duration.Value);
        Assert.Equal(FactUnit.Seconds, duration.Unit);
        Assert.Null(duration.Denominator);
    }

    [Fact]
    public void Determinism_compares_every_output_field_and_every_source_event()
    {
        var first = FootballIntelligenceEngine.Analyze(Reference());
        var second = FootballIntelligenceEngine.Analyze(Reference());

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(first.SourceEvents.ToArray(), second.SourceEvents.ToArray());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Entire_analysis_is_independent_of_current_culture(string culture)
    {
        var dataset = Reference();
        var expected = JsonSerializer.Serialize(FootballIntelligenceEngine.Analyze(dataset));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, JsonSerializer.Serialize(FootballIntelligenceEngine.Analyze(dataset)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Independent_concurrent_analyses_have_identical_results()
    {
        var dataset = Reference();
        var expected = JsonSerializer.Serialize(FootballIntelligenceEngine.Analyze(dataset));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => JsonSerializer.Serialize(FootballIntelligenceEngine.Analyze(dataset)))));
        Assert.All(results, result => Assert.Equal(expected, result));
    }

    [Fact]
    public void Every_prefix_is_analyzed_without_future_events_or_duration()
    {
        var dataset = Reference();
        for (var count = 0; count <= dataset.Events.Length; count++)
        {
            var prefix = Prefix(dataset, count);
            var analysis = FootballIntelligenceEngine.Analyze(prefix);
            Assert.Equal(MatchReducer.Replay(prefix), analysis.Summary.Snapshot);
            Assert.Equal(count, analysis.Scope.ThroughSequence);
            Assert.Equal(count, analysis.SourceEvents.Length);
            Assert.All(analysis.Moments, moment => Assert.True(moment.Position.Sequence <= count));
            Assert.All(analysis.Facts, fact =>
                Assert.All(fact.Evidence.SourceEventIds, id => Assert.Contains(prefix.Events, item => item.Id == id)));
            var expectedTicks = prefix.Events.GroupBy(item => item.Period).Sum(group => group.Last().Clock.Ticks);
            Assert.Equal(TimeSpan.FromTicks(expectedTicks), analysis.Possession.ObservedDuration);
            Assert.Equal(analysis.Possession.ObservedDuration.Ticks,
                analysis.ControlSequences.Sum(sequence => sequence.ObservedDuration.Ticks));
        }
    }

    [Fact]
    public void Example_analysis_of_reference_json_is_printable_and_verified()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        Assert.Equal((1, 1), (analysis.Summary.Snapshot.HomeScore, analysis.Summary.Snapshot.AwayScore));
        output.WriteLine(FormattableString.Invariant(
            $"Score: 1-1; events: {analysis.Summary.EventCount}; explicit transitions: {analysis.Possession.ExplicitTransitionCount}"));
        foreach (var team in analysis.Shooting)
        {
            var control = analysis.Possession.Teams.Single(item => item.Team == team.Team);
            output.WriteLine(FormattableString.Invariant(
                $"Team {team.Team.Value}: shots={team.Shots}, goals={team.Goals}, conversion={team.ConversionPercent}%, control={control.ObservedDuration.TotalSeconds}s, sequences={control.SequenceCount}"));
        }

        output.WriteLine(FormattableString.Invariant(
            $"Contested={analysis.Possession.ContestedDuration.TotalSeconds}s; dead={analysis.Possession.DeadBallDuration.TotalSeconds}s; moments={analysis.Moments.Length}; facts={analysis.Facts.Length}"));
        foreach (var candidate in analysis.Candidates.Where(candidate => candidate.Type == MomentKind.Goal))
        {
            output.WriteLine(candidate.Statement);
            output.WriteLine($"Evidence event IDs: {string.Join(", ", candidate.Evidence.SourceEventIds.Select(id => id.Value))}");
        }
    }
}
