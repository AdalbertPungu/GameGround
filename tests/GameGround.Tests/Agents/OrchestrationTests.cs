using System.Collections.Immutable;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Intelligence;
using Xunit.Abstractions;
using static GameGround.Tests.Agents.AgentTestData;

namespace GameGround.Tests.Agents;

public sealed class OrchestrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Reference_workflow_is_verified_end_to_end_without_external_runtime()
    {
        var state = await Workflow(new SimulatedAnalyst()).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(16, state.Observations.Length);
        Assert.Equal(16, state.NarrativeClaims.Length);
        Assert.Equal(32, state.Verifications.Length);
        Assert.All(state.Observations.Concat(state.NarrativeClaims),
            claim => Assert.Equal(ClaimStatus.Verified, claim.Status));
        Assert.Contains(state.History, decision => decision.Agent.Role == AgentRole.Narrator
            && decision.Kind == WorkflowDecisionKind.Invoked);
        output.WriteLine(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task Rejected_claim_is_corrected_with_explicit_feedback_then_accepted()
    {
        var analyst = Producing(Analyst, request =>
        {
            var claim = PassClaim(request.Context);
            return [request.Attempt == 0
                ? claim with { Metric = claim.Metric! with { Value = 42 } }
                : claim];
        });
        var state = await Workflow(analyst).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, analyst.Requests.Count);
        Assert.Equal(ClaimStatus.Rejected, Assert.Single(analyst.Requests[1].Feedback).Status);
        Assert.NotEmpty(analyst.Requests[1].RevisionReasons);
        Assert.Single(analyst.Requests[1].PreviousClaims);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.RevisionRequested);
        Assert.Equal(ClaimStatus.Verified, Assert.Single(state.NarrativeClaims).Status);
    }

    [Fact]
    public async Task Revision_limit_stops_bad_agent_without_publishing_partial_batch()
    {
        var analyst = Producing(Analyst, request =>
        {
            var valid = PassClaim(request.Context);
            return [valid, valid with
            {
                Id = new ClaimId(2),
                Text = "Meadow United dominated the midfield."
            }];
        });
        var narrator = Producing(Narrator, _ => throw new InvalidOperationException("Must not run."));
        var state = await Workflow(analyst, narrator, revisions: 2).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Equal(3, analyst.Requests.Count);
        Assert.Empty(narrator.Requests);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(6, state.Verifications.Length);
    }

    [Fact]
    public async Task Two_analysts_producing_same_claim_are_deduplicated_by_meaning()
    {
        var second = new AgentIdentity("second-test-analyst", AgentRole.Analyst);
        var firstAgent = Producing(Analyst, request => [PassClaim(request.Context)]);
        var secondAgent = Producing(second, request => [PassClaim(request.Context, second)]);
        var workflow = new LocalOrchestrator([firstAgent, secondAgent], new SimulatedNarrator());
        var state = await workflow.RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(Analyst, Assert.Single(state.Observations).Producer);
        Assert.Single(state.NarrativeClaims);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.DuplicateRemoved
            && decision.Agent == second);
    }

    [Fact]
    public async Task Same_batch_semantic_duplicates_are_deduplicated()
    {
        var analyst = Producing(Analyst, request =>
        {
            var claim = PassClaim(request.Context);
            return [claim, claim with { Id = new ClaimId(2) }];
        });
        var state = await Workflow(analyst).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Single(state.Observations);
        Assert.Single(state.NarrativeClaims);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.DuplicateRemoved);
    }

    [Fact]
    public async Task Cross_agent_deduplication_records_the_actual_revision_attempt()
    {
        var second = new AgentIdentity("second-test-analyst", AgentRole.Analyst);
        var firstAgent = Producing(Analyst, request => [PassClaim(request.Context)]);
        var secondAgent = Producing(second, request =>
        {
            var claim = PassClaim(request.Context, second);
            return [request.Attempt == 0 ? claim with { Evidence = [] } : claim];
        });
        var workflow = new LocalOrchestrator([firstAgent, secondAgent], new SimulatedNarrator());
        var state = await workflow.RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        var deduplication = Assert.Single(state.History.Where(decision =>
            decision.Kind == WorkflowDecisionKind.DuplicateRemoved && decision.Agent == second));
        Assert.Equal(1, deduplication.Attempt);
    }

    [Fact]
    public async Task Repeated_workflow_has_identical_entire_serialized_state()
    {
        var workflow = Workflow(new SimulatedAnalyst());
        var first = await workflow.RunAsync(Context());
        var second = await workflow.RunAsync(Context());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(Enumerable.Range(1, first.History.Length), first.History.Select(decision => decision.Step));
    }

    [Fact]
    public async Task Narrative_cannot_add_even_a_correct_fact_not_in_approved_handoff()
    {
        var analyst = Producing(Analyst, request => [PassClaim(request.Context)]);
        var narrator = Producing(Narrator, request =>
        {
            Assert.Single(request.ApprovedObservations);
            var fact = request.Context.Facts.First(item => item.Kind == FactKind.Shots);
            return [ClaimText.ForFact(request.Context, fact.Id, new ClaimId(1), Narrator)];
        });
        var state = await Workflow(analyst, narrator, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.Verifications, attempt => attempt.Result.RejectionReason is not null
            && attempt.Result.RejectionReason.Contains("not handed off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Narrative_is_verified_again_not_trusted_because_observations_were_verified()
    {
        var analyst = Producing(Analyst, request => [PassClaim(request.Context)]);
        var narrator = Producing(Narrator, request =>
        {
            var claim = request.ApprovedObservations.Single();
            return [claim with
            {
                Producer = Narrator,
                Status = ClaimStatus.Proposed,
                Text = claim.Text + " This proves tactical superiority."
            }];
        });
        var state = await Workflow(analyst, narrator, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(ClaimStatus.Rejected, state.Verifications.Last().Result.Status);
    }

    [Fact]
    public async Task Narrator_can_revise_a_rejected_claim()
    {
        var analyst = Producing(Analyst, request => [PassClaim(request.Context)]);
        var narrator = Producing(Narrator, request =>
        {
            var claim = request.ApprovedObservations.Single() with
            {
                Producer = Narrator,
                Status = ClaimStatus.Proposed
            };
            return [request.Attempt == 0 ? claim with { Evidence = [] } : claim];
        });
        var state = await Workflow(analyst, narrator).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, narrator.Requests.Count);
        Assert.Single(state.NarrativeClaims);
    }

    [Fact]
    public async Task Null_output_is_rejected_with_an_explanatory_revision_request()
    {
        var analyst = new ScriptedAgent(Analyst, (request, _) =>
            Task.FromResult(request.Attempt == 0 ? null! : new AgentOutput([PassClaim(request.Context)])));
        var state = await Workflow(analyst).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.NotEmpty(analyst.Requests[1].RevisionReasons);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.MalformedOutput);
    }

    [Fact]
    public async Task Uninitialized_claim_array_is_malformed_not_an_empty_success()
    {
        var analyst = new ScriptedAgent(Analyst, (_, _) => Task.FromResult(new AgentOutput(default)));
        var state = await Workflow(analyst, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.MalformedOutput);
    }

    [Fact]
    public async Task Producer_spoofing_is_rejected()
    {
        var analyst = Producing(Analyst, request => [PassClaim(request.Context, Narrator)]);
        var state = await Workflow(analyst, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.MalformedOutput);
    }

    [Fact]
    public async Task Duplicate_batch_ids_are_malformed_not_semantically_deduplicated()
    {
        var analyst = Producing(Analyst, request =>
        {
            var claim = PassClaim(request.Context);
            return [claim, claim];
        });
        var state = await Workflow(analyst, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.DoesNotContain(state.History, decision => decision.Kind == WorkflowDecisionKind.DuplicateRemoved);
    }

    [Fact]
    public async Task Timeout_fails_explicitly_without_overlapping_retry()
    {
        var analyst = new ScriptedAgent(Analyst, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentOutput([]);
        });
        var state = await Workflow(analyst, revisions: 2, timeout: TimeSpan.FromMilliseconds(100)).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Single(analyst.Requests);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.Timeout);
        Assert.DoesNotContain(state.History, decision => decision.Kind == WorkflowDecisionKind.RevisionRequested);
    }

    [Fact]
    public async Task Known_agent_failure_is_recorded_without_success_fallback()
    {
        var analyst = Producing(Analyst, _ => throw new AgentExecutionException("Local fixture unavailable."));
        var state = await Workflow(analyst).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.AgentFailure
            && decision.Reason == "Local fixture unavailable.");
    }

    [Fact]
    public async Task Unexpected_programming_error_is_not_silently_caught()
    {
        var analyst = Producing(Analyst, _ => throw new InvalidOperationException("Programming error."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(analyst).RunAsync(Context()));
    }

    [Fact]
    public async Task Precancelled_workflow_has_explicit_cancelled_state()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var analyst = Producing(Analyst, _ => throw new InvalidOperationException("Must not run."));
        var state = await Workflow(analyst).RunAsync(Context(), cancellation.Token);
        Assert.Equal(WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(analyst.Requests);
        Assert.Empty(state.NarrativeClaims);
    }

    [Fact]
    public async Task Cancellation_during_agent_execution_stops_workflow()
    {
        using var cancellation = new CancellationTokenSource();
        var analyst = new ScriptedAgent(Analyst, async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AgentOutput([]);
        });
        var state = await Workflow(analyst).RunAsync(Context(), cancellation.Token);
        Assert.Equal(WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(state.NarrativeClaims);
    }

    [Fact]
    public async Task Empty_observation_selection_skips_narrator_explicitly()
    {
        var analyst = Producing(Analyst, _ => []);
        var narrator = Producing(Narrator, _ => throw new InvalidOperationException("Must not run."));
        var state = await Workflow(analyst, narrator).RunAsync(Context(0));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(narrator.Requests);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.Skipped);
    }

    [Fact]
    public async Task Empty_prefix_does_not_generate_an_automatic_match_recap()
    {
        var state = await Workflow(new SimulatedAnalyst()).RunAsync(Context(0));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Empty(state.Verifications);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.Skipped);
    }

    [Fact]
    public async Task Empty_narration_after_nonempty_observations_is_not_a_success()
    {
        var analyst = Producing(Analyst, request => [PassClaim(request.Context)]);
        var narrator = Producing(Narrator, _ => []);
        var state = await Workflow(analyst, narrator, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
    }

    [Fact]
    public async Task Agents_receive_only_window_scoped_sources()
    {
        var context = Context(8);
        var state = await Workflow(new SimulatedAnalyst()).RunAsync(context);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.All(state.Observations.Concat(state.NarrativeClaims), claim =>
        {
            Assert.All(claim.CoveredSourceEvents, id => Assert.InRange(id.Value, 1, 8));
            Assert.Equal(context.Identity, claim.Context);
        });
    }

    [Fact]
    public async Task Second_period_workflow_narrates_only_recorded_moments_not_global_counts()
    {
        var context = AgentContextFactory.Create(
            GameGround.Tests.Intelligence.IntelligenceTestData.Reference(), new AnalysisWindow(17, 20));
        var state = await Workflow(new SimulatedAnalyst()).RunAsync(context);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, state.NarrativeClaims.Length);
        Assert.All(state.NarrativeClaims, claim =>
        {
            Assert.Equal(ClaimType.RecordedMoment, claim.Type);
            Assert.All(claim.CoveredSourceEvents, id => Assert.InRange(id.Value, 17, 20));
        });
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(11, 1000)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 300001)]
    public void Invalid_workflow_bounds_are_rejected(int revisions, int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Workflow(new SimulatedAnalyst(), revisions: revisions,
            timeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Agent_identity_roles_and_uniqueness_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new LocalOrchestrator([], new SimulatedNarrator()));
        Assert.Throws<ArgumentException>(() => new LocalOrchestrator([new SimulatedNarrator()], new SimulatedNarrator()));
        Assert.Throws<ArgumentException>(() => new LocalOrchestrator([new SimulatedAnalyst()], new SimulatedAnalyst()));
        var analyst = new SimulatedAnalyst();
        Assert.Throws<ArgumentException>(() =>
            new LocalOrchestrator([analyst, analyst], new SimulatedNarrator()));
    }
}
