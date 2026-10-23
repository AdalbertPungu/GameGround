using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Fixtures.SyntheticMatch;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Intelligence;

public sealed class EvidenceTests
{
    [Fact]
    public void Every_fact_resolves_to_ordered_original_events_within_scope()
    {
        var dataset = Reference();
        var analysis = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Equal(dataset.Manifest, analysis.Scope.Manifest);
        Assert.True(analysis.Scope.Manifest.IsSynthetic);
        Assert.Equal(dataset.Match.MatchId, analysis.Scope.MatchId);
        Assert.Equal(FootballIntelligenceEngine.RulesVersion, analysis.Scope.RulesVersion);
        Assert.Equal(20, analysis.Scope.ThroughSequence);
        Assert.Equal(analysis.Facts.Length, analysis.Facts.Select(fact => fact.Id).Distinct().Count());
        foreach (var fact in analysis.Facts)
        {
            var resolved = EvidenceResolver.Resolve(analysis, fact.Evidence);
            Assert.Equal(fact.Evidence.SourceEventIds, resolved.Select(item => item.Id));
            Assert.Equal(resolved.OrderBy(item => item.Sequence), resolved);
            Assert.All(resolved, item => Assert.Contains(item, dataset.Events));
            Assert.Equal(analysis.Scope, fact.Evidence.Scope);
        }
    }

    [Fact]
    public void Every_candidate_links_to_a_moment_facts_and_resolvable_evidence()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());

        Assert.Equal(analysis.Moments.Length, analysis.Candidates.Length);
        foreach (var candidate in analysis.Candidates)
        {
            var moment = Assert.Single(analysis.Moments.Where(moment => moment.Id == candidate.MomentId));
            Assert.Equal(moment.Kind, candidate.Type);
            Assert.Equal(moment.Position, candidate.Position);
            Assert.Equal(moment.Team, candidate.Team);
            Assert.Equal(moment.FactIds, candidate.FactIds);
            Assert.Equal(moment.Evidence, candidate.Evidence);
            Assert.NotEmpty(EvidenceResolver.Resolve(analysis, candidate.Evidence));
            Assert.All(candidate.FactIds, id =>
            {
                var fact = Assert.Single(analysis.Facts.Where(fact => fact.Id == id));
                Assert.Equal(1m, fact.Value);
                Assert.Equal(candidate.Evidence, fact.Evidence);
            });
        }
    }

    [Fact]
    public void Control_evidence_includes_start_end_and_intervening_opponent_actions()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var sequence = analysis.ControlSequences[1];
        var sources = EvidenceResolver.Resolve(analysis, sequence.Evidence);

        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, sources.Select(item => item.Id.Value));
        Assert.IsType<Pressure>(sources[2]);
        Assert.IsType<Tackle>(sources[3]);
        Assert.Equal(BallControl.By(Home), sequence.Control);
    }

    [Fact]
    public void Zero_counts_are_scoped_absence_not_invented_positive_sources()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var fouls = Assert.Single(analysis.Facts.Where(fact => fact.Kind == FactKind.Fouls && fact.Team == Home));

        Assert.Equal(0m, fouls.Value);
        Assert.Empty(fouls.Evidence.SourceEventIds);
        Assert.Equal(20, fouls.Evidence.Scope.ThroughSequence);
        Assert.Empty(EvidenceResolver.Resolve(analysis, fouls.Evidence));
    }

    [Fact]
    public void Unknown_evidence_source_is_rejected_explicitly()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var evidence = new Evidence(analysis.Scope, [new EventId(999)]);

        Assert.Throws<ArgumentException>(() => EvidenceResolver.Resolve(analysis, evidence));
    }

    [Fact]
    public void Evidence_from_a_different_dataset_scope_is_rejected()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var otherScope = analysis.Scope with
        {
            Manifest = analysis.Scope.Manifest with { DatasetId = "another-dataset" }
        };

        Assert.Throws<ArgumentException>(() => EvidenceResolver.Resolve(
            analysis, new Evidence(otherScope, [new EventId(9)])));
    }

    [Theory]
    [InlineData("match")]
    [InlineData("cutoff")]
    [InlineData("rules")]
    public void Other_scope_dimensions_cannot_be_interchanged(string dimension)
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var scope = dimension switch
        {
            "match" => analysis.Scope with { MatchId = "another-match" },
            "cutoff" => analysis.Scope with { ThroughSequence = 9 },
            "rules" => analysis.Scope with { RulesVersion = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

        Assert.Throws<ArgumentException>(() =>
            EvidenceResolver.Resolve(analysis, new Evidence(scope, [new EventId(9)])));
    }

    [Fact]
    public void Evidence_from_a_later_prefix_is_rejected()
    {
        var dataset = Reference();
        var early = FootballIntelligenceEngine.Analyze(Prefix(dataset, 9));
        var late = FootballIntelligenceEngine.Analyze(dataset);

        Assert.Throws<ArgumentException>(() => EvidenceResolver.Resolve(early, late.Facts[0].Evidence));
        Assert.Throws<ArgumentException>(() => EvidenceResolver.Resolve(
            early, new Evidence(early.Scope, [new EventId(19)])));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Duplicate_or_reordered_evidence_sources_are_rejected(bool duplicate)
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var evidence = new Evidence(analysis.Scope, duplicate
            ? [new EventId(9), new EventId(9)]
            : [new EventId(9), new EventId(7)]);

        Assert.Throws<ArgumentException>(() => EvidenceResolver.Resolve(analysis, evidence));
    }

    [Fact]
    public void Uninitialized_evidence_is_rejected()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        Assert.Throws<ArgumentException>(() =>
            EvidenceResolver.Resolve(analysis, new Evidence(analysis.Scope, default)));
    }

    [Fact]
    public void All_other_evidence_surfaces_resolve_to_source_events()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var evidence = analysis.Summary.Periods.Select(period => period.Evidence)
            .Concat(analysis.Possession.Teams.Select(team => team.Evidence))
            .Concat(analysis.Shooting.Select(team => team.Evidence))
            .Concat(analysis.ControlSequences.Select(sequence => sequence.Evidence))
            .Concat(analysis.ShotTimeline.Select(shot => shot.Evidence));

        foreach (var item in evidence)
        {
            Assert.Equal(item.SourceEventIds.Length, EvidenceResolver.Resolve(analysis, item).Length);
        }
    }

    [Fact]
    public void Every_count_fact_can_be_recomputed_from_its_sources()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        var eventCountKinds = new[]
        {
            FactKind.Events, FactKind.PeriodsStarted, FactKind.PeriodsEnded,
            FactKind.Goals, FactKind.Shots, FactKind.SavedShots, FactKind.OffTargetShots,
            FactKind.BlockedShots, FactKind.ShotsOnTarget, FactKind.Passes,
            FactKind.CompletedPasses, FactKind.Fouls, FactKind.Tackles,
            FactKind.PressureEvents, FactKind.ExplicitControlChanges, FactKind.GoalOccurrence,
            FactKind.ControlTransitionOccurrence
        };
        foreach (var fact in analysis.Facts.Where(fact => eventCountKinds.Contains(fact.Kind)))
        {
            Assert.Equal((decimal)EvidenceResolver.Resolve(analysis, fact.Evidence).Length, fact.Value);
        }
    }

    [Fact]
    public void Every_percentage_preserves_the_exact_ratio_without_rounding()
    {
        var analysis = FootballIntelligenceEngine.Analyze(Reference());
        foreach (var fact in analysis.Facts.Where(fact => fact.Unit == FactUnit.Percent))
        {
            Assert.NotNull(fact.Numerator);
            Assert.NotNull(fact.Denominator);
            Assert.Equal(fact.Denominator, fact.Evidence.SourceEventIds.Length);
            Assert.Equal(100m * fact.Numerator / fact.Denominator, fact.Value);
        }
    }
}
