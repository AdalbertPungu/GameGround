using System.Collections.Immutable;
using GameGround.Core;
using GameGround.Tests.Fixtures;
using static GameGround.Tests.Fixtures.SyntheticMatch;

namespace GameGround.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Valid_dataset_reconstructs_the_exact_final_snapshot()
    {
        var snapshot = MatchReducer.Replay(Create());

        Assert.Equal(new MatchSnapshot(
            "harbor-meadow-miniature", 20, 2, At(60), PeriodStatus.Ended,
            1, 1, BallControl.DeadBall, true), snapshot);
    }

    [Fact]
    public void Empty_replay_has_zero_score_and_no_started_period()
    {
        var dataset = Create() with { Events = [] };

        Assert.Equal(new MatchSnapshot(
            dataset.Match.MatchId, 0, 0, At(0), PeriodStatus.NotStarted,
            0, 0, BallControl.DeadBall, false), MatchReducer.Replay(dataset));
    }

    [Fact]
    public void Every_valid_prefix_can_be_replayed()
    {
        var dataset = Create();
        for (var length = 0; length <= dataset.Events.Length; length++)
        {
            var snapshot = MatchReducer.Replay(Prefix(dataset, length));
            Assert.Equal(length, snapshot.LastSequence);
            Assert.Equal(length == dataset.Events.Length, snapshot.IsComplete);
        }
    }

    [Fact]
    public void Unknown_actor_is_rejected_with_event_context()
    {
        var dataset = Replace(2, PassAt(3, Home, new PlayerId(999), HomeTwo));

        var error = Reject(dataset, ValidationError.UnknownPlayer);

        Assert.Equal(new EventId(3), error.EventId);
        Assert.Contains("Player", error.Message);
    }

    [Fact]
    public void Unknown_pass_recipient_is_rejected()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, new PlayerId(999))),
            ValidationError.UnknownPlayer);
    }

    [Fact]
    public void Player_cannot_act_for_the_opponent()
    {
        Reject(Replace(2, PassAt(3, Home, AwayOne, HomeTwo)),
            ValidationError.PlayerTeamMismatch);
    }

    [Fact]
    public void Pass_recipient_must_belong_to_the_passing_team()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, AwayOne)),
            ValidationError.PlayerTeamMismatch);
    }

    [Fact]
    public void Unknown_event_team_is_rejected()
    {
        Reject(Replace(2, PassAt(3, new TeamId(999), HomeOne, HomeTwo)),
            ValidationError.UnknownTeam);
    }

    [Fact]
    public void Player_cannot_pass_to_themselves()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeOne)), ValidationError.InvalidAction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void First_sequence_must_be_one(int sequence)
    {
        Reject(Replace(0, new PeriodStarted(new(1), sequence, 1, At(0))),
            ValidationError.InvalidSequence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Repeated_backwards_or_skipped_sequences_are_rejected(int sequence)
    {
        Reject(Replace(2, PassAt(sequence, Home, HomeOne, HomeTwo)),
            ValidationError.InvalidSequence);
    }

    [Fact]
    public void Reordered_events_are_rejected()
    {
        var dataset = Create();
        Reject(dataset with
        {
            Events = dataset.Events.SetItem(2, dataset.Events[3]).SetItem(3, dataset.Events[2])
        }, ValidationError.InvalidSequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Event_identifier_must_be_positive(int id)
    {
        Reject(Replace(0, new PeriodStarted(new(id), 1, 1, At(0))), ValidationError.InvalidEvent);
    }

    [Fact]
    public void Duplicate_non_goal_event_is_rejected()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeTwo) with { Id = new(2) }),
            ValidationError.DuplicateEvent);
    }

    [Fact]
    public void Duplicate_goal_delivery_is_rejected_even_with_a_new_sequence()
    {
        var dataset = Create();
        var goal = Assert.IsType<Shot>(dataset.Events[8]);
        var duplicate = goal with { Sequence = 10 };

        Reject(Prefix(dataset, 9) with { Events = dataset.Events[..9].Add(duplicate) },
            ValidationError.DuplicateEvent);
        Assert.Equal(1, MatchReducer.Replay(Prefix(dataset, 9)).AwayScore);
    }

    [Fact]
    public void Goal_cannot_be_repeated_under_a_new_id_without_a_restart()
    {
        var dataset = Create();
        var secondShot = new Shot(new(99), 10, 1, At(12), Away, AwayOne, ShotOutcome.Goal);
        Reject(Prefix(dataset, 9) with { Events = dataset.Events[..9].Add(secondShot) },
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Score_changes_only_on_goals_and_has_no_hidden_accumulator()
    {
        var dataset = Create();
        var before = MatchReducer.Replay(Prefix(dataset, 8));
        var after = MatchReducer.Replay(Prefix(dataset, 9));
        var final = MatchReducer.Replay(dataset);

        Assert.Equal((0, 0), (before.HomeScore, before.AwayScore));
        Assert.Equal((0, 1), (after.HomeScore, after.AwayScore));
        Assert.Equal(BallControl.DeadBall, after.Control);
        Assert.Equal((1, 1), (final.HomeScore, final.AwayScore));
        Assert.Equal(final, MatchReducer.Replay(dataset));
        Assert.Equal(before, MatchReducer.Replay(Prefix(dataset, 8)));
    }

    [Theory]
    [InlineData(ShotOutcome.Saved)]
    [InlineData(ShotOutcome.OffTarget)]
    [InlineData(ShotOutcome.Blocked)]
    public void Non_goal_shots_do_not_change_score_or_infer_control(ShotOutcome outcome)
    {
        var dataset = Prefix(Create(), 9);
        var shot = Assert.IsType<Shot>(dataset.Events[8]);
        var snapshot = MatchReducer.Replay(dataset with
        {
            Events = dataset.Events.SetItem(8, shot with { Outcome = outcome })
        });

        Assert.Equal((0, 0), (snapshot.HomeScore, snapshot.AwayScore));
        Assert.Equal(BallControl.By(Away), snapshot.Control);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Undefined_period_is_rejected(int period)
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeTwo) with { Period = period }),
            ValidationError.InvalidPeriod);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void Clock_outside_period_is_rejected(int seconds)
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeTwo) with { Clock = At(seconds) }),
            ValidationError.InvalidClock);
    }

    [Fact]
    public void Clock_cannot_move_backwards_inside_a_period()
    {
        Reject(Replace(3, new Pressure(new(4), 4, 1, At(4), Away, AwayOne)),
            ValidationError.InvalidClock);
    }

    [Fact]
    public void Equal_timestamps_are_allowed_and_ordered_by_sequence()
    {
        var snapshot = MatchReducer.Replay(Prefix(Create(), 6));
        Assert.Equal(At(7), snapshot.Clock);
        Assert.Equal(BallControl.Contested, snapshot.Control);
    }

    [Fact]
    public void Second_period_resets_clock_without_resetting_score()
    {
        var snapshot = MatchReducer.Replay(Prefix(Create(), 17));
        Assert.Equal(2, snapshot.Period);
        Assert.Equal(At(0), snapshot.Clock);
        Assert.Equal((0, 1), (snapshot.HomeScore, snapshot.AwayScore));
        Assert.Equal(PeriodStatus.Active, snapshot.PeriodStatus);
    }

    [Fact]
    public void Period_start_requires_zero_clock()
    {
        Reject(Replace(0, new PeriodStarted(new(1), 1, 1, At(1))),
            ValidationError.InvalidClock);
    }

    [Fact]
    public void Period_cannot_be_skipped()
    {
        Reject(Replace(0, new PeriodStarted(new(1), 1, 2, At(0))),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Next_period_cannot_start_before_current_period_ends()
    {
        Reject(Replace(2, new PeriodStarted(new(3), 3, 2, At(0))),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Active_period_cannot_be_started_again()
    {
        Reject(Replace(2, new PeriodStarted(new(3), 3, 1, At(0))),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Action_cannot_be_assigned_to_an_inactive_period()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeTwo) with { Period = 2 }),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Action_requires_period_start()
    {
        Reject(Replace(0, PassAt(1, Home, HomeOne, HomeTwo)),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Period_end_must_match_declared_duration()
    {
        Reject(Replace(15, new PeriodEnded(new(16), 16, 1, At(59))),
            ValidationError.InvalidClock);
    }

    [Fact]
    public void Action_after_period_end_is_rejected()
    {
        Reject(Replace(16, new Foul(new(17), 17, 1, At(60), Home, HomeOne)),
            ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Period_cannot_restart_after_match_completion()
    {
        var dataset = Create();
        Reject(dataset with
        {
            Events = dataset.Events.Add(new PeriodStarted(new(21), 21, 2, At(0)))
        }, ValidationError.InvalidPeriodBoundary);
    }

    [Fact]
    public void Control_source_must_match_current_control()
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Away), BallControl.Contested)), ValidationError.InvalidControl);
    }

    [Fact]
    public void No_op_control_transition_is_rejected()
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Home), BallControl.By(Home))), ValidationError.InvalidControl);
    }

    [Fact]
    public void Control_cannot_belong_to_an_unknown_team()
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Home), BallControl.By(new TeamId(999)))), ValidationError.InvalidControl);
    }

    [Theory]
    [InlineData(BallControlKind.DeadBall)]
    [InlineData(BallControlKind.Contested)]
    public void Neutral_control_cannot_specify_a_team(BallControlKind kind)
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Home), new BallControl(kind, Away))), ValidationError.InvalidControl);
    }

    [Fact]
    public void Team_control_requires_a_team()
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Home), new BallControl(BallControlKind.Team, null))),
            ValidationError.InvalidControl);
    }

    [Fact]
    public void Unknown_control_kind_is_rejected()
    {
        Reject(Replace(5, new ControlChanged(new(6), 6, 1, At(7),
            BallControl.By(Home), new BallControl((BallControlKind)999, null))),
            ValidationError.InvalidControl);
    }

    [Fact]
    public void Pass_requires_acting_team_control()
    {
        Reject(Replace(2, PassAt(3, Away, AwayOne, AwayTwo)), ValidationError.InvalidAction);
    }

    [Fact]
    public void Shot_requires_acting_team_control()
    {
        Reject(Replace(2, new Shot(new(3), 3, 1, At(5), Away, AwayOne, ShotOutcome.Goal)),
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Shot_is_not_allowed_on_a_dead_ball_without_a_control_transition()
    {
        Reject(Replace(1, new Shot(new(2), 2, 1, At(0), Home, HomeOne, ShotOutcome.Goal)),
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Pressure_requires_opponent_control()
    {
        Reject(Replace(3, new Pressure(new(4), 4, 1, At(6), Home, HomeOne)),
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Tackle_requires_opponent_control()
    {
        Reject(Replace(4, new Tackle(new(5), 5, 1, At(7), Home, HomeOne)),
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Tackle_does_not_implicitly_transfer_control()
    {
        Assert.Equal(BallControl.By(Home), MatchReducer.Replay(Prefix(Create(), 5)).Control);
    }

    [Fact]
    public void Foul_makes_ball_dead_and_requires_explicit_restart()
    {
        var dataset = Create();
        Assert.Equal(BallControl.DeadBall, MatchReducer.Replay(Prefix(dataset, 11)).Control);
        Reject(Replace(11, new Shot(new(12), 12, 1, At(15), Home, HomeOne, ShotOutcome.Goal)),
            ValidationError.InvalidAction);
    }

    [Fact]
    public void Incomplete_pass_does_not_infer_the_next_controlling_team()
    {
        Assert.Equal(BallControl.By(Home), MatchReducer.Replay(Prefix(Create(), 13)).Control);
    }

    [Fact]
    public void Unknown_pass_outcome_is_rejected()
    {
        Reject(Replace(2, PassAt(3, Home, HomeOne, HomeTwo) with { Outcome = (PassOutcome)999 }),
            ValidationError.InvalidOutcome);
    }

    [Fact]
    public void Unknown_shot_outcome_is_rejected()
    {
        Reject(Replace(8, new Shot(new(9), 9, 1, At(12), Away, AwayOne, (ShotOutcome)999)),
            ValidationError.InvalidOutcome);
    }

    [Fact]
    public void Independent_datasets_have_equal_value_snapshots()
    {
        Assert.Equal(MatchReducer.Replay(Create()), MatchReducer.Replay(Create()));
    }

    [Fact]
    public async Task Concurrent_replays_do_not_share_mutable_state()
    {
        var dataset = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => MatchReducer.Replay(dataset))));

        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(20, dataset.Events.Length);
    }

    [Fact]
    public void Synthetic_provenance_is_explicit()
    {
        var dataset = Create();
        Assert.True(dataset.Manifest.IsSynthetic);
        Assert.Equal(DatasetManifest.SupportedSchemaVersion, dataset.Manifest.SchemaVersion);
        Assert.Equal("synthetic-miniature-v1", dataset.Manifest.DatasetId);
    }

    [Fact]
    public void Non_synthetic_dataset_is_rejected()
    {
        var dataset = Create();
        Reject(dataset with { Manifest = dataset.Manifest with { IsSynthetic = false } },
            ValidationError.InvalidManifest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Unsupported_schema_version_is_rejected(int version)
    {
        var dataset = Create();
        Reject(dataset with { Manifest = dataset.Manifest with { SchemaVersion = version } },
            ValidationError.InvalidManifest);
    }

    [Fact]
    public void Missing_dataset_name_is_rejected()
    {
        var dataset = Create();
        Reject(dataset with { Manifest = dataset.Manifest with { DatasetId = " " } },
            ValidationError.InvalidManifest);
    }

    [Fact]
    public void Player_cannot_be_registered_for_both_teams()
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with
            {
                AwayTeam = dataset.Match.AwayTeam with
                {
                    Players = [new Player(HomeOne, "Duplicate identity")]
                }
            }
        }, ValidationError.InvalidDefinition);
    }

    [Fact]
    public void Home_and_away_teams_must_differ()
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with
            {
                AwayTeam = dataset.Match.AwayTeam with { Id = Home }
            }
        }, ValidationError.InvalidDefinition);
    }

    [Fact]
    public void Team_must_have_players()
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with
            {
                HomeTeam = dataset.Match.HomeTeam with { Players = [] }
            }
        }, ValidationError.InvalidDefinition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Player_identifier_must_be_positive(int id)
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with
            {
                HomeTeam = dataset.Match.HomeTeam with
                {
                    Players = [new Player(new(id), "Invalid identity")]
                }
            }
        }, ValidationError.InvalidDefinition);
    }

    [Fact]
    public void Periods_must_be_initialized_and_nonempty()
    {
        var dataset = Create();
        Reject(dataset with { Match = dataset.Match with { Periods = [] } },
            ValidationError.InvalidDefinition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Period_definitions_must_be_numbered_in_order(int number)
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with { Periods = [new MatchPeriod(number, At(60))] }
        }, ValidationError.InvalidDefinition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Period_duration_must_be_positive(int seconds)
    {
        var dataset = Create();
        Reject(dataset with
        {
            Match = dataset.Match with { Periods = [new MatchPeriod(1, At(seconds))] }
        }, ValidationError.InvalidDefinition);
    }

    [Fact]
    public void Uninitialized_events_are_rejected()
    {
        Reject(Create() with { Events = default }, ValidationError.InvalidEvent);
    }

    [Fact]
    public void Unsupported_event_subtype_is_rejected()
    {
        Reject(Replace(2, new UnknownEvent(new(3), 3, 1, At(5))),
            ValidationError.UnsupportedEvent);
    }

    private sealed record UnknownEvent(EventId Id, int Sequence, int Period, TimeSpan Clock)
        : MatchEvent(Id, Sequence, Period, Clock);

    private static Pass PassAt(int sequence, TeamId team, PlayerId player, PlayerId recipient) =>
        new(new(3), sequence, 1, At(5), team, player, recipient, PassOutcome.Completed);

    private static MatchDataset Replace(int index, MatchEvent replacement)
    {
        var dataset = Create();
        return dataset with { Events = dataset.Events.SetItem(index, replacement) };
    }

    private static MatchDataset Prefix(MatchDataset dataset, int count) =>
        dataset with { Events = dataset.Events.Take(count).ToImmutableArray() };

    private static DomainValidationException Reject(MatchDataset dataset, ValidationError expected)
    {
        var error = Assert.Throws<DomainValidationException>(() => MatchReducer.Replay(dataset));
        Assert.Equal(expected, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }
}
