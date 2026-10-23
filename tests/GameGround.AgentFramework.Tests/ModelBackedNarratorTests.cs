using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Data;
using GameGround.Intelligence;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework.Tests;

public sealed class ModelBackedNarratorTests
{
    [Fact]
    public async Task Selected_subset_keeps_its_host_assigned_claim_identity_and_provenance()
    {
        var model = Selecting(prompt => new NarratorModelOutput([SelectAll(prompt).Claims[2]]));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        var claim = Assert.Single(state.NarrativeClaims);
        Assert.Equal(3, claim.Id.Value);
        Assert.Equal(state.Observations[2].Text, claim.Text);
        Assert.Equal(state.Observations[2].FactIds, claim.FactIds);
        Assert.Equal(state.Observations[2].Evidence, claim.Evidence);
        Assert.Equal(state.Observations[2].CoveredSourceEvents, claim.CoveredSourceEvents);
        Assert.Equal(ClaimStatus.Verified, claim.Status);
    }

    [Fact]
    public async Task Grounded_output_is_ordered_and_passes_the_existing_final_verifier()
    {
        var model = Selecting(prompt => SelectAll(prompt) with { Claims = SelectAll(prompt).Claims.Reverse().ToArray() });
        var evaluations = new List<NarratorEvaluation>();
        var state = await Run(model, evaluation: evaluations.Add);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(state.Observations.Length, state.NarrativeClaims.Length);
        Assert.Equal(state.Observations.Reverse().Select(item => item.Text), state.NarrativeClaims.Select(item => item.Text));
        Assert.All(state.NarrativeClaims, claim =>
        {
            Assert.Equal(ClaimStatus.Verified, claim.Status);
            Assert.Equal(AgentRole.Narrator, claim.Producer.Role);
            Assert.Equal(ClaimStatus.Verified, new ClaimVerifier().Verify(
                state.Context, claim with { Status = ClaimStatus.Proposed }).Status);
        });
        var prompt = Assert.Single(model.Prompts);
        Assert.Empty(prompt.Feedback);
        Assert.Equal(NarratorSystemPrompt.Version, prompt.Version);
        Assert.Equal(20, prompt.Scope.ThroughSequence);
        Assert.Equal(state.Observations.Select(claim => new ObservationReference(claim.Producer.Id, claim.Id.Value)),
            prompt.Observations.Select(item => item.Id));
        var evaluation = Assert.Single(evaluations);
        Assert.Equal(model.Identity, evaluation.Model);
        Assert.Equal("Proposed", evaluation.Outcome);
        Assert.Empty(evaluation.Rejections);
        Assert.Equal(prompt.Scope, evaluation.Scope);
        Assert.Equal(prompt.Observations.Length, evaluation.InputObservationIds.Length);
        Assert.True(evaluation.Latency >= TimeSpan.Zero);
        Assert.Equal(state.NarrativeClaims.Length,
            state.Verifications.Count(item => item.Agent.Role == AgentRole.Narrator));
    }

    [Theory]
    [InlineData("null-output", NarratorRejection.InvalidStructure)]
    [InlineData("null-claims", NarratorRejection.InvalidStructure)]
    [InlineData("null-claim", NarratorRejection.InvalidStructure)]
    [InlineData("empty", NarratorRejection.EmptyNarrative)]
    [InlineData("blank", NarratorRejection.InvalidStructure)]
    [InlineData("zero-id", NarratorRejection.InvalidClaimId)]
    [InlineData("negative-id", NarratorRejection.InvalidClaimId)]
    [InlineData("unstable-id", NarratorRejection.InvalidClaimId)]
    [InlineData("duplicate-id", NarratorRejection.InvalidClaimId)]
    [InlineData("unknown-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("unapproved-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("duplicate-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("reused-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("no-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("null-references", NarratorRejection.InvalidObservationReference)]
    [InlineData("null-reference", NarratorRejection.InvalidObservationReference)]
    [InlineData("composite", NarratorRejection.InvalidObservationReference)]
    [InlineData("match", NarratorRejection.ScopeMismatch)]
    [InlineData("context", NarratorRejection.ScopeMismatch)]
    [InlineData("window", NarratorRejection.ScopeMismatch)]
    [InlineData("future", NarratorRejection.ScopeMismatch)]
    [InlineData("null-scope", NarratorRejection.InvalidStructure)]
    [InlineData("category", NarratorRejection.CategoryMismatch)]
    [InlineData("value", NarratorRejection.NumericMismatch)]
    [InlineData("team", NarratorRejection.NumericMismatch)]
    [InlineData("unit", NarratorRejection.NumericMismatch)]
    [InlineData("denominator", NarratorRejection.NumericMismatch)]
    [InlineData("metric-missing", NarratorRejection.NumericMismatch)]
    [InlineData("number-text", NarratorRejection.UnsupportedWording)]
    [InlineData("team-text", NarratorRejection.UnsupportedWording)]
    [InlineData("player", NarratorRejection.UnsupportedWording)]
    [InlineData("chronology", NarratorRejection.UnsupportedWording)]
    [InlineData("outcome", NarratorRejection.UnsupportedWording)]
    [InlineData("xg", NarratorRejection.UnsupportedWording)]
    [InlineData("causality", NarratorRejection.UnsupportedWording)]
    public async Task Invalid_narratives_exhaust_one_revision_without_partial_publication(
        string mutation, NarratorRejection reason)
    {
        var model = Selecting(prompt => Mutate(prompt, mutation));
        var evaluations = new List<NarratorEvaluation>();
        var state = await Run(model, cutoff: 8, evaluation: evaluations.Add);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.NotEmpty(state.Observations);
        Assert.DoesNotContain(state.Verifications, item => item.Agent.Role == AgentRole.Narrator);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Equal(2, evaluations.Count);
        Assert.All(evaluations, item =>
        {
            Assert.Equal("Rejected", item.Outcome);
            Assert.Contains(item.Rejections, rejection => rejection.Reason == reason);
        });
        Assert.Contains(model.Prompts.Last().Feedback, item => item.Reason == reason);
    }

    [Fact]
    public async Task Revision_receives_only_approved_input_and_bounded_deterministic_codes()
    {
        var model = Selecting(prompt => prompt.Attempt == 0 ? Mutate(prompt, "player") : SelectAll(prompt));
        var state = await Run(model);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        var prompts = model.Prompts.ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.Equal(prompts[0].Scope, prompts[1].Scope);
        Assert.Equal(prompts[0].Observations, prompts[1].Observations);
        Assert.Equal(1, prompts[1].Attempt);
        Assert.Single(prompts[1].Feedback);
        var serialized = JsonSerializer.Serialize(prompts[1]);
        Assert.DoesNotContain("Invented Player", serialized);
        Assert.DoesNotContain("PreviousClaims", serialized);
        Assert.DoesNotContain("EvidenceQuery", serialized);
        Assert.DoesNotContain("SourceEvents", serialized);
        Assert.DoesNotContain("API_KEY", serialized);
    }

    [Fact]
    public async Task Foreign_rejected_claim_ids_are_not_disclosed_in_revision_feedback()
    {
        var model = Selecting(prompt => prompt.Attempt == 0 ? Mutate(prompt, "unstable-id") : SelectAll(prompt));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.All(model.Prompts.Last().Feedback, item => Assert.Null(item.ClaimId));
    }

    [Fact]
    public async Task Outer_workflow_cannot_grant_a_second_model_revision_budget()
    {
        var model = Selecting(SelectAll);
        var narrator = new ModelBackedNarrator(model);
        var request = await ApprovedRequest();
        await Assert.ThrowsAsync<AgentExecutionException>(() =>
            narrator.GenerateAsync(request with { Attempt = 1 }, CancellationToken.None));
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task Rejected_analyst_observations_never_reach_the_model()
    {
        var analyst = new TestAnalyst("invalid-analyst", context => [MetricClaim(context, "invalid-analyst") with
        {
            Text = "Invented Player scored nine goals."
        }]);
        var model = Selecting(_ => throw new InvalidOperationException("Must not run."));
        var state = await new FrameworkWorkflowRunner([analyst], new ModelBackedNarrator(model),
            new WorkflowOptions(1, TimeSpan.FromSeconds(5))).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(model.Prompts);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
    }

    [Theory]
    [InlineData("proposed")]
    [InlineData("rejected")]
    [InlineData("foreign-scope")]
    [InlineData("duplicate")]
    [InlineData("narrator-producer")]
    public async Task Direct_unapproved_or_ambiguous_handoffs_fail_before_inference(string mutation)
    {
        var request = await ApprovedRequest();
        var first = request.ApprovedObservations[0];
        var claims = mutation switch
        {
            "proposed" => [first with { Status = ClaimStatus.Proposed }],
            "rejected" => [first with { Status = ClaimStatus.Rejected }],
            "foreign-scope" => ImmutableArray.Create(first, request.ApprovedObservations[1] with
            {
                Context = first.Context with { ContentId = "foreign-content" }
            }),
            "duplicate" => [first, first],
            "narrator-producer" => [first with { Producer = new("spoofed-narrator", AgentRole.Narrator) }],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var model = Selecting(SelectAll);
        await Assert.ThrowsAsync<AgentExecutionException>(() => new ModelBackedNarrator(model)
            .GenerateAsync(request with { ApprovedObservations = claims }, CancellationToken.None));
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task Composite_references_disambiguate_equal_claim_ids_from_two_analysts()
    {
        var first = new TestAnalyst("first", context => [MetricClaim(context, "first")]);
        var second = new TestAnalyst("second", context => [MetricClaim(context, "second", FactKind.Shots)]);
        var model = Selecting(SelectAll);
        var state = await new FrameworkWorkflowRunner([first, second], new ModelBackedNarrator(model)).RunAsync(Context());

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, state.NarrativeClaims.Length);
        var prompt = Assert.Single(model.Prompts);
        Assert.All(prompt.Observations, item => Assert.Equal(1, item.Id.ClaimId));
        Assert.Equal(new[] { "first", "second" }, prompt.Observations.Select(item => item.Id.AgentId));
        Assert.Equal(new[] { 1, 2 }, state.NarrativeClaims.Select(item => item.Id.Value));
    }

    [Fact]
    public async Task Every_reference_prefix_matches_the_protected_local_oracle()
    {
        var dataset = Dataset();
        for (var cutoff = 0; cutoff <= dataset.Events.Length; cutoff++)
        {
            var context = AgentContextFactory.Through(dataset, cutoff);
            var expected = await new LocalOrchestrator([new SimulatedAnalyst()], new SimulatedNarrator()).RunAsync(context);
            var model = Selecting(SelectAll);
            var actual = await new FrameworkWorkflowRunner([new SimulatedAnalyst()],
                new ModelBackedNarrator(model)).RunAsync(context);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(Semantics(expected.Observations), Semantics(actual.Observations));
            Assert.Equal(Semantics(expected.NarrativeClaims), Semantics(actual.NarrativeClaims));
            Assert.Equal(expected.Verifications.Select(item => item.Result.Status),
                actual.Verifications.Select(item => item.Result.Status));
            if (cutoff == 0)
                Assert.Empty(model.Prompts);
        }
    }

    [Fact]
    public async Task Two_concurrent_workflows_share_no_match_window_or_revision_state()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var model = new FakeModel(async (prompt, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(token);
            return prompt.Scope.ThroughSequence == 8 ? Mutate(prompt, "match") : SelectAll(prompt);
        });
        var evaluations = new ConcurrentQueue<NarratorEvaluation>();
        var runner = new FrameworkWorkflowRunner([new SimulatedAnalyst()],
            new ModelBackedNarrator(model, evaluations.Enqueue));
        var tasks = new[] { runner.RunAsync(Context(8)), runner.RunAsync(Context(20)) };
        var states = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowStatus.Failed, states[0].Status);
        Assert.Empty(states[0].NarrativeClaims);
        Assert.Equal(WorkflowStatus.Completed, states[1].Status);
        Assert.All(states[1].NarrativeClaims, claim => Assert.Equal(20, claim.Context.Window.ThroughSequence));
        Assert.Equal(2, model.Prompts.Count(item => item.Scope.ThroughSequence == 8));
        Assert.Single(model.Prompts, item => item.Scope.ThroughSequence == 20);
        Assert.All(evaluations, item => Assert.Equal(
            item.Scope.ThroughSequence == 8 ? "Rejected" : "Proposed", item.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_timeout_during_execution_prevents_publication(bool timeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeModel(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Infinite delay returned.");
            }
            finally { cancelled.SetResult(); }
        });
        using var caller = new CancellationTokenSource();
        var task = Run(model, timeout: timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5),
            cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!timeout)
            caller.Cancel();
        var state = await task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(timeout ? WorkflowStatus.Failed : WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.DoesNotContain(state.Verifications, item => item.Agent.Role == AgentRole.Narrator);
        Assert.Contains(state.History, item => item.Kind ==
            (timeout ? WorkflowDecisionKind.Timeout : WorkflowDecisionKind.Cancelled));
        Assert.Single(model.Prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_output_after_cancellation_or_timeout_has_no_publication_route(bool timeout)
    {
        var started = new TaskCompletionSource<NarratorPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NarratorModelOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluated = new TaskCompletionSource<NarratorEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeModel((prompt, _) => { started.SetResult(prompt); return release.Task; });
        using var caller = new CancellationTokenSource();
        var task = Run(model, timeout: timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5),
            cancellationToken: caller.Token, evaluation: item => evaluated.TrySetResult(item));
        var prompt = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!timeout)
            caller.Cancel();
        var state = await task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult(SelectAll(prompt));
        var result = await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Cancelled", result.Outcome);
        Assert.Empty(state.NarrativeClaims);
        Assert.DoesNotContain(state.Verifications, item => item.Agent.Role == AgentRole.Narrator);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task Precancelled_workflow_never_invokes_model()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var model = Selecting(SelectAll);
        var state = await Run(model, cancellationToken: source.Token);
        Assert.Equal(WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(model.Prompts);
        Assert.Empty(state.NarrativeClaims);
    }

    [Theory]
    [InlineData("claims")]
    [InlineData("text")]
    public async Task Output_size_limits_are_enforced_before_conversion(string kind)
    {
        var model = Selecting(prompt =>
        {
            var draft = SelectAll(prompt).Claims[0];
            return kind == "claims"
                ? new NarratorModelOutput(Enumerable.Repeat(draft, NarratorLimits.MaxClaims + 1).ToArray())
                : new NarratorModelOutput([draft with { Text = new string('x', NarratorLimits.MaxTextCharacters + 1) }]);
        });
        var evaluations = new List<NarratorEvaluation>();
        var state = await Run(model, evaluation: evaluations.Add);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.All(evaluations, item => Assert.Contains(item.Rejections, reason => reason.Reason == NarratorRejection.SizeLimit));
    }

    [Fact]
    public async Task Input_count_limit_fails_explicitly_without_inference()
    {
        var request = await ApprovedRequest();
        var claim = request.ApprovedObservations[0];
        var model = Selecting(SelectAll);
        var oversized = Enumerable.Range(1, NarratorLimits.MaxObservations + 1)
            .Select(index => claim with { Id = new ClaimId(index) }).ToImmutableArray();
        await Assert.ThrowsAsync<AgentExecutionException>(() => new ModelBackedNarrator(model)
            .GenerateAsync(request with { ApprovedObservations = oversized }, CancellationToken.None));
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task Input_accepts_exactly_the_observation_count_limit()
    {
        var request = await ApprovedRequest();
        var claim = request.ApprovedObservations[0];
        var model = Selecting(SelectAll);
        var approved = Enumerable.Range(1, NarratorLimits.MaxObservations)
            .Select(index => claim with { Id = new ClaimId(index) }).ToImmutableArray();
        var result = await new ModelBackedNarrator(model)
            .GenerateAsync(request with { ApprovedObservations = approved }, CancellationToken.None);
        Assert.Equal(NarratorLimits.MaxObservations, result.Claims.Length);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task A_larger_outer_revision_option_cannot_expand_the_model_revision_limit()
    {
        var model = Selecting(prompt => Mutate(prompt, "unknown-reference"));
        var state = await new FrameworkWorkflowRunner([new SimulatedAnalyst()], new ModelBackedNarrator(model),
            new WorkflowOptions(10, TimeSpan.FromSeconds(5))).RunAsync(Context());
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(2, model.Prompts.Count);
    }

    [Fact]
    public async Task Both_existing_model_backed_analyst_and_new_narrator_use_the_same_typed_workflow()
    {
        using var analystClient = new ScriptedChatClient((messages, _) =>
        {
            var prompt = JsonSerializer.Deserialize<AnalystPrompt>(messages.Single(item => item.Role == ChatRole.User).Text)!;
            return Response(JsonSerializer.Serialize(new AnalystModelOutput(prompt.AllowedObservations.ToArray())));
        });
        using var analyst = new ChatClientAnalystModel(analystClient, new("scripted", "analyst", "v1"));
        var model = Selecting(SelectAll);
        var state = await new FrameworkWorkflowRunner([new ModelBackedAnalyst(analyst)],
            new ModelBackedNarrator(model)).RunAsync(Context(8));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.NotEmpty(state.NarrativeClaims);
        Assert.Single(model.Prompts);
        Assert.All(state.Observations, item => Assert.Equal(ClaimStatus.Verified, item.Status));
        Assert.All(state.NarrativeClaims.SelectMany(item => item.CoveredSourceEvents),
            id => Assert.InRange(id.Value, 1, 8));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Claims\":null}")]
    [InlineData("{\"Claims\":[],\"Extra\":true}")]
    [InlineData("{\"Claims\":[],\"Claims\":[]}")]
    [InlineData("{\"Claims\":[{}]}")]
    public async Task Malformed_json_and_schema_errors_are_explicit_nonpublishable_failures(string json)
    {
        using var client = new ScriptedChatClient((_, _) => Response(json), (_, _) => Response(json));
        using var model = new ChatClientNarratorModel(client, new("offline-scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(2, client.CallCount);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Text")]
    [InlineData("ObservationIds")]
    [InlineData("Category")]
    [InlineData("Scope")]
    [InlineData("Metric")]
    public async Task Missing_claim_properties_are_not_silently_defaulted(string property)
    {
        ChatResponse Missing(IReadOnlyList<ChatMessage> messages, ChatOptions? _)
        {
            var prompt = Prompt(messages);
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(SelectAll(prompt),
                NarratorLimits.Serialization))!;
            node["Claims"]![0]!.AsObject().Remove(property);
            return Response(node.ToJsonString());
        }
        using var client = new ScriptedChatClient(Missing, Missing);
        using var model = new ChatClientNarratorModel(client, new("scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
    }

    [Theory]
    [InlineData("missing-metric-value")]
    [InlineData("unknown-category")]
    [InlineData("numeric-category")]
    [InlineData("duplicate-text")]
    public async Task Nested_required_fields_and_strict_categories_are_enforced(string mutation)
    {
        ChatResponse Invalid(IReadOnlyList<ChatMessage> messages, ChatOptions? _)
        {
            var output = SelectAll(Prompt(messages));
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(output, NarratorLimits.Serialization))!;
            var claim = node["Claims"]![0]!;
            switch (mutation)
            {
                case "missing-metric-value": claim["Metric"]!.AsObject().Remove("Value"); break;
                case "unknown-category": claim["Category"] = "TacticalDominance"; break;
                case "numeric-category": claim["Category"] = 0; break;
                case "duplicate-text":
                    return Response(node.ToJsonString().Replace("\"Id\":1,", "\"Id\":1,\"Text\":\"duplicate\",", StringComparison.Ordinal));
            }
            return Response(node.ToJsonString());
        }
        using var client = new ScriptedChatClient(Invalid, Invalid);
        using var model = new ChatClientNarratorModel(client, new("scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task Incomplete_or_missing_finish_reason_is_rejected(string? finish)
    {
        ChatResponse Incomplete(IReadOnlyList<ChatMessage> messages, ChatOptions? _)
        {
            var response = Response(JsonSerializer.Serialize(SelectAll(Prompt(messages)), NarratorLimits.Serialization));
            response.FinishReason = finish is null ? null : new ChatFinishReason(finish);
            return response;
        }
        using var client = new ScriptedChatClient(Incomplete, Incomplete);
        using var model = new ChatClientNarratorModel(client, new("scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
    }

    [Fact]
    public async Task Real_adapter_uses_structured_output_versioned_instructions_and_fresh_sessions()
    {
        ChatResponse Valid(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        {
            Assert.Equal(NarratorSystemPrompt.Text, options!.Instructions);
            Assert.Equal(NarratorLimits.MaxOutputTokens, options.MaxOutputTokens);
            Assert.Null(options.Tools);
            Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
            Assert.Single(messages, message => message.Role == ChatRole.User);
            Assert.DoesNotContain(messages, message => message.Role == ChatRole.Assistant);
            return Response(JsonSerializer.Serialize(SelectAll(Prompt(messages)), NarratorLimits.Serialization));
        }
        using var client = new ScriptedChatClient(Valid, Valid);
        using var model = new ChatClientNarratorModel(client, new("offline-scripted", "fixture", "v1"));
        var first = await Run(model, cutoff: 8);
        var second = await Run(model, cutoff: 20);
        Assert.Equal(WorkflowStatus.Completed, first.Status);
        Assert.Equal(WorkflowStatus.Completed, second.Status);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Malformed_structured_response_can_be_revised_once_without_forwarding_raw_response()
    {
        using var client = new ScriptedChatClient((_, _) => Response("{ invalid-private-output"),
            (messages, _) =>
            {
                var prompt = Prompt(messages);
                Assert.Equal(1, prompt.Attempt);
                Assert.Equal(NarratorRejection.InvalidStructure, Assert.Single(prompt.Feedback).Reason);
                Assert.DoesNotContain("invalid-private-output", JsonSerializer.Serialize(prompt));
                return Response(JsonSerializer.Serialize(SelectAll(prompt), NarratorLimits.Serialization));
            });
        using var model = new ChatClientNarratorModel(client, new("scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, client.CallCount);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("read-failure")]
    [InlineData("timeout")]
    public async Task Provider_failures_are_sanitized_and_do_not_trigger_a_revision(string kind)
    {
        using var client = new ScriptedChatClient((_, _) => throw kind switch
        {
            "transport" => new HttpRequestException("secret-response-body"),
            "read-failure" => new IOException("secret-response-body"),
            _ => new TaskCanceledException("secret-response-body")
        });
        using var model = new ChatClientNarratorModel(client, new("offline-scripted", "fixture", "v1"));
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(1, client.CallCount);
        Assert.DoesNotContain("secret-response-body", JsonSerializer.Serialize(state.History));
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
    }

    [Fact]
    public async Task Adapter_configured_timeout_cancels_a_cooperative_chat_client()
    {
        using var client = new BlockingClient();
        using var model = new ChatClientNarratorModel(client, new("scripted", "blocked", "v1"),
            TimeSpan.FromMilliseconds(100));
        var state = await Run(model);
        await client.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Reason == "Local Narrator provider request timed out.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("file:///C:/model")]
    [InlineData("http://user:password@localhost/v1")]
    [InlineData("http://localhost/v1?key=forbidden")]
    [InlineData("http://localhost/v1#fragment")]
    public void Local_provider_requires_an_explicit_safe_endpoint(string? endpoint)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LocalNarratorModel.Create(name => name switch
        {
            "GAMEGROUND_NARRATOR_ENDPOINT" => endpoint,
            "GAMEGROUND_NARRATOR_MODEL" => "fixture",
            _ => null
        }));
        Assert.Contains("GAMEGROUND_NARRATOR_ENDPOINT", exception.Message);
        Assert.DoesNotContain("password", exception.Message);
        Assert.DoesNotContain("forbidden", exception.Message);
    }

    [Fact]
    public void Missing_model_configuration_fails_clearly_without_credentials_or_network()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LocalNarratorModel.Create(
            name => name == "GAMEGROUND_NARRATOR_ENDPOINT" ? "http://127.0.0.1:1234/v1" : null));
        Assert.Contains("GAMEGROUND_NARRATOR_MODEL", exception.Message);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("301")]
    [InlineData("not-seconds")]
    public void Invalid_configured_timeouts_fail_before_client_creation(string timeout)
    {
        Assert.Throws<InvalidOperationException>(() => LocalNarratorModel.Create(name => name switch
        {
            "GAMEGROUND_NARRATOR_ENDPOINT" => "http://127.0.0.1:1234/v1",
            "GAMEGROUND_NARRATOR_MODEL" => "fixture",
            "GAMEGROUND_NARRATOR_TIMEOUT_SECONDS" => timeout,
            _ => null
        }));
    }

    [Fact]
    public void Local_configuration_builds_an_opt_in_adapter_without_calling_a_server()
    {
        using var model = LocalNarratorModel.Create(name => name switch
        {
            "GAMEGROUND_NARRATOR_ENDPOINT" => "http://127.0.0.1:1234/v1",
            "GAMEGROUND_NARRATOR_MODEL" => "offline-construction-only",
            _ => null
        });
        Assert.Equal(new NarratorModelIdentity("local-openai-compatible", "offline-construction-only", "unreported"), model.Identity);
    }

    internal static NarratorModelOutput SelectAll(NarratorPrompt prompt) =>
        new(prompt.Observations.Select(item => new NarrativeClaimDraft(item.NarrativeClaimId, item.Text,
            [item.Id], item.Category, prompt.Scope, item.Metric)).ToArray());

    private static NarratorModelOutput Mutate(NarratorPrompt prompt, string mutation)
    {
        var output = SelectAll(prompt);
        var first = output.Claims[0];
        var metric = first.Metric!;
        if (mutation == "null-output") return null!;
        if (mutation == "null-claims") return new(null!);
        if (mutation == "null-claim") return new([null!]);
        if (mutation == "empty") return new([]);
        if (mutation == "duplicate-id") return new([first, output.Claims[1] with { Id = first.Id }]);
        if (mutation == "reused-reference") return new([first, first with { Id = 2 }]);
        var changed = mutation switch
        {
            "blank" => first with { Text = " " },
            "zero-id" => first with { Id = 0 },
            "negative-id" => first with { Id = -1 },
            "unstable-id" => first with { Id = 999999 },
            "unknown-reference" => first with { ObservationIds = [first.ObservationIds[0] with { ClaimId = 999999 }] },
            "unapproved-reference" => first with { ObservationIds = [first.ObservationIds[0] with { AgentId = "unapproved-agent" }] },
            "duplicate-reference" => first with { ObservationIds = [first.ObservationIds[0], first.ObservationIds[0]] },
            "no-reference" => first with { ObservationIds = [] },
            "null-references" => first with { ObservationIds = null! },
            "null-reference" => first with { ObservationIds = [null!] },
            "composite" => first with { ObservationIds = [first.ObservationIds[0], output.Claims[1].ObservationIds[0]] },
            "match" => first with { Scope = first.Scope with { MatchId = "another-match" } },
            "context" => first with { Scope = first.Scope with { ContextId = "foreign-content" } },
            "window" => first with { Scope = first.Scope with { FromSequence = 2 } },
            "future" => first with { Scope = first.Scope with { ThroughSequence = 9 } },
            "null-scope" => first with { Scope = null! },
            "category" => first with { Category = ClaimType.RecordedMoment },
            "value" => first with { Metric = metric with { Value = metric.Value + 100 } },
            "team" => first with { Metric = metric with { Team = new TeamId(999) } },
            "unit" => first with { Metric = metric with { Unit = FactUnit.Seconds } },
            "denominator" => first with { Metric = metric with { Denominator = 999 } },
            "metric-missing" => first with { Metric = null },
            "number-text" => first with { Text = first.Text + " 999 shots." },
            "team-text" => first with { Text = "Unknown FC won." },
            "player" => first with { Text = "Invented Player scored." },
            "chronology" => first with { Text = "The winning goal came after event 99." },
            "outcome" => first with { Text = "Harbor FC won 9-0." },
            "xg" => first with { Text = "xG = 2.7." },
            "causality" => first with { Text = "Tactical dominance caused the goal." },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        return output with { Claims = [changed, .. output.Claims.Skip(1)] };
    }

    private static Task<SharedState> Run(INarratorModel model, int cutoff = 20,
        Action<NarratorEvaluation>? evaluation = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        new FrameworkWorkflowRunner([new SimulatedAnalyst()], new ModelBackedNarrator(model, evaluation),
            new WorkflowOptions(1, timeout ?? TimeSpan.FromSeconds(5))).RunAsync(Context(cutoff), cancellationToken);

    private static async Task<NarrativeRequest> ApprovedRequest()
    {
        var context = Context();
        var output = await new SimulatedAnalyst().GenerateAsync(new AgentRequest(context, [], [], [], [], 0), CancellationToken.None);
        return new(output.Claims.Select(claim => claim with
        {
            Status = new ClaimVerifier().Verify(context, claim).Status
        }).ToImmutableArray(), [], [], [], 0);
    }

    private static MatchDataset Dataset() =>
        new DatasetLoader().Load(Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json"));
    private static AgentContext Context(int cutoff = 20) => AgentContextFactory.Through(Dataset(), cutoff);
    private static FakeModel Selecting(Func<NarratorPrompt, NarratorModelOutput> select) =>
        new((prompt, _) => Task.FromResult(select(prompt)));
    private static ChatResponse Response(string json) => new(new ChatMessage(ChatRole.Assistant, json))
    {
        FinishReason = ChatFinishReason.Stop
    };
    private static NarratorPrompt Prompt(IReadOnlyList<ChatMessage> messages) =>
        JsonSerializer.Deserialize<NarratorPrompt>(messages.Single(item => item.Role == ChatRole.User).Text,
            NarratorLimits.Serialization)!;
    private static Claim MetricClaim(AgentContext context, string producer, FactKind kind = FactKind.Goals) =>
        ClaimText.ForFact(context, context.Facts.First(item => item.Kind == kind && item.Team is not null).Id,
            new ClaimId(1), new AgentIdentity(producer, AgentRole.Analyst));
    private static string Semantics(ImmutableArray<Claim> claims) => JsonSerializer.Serialize(claims.Select(claim => new
    {
        claim.Type,
        claim.Context,
        claim.FactIds,
        claim.Evidence,
        claim.CoveredSourceEvents,
        claim.Metric,
        claim.Moment,
        claim.Status
    }));

    private sealed class FakeModel(Func<NarratorPrompt, CancellationToken, Task<NarratorModelOutput>> generate) : INarratorModel
    {
        public NarratorModelIdentity Identity { get; } = new("offline-scripted", "deterministic-fixture", "v1");
        public ConcurrentQueue<NarratorPrompt> Prompts { get; } = [];
        public Task<NarratorModelOutput> NarrateAsync(NarratorPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return generate(prompt, cancellationToken);
        }
    }

    private sealed class TestAnalyst(string id, Func<AgentContext, ImmutableArray<Claim>> generate) : ILocalAgent
    {
        public AgentIdentity Identity { get; } = new(id, AgentRole.Analyst);
        public Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentOutput(generate(request.Context)));
    }

    private sealed class BlockingClient : IChatClient
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite delay returned.");
            }
            finally { Cancelled.TrySetResult(); }
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
