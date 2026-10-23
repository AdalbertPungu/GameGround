using System.Globalization;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Agents.AgentTestData;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Agents;

public sealed class ClaimVerificationTests
{
    private readonly ClaimVerifier verifier = new();

    [Fact]
    public void Correct_sourced_numeric_claim_is_accepted()
    {
        var context = Context();
        var claim = PassClaim(context);
        var result = verifier.Verify(context, claim);
        Assert.Equal(ClaimStatus.Verified, result.Status);
        Assert.All(result.Checks, check => Assert.True(check.Passed, check.Kind.ToString()));
        Assert.Equal(11, result.Checks.Length);
        Assert.Equal(new EventId(8), Assert.Single(result.SourceEvents));
        Assert.Null(result.RejectionReason);
        Assert.Equal("Meadow United: recorded pass completion = 100% (1/1), within sequences 1-20.", claim.Text);
    }

    [Fact]
    public void Wrong_structured_number_is_rejected_even_with_correct_text()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { Metric = claim.Metric! with { Value = 90 } },
            VerificationCheckKind.NumericValuesConsistent);
    }

    [Fact]
    public void Wrong_printed_number_is_rejected_even_with_correct_structured_metric()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { Text = claim.Text.Replace("100%", "90%", StringComparison.Ordinal) },
            VerificationCheckKind.NumericValuesConsistent);
    }

    [Fact]
    public void Wrong_ratio_is_rejected()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { Metric = claim.Metric! with { Denominator = 2 } },
            VerificationCheckKind.NumericValuesConsistent);
    }

    [Fact]
    public void Evidence_is_required()
    {
        var context = Context();
        Reject(context, PassClaim(context) with { Evidence = [] }, VerificationCheckKind.HasEvidence);
    }

    [Fact]
    public void Unknown_evidence_is_rejected()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with
        {
            Evidence = [new EvidenceReference(new EvidenceId(999), context.Identity)]
        }, VerificationCheckKind.EvidenceExists);
    }

    [Fact]
    public void Evidence_from_another_match_is_rejected()
    {
        var context = Context();
        var dataset = Reference();
        var other = AgentContextFactory.Through(dataset with
        {
            Match = dataset.Match with { MatchId = "another-match" }
        }, 20);
        Reject(context, PassClaim(context) with { Evidence = PassClaim(other).Evidence },
            VerificationCheckKind.SameMatch);
    }

    [Fact]
    public void Evidence_from_another_manifest_is_rejected()
    {
        var context = Context();
        var dataset = Reference();
        var other = AgentContextFactory.Through(dataset with
        {
            Manifest = dataset.Manifest with { DatasetId = "another-dataset" }
        }, 20);
        Reject(context, PassClaim(context) with { Evidence = PassClaim(other).Evidence },
            VerificationCheckKind.SameMatch);
    }

    [Fact]
    public void Future_evidence_is_rejected_even_when_fact_ordinal_exists()
    {
        var context = Context(8);
        Reject(context, PassClaim(Context()), VerificationCheckKind.NoFutureEventLeakage);
    }

    [Fact]
    public void Altered_content_with_same_ids_and_cutoff_is_rejected()
    {
        var dataset = Reference();
        var other = AgentContextFactory.Through(dataset with
        {
            Events = dataset.Events.SetItem(2, ((Pass)dataset.Events[2]) with { Outcome = PassOutcome.Incomplete })
        }, 20);
        Reject(Context(), PassClaim(other), VerificationCheckKind.SameWindow);
    }

    [Fact]
    public void Dominance_is_not_inferred_from_pass_completion()
    {
        var context = Context();
        Reject(context, PassClaim(context) with { Text = "Meadow United dominated the midfield." },
            VerificationCheckKind.NoUnsupportedTacticalInference);
    }

    [Fact]
    public void Correct_number_with_broader_unrecorded_scope_is_rejected()
    {
        var context = Context();
        Reject(context, PassClaim(context) with { Text = "Meadow United completed 100% of all its passes (1/1)." },
            VerificationCheckKind.WordingWithinKnownData);
    }

    [Fact]
    public void Wrong_team_and_metric_are_rejected()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { Metric = claim.Metric! with { Team = new TeamId(1), Kind = FactKind.Shots } },
            VerificationCheckKind.ReferencedMetricMatches);
    }

    [Fact]
    public void Extra_fact_does_not_broaden_claim_evidence()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { FactIds = claim.FactIds.Add(context.Facts[0].Id) },
            VerificationCheckKind.ReferencedMetricMatches);
    }

    [Fact]
    public void Unknown_fact_is_explicit_in_result()
    {
        var context = Context();
        var result = verifier.Verify(context, PassClaim(context) with { FactIds = [new FactId(900)] });
        Assert.Equal(ClaimStatus.Rejected, result.Status);
        Assert.Equal(new FactId(900), Assert.Single(result.UnsupportedFacts));
        Assert.Empty(result.SupportedFacts);
    }

    [Fact]
    public void Extra_future_source_id_is_rejected()
    {
        var context = Context(8);
        var claim = PassClaim(context);
        Reject(context, claim with { CoveredSourceEvents = claim.CoveredSourceEvents.Add(new EventId(9)) },
            VerificationCheckKind.NoFutureEventLeakage);
    }

    [Fact]
    public void Missing_and_duplicate_covered_source_ids_are_rejected()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { CoveredSourceEvents = [] }, VerificationCheckKind.SourceCoverageMatches);
        Reject(context, claim with { CoveredSourceEvents = [new EventId(8), new EventId(8)] },
            VerificationCheckKind.SourceCoverageMatches);
    }

    [Fact]
    public void Duplicate_fact_references_are_rejected()
    {
        var context = Context();
        var claim = PassClaim(context);
        Reject(context, claim with { FactIds = [claim.FactIds[0], claim.FactIds[0]] },
            VerificationCheckKind.EvidenceExists);
    }

    [Fact]
    public void All_engine_moment_claims_are_verifiable()
    {
        var context = Context();
        foreach (var candidate in context.Candidates)
        {
            var claim = ClaimText.ForMoment(context, candidate.MomentId, new ClaimId(1), Analyst);
            Assert.Equal(ClaimStatus.Verified, verifier.Verify(context, claim).Status);
        }
    }

    [Fact]
    public void All_available_non_null_metrics_are_verifiable()
    {
        var context = Context();
        foreach (var fact in context.Facts.Where(fact => fact.Value.HasValue
            && fact.Kind is not (FactKind.GoalOccurrence or FactKind.ControlTransitionOccurrence
                or FactKind.FoulInterruptionOccurrence)))
        {
            var claim = ClaimText.ForFact(context, fact.Id, new ClaimId(1), Analyst);
            Assert.Equal(ClaimStatus.Verified, verifier.Verify(context, claim).Status);
        }
    }

    [Fact]
    public void Zero_is_supported_by_a_scoped_fact_not_invented_positive_events()
    {
        var context = Context(0);
        var fact = context.Facts.First(item => item.Kind == FactKind.Goals);
        var claim = ClaimText.ForFact(context, fact.Id, new ClaimId(1), Analyst);
        Assert.NotEmpty(claim.Evidence);
        Assert.Empty(claim.CoveredSourceEvents);
        Assert.Equal(ClaimStatus.Verified, verifier.Verify(context, claim).Status);
    }

    [Fact]
    public void Undefined_percentage_cannot_be_asserted()
    {
        var context = Context(0);
        var fact = context.Facts.First(item => item.Kind == FactKind.PassCompletion);
        Assert.Null(fact.Value);
        Assert.Throws<ArgumentException>(() => ClaimText.ForFact(context, fact.Id, new ClaimId(1), Analyst));
    }

    [Fact]
    public void Claim_helpers_reject_unavailable_fact_moment_and_team()
    {
        var context = Context();
        Assert.Throws<ArgumentException>(() => ClaimText.ForFact(context, new FactId(900), new ClaimId(1), Analyst));
        Assert.Throws<ArgumentException>(() => ClaimText.ForMoment(context, new MomentId(900), new ClaimId(1), Analyst));
        Assert.Throws<ArgumentException>(() => ClaimText.RenderMetric(context,
            PassClaim(context).Metric! with { Team = new TeamId(900) }));
    }

    [Fact]
    public void Uninitialized_output_is_rejected_not_thrown()
    {
        var context = Context();
        Reject(context, PassClaim(context) with { Evidence = default }, VerificationCheckKind.WellFormedClaim);
        Reject(context, PassClaim(context) with { Context = null! }, VerificationCheckKind.WellFormedClaim);
        Reject(context, PassClaim(context) with
        {
            Evidence = [new EvidenceReference(new EvidenceId(1), context.Identity with { Scope = null! })]
        }, VerificationCheckKind.WellFormedClaim);
    }

    [Theory]
    [InlineData(ClaimStatus.Verified)]
    [InlineData(ClaimStatus.Rejected)]
    public void Agent_cannot_self_approve_or_reuse_a_terminal_status(ClaimStatus status)
    {
        var context = Context();
        Reject(context, PassClaim(context) with { Status = status }, VerificationCheckKind.WellFormedClaim);
    }

    [Fact]
    public void Rendering_and_verification_are_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var context = Context();
            var claim = PassClaim(context);
            Assert.Contains("100% (1/1)", claim.Text);
            Assert.Equal(ClaimStatus.Verified, verifier.Verify(context, claim).Status);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private void Reject(AgentContext context, Claim claim, VerificationCheckKind failedCheck)
    {
        var result = verifier.Verify(context, claim);
        Assert.Equal(ClaimStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains(result.Checks, check => check.Kind == failedCheck && !check.Passed);
    }
}
