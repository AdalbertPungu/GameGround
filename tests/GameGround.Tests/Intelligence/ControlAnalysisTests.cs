using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Fixtures.SyntheticMatch;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Intelligence;

public sealed class ControlAnalysisTests
{
    [Theory]
    [InlineData(BallControlKind.Contested)]
    [InlineData(BallControlKind.DeadBall)]
    public void Team_neutral_team_transitions_preserve_every_interval(BallControlKind neutralKind)
    {
        var neutral = new BallControl(neutralKind, null);
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(10), BallControl.By(Home), neutral),
            new ControlChanged(new(4), 4, 1, At(15), neutral, BallControl.By(Away)),
            new PeriodEnded(new(5), 5, 1, At(60)));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(At(10), analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(At(45), analysis.Possession.Teams[1].ObservedDuration);
        Assert.Equal(3, analysis.Possession.ExplicitTransitionCount);
        Assert.Equal(4, analysis.ControlSequences.Length);
        Assert.Equal(At(5), analysis.ControlSequences[2].ObservedDuration);
        Assert.Equal(neutral, analysis.ControlSequences[2].Control);
        Assert.Equal(At(neutralKind == BallControlKind.Contested ? 5 : 0), analysis.Possession.ContestedDuration);
        Assert.Equal(At(neutralKind == BallControlKind.DeadBall ? 5 : 0), analysis.Possession.DeadBallDuration);
        Assert.All(analysis.Possession.Teams, team => Assert.Equal(1, team.SequenceCount));
    }

    [Fact]
    public void Direct_team_transfer_splits_sequences_without_a_neutral_gap()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(10), BallControl.By(Home), BallControl.By(Away)),
            new PeriodEnded(new(4), 4, 1, At(60)));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(At(10), analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(At(50), analysis.Possession.Teams[1].ObservedDuration);
        Assert.Equal(At(0), analysis.Possession.ContestedDuration);
        Assert.Equal(At(0), analysis.Possession.DeadBallDuration);
        Assert.Equal(2, analysis.Possession.ExplicitTransitionCount);
    }

    [Fact]
    public void Same_team_actions_do_not_start_new_control_sequences()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Pass(new(3), 3, 1, At(5), Home, HomeOne, HomeTwo, PassOutcome.Completed),
            new Pass(new(4), 4, 1, At(6), Home, HomeTwo, HomeOne, PassOutcome.Completed));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(1, analysis.Possession.Teams[0].SequenceCount);
        Assert.Equal(BallControl.By(Home), analysis.Possession.CurrentControl);
        Assert.Equal(At(6), analysis.Possession.Teams[0].ObservedDuration);
        var sequence = analysis.ControlSequences[^1];
        Assert.False(sequence.IsClosed);
        Assert.Equal(new[] { new EventId(2), new EventId(3), new EventId(4) }, sequence.Evidence.SourceEventIds);
    }

    [Fact]
    public void Returning_through_contested_starts_a_second_sequence_for_the_same_team()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(5), BallControl.By(Home), BallControl.Contested),
            new ControlChanged(new(4), 4, 1, At(7), BallControl.Contested, BallControl.By(Home)),
            new Pass(new(5), 5, 1, At(10), Home, HomeOne, HomeTwo, PassOutcome.Completed));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(2, analysis.Possession.Teams[0].SequenceCount);
        Assert.Equal(At(8), analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(At(2), analysis.Possession.ContestedDuration);
    }

    [Fact]
    public void Same_team_no_op_control_event_remains_a_domain_error()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(5), BallControl.By(Home), BallControl.By(Home)));

        var error = Assert.Throws<DomainValidationException>(() => FootballIntelligenceEngine.Analyze(dataset));
        Assert.Equal(ValidationError.InvalidControl, error.Code);
        Assert.Equal(new EventId(3), error.EventId);
    }

    [Fact]
    public void Wrong_transition_source_is_not_repaired_by_analysis()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(5), BallControl.By(Away), BallControl.By(Home)));

        Assert.Equal(ValidationError.InvalidControl,
            Assert.Throws<DomainValidationException>(() => FootballIntelligenceEngine.Analyze(dataset)).Code);
    }

    [Fact]
    public void Consecutive_transitions_at_one_timestamp_keep_zero_duration_intervals()
    {
        var dataset = WithEvents(
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new ControlChanged(new(3), 3, 1, At(0), BallControl.By(Home), BallControl.Contested),
            new ControlChanged(new(4), 4, 1, At(0), BallControl.Contested, BallControl.By(Away)));
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(4, analysis.ControlSequences.Length);
        Assert.All(analysis.ControlSequences, sequence => Assert.Equal(TimeSpan.Zero, sequence.ObservedDuration));
        Assert.Equal(At(0), analysis.Possession.ObservedDuration);
        Assert.False(analysis.ControlSequences[^1].IsClosed);
        Assert.Equal(BallControl.By(Away), analysis.Possession.CurrentControl);
        Assert.Equal(new[] { 2, 3, 4 }, analysis.Moments.Select(moment => moment.Position.Sequence));
    }

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(4, 6, 0, 0)]
    [InlineData(6, 7, 0, 0)]
    [InlineData(7, 7, 0, 1)]
    [InlineData(9, 7, 4, 1)]
    [InlineData(15, 10, 5, 1)]
    [InlineData(17, 10, 47, 1)]
    [InlineData(19, 15, 47, 1)]
    public void Prefix_durations_stop_at_the_last_observation(int count, int home, int away, int contested)
    {
        var analysis = FootballIntelligenceEngine.Analyze(Prefix(Reference(), count));

        Assert.Equal(At(home), analysis.Possession.Teams[0].ObservedDuration);
        Assert.Equal(At(away), analysis.Possession.Teams[1].ObservedDuration);
        Assert.Equal(At(contested), analysis.Possession.ContestedDuration);
        Assert.False(analysis.ControlSequences[^1].IsClosed);
    }

    [Fact]
    public void Period_boundaries_never_merge_control_sequences_or_count_halftime()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        Assert.DoesNotContain(analysis.ControlSequences, sequence => sequence.Start.Period != sequence.End.Period);
        Assert.Equal(At(120), analysis.Possession.ObservedDuration);
        Assert.Equal(new[] { 1, 2 }, analysis.Summary.Periods.Select(period => period.Number));
        Assert.DoesNotContain(analysis.Moments,
            moment => moment.Position.Sequence is 1 or 16 or 17 or 20);
    }

    [Fact]
    public void Goal_at_end_of_prefix_starts_only_a_zero_duration_dead_ball_interval()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Prefix(Reference(), 9));

        Assert.Equal(BallControl.DeadBall, analysis.ControlSequences[^1].Control);
        Assert.Equal(At(0), analysis.ControlSequences[^1].ObservedDuration);
        Assert.False(analysis.ControlSequences[^1].IsClosed);
        Assert.Equal(At(12), analysis.Possession.ObservedDuration);
        Assert.Single(analysis.ShotTimeline);
    }

    [Fact]
    public void Saved_shot_does_not_implicitly_transfer_or_end_control()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var sequence = analysis.ControlSequences.Single(sequence => sequence.Id == new ControlSequenceId(9));

        Assert.Equal(BallControl.By(Away), sequence.Control);
        Assert.Equal(At(43), sequence.ObservedDuration);
        Assert.Equal(16, sequence.End.Sequence);
    }

    [Fact]
    public void Sequence_leading_to_shot_retains_passes_and_terminal_shot()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var firstShot = analysis.ShotTimeline[0];
        var sequence = analysis.ControlSequences.Single(sequence => sequence.Id == firstShot.ControlSequence);
        var sources = EvidenceResolver.Resolve(analysis, sequence.Evidence);

        Assert.IsType<ControlChanged>(sources[0]);
        Assert.IsType<Pass>(sources[1]);
        Assert.IsType<Shot>(sources[2]);
        Assert.Equal(new[] { new EventId(7), new EventId(8), new EventId(9) }, sources.Select(item => item.Id));
    }
}
