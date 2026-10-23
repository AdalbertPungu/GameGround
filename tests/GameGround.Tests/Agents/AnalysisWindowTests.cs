using System.Collections.Immutable;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Agents.AgentTestData;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Agents;

public sealed class AnalysisWindowTests
{
    [Fact]
    public void Full_context_exposes_engine_projections_not_raw_events()
    {
        var context = Context();
        Assert.Equal((47, 10, 12), (context.Facts.Length, context.Candidates.Length, context.Sequences.Length));
        Assert.Equal("harbor-meadow-miniature", context.Identity.Scope.MatchId);
        Assert.Equal("synthetic-miniature-v1", context.Identity.Scope.Manifest.DatasetId);
        Assert.Equal(FootballIntelligenceEngine.RulesVersion, context.Identity.Scope.RulesVersion);
        Assert.Null(typeof(AgentContext).GetProperty("SourceEvents"));
        Assert.Equal(new EventPosition(1, 1, TimeSpan.Zero), context.From);
        Assert.Equal(new EventPosition(20, 2, TimeSpan.FromSeconds(60)), context.Through);
    }

    [Fact]
    public void Every_prefix_is_calculated_before_projection_and_never_contains_future_sources()
    {
        var dataset = Reference();
        for (var cutoff = 0; cutoff <= dataset.Events.Length; cutoff++)
        {
            var context = AgentContextFactory.Through(dataset, cutoff);
            var expected = FootballIntelligenceEngine.Analyze(Prefix(dataset, cutoff));
            Assert.Equal(JsonSerializer.Serialize(expected.Facts), JsonSerializer.Serialize(context.Facts));
            Assert.Equal(cutoff, context.Identity.Scope.ThroughSequence);
            var query = new EvidenceQuery(context);
            foreach (var fact in query.GetFacts())
            {
                var entry = Assert.Single(query.GetEvidence([fact.Id]));
                Assert.Equal(fact.Evidence, entry.Evidence);
                Assert.All(entry.Evidence.SourceEventIds, id => Assert.InRange(id.Value, 1, cutoff));
            }

            Assert.All(context.Sequences, sequence =>
            {
                Assert.InRange(sequence.Start.Sequence, 1, cutoff);
                Assert.InRange(sequence.End.Sequence, 1, cutoff);
                Assert.All(sequence.Evidence.SourceEventIds, id => Assert.InRange(id.Value, 1, cutoff));
            });
            Assert.All(context.Moments, moment => Assert.InRange(query.GetMoment(moment.Id).Position.Sequence, 1, cutoff));
            Assert.All(context.Candidates, candidate => Assert.InRange(candidate.Position.Sequence, 1, cutoff));
        }
    }

    [Fact]
    public void Unseen_suffix_neither_changes_identity_nor_visible_state_even_if_invalid()
    {
        var dataset = Reference();
        var changed = dataset with
        {
            Events = dataset.Events.SetItem(19, dataset.Events[19] with { Sequence = 900 })
        };
        Assert.Equal(JsonSerializer.Serialize(AgentContextFactory.Through(dataset, 9)),
            JsonSerializer.Serialize(AgentContextFactory.Through(changed, 9)));
    }

    [Fact]
    public void Same_ordinal_ids_with_different_source_content_get_different_identities()
    {
        var dataset = Reference();
        var changed = dataset with
        {
            Events = dataset.Events.SetItem(8, ((Shot)dataset.Events[8]) with { Outcome = ShotOutcome.Saved })
        };
        Assert.NotEqual(AgentContextFactory.Through(dataset, 9).Identity,
            AgentContextFactory.Through(changed, 9).Identity);
    }

    [Fact]
    public void Structurally_identical_actions_with_distinct_types_have_distinct_content_identity()
    {
        var dataset = Reference();
        var pressure = Assert.IsType<Pressure>(dataset.Events[3]);
        var tackle = new Tackle(pressure.Id, pressure.Sequence, pressure.Period,
            pressure.Clock, pressure.Team, pressure.Player);
        var changed = dataset with { Events = dataset.Events.SetItem(3, tackle) };
        Assert.NotEqual(AgentContextFactory.Through(dataset, 4).Identity,
            AgentContextFactory.Through(changed, 4).Identity);
    }

    [Fact]
    public void Null_query_context_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new EvidenceQuery(null!));
    }

    [Fact]
    public void Second_period_keeps_occurrences_but_does_not_relabel_cumulative_metrics()
    {
        var context = AgentContextFactory.Period(Reference(), 2, 20);
        Assert.Equal(new AnalysisWindow(17, 20), context.Identity.Window);
        Assert.Equal(2, context.Facts.Length);
        Assert.Equal(2, context.Candidates.Length);
        Assert.Equal(3, context.Sequences.Length);
        Assert.DoesNotContain(context.Facts, fact => fact.Kind is FactKind.Goals or FactKind.Shots or FactKind.ControlDuration);
        Assert.All(context.Evidence, entry =>
            Assert.All(entry.Evidence.SourceEventIds, id => Assert.InRange(id.Value, 17, 20)));
    }

    [Fact]
    public void Time_window_preserves_tie_order_and_removes_sequences_started_before_window()
    {
        var context = AgentContextFactory.TimeWindow(Reference(), 1, TimeSpan.FromSeconds(7),
            TimeSpan.FromSeconds(12), 9);
        Assert.Equal(new AnalysisWindow(5, 9), context.Identity.Window);
        Assert.All(context.Sequences, sequence => Assert.True(sequence.Start.Sequence >= 5));
        Assert.DoesNotContain(context.Sequences, sequence => sequence.Start.Sequence == 2);
        Assert.All(context.Moments.Where(moment => moment.ControlSequence.HasValue),
            moment => Assert.Contains(context.Sequences, sequence => sequence.Id == moment.ControlSequence));
    }

    [Fact]
    public void Partially_supported_foul_is_omitted_not_rewritten()
    {
        var context = AgentContextFactory.Create(Reference(), new AnalysisWindow(11, 11));
        Assert.Empty(context.Facts);
        Assert.Empty(context.Candidates);
        Assert.Empty(context.Moments);
    }

    [Fact]
    public void Time_window_never_broadens_its_sequence_cutoff_at_equal_clock()
    {
        var context = AgentContextFactory.TimeWindow(Reference(), 1, TimeSpan.Zero, TimeSpan.Zero, 1);
        Assert.Equal(new AnalysisWindow(1, 1), context.Identity.Window);
        Assert.Empty(context.Candidates);
        Assert.DoesNotContain(context.Evidence.SelectMany(entry => entry.Evidence.SourceEventIds),
            id => id.Value == 2);
    }

    [Fact]
    public void Before_moment_is_exclusive()
    {
        var context = AgentContextFactory.Before(Reference(), new EventPosition(9, 1, TimeSpan.FromSeconds(12)));
        Assert.Equal(8, context.Identity.Window.ThroughSequence);
        Assert.DoesNotContain(context.Facts, fact => fact.Kind == FactKind.GoalOccurrence);
        Assert.All(context.Evidence, entry => Assert.DoesNotContain(new EventId(9), entry.Evidence.SourceEventIds));
    }

    [Fact]
    public void Before_rejects_a_position_that_does_not_match_source()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentContextFactory.Before(Reference(), new EventPosition(9, 2, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 21)]
    [InlineData(4, 2)]
    [InlineData(2, 1)]
    [InlineData(1, -1)]
    public void Invalid_sequence_ranges_fail_explicitly(int from, int through)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentContextFactory.Create(Reference(), new AnalysisWindow(from, through)));
    }

    [Fact]
    public void Empty_prefix_has_no_future_duration_or_moment()
    {
        var context = Context(0);
        Assert.Null(context.From);
        Assert.Null(context.Through);
        Assert.Empty(context.Sequences);
        Assert.Empty(context.Moments);
        Assert.All(context.Facts, fact => Assert.Empty(fact.Evidence.SourceEventIds));
        Assert.Contains(context.Facts, fact => fact.Kind == FactKind.ObservedDuration && fact.Value == 0);
    }

    [Fact]
    public void Unknown_queries_fail_instead_of_returning_success_shaped_empty_data()
    {
        var query = new EvidenceQuery(Context(8));
        Assert.Throws<ArgumentException>(() => query.GetEvidence([new FactId(900)]));
        Assert.Throws<ArgumentException>(() => query.GetEvidence(default));
        Assert.Throws<ArgumentException>(() => query.GetSequence(new ControlSequenceId(900)));
        Assert.Throws<ArgumentException>(() => query.GetMoment(new MomentId(900)));
    }

    [Fact]
    public void Queries_cannot_resolve_full_match_future_ids_in_a_prefix()
    {
        var prefix = Context(8);
        var full = Context();
        var future = full.Candidates.Last();
        var query = new EvidenceQuery(prefix);
        Assert.Throws<ArgumentException>(() => query.GetMoment(future.MomentId));
        Assert.Throws<ArgumentException>(() => query.GetEvidence(future.FactIds));
        Assert.Throws<ArgumentException>(() => query.GetSequence(full.Sequences.Last().Id));
    }

    [Fact]
    public void Absent_period_and_reversed_time_ranges_fail_explicitly()
    {
        Assert.Throws<ArgumentException>(() => AgentContextFactory.Period(Reference(), 2, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentContextFactory.TimeWindow(
            Reference(), 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 20));
    }

    [Fact]
    public void Invalid_visible_prefix_still_uses_domain_validation()
    {
        var dataset = Reference();
        dataset = dataset with { Events = dataset.Events.SetItem(2, dataset.Events[2] with { Sequence = 90 }) };
        Assert.Throws<DomainValidationException>(() => AgentContextFactory.Through(dataset, 3));
    }

    [Fact]
    public void Event_identity_is_not_mistaken_for_sequence_or_clock()
    {
        var dataset = Reference();
        dataset = dataset with
        {
            Events = dataset.Events.Select(item => item with { Id = new EventId(item.Id.Value + 1000) })
                .ToImmutableArray()
        };
        var context = AgentContextFactory.Through(dataset, 8);
        Assert.Equal(ClaimStatus.Verified, new ClaimVerifier().Verify(context, PassClaim(context)).Status);
        Assert.All(context.Evidence, entry => Assert.All(entry.Evidence.SourceEventIds,
            id => Assert.InRange(id.Value, 1001, 1008)));
    }
}
