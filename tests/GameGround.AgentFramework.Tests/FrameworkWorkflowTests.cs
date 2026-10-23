using System.Collections.Immutable;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Data;
using GameGround.Intelligence;

namespace GameGround.AgentFramework.Tests;

public sealed class FrameworkWorkflowTests
{
    private static readonly AgentIdentity Analyst = new("test-analyst", AgentRole.Analyst);
    private static readonly AgentIdentity Narrator = new("test-narrator", AgentRole.Narrator);

    [Fact]
    public async Task Happy_path_hands_off_only_verified_observations_and_publishes_verified_claims()
    {
        var analyst = Producing(request => [PassClaim(request.Context)]);
        var narrator = Narrating(request =>
        {
            Assert.All(request.ApprovedObservations, claim => Assert.Equal(ClaimStatus.Verified, claim.Status));
            Assert.Single(request.ApprovedObservations);
            Assert.Empty(request.PreviousClaims);
            return Narrate(request);
        });

        var state = await Runner(analyst, narrator).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Single(analyst.Requests);
        Assert.Single(narrator.Requests);
        Assert.Single(state.Observations);
        Assert.Equal(ClaimStatus.Verified, Assert.Single(state.NarrativeClaims).Status);
        Assert.Equal(2, state.Verifications.Length);
        Assert.All(state.Verifications, item => Assert.Equal(ClaimStatus.Verified, item.Result.Status));
        Assert.All(state.History.Select((decision, index) => (decision, index)),
            item => Assert.Equal(item.index + 1, item.decision.Step));
        Assert.DoesNotContain(typeof(NarrativeRequest).GetProperties(),
            property => property.PropertyType == typeof(AgentContext) || property.PropertyType == typeof(EvidenceQuery));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    public async Task Simulated_workflow_matches_the_local_oracle(int cutoff)
    {
        var context = Context(cutoff);
        var expected = await new LocalOrchestrator([new SimulatedAnalyst()], new SimulatedNarrator()).RunAsync(context);
        var actual = await Runner(new SimulatedAnalyst()).RunAsync(context);

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public async Task Every_reference_prefix_has_equivalent_verified_outcomes()
    {
        var dataset = Dataset();
        for (var cutoff = 0; cutoff <= dataset.Events.Length; cutoff++)
        {
            var context = AgentContextFactory.Through(dataset, cutoff);
            var expected = await new LocalOrchestrator([new SimulatedAnalyst()], new SimulatedNarrator()).RunAsync(context);
            var actual = await Runner(new SimulatedAnalyst()).RunAsync(context);
            AssertEquivalent(expected, actual);
        }
    }

    [Fact]
    public async Task Invalid_analyst_batch_is_preserved_as_rejected_and_never_reaches_narrator()
    {
        var analyst = Producing(request =>
        {
            var valid = PassClaim(request.Context);
            return [valid, valid with { Id = new ClaimId(2), Evidence = [] }];
        });
        var narrator = Narrating(_ => throw new InvalidOperationException("Narrator must not run."));

        var state = await Runner(analyst, narrator, revisions: 0).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Empty(narrator.Requests);
        var rejection = Assert.Single(state.Verifications.Where(item => item.Result.Status == ClaimStatus.Rejected));
        Assert.Equal(Analyst, rejection.Agent);
        Assert.Equal(new ClaimId(2), rejection.Claim.Id);
        Assert.NotEmpty(rejection.Result.RejectionReason!);
        Assert.Contains(state.History, decision => decision.Kind == WorkflowDecisionKind.Rejected
            && decision.Agent == Analyst && decision.ClaimIds.Contains(new ClaimId(2)));
    }

    [Fact]
    public async Task Unsupported_narrative_is_rejected_without_partial_publication()
    {
        var narrator = Narrating(request =>
        {
            var valid = Narrate(request).Single();
            return [valid, valid with { Id = new ClaimId(2), Text = "Meadow United dominated the midfield." }];
        });
        var state = await Runner(Producing(request => [PassClaim(request.Context)]), narrator, revisions: 0)
            .RunAsync(Context());

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Single(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.Verifications, item => item.Agent == Narrator
            && item.Claim.Id == new ClaimId(2) && item.Result.Status == ClaimStatus.Rejected
            && item.Result.Checks.Any(check => check.Kind == VerificationCheckKind.NoUnsupportedTacticalInference
                && !check.Passed));
    }

    [Fact]
    public async Task Valid_but_not_handed_off_fact_is_rejected()
    {
        var context = Context();
        var other = new EvidenceQuery(context).GetFacts().Single(fact =>
            fact.Kind == FactKind.Shots && fact.Team == new TeamId(1));
        var narrator = Narrating(_ => [ClaimText.ForFact(context, other.Id, new ClaimId(1), Narrator)]);

        var state = await Runner(Producing(request => [PassClaim(request.Context)]), narrator, revisions: 0)
            .RunAsync(context);

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.Verifications, item =>
            item.Result.RejectionReason == "Narrative introduces a fact not handed off as an approved observation.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_stage_revises_with_feedback_then_matches_the_oracle(bool reviseNarrator)
    {
        ImmutableArray<Claim> Analyze(AgentRequest request)
        {
            var claim = PassClaim(request.Context);
            return [!reviseNarrator && request.Attempt == 0 ? claim with { Evidence = [] } : claim];
        }
        ImmutableArray<Claim> Narration(NarrativeRequest request)
        {
            var claim = Narrate(request).Single();
            return [reviseNarrator && request.Attempt == 0 ? claim with { Evidence = [] } : claim];
        }
        var analyst = Producing(Analyze);
        var narrator = Narrating(Narration);
        var expected = await new LocalOrchestrator([Producing(Analyze)], new ScriptedLocalAgent(Narrator,
            (request, _) => Task.FromResult(new AgentOutput(Narration(new NarrativeRequest(
                request.ApprovedObservations, request.PreviousClaims, request.Feedback,
                request.RevisionReasons, request.Attempt)))))).RunAsync(Context());

        var actual = await Runner(analyst, narrator).RunAsync(Context());

        AssertEquivalent(expected, actual);
        var feedback = reviseNarrator ? narrator.Requests[1].Feedback : analyst.Requests[1].Feedback;
        Assert.Equal(ClaimStatus.Rejected, Assert.Single(feedback).Status);
        Assert.Contains(actual.History, decision => decision.Kind == WorkflowDecisionKind.RevisionRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revision_exhaustion_is_bounded_for_both_roles(bool rejectNarrator)
    {
        var analyst = Producing(request =>
        {
            var claim = PassClaim(request.Context);
            return [rejectNarrator ? claim : claim with { Evidence = [] }];
        });
        var narrator = Narrating(request => [Narrate(request).Single() with { Evidence = [] }]);
        var state = await Runner(analyst, narrator, revisions: 2).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(rejectNarrator ? 1 : 3, analyst.Requests.Count);
        Assert.Equal(rejectNarrator ? 3 : 0, narrator.Requests.Count);
        Assert.Equal(2, state.History.Count(item => item.Kind == WorkflowDecisionKind.RevisionRequested));
    }

    [Fact]
    public async Task Cutoff_tools_and_verified_results_cannot_consume_future_evidence()
    {
        var dataset = Dataset();
        var cutoff = AgentContextFactory.Through(dataset, 8);
        var full = AgentContextFactory.Through(dataset, 20);
        var allowed = dataset.Events.Take(8).Select(item => item.Id).ToHashSet();
        var future = dataset.Events.Skip(8).Select(item => item.Id).ToHashSet();
        var analyst = new ScriptedLocalAgent(Analyst, async (request, token) =>
        {
            var query = new EvidenceQuery(request.Context);
            Assert.All(query.GetFacts(), fact => Assert.All(fact.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            Assert.All(query.GetEvidence(query.GetFacts().Select(fact => fact.Id).ToImmutableArray()),
                evidence => Assert.All(evidence.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            var output = await new SimulatedAnalyst().GenerateAsync(request, token);
            return new AgentOutput(output.Claims.Select(claim => claim with { Producer = Analyst }).ToImmutableArray());
        });

        var fullState = await Runner(new SimulatedAnalyst()).RunAsync(full);
        var cutoffState = await Runner(analyst).RunAsync(cutoff);

        Assert.Contains(fullState.NarrativeClaims.SelectMany(claim => claim.CoveredSourceEvents), future.Contains);
        Assert.Equal(WorkflowStatus.Completed, cutoffState.Status);
        Assert.All(cutoffState.Observations.Concat(cutoffState.NarrativeClaims), claim =>
        {
            Assert.Equal(cutoff.Identity, claim.Context);
            Assert.All(claim.CoveredSourceEvents, id => Assert.Contains(id, allowed));
        });
        Assert.All(cutoffState.Verifications,
            verification => Assert.All(verification.Result.SourceEvents, id => Assert.Contains(id, allowed)));
    }

    [Fact]
    public async Task Attempt_to_submit_a_future_context_claim_is_rejected()
    {
        var fullClaim = PassClaim(Context());
        var state = await Runner(Producing(_ => [fullClaim]), revisions: 0).RunAsync(Context(8));
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.Verifications, item => item.Result.Checks.Any(check =>
            check.Kind == VerificationCheckKind.NoFutureEventLeakage && !check.Passed));
    }

    [Fact]
    public async Task Incorrect_numeric_assertion_is_retained_as_a_rejected_claim()
    {
        var analyst = Producing(request =>
        {
            var claim = PassClaim(request.Context);
            return [claim with { Metric = claim.Metric! with { Value = 42 } }];
        });

        var state = await Runner(analyst, revisions: 0).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        var attempt = Assert.Single(state.Verifications);
        Assert.Equal(42, attempt.Claim.Metric!.Value);
        Assert.Contains(attempt.Result.Checks, check =>
            check.Kind == VerificationCheckKind.NumericValuesConsistent && !check.Passed);
    }

    [Fact]
    public async Task Precancelled_caller_does_not_invoke_either_agent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var analyst = Producing(_ => throw new InvalidOperationException("Must not run."));
        var narrator = Narrating(_ => throw new InvalidOperationException("Must not run."));

        var state = await Runner(analyst, narrator).RunAsync(Context(), cancellation.Token);

        Assert.Equal(WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(analyst.Requests);
        Assert.Empty(narrator.Requests);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(WorkflowDecisionKind.Cancelled, Assert.Single(state.History).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_stops_the_active_stage_before_returning(bool cancelNarrator)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentOutput> Block(CancellationToken token)
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Cancellation was not observed.");
            }
            finally
            {
                terminated.TrySetResult();
            }
        }
        var analyst = cancelNarrator ? Producing(request => [PassClaim(request.Context)])
            : new ScriptedLocalAgent(Analyst, (_, token) => Block(token));
        var narrator = cancelNarrator ? new ScriptedNarrator((_, token) => Block(token))
            : Narrating(_ => throw new InvalidOperationException("Must not run."));
        var execution = Runner(analyst, narrator).RunAsync(Context(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var state = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(WorkflowStatus.Cancelled, state.Status);
            Assert.Empty(state.NarrativeClaims);
            Assert.Equal(cancelNarrator ? Narrator : Analyst,
                Assert.Single(state.History.Where(item => item.Kind == WorkflowDecisionKind.Cancelled)).Agent);
            Assert.Equal(cancelNarrator ? 1 : 0, narrator.Requests.Count);
        }
        finally
        {
            cancellation.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("default")]
    [InlineData("producer")]
    [InlineData("duplicate-ids")]
    public async Task Malformed_analyst_output_has_an_explicit_bounded_rejection(string kind)
    {
        var analyst = new ScriptedLocalAgent(Analyst, (request, _) =>
        {
            var claim = PassClaim(request.Context);
            return Task.FromResult(kind switch
            {
                "null" => null!,
                "default" => new AgentOutput(default),
                "producer" => new AgentOutput([claim with { Producer = Narrator }]),
                _ => new AgentOutput([claim, claim])
            });
        });
        var state = await Runner(analyst, revisions: 0).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        var rejection = Assert.Single(state.History.Where(item => item.Kind == WorkflowDecisionKind.MalformedOutput));
        Assert.Equal(Analyst, rejection.Agent);
        Assert.NotEmpty(rejection.Reason);
    }

    [Fact]
    public async Task Empty_narration_is_rejected_after_nonempty_observations()
    {
        var state = await Runner(Producing(request => [PassClaim(request.Context)]), Narrating(_ => []), revisions: 0)
            .RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.MalformedOutput);
    }

    [Fact]
    public async Task Semantic_duplicates_across_analysts_match_the_local_oracle()
    {
        var first = Producing(request => [PassClaim(request.Context)]);
        var secondIdentity = new AgentIdentity("second-analyst", AgentRole.Analyst);
        var second = new ScriptedLocalAgent(secondIdentity,
            (request, _) => Task.FromResult(new AgentOutput([PassClaim(request.Context, secondIdentity)])));
        var context = Context();
        var expected = await new LocalOrchestrator([first, second], new SimulatedNarrator()).RunAsync(context);
        var actual = await new FrameworkWorkflowRunner([first, second], new SimulatedNarrativeAgent()).RunAsync(context);

        AssertEquivalent(expected, actual);
        Assert.Single(actual.Observations);
        Assert.Contains(actual.History, item => item.Kind == WorkflowDecisionKind.DuplicateRemoved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_stage_failures_are_explicit_and_never_publish(bool failNarrator)
    {
        var analyst = Producing(request => failNarrator ? [PassClaim(request.Context)]
            : throw new AgentExecutionException("Scripted stage failed."));
        var narrator = Narrating(_ => throw new AgentExecutionException("Scripted stage failed."));
        var state = await Runner(analyst, narrator).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure
            && item.Reason == "Scripted stage failed." && item.Agent == (failNarrator ? Narrator : Analyst));
    }

    [Fact]
    public async Task Unexpected_programming_errors_propagate_out_of_the_sdk()
    {
        var runner = Runner(Producing(_ => throw new InvalidOperationException("Scripted programming error.")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Context()));
        Assert.Equal("Scripted programming error.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_fails_once_without_overlapping_retry_or_publication(bool timeoutNarrator)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentOutput> Block(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new AgentOutput([]);
        }
        var analyst = timeoutNarrator ? Producing(request => [PassClaim(request.Context)])
            : new ScriptedLocalAgent(Analyst, (_, token) => Block(token));
        var narrator = timeoutNarrator ? new ScriptedNarrator((_, token) => Block(token)) : Narrating(Narrate);
        var state = await Runner(analyst, narrator, timeout: TimeSpan.FromMilliseconds(100))
            .RunAsync(Context()).WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Single(analyst.Requests);
        Assert.Equal(timeoutNarrator ? 1 : 0, narrator.Requests.Count);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.Timeout);
        Assert.DoesNotContain(state.History, item => item.Kind == WorkflowDecisionKind.RevisionRequested);
    }

    [Fact]
    public async Task Repeated_and_concurrent_runs_keep_their_contexts_isolated()
    {
        var runner = Runner(new SimulatedAnalyst());
        var full = Context();
        var cutoff = Context(8);
        var states = await Task.WhenAll(runner.RunAsync(full), runner.RunAsync(cutoff), runner.RunAsync(full));

        AssertEquivalent(states[0], states[2]);
        Assert.Equal(cutoff.Identity, states[1].Context.Identity);
        Assert.All(states[1].NarrativeClaims.SelectMany(claim => claim.CoveredSourceEvents),
            id => Assert.InRange(id.Value, 1, 8));
    }

    [Fact]
    public async Task Late_output_after_timeout_has_no_route_to_publication_or_a_subsequent_run()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyst = new ScriptedLocalAgent(Analyst, async (request, _) =>
        {
            if (request.Context.Identity.Window.ThroughSequence == 20)
            {
                try { await release.Task; }
                finally { terminated.TrySetResult(); }
            }
            return new AgentOutput([PassClaim(request.Context)]);
        });
        var narrator = Narrating(Narrate);
        var runner = Runner(analyst, narrator, timeout: TimeSpan.FromMilliseconds(100));
        try
        {
            var failed = await runner.RunAsync(Context()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowStatus.Failed, failed.Status);
            Assert.Empty(narrator.Requests);

            release.TrySetResult();
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var succeeded = await runner.RunAsync(Context(8)).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(failed.NarrativeClaims);
            Assert.Empty(failed.Verifications);
            Assert.Equal(WorkflowStatus.Completed, succeeded.Status);
            Assert.Single(narrator.Requests);
            Assert.All(succeeded.NarrativeClaims.SelectMany(claim => claim.CoveredSourceEvents),
                id => Assert.InRange(id.Value, 1, 8));
        }
        finally
        {
            release.TrySetResult();
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Runtime_can_be_used_again_after_active_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyst = new ScriptedLocalAgent(Analyst, async (request, token) =>
        {
            if (request.Context.Identity.Window.ThroughSequence == 20)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new AgentOutput([PassClaim(request.Context)]);
        });
        var runner = Runner(analyst);
        var execution = runner.RunAsync(Context(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.Equal(WorkflowStatus.Cancelled,
                (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            var next = await runner.RunAsync(Context(8)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowStatus.Completed, next.Status);
            Assert.DoesNotContain(next.History, item => item.Kind == WorkflowDecisionKind.Cancelled);
        }
        finally
        {
            cancellation.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static MatchDataset Dataset() =>
        new DatasetLoader().Load(Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json"));
    private static AgentContext Context(int cutoff = 20) => AgentContextFactory.Through(Dataset(), cutoff);
    private static Claim PassClaim(AgentContext context, AgentIdentity? producer = null) =>
        ClaimText.ForFact(context, new EvidenceQuery(context).GetFacts().Single(fact =>
            fact.Kind == FactKind.PassCompletion && fact.Team == new TeamId(2)).Id,
            new ClaimId(1), producer ?? Analyst);
    private static ImmutableArray<Claim> Narrate(NarrativeRequest request) =>
        request.ApprovedObservations.Select((claim, index) => claim with
        {
            Id = new ClaimId(index + 1),
            Producer = Narrator,
            Status = ClaimStatus.Proposed
        }).ToImmutableArray();
    private static FrameworkWorkflowRunner Runner(ILocalAgent analyst, INarrativeAgent? narrator = null,
        int revisions = 1, TimeSpan? timeout = null) =>
        new([analyst], narrator ?? new SimulatedNarrativeAgent(),
            new WorkflowOptions(revisions, timeout ?? TimeSpan.FromSeconds(5)));
    private static ScriptedLocalAgent Producing(Func<AgentRequest, ImmutableArray<Claim>> produce) =>
        new(Analyst, (request, _) => Task.FromResult(new AgentOutput(produce(request))));
    private static ScriptedNarrator Narrating(Func<NarrativeRequest, ImmutableArray<Claim>> produce) =>
        new((request, _) => Task.FromResult(new AgentOutput(produce(request))));
    private static void AssertEquivalent(SharedState expected, SharedState actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(JsonSerializer.Serialize(expected.Observations), JsonSerializer.Serialize(actual.Observations));
        Assert.Equal(JsonSerializer.Serialize(expected.NarrativeClaims), JsonSerializer.Serialize(actual.NarrativeClaims));
        Assert.Equal(JsonSerializer.Serialize(expected.Verifications), JsonSerializer.Serialize(actual.Verifications));
    }

    private sealed class ScriptedLocalAgent(
        AgentIdentity identity, Func<AgentRequest, CancellationToken, Task<AgentOutput>> generate) : ILocalAgent
    {
        public AgentIdentity Identity { get; } = identity;
        public List<AgentRequest> Requests { get; } = [];
        public Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return generate(request, cancellationToken);
        }
    }

    private sealed class ScriptedNarrator(
        Func<NarrativeRequest, CancellationToken, Task<AgentOutput>> generate) : INarrativeAgent
    {
        public AgentIdentity Identity { get; } = Narrator;
        public List<NarrativeRequest> Requests { get; } = [];
        public Task<AgentOutput> GenerateAsync(NarrativeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return generate(request, cancellationToken);
        }
    }
}
