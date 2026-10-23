using System.Collections.Immutable;
using System.Text.Json;
using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Fixtures.SyntheticMatch;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Intelligence;

public sealed class IntelligenceEdgeTests
{
    [Fact]
    public void Empty_prefix_has_no_time_sequences_shots_moments_or_candidates()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Prefix(Reference(), 0));

        Assert.Equal(0, analysis.Summary.EventCount);
        Assert.Empty(analysis.Summary.Periods);
        Assert.Empty(analysis.ControlSequences);
        Assert.Empty(analysis.ShotTimeline);
        Assert.Empty(analysis.Moments);
        Assert.Empty(analysis.Candidates);
        Assert.Equal(TimeSpan.Zero, analysis.Possession.ObservedDuration);
        Assert.Equal(BallControl.DeadBall, analysis.Possession.CurrentControl);
        Assert.All(analysis.Shooting, team =>
        {
            Assert.Equal(0, team.Shots);
            Assert.Null(team.ConversionPercent);
        });
        Assert.All(analysis.Summary.Teams, team => Assert.Null(team.PassCompletionPercent));
        Assert.All(analysis.Facts, fact =>
        {
            Assert.Empty(fact.Evidence.SourceEventIds);
            if (fact.Unit == FactUnit.Percent)
            {
                Assert.Null(fact.Value);
                Assert.Equal(0, fact.Numerator);
                Assert.Equal(0, fact.Denominator);
            }
            else
            {
                Assert.Equal(0m, fact.Value);
            }
        });
    }

    [Fact]
    public void Observing_only_a_period_start_does_not_invent_its_duration()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Prefix(Reference(), 1));

        var sequence = Assert.Single(analysis.ControlSequences);
        Assert.Equal(TimeSpan.Zero, sequence.ObservedDuration);
        Assert.False(sequence.IsClosed);
        Assert.False(analysis.Summary.Periods[0].HasEnded);
        Assert.Equal(TimeSpan.Zero, analysis.Summary.Periods[0].ObservedUntil);
        Assert.Empty(analysis.Moments);
    }

    [Theory]
    [InlineData(ShotOutcome.Goal, 1, 1, 100)]
    [InlineData(ShotOutcome.Saved, 0, 1, 0)]
    [InlineData(ShotOutcome.OffTarget, 0, 0, 0)]
    [InlineData(ShotOutcome.Blocked, 0, 0, 0)]
    public void Each_shot_outcome_has_an_explicit_non_overlapping_classification(
        ShotOutcome outcome, int goals, int onTarget, int conversion)
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Shot(new(3), 3, 1, At(5), Home, HomeOne, outcome));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);
        var shots = analysis.Shooting[0];

        Assert.Equal(1, shots.Shots);
        Assert.Equal(goals, shots.Goals);
        Assert.Equal(onTarget, shots.OnTarget);
        Assert.Equal((decimal)conversion, shots.ConversionPercent);
        Assert.Equal(outcome == ShotOutcome.Saved ? 1 : 0, shots.Saved);
        Assert.Equal(outcome == ShotOutcome.OffTarget ? 1 : 0, shots.OffTarget);
        Assert.Equal(outcome == ShotOutcome.Blocked ? 1 : 0, shots.Blocked);
        Assert.Equal(shots.Shots, shots.Goals + shots.Saved + shots.OffTarget + shots.Blocked);
        Assert.Equal(goals, analysis.Moments.Count(moment => moment.Kind == MomentKind.Goal));
        Assert.Equal(outcome, Assert.Single(analysis.ShotTimeline).Outcome);
    }

    [Fact]
    public void Multiple_shots_in_one_control_sequence_remain_distinct()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Shot(new(3), 3, 1, At(5), Home, HomeOne, ShotOutcome.Blocked),
            new Shot(new(4), 4, 1, At(5), Home, HomeTwo, ShotOutcome.OffTarget),
            new Shot(new(5), 5, 1, At(6), Home, HomeOne, ShotOutcome.Goal));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(3, analysis.Shooting[0].Shots);
        Assert.Equal(100m / 3, analysis.Shooting[0].ConversionPercent);
        Assert.Equal(1, analysis.Shooting[0].Goals);
        Assert.Equal(1, analysis.Shooting[0].OnTarget);
        Assert.Equal(1, analysis.Shooting[0].Blocked);
        Assert.Equal(1, analysis.Shooting[0].OffTarget);
        Assert.Single(analysis.ShotTimeline.Select(shot => shot.ControlSequence).Distinct());
        Assert.Equal(new[] { new EventId(3), new EventId(4), new EventId(5) },
            analysis.ShotTimeline.Select(shot => shot.EventId));
    }

    [Fact]
    public void Consecutive_goals_require_restarts_and_each_counts_once()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Shot(new(3), 3, 1, At(5), Home, HomeOne, ShotOutcome.Goal),
            new ControlChanged(new(4), 4, 1, At(5), BallControl.DeadBall, BallControl.By(Away)),
            new Shot(new(5), 5, 1, At(5), Away, AwayOne, ShotOutcome.Goal));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal((1, 1), (analysis.Summary.Snapshot.HomeScore, analysis.Summary.Snapshot.AwayScore));
        Assert.Equal(2, analysis.Moments.Count(moment => moment.Kind == MomentKind.Goal));
        Assert.Equal(2, analysis.ShotTimeline.Length);
        Assert.Equal(TimeSpan.Zero, analysis.Possession.Teams[1].ObservedDuration);
    }

    [Fact]
    public void Foul_on_dead_ball_does_not_invent_an_interrupted_team_sequence()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new Foul(new(2), 2, 1, At(5), Away, AwayOne));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(1, analysis.Summary.Fouls);
        Assert.Empty(analysis.Moments);
        Assert.Single(analysis.ControlSequences);
        Assert.Equal(At(5), analysis.Possession.DeadBallDuration);
    }

    [Fact]
    public void Foul_on_contested_ball_closes_neutral_sequence_without_team_interruption()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.Contested),
            new Foul(new(3), 3, 1, At(5), Away, AwayOne));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.DoesNotContain(analysis.Moments, moment => moment.Kind == MomentKind.FoulInterruption);
        Assert.Equal(At(5), analysis.Possession.ContestedDuration);
        Assert.Equal(BallControl.DeadBall, analysis.Possession.CurrentControl);
    }

    [Fact]
    public void Consecutive_fouls_on_dead_ball_do_not_duplicate_interruption_moments()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Foul(new(3), 3, 1, At(5), Away, AwayOne),
            new Foul(new(4), 4, 1, At(6), Away, AwayTwo));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(2, analysis.Summary.Fouls);
        Assert.Single(analysis.Moments.Where(moment => moment.Kind == MomentKind.FoulInterruption));
        Assert.Equal(At(1), analysis.Possession.DeadBallDuration);
    }

    [Fact]
    public void Fractional_ticks_are_preserved_in_durations_and_facts()
    {
        var time = TimeSpan.FromTicks(1234567);
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Pass(new(3), 3, 1, time, Home, HomeOne, HomeTwo, PassOutcome.Completed));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(time, analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(0.1234567m, Assert.Single(analysis.Facts.Where(fact =>
            fact.Kind == FactKind.ControlDuration && fact.Team == Home)).Value);
    }

    [Fact]
    public void A_period_with_only_dead_ball_has_a_complete_observed_duration()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new PeriodEnded(new(2), 2, 1, At(60)));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(At(60), analysis.Possession.DeadBallDuration);
        Assert.Equal(At(60), analysis.Possession.ObservedDuration);
        Assert.All(analysis.Possession.Teams, team => Assert.Equal(0, team.SequenceCount));
        Assert.True(Assert.Single(analysis.ControlSequences).IsClosed);
    }

    [Fact]
    public void Goal_at_exact_period_end_is_counted_once_with_zero_remaining_dead_time()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Shot(new(3), 3, 1, At(60), Home, HomeOne, ShotOutcome.Goal),
            new PeriodEnded(new(4), 4, 1, At(60)));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(1, analysis.Summary.Goals);
        Assert.Equal(At(60), analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(TimeSpan.Zero, analysis.Possession.DeadBallDuration);
        Assert.Equal(TimeSpan.Zero, analysis.ControlSequences[^1].ObservedDuration);
        Assert.True(analysis.ControlSequences[^1].IsClosed);
    }

    [Theory]
    [InlineData("unknownPlayer", ValidationError.UnknownPlayer)]
    [InlineData("unknownTeam", ValidationError.UnknownTeam)]
    [InlineData("sequence", ValidationError.InvalidSequence)]
    [InlineData("duplicateGoal", ValidationError.DuplicateEvent)]
    [InlineData("invalidClock", ValidationError.InvalidClock)]
    [InlineData("invalidPeriod", ValidationError.InvalidPeriod)]
    [InlineData("opponentShot", ValidationError.InvalidAction)]
    [InlineData("synthetic", ValidationError.InvalidManifest)]
    public void Contradictory_datasets_are_rejected_before_returning_intelligence(
        string scenario, ValidationError expected)
    {
        var dataset = Reference();
        dataset = scenario switch
        {
            "unknownPlayer" => dataset with
            {
                Events = dataset.Events.SetItem(2, new Pass(new(3), 3, 1, At(5),
                    Home, new PlayerId(999), HomeTwo, PassOutcome.Completed))
            },
            "unknownTeam" => dataset with
            {
                Events = dataset.Events.SetItem(2, new Pass(new(3), 3, 1, At(5),
                    new TeamId(999), HomeOne, HomeTwo, PassOutcome.Completed))
            },
            "sequence" => dataset with { Events = dataset.Events.SetItem(2, dataset.Events[2] with { Sequence = 99 }) },
            "duplicateGoal" => dataset with { Events = dataset.Events.SetItem(18, dataset.Events[18] with { Id = new(9) }) },
            "invalidClock" => dataset with { Events = dataset.Events.SetItem(2, dataset.Events[2] with { Clock = At(-1) }) },
            "invalidPeriod" => dataset with { Events = dataset.Events.SetItem(2, dataset.Events[2] with { Period = 3 }) },
            "opponentShot" => dataset with
            {
                Events = dataset.Events.SetItem(2, new Shot(new(3), 3, 1, At(5), Away, AwayOne, ShotOutcome.Goal))
            },
            "synthetic" => dataset with { Manifest = dataset.Manifest with { IsSynthetic = false } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        Assert.Equal(expected,
            Assert.Throws<DomainValidationException>(() => FootballIntelligenceEngine.Analyze(dataset)).Code);
    }

    [Fact]
    public void Full_input_is_validated_even_if_the_invalid_event_is_at_the_tail()
    {
        var dataset = Reference();
        dataset = dataset with
        {
            Events = dataset.Events.Add(new Shot(new(21), 21, 2, At(60), Home, HomeOne, ShotOutcome.Goal))
        };

        Assert.Equal(ValidationError.InvalidPeriodBoundary,
            Assert.Throws<DomainValidationException>(() => FootballIntelligenceEngine.Analyze(dataset)).Code);
    }

    [Fact]
    public void New_reducer_timeline_matches_all_existing_prefix_replays()
    {
        var dataset = Reference();
        var timeline = MatchReducer.ReplayTimeline(dataset);

        Assert.Equal(dataset.Events.Length + 1, timeline.Length);
        for (var count = 0; count < timeline.Length; count++)
        {
            Assert.Equal(MatchReducer.Replay(Prefix(dataset, count)), timeline[count]);
        }
    }

    [Fact]
    public void Event_identity_is_not_confused_with_contiguous_sequence()
    {
        var dataset = Reference();
        dataset = dataset with
        {
            Events = dataset.Events.Select(item => item with
            {
                Id = new EventId(item.Id.Value + 1000)
            }).ToImmutableArray()
        };
        var analysis = FootballIntelligenceEngine.Analyze(dataset);
        var firstShot = analysis.ShotTimeline[0];

        Assert.Equal(new EventId(1009), firstShot.EventId);
        Assert.Equal(9, firstShot.Position.Sequence);
        Assert.Equal(new[] { new EventId(1007), new EventId(1008), new EventId(1009) },
            analysis.ControlSequences.Single(sequence => sequence.Id == firstShot.ControlSequence)
                .Evidence.SourceEventIds);
        Assert.All(analysis.Facts, fact => Assert.Equal(fact.Evidence.SourceEventIds,
            EvidenceResolver.Resolve(analysis, fact.Evidence).Select(item => item.Id)));
    }

    [Fact]
    public void More_than_two_configured_periods_are_kept_separate_without_overflowing_clock()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new PeriodEnded(new(2), 2, 1, At(60)),
            new PeriodStarted(new(3), 3, 2, At(0)),
            new PeriodEnded(new(4), 4, 2, At(60)),
            new PeriodStarted(new(5), 5, 3, At(0)),
            new PeriodEnded(new(6), 6, 3, At(30)));
        dataset = dataset with
        {
            Match = dataset.Match with
            {
                Periods = [new MatchPeriod(1, At(60)), new MatchPeriod(2, At(60)), new MatchPeriod(3, At(30))]
            }
        };
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(At(150), analysis.Possession.ObservedDuration);
        Assert.Equal(At(150), analysis.Possession.DeadBallDuration);
        Assert.Equal(3, analysis.ControlSequences.Length);
        Assert.Equal(3, analysis.Summary.Periods.Length);
        Assert.True(analysis.Summary.Snapshot.IsComplete);
    }

    [Fact]
    public void Input_events_remain_unchanged_after_analysis()
    {
        var dataset = Reference();
        var events = dataset.Events.ToArray();
        var snapshot = MatchReducer.Replay(dataset);
        FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(events, dataset.Events.ToArray());
        Assert.Equal(snapshot, MatchReducer.Replay(dataset));
    }

    [Fact]
    public void Reanalysis_does_not_accumulate_counts()
    {
        var full = Reference();
        var first = FootballIntelligenceEngine.Analyze(full);
        FootballIntelligenceEngine.Analyze(Prefix(full, 0));
        var repeated = FootballIntelligenceEngine.Analyze(full);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
        Assert.Equal(2, repeated.Summary.Goals);
    }

    [Fact]
    public void Null_input_has_an_explicit_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => FootballIntelligenceEngine.Analyze(null!));
    }
}
