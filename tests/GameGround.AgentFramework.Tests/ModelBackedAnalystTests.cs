using System.Collections.Immutable;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Data;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework.Tests;

public sealed class ModelBackedAnalystTests
{
    [Fact]
    public async Task Valid_structured_model_selection_passes_both_gates_and_simulated_narration()
    {
        var evaluations = new List<AnalystEvaluation>();
        var model = Selecting(prompt => [prompt.AllowedObservations.First()]);
        var state = await Run(model, evaluations.Add);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Single(state.Observations);
        Assert.Single(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Agent.Role == AgentRole.Narrator
            && item.Kind == WorkflowDecisionKind.Invoked);
        Assert.All(state.Verifications, item => Assert.Equal(ClaimStatus.Verified, item.Result.Status));
        var record = Assert.Single(evaluations);
        Assert.Equal(model.Identity, record.Model);
        Assert.Equal(AnalystSystemPrompt.Version, record.PromptVersion);
        Assert.Equal(state.Context.Identity, record.Context);
        Assert.NotEmpty(record.InputEvidenceIds);
        Assert.Equal(1, Assert.Single(record.OutputObservationIds));
        Assert.Equal(ClaimStatus.Verified, Assert.Single(record.Verifications).Status);
        Assert.Equal("Proposed", record.Outcome);
        Assert.True(record.Latency >= TimeSpan.Zero);
        Assert.Null(record.FailureReason);
    }

    [Theory]
    [InlineData("unknown-evidence")]
    [InlineData("wrong-match")]
    [InlineData("wrong-context")]
    [InlineData("future-event")]
    [InlineData("unknown-fact")]
    [InlineData("missing-evidence")]
    [InlineData("numeric-hallucination")]
    [InlineData("xg")]
    [InlineData("speed")]
    [InlineData("distance")]
    [InlineData("dominance")]
    public async Task Untrusted_semantic_output_reaches_the_gate_and_is_rejected(string fault)
    {
        var records = new List<AnalystEvaluation>();
        var model = Selecting(prompt =>
        {
            var observation = prompt.AllowedObservations.First();
            return [fault switch
            {
                "unknown-evidence" => observation with { EvidenceIds = [9999] },
                "wrong-match" => observation with { MatchId = "another-synthetic-match" },
                "wrong-context" => observation with { ContextId = "another-content-identity" },
                "future-event" => observation with { SourceEventIds = [9] },
                "unknown-fact" => observation with { FactIds = [9999] },
                "missing-evidence" => observation with { EvidenceIds = [] },
                "numeric-hallucination" => observation with { Statement = observation.Statement + " 999 recorded passes." },
                "xg" => observation with { Statement = "Expected goals (xG) = 1.7." },
                "speed" => observation with { Statement = "The player ran at 35 km/h." },
                "distance" => observation with { Statement = "The pass travelled 40 metres." },
                _ => observation with { Statement = "Meadow United dominated the midfield." }
            }];
        });

        var state = await Run(model, records.Add, cutoff: 8);

        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.DoesNotContain(state.History, item => item.Agent.Role == AgentRole.Narrator);
        var rejection = Assert.Single(state.Verifications);
        Assert.Equal(ClaimStatus.Rejected, rejection.Result.Status);
        Assert.NotEmpty(rejection.Result.RejectionReason!);
        Assert.Equal(ClaimStatus.Rejected, Assert.Single(Assert.Single(records).Verifications).Status);
        if (fault == "numeric-hallucination")
            Assert.Contains(rejection.Result.Checks, check =>
                check.Kind == VerificationCheckKind.NumericValuesConsistent && !check.Passed);
    }

    [Fact]
    public async Task Partly_valid_model_batch_does_not_publish_its_valid_subset()
    {
        var model = Selecting(prompt =>
        {
            var item = prompt.AllowedObservations.First();
            return [item, item with { Id = 2, EvidenceIds = [9999] }];
        });
        var state = await Run(model);
        Assert.Equal(WorkflowStatus.Rejected, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Equal(2, state.Verifications.Length);
    }

    [Fact]
    public async Task Empty_model_output_is_safe_and_skips_the_simulated_narrator()
    {
        var state = await Run(Selecting(_ => []));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Empty(state.Observations);
        Assert.Empty(state.NarrativeClaims);
        Assert.Empty(state.Verifications);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.Skipped);
    }

    [Fact]
    public async Task Model_prompt_contains_only_bounded_projections_not_raw_events()
    {
        var dataset = Dataset();
        var allowed = dataset.Events.Take(8).Select(item => item.Id).ToHashSet();
        var model = Selecting(prompt =>
        {
            Assert.Equal(8, prompt.Context.Window.ThroughSequence);
            Assert.All(prompt.Facts, fact => Assert.All(fact.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            Assert.All(prompt.Evidence, item => Assert.All(item.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            Assert.All(prompt.Sequences, item => Assert.All(item.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            Assert.All(prompt.Moments, item => Assert.All(item.Evidence.SourceEventIds, id => Assert.Contains(id, allowed)));
            Assert.All(prompt.AllowedObservations,
                item => Assert.All(item.SourceEventIds, id => Assert.Contains(new EventId(id), allowed)));
            Assert.DoesNotContain(typeof(AnalystPrompt).GetProperties(),
                property => property.PropertyType == typeof(MatchDataset)
                    || property.PropertyType == typeof(AgentContext)
                    || property.PropertyType == typeof(ImmutableArray<MatchEvent>));
            return [];
        });
        await Run(model, cutoff: 8);
        Assert.Single(model.Prompts);
    }

    [Fact]
    public async Task Scripted_model_is_equivalent_to_Phase4B_at_every_reference_prefix()
    {
        var dataset = Dataset();
        for (var cutoff = 0; cutoff <= dataset.Events.Length; cutoff++)
        {
            var context = AgentContextFactory.Through(dataset, cutoff);
            var simulated = await new SimulatedAnalyst().GenerateAsync(
                new AgentRequest(context, [], [], [], [], 0), CancellationToken.None);
            var model = Selecting(prompt => simulated.Claims.Select(claim =>
            {
                var selection = prompt.AllowedObservations.Single(item => item.Type == claim.Type.ToString()
                    && item.Statement == claim.Text);
                return selection with { Id = claim.Id.Value };
            }).ToArray());
            var expected = await new FrameworkWorkflowRunner([new SimulatedAnalyst()], new SimulatedNarrativeAgent())
                .RunAsync(context);
            var actual = await new FrameworkWorkflowRunner([new ModelBackedAnalyst(model)], new SimulatedNarrativeAgent())
                .RunAsync(context);

            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(Semantics(expected.Observations), Semantics(actual.Observations));
            Assert.Equal(Semantics(expected.NarrativeClaims), Semantics(actual.NarrativeClaims));
            Assert.Equal(expected.Verifications.Select(item => item.Result.Status),
                actual.Verifications.Select(item => item.Result.Status));
        }
    }

    [Fact]
    public async Task Rejected_model_observation_receives_feedback_and_can_be_revised()
    {
        var model = Selecting(prompt =>
        {
            var item = prompt.AllowedObservations.First();
            if (prompt.Attempt == 0)
                return [item with { EvidenceIds = [9999] }];
            Assert.NotEmpty(prompt.RevisionReasons);
            return [item];
        });
        var records = new List<AnalystEvaluation>();
        var state = await Run(model, records.Add, revisions: 1);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(2, records.Count);
        Assert.Equal("Rejected", records[0].Outcome);
        Assert.Equal("Proposed", records[1].Outcome);
        Assert.Equal(2, model.Prompts.Count);
    }

    [Theory]
    [InlineData("null-output")]
    [InlineData("null-array")]
    [InlineData("null-item")]
    [InlineData("duplicate-id")]
    [InlineData("unknown-type")]
    public async Task Malformed_fake_output_is_an_explicit_failure_without_publication(string fault)
    {
        var model = new FakeAnalystModel((prompt, _) =>
        {
            var item = prompt.AllowedObservations.First();
            return Task.FromResult(fault switch
            {
                "null-output" => null!,
                "null-array" => new AnalystModelOutput(null!),
                "null-item" => new AnalystModelOutput([null!]),
                "duplicate-id" => new AnalystModelOutput([item, item]),
                _ => new AnalystModelOutput([item with { Type = "xG" }])
            });
        });
        var records = new List<AnalystEvaluation>();
        var state = await Run(model, records.Add);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
        Assert.NotEmpty(Assert.Single(records).FailureReason!);
    }

    [Fact]
    public async Task Real_AgentFramework_adapter_uses_schema_and_versioned_prompt_with_scripted_client()
    {
        using var client = new ScriptedChatClient((messages, options) =>
        {
            Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            Assert.True(options?.Tools is null || options.Tools.Count == 0);
            Assert.Contains("synthetic football", options?.Instructions);
            var prompt = JsonSerializer.Deserialize<AnalystPrompt>(messages.Last(item => item.Role == ChatRole.User).Text)!;
            Assert.Equal(AnalystSystemPrompt.Version, prompt.Version);
            return Response(JsonSerializer.Serialize(new AnalystModelOutput([prompt.AllowedObservations.First()])));
        });
        using var model = new ChatClientAnalystModel(client, new("scripted", "not-a-live-model", "fixture-v1"));
        var state = await Run(model);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(1, client.CallCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Observations\":null}")]
    [InlineData("{\"Observations\":[],\"xG\":9}")]
    [InlineData("{\"Observations\":[{}]}")]
    [InlineData("{\"Observations\":[")]
    [InlineData("{\"Observations\":[],\"Observations\":[]}")]
    public async Task Invalid_structured_JSON_is_not_repaired_into_success(string json)
    {
        using var client = new ScriptedChatClient((_, _) => Response(json));
        using var model = new ChatClientAnalystModel(client, new("scripted", "fixture", "v1"));
        var state = await Run(model);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.DoesNotContain(state.History, item => item.Agent.Role == AgentRole.Narrator);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
    }

    [Fact]
    public async Task Agent_session_is_not_reused_between_full_and_cutoff_contexts()
    {
        using var client = new ScriptedChatClient(
            (messages, _) =>
            {
                Assert.Single(messages, item => item.Role == ChatRole.User);
                return Response("{\"Observations\":[]}");
            },
            (messages, _) =>
            {
                Assert.Single(messages, item => item.Role == ChatRole.User);
                Assert.DoesNotContain(messages, item => item.Role == ChatRole.Assistant);
                var prompt = JsonSerializer.Deserialize<AnalystPrompt>(messages.Last().Text)!;
                Assert.Equal(8, prompt.Context.Window.ThroughSequence);
                return Response("{\"Observations\":[]}");
            });
        using var model = new ChatClientAnalystModel(client, new("scripted", "fixture", "v1"));
        await Run(model);
        await Run(model, cutoff: 8);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_blocked_model_and_prevents_publication()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluated = new TaskCompletionSource<AnalystEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeAnalystModel(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
            return new AnalystModelOutput([]);
        });
        var execution = Run(model, record => evaluated.TrySetResult(record), cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var state = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowStatus.Cancelled, state.Status);
            Assert.Empty(state.NarrativeClaims);
            Assert.Equal("Cancelled", (await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        }
        finally
        {
            cancellation.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Blocked_model_times_out_without_retry_or_publication()
    {
        var evaluated = new TaskCompletionSource<AnalystEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeAnalystModel(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AnalystModelOutput([]);
        });
        var state = await Run(model, record => evaluated.TrySetResult(record), timeout: TimeSpan.FromMilliseconds(100))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Single(model.Prompts);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.Timeout);
        Assert.Equal("Cancelled", (await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    [Fact]
    public async Task Precancelled_model_is_not_invoked()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = Selecting(_ => throw new InvalidOperationException("Must not run."));
        var state = await Run(model, cancellationToken: cancellation.Token);
        Assert.Equal(WorkflowStatus.Cancelled, state.Status);
        Assert.Empty(model.Prompts);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://127.0.0.1:1234/v1", null)]
    [InlineData("https://example.com/v1", "model")]
    [InlineData("file:///C:/model", "model")]
    [InlineData("http://key@localhost:1234/v1", "model")]
    [InlineData("http://localhost:1234/v1?key=secret", "model")]
    public void Missing_or_nonlocal_configuration_fails_before_client_use(string? endpoint, string? model)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LocalAnalystModel.Create(name => name switch
        {
            "GAMEGROUND_ANALYST_ENDPOINT" => endpoint,
            "GAMEGROUND_ANALYST_MODEL" => model,
            _ => null
        }));
        Assert.Contains("GAMEGROUND_ANALYST_", error.Message);
        Assert.DoesNotContain("key=secret", error.Message);
    }

    [Fact]
    public void Local_adapter_can_be_constructed_without_credentials_and_without_contacting_a_server()
    {
        using var model = LocalAnalystModel.Create(name => name switch
        {
            "GAMEGROUND_ANALYST_ENDPOINT" => "http://127.0.0.1:1234/v1",
            "GAMEGROUND_ANALYST_MODEL" => "configured-local-model",
            _ => null
        });
        Assert.Equal(new AnalystModelIdentity("local-openai-compatible", "configured-local-model", "unreported"),
            model.Identity);
    }

    [Fact]
    public async Task Provider_transport_errors_are_explicit_and_do_not_log_response_bodies()
    {
        using var client = new ScriptedChatClient((_, _) =>
            throw new HttpRequestException("Sensitive provider configuration must not enter history."));
        using var model = new ChatClientAnalystModel(client, new("scripted", "fixture", "v1"));
        var records = new List<AnalystEvaluation>();

        var state = await Run(model, records.Add);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.DoesNotContain("Sensitive provider", JsonSerializer.Serialize(state.History));
        Assert.DoesNotContain("Sensitive provider", JsonSerializer.Serialize(records));
    }

    [Fact]
    public async Task Incomplete_model_response_is_rejected_even_when_its_JSON_is_valid()
    {
        using var client = new ScriptedChatClient((_, _) => new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "{\"Observations\":[]}"))
        {
            FinishReason = ChatFinishReason.Length
        });
        using var model = new ChatClientAnalystModel(client, new("scripted", "fixture", "v1"));

        var state = await Run(model);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
    }

    [Fact]
    public async Task Provider_timeout_without_caller_cancellation_is_an_explicit_failure()
    {
        using var client = new ScriptedChatClient((_, _) => throw new TaskCanceledException("Provider timeout."));
        using var model = new ChatClientAnalystModel(client, new("scripted", "fixture", "v1"));

        var state = await Run(model);

        Assert.Equal(WorkflowStatus.Failed, state.Status);
        Assert.Empty(state.NarrativeClaims);
        Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure
            && item.Reason == "Local Analyst provider request timed out.");
    }

    [Fact]
    public async Task OpenAI_compatible_protocol_is_exercised_with_an_in_memory_transport_only()
    {
        using var handler = new InMemoryModelHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAI.Chat.ChatClient("offline-protocol-fixture",
            new System.ClientModel.ApiKeyCredential("non-secret-offline-fixture"),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri("http://127.0.0.1:1234/v1"),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http),
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0)
            }).AsIChatClient();
        using var model = new ChatClientAnalystModel(client, new("in-memory-protocol", "fixture", "v1"));

        var state = await Run(model, cutoff: 8);

        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Single(state.NarrativeClaims);
        Assert.Equal(1, handler.Calls);
    }

    private static FakeAnalystModel Selecting(Func<AnalystPrompt, AnalystObservation[]> select) =>
        new((prompt, _) => Task.FromResult(new AnalystModelOutput(select(prompt))));

    private static Task<SharedState> Run(IAnalystModel model, Action<AnalystEvaluation>? evaluation = null,
        int cutoff = 20, int revisions = 0, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        new FrameworkWorkflowRunner([new ModelBackedAnalyst(model, evaluation)], new SimulatedNarrativeAgent(),
            new WorkflowOptions(revisions, timeout ?? TimeSpan.FromSeconds(5)))
            .RunAsync(AgentContextFactory.Through(Dataset(), cutoff), cancellationToken);

    private static MatchDataset Dataset() =>
        new DatasetLoader().Load(Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json"));
    private static ChatResponse Response(string json) => new(new ChatMessage(ChatRole.Assistant, json));
    private static string Semantics(ImmutableArray<Claim> claims) => JsonSerializer.Serialize(claims.Select(claim => new
    {
        claim.Type,
        claim.FactIds,
        claim.Evidence,
        claim.CoveredSourceEvents,
        claim.Status
    }));

    private sealed class FakeAnalystModel(
        Func<AnalystPrompt, CancellationToken, Task<AnalystModelOutput>> analyze) : IAnalystModel
    {
        public AnalystModelIdentity Identity { get; } = new("scripted", "deterministic-fixture", "v1");
        public List<AnalystPrompt> Prompts { get; } = [];
        public Task<AnalystModelOutput> AnalyzeAsync(AnalystPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return analyze(prompt, cancellationToken);
        }
    }

    private sealed class InMemoryModelHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var user = body.RootElement.GetProperty("messages").EnumerateArray()
                .Last(message => message.GetProperty("role").GetString() == "user");
            var content = user.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()!
                : content.EnumerateArray().Single().GetProperty("text").GetString()!;
            var prompt = JsonSerializer.Deserialize<AnalystPrompt>(text)!;
            Assert.Equal(8, prompt.Context.Window.ThroughSequence);
            Assert.All(prompt.Evidence.SelectMany(item => item.Evidence.SourceEventIds),
                id => Assert.InRange(id.Value, 1, 8));
            var output = new AnalystModelOutput([prompt.AllowedObservations.First()]);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "offline-response",
                    @object = "chat.completion",
                    created = 0,
                    model = "offline-protocol-fixture",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = JsonSerializer.Serialize(output) },
                            finish_reason = "stop"
                        }
                    }
                }), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
