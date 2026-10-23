using System.ClientModel.Primitives;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using GameGround.Agents;
using GameGround.Data;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework.Tests;

public sealed class NarratorTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_OpenAI_connector_uses_only_in_memory_HTTP_and_enforces_provider_failures(bool providerError)
    {
        using var handler = new ProtocolHandler(providerError);
        using var http = new HttpClient(new NarratorResponseLimitHandler(handler));
        using var model = Model(http);
        var state = await new FrameworkWorkflowRunner([new SimulatedAnalyst()],
            new ModelBackedNarrator(model)).RunAsync(Context());

        Assert.Equal(providerError ? WorkflowStatus.Failed : WorkflowStatus.Completed, state.Status);
        Assert.Equal(1, handler.Calls);
        if (providerError)
        {
            Assert.Empty(state.NarrativeClaims);
            Assert.Contains(state.History, item => item.Kind == WorkflowDecisionKind.AgentFailure);
            Assert.DoesNotContain("private-provider-detail", JsonSerializer.Serialize(state.History));
        }
        else
        {
            Assert.NotEmpty(state.NarrativeClaims);
            Assert.All(state.NarrativeClaims, item => Assert.Equal(ClaimStatus.Verified, item.Status));
        }
    }

    [Theory]
    [InlineData(NarratorLimits.MaxTransportBytes, false)]
    [InlineData(NarratorLimits.MaxTransportBytes + 1, false)]
    [InlineData(NarratorLimits.MaxTransportBytes, true)]
    [InlineData(NarratorLimits.MaxTransportBytes + 1, true)]
    public async Task HTTP_body_cap_checks_actual_bytes_as_well_as_content_length(int bytes, bool streamWithoutLength)
    {
        using var handler = new PayloadHandler(bytes, streamWithoutLength);
        using var http = new HttpClient(new NarratorResponseLimitHandler(handler));
        if (bytes > NarratorLimits.MaxTransportBytes)
        {
            var exception = await Assert.ThrowsAsync<NarratorOutputException>(() => http.GetAsync("http://localhost/unused"));
            Assert.Equal(NarratorRejection.SizeLimit, exception.Reason);
        }
        else
        {
            using var response = await http.GetAsync("http://localhost/unused");
            Assert.Equal(bytes, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        Assert.True(handler.DisposedContent);
    }

    [Theory]
    [InlineData(NarratorLimits.MaxResponseBytes)]
    [InlineData(NarratorLimits.MaxResponseBytes + 1)]
    public async Task Structured_output_byte_cap_accepts_exact_limit_and_rejects_one_more_byte(int bytes)
    {
        var prompt = Prompt();
        var json = JsonSerializer.Serialize(ModelBackedNarratorTests.SelectAll(prompt), NarratorLimits.Serialization);
        Assert.True(Encoding.UTF8.GetByteCount(json) < NarratorLimits.MaxResponseBytes);
        json += new string(' ', bytes - Encoding.UTF8.GetByteCount(json));
        using var client = new ScriptedChatClient((_, _) => Response(json));
        using var model = new ChatClientNarratorModel(client, new("scripted", "boundary", "v1"));
        if (bytes > NarratorLimits.MaxResponseBytes)
        {
            var error = await Assert.ThrowsAsync<NarratorOutputException>(() => model.NarrateAsync(prompt, CancellationToken.None));
            Assert.Equal(NarratorRejection.SizeLimit, error.Reason);
        }
        else
        {
            Assert.Single((await model.NarrateAsync(prompt, CancellationToken.None)).Claims);
        }
    }

    [Theory]
    [InlineData(NarratorLimits.MaxPromptBytes)]
    [InlineData(NarratorLimits.MaxPromptBytes + 1)]
    public async Task Prompt_byte_cap_checks_exact_threshold_before_model_execution(int bytes)
    {
        var prompt = PromptAtBytes(bytes);
        using var client = new ScriptedChatClient((_, _) => Response("{\"Claims\":[]}"));
        using var model = new ChatClientNarratorModel(client, new("scripted", "boundary", "v1"));
        if (bytes > NarratorLimits.MaxPromptBytes)
        {
            await Assert.ThrowsAsync<AgentExecutionException>(() => model.NarrateAsync(prompt, CancellationToken.None));
            Assert.Equal(0, client.CallCount);
        }
        else
        {
            Assert.Empty((await model.NarrateAsync(prompt, CancellationToken.None)).Claims);
            Assert.Equal(1, client.CallCount);
        }
    }

    [Theory]
    [InlineData(NarratorLimits.MaxTextCharacters)]
    [InlineData(NarratorLimits.MaxTextCharacters + 1)]
    public void Grounding_text_limit_is_measured_independently_of_semantic_comparison(int characters)
    {
        var prompt = Prompt();
        var observation = prompt.Observations[0] with { Text = new string('x', characters) };
        prompt = prompt with { Observations = [observation] };
        var feedback = NarratorGroundingGate.Validate(prompt, ModelBackedNarratorTests.SelectAll(prompt));
        if (characters > NarratorLimits.MaxTextCharacters)
            Assert.Contains(feedback, item => item.Reason == NarratorRejection.SizeLimit);
        else
            Assert.Empty(feedback);
    }

    [Theory]
    [InlineData(NarratorLimits.MaxClaims)]
    [InlineData(NarratorLimits.MaxClaims + 1)]
    public void Grounding_claim_count_limit_accepts_exact_limit(int count)
    {
        var prompt = Prompt();
        prompt = prompt with
        {
            Observations = Enumerable.Range(1, count).Select(index => prompt.Observations[0] with
            {
                NarrativeClaimId = index,
                Id = new ObservationReference("test-analyst", index)
            }).ToImmutableArray()
        };
        var feedback = NarratorGroundingGate.Validate(prompt, ModelBackedNarratorTests.SelectAll(prompt));
        if (count > NarratorLimits.MaxClaims)
            Assert.Contains(feedback, item => item.Reason == NarratorRejection.SizeLimit);
        else
            Assert.Empty(feedback);
    }

    [Fact]
    public async Task UTF8_byte_limit_is_not_a_character_count_proxy()
    {
        var json = "{\"Claims\":[]}" + new string(' ', 100);
        // The malformed multibyte suffix exceeds the byte cap despite its smaller character count.
        json += new string('\u00e9', NarratorLimits.MaxResponseBytes / 2);
        Assert.True(json.Length < NarratorLimits.MaxResponseBytes);
        using var client = new ScriptedChatClient((_, _) => Response(json));
        using var model = new ChatClientNarratorModel(client, new("scripted", "boundary", "v1"));
        var error = await Assert.ThrowsAsync<NarratorOutputException>(() => model.NarrateAsync(Prompt(), CancellationToken.None));
        Assert.Equal(NarratorRejection.SizeLimit, error.Reason);
    }

    [Fact]
    public async Task Configured_timeout_ignores_late_uncooperative_chat_output()
    {
        using var client = new LateClient();
        using var model = new ChatClientNarratorModel(client, new("scripted", "late", "v1"), TimeSpan.FromMilliseconds(100));
        var prompt = Prompt();
        var pending = model.NarrateAsync(prompt, CancellationToken.None);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Assert.ThrowsAsync<AgentExecutionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            client.Release.SetResult(Response(JsonSerializer.Serialize(ModelBackedNarratorTests.SelectAll(prompt),
                NarratorLimits.Serialization)));
        }
        await client.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_provider_timeout()
    {
        using var client = new LateClient();
        using var model = new ChatClientNarratorModel(client, new("scripted", "late", "v1"));
        using var caller = new CancellationTokenSource();
        var prompt = Prompt();
        var pending = model.NarrateAsync(prompt, caller.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            client.Release.SetResult(Response(JsonSerializer.Serialize(ModelBackedNarratorTests.SelectAll(prompt),
                NarratorLimits.Serialization)));
        }
        await client.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ChatClientNarratorModel Model(HttpClient http)
    {
        var client = new OpenAI.Chat.ChatClient("offline-protocol-fixture",
            new System.ClientModel.ApiKeyCredential("non-secret-offline-fixture"),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri("http://127.0.0.1:1234/v1"),
                Transport = new HttpClientPipelineTransport(http),
                RetryPolicy = new ClientRetryPolicy(0)
            }).AsIChatClient();
        return new ChatClientNarratorModel(client, new("in-memory-protocol", "fixture", "v1"));
    }

    private static AgentContext Context() => AgentContextFactory.Through(
        new DatasetLoader().Load(Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json")), 8);

    private static NarratorPrompt Prompt() =>
        new(NarratorSystemPrompt.Version, new NarrativeScope("match-001", "content-fixture", 1, 8),
            [new ApprovedNarrativeObservation(1, new("test-analyst", 1), "Canonical supplied observation.",
                ClaimType.RecordedMoment, null)], [], 0);

    private static NarratorPrompt PromptAtBytes(int bytes)
    {
        var prompt = Prompt() with
        {
            Observations = Enumerable.Range(1, NarratorLimits.MaxObservations).Select(index =>
                new ApprovedNarrativeObservation(index, new("test-analyst", index), "", ClaimType.RecordedMoment, null))
                .ToImmutableArray()
        };
        var remaining = bytes - JsonSerializer.SerializeToUtf8Bytes(prompt, NarratorLimits.Serialization).Length;
        prompt = prompt with
        {
            Observations = prompt.Observations.Select(item =>
            {
                var take = Math.Min(remaining, NarratorLimits.MaxTextCharacters);
                remaining -= take;
                return item with { Text = new string('x', take) };
            }).ToImmutableArray()
        };
        Assert.Equal(0, remaining);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(prompt, NarratorLimits.Serialization).Length);
        return prompt;
    }

    private static ChatResponse Response(string json) => new(new ChatMessage(ChatRole.Assistant, json))
    {
        FinishReason = ChatFinishReason.Stop
    };

    private sealed class ProtocolHandler(bool error) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
            var user = root.GetProperty("messages").EnumerateArray().Single(item => item.GetProperty("role").GetString() == "user");
            var content = user.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()!
                : content.EnumerateArray().Single().GetProperty("text").GetString()!;
            var prompt = JsonSerializer.Deserialize<NarratorPrompt>(text, NarratorLimits.Serialization)!;
            Assert.Equal(8, prompt.Scope.ThroughSequence);
            Assert.NotEmpty(prompt.Observations);
            Assert.DoesNotContain("SourceEvents", text);
            Assert.DoesNotContain("FactIds", text);
            Assert.DoesNotContain("API_KEY", text);
            if (error)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"private-provider-detail\",\"type\":\"server_error\"}}",
                        Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "offline-narration",
                    @object = "chat.completion",
                    created = 0,
                    model = "offline-protocol-fixture",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = JsonSerializer.Serialize(ModelBackedNarratorTests.SelectAll(prompt),
                                    NarratorLimits.Serialization)
                            },
                            finish_reason = "stop"
                        }
                    }
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class PayloadHandler(int bytes, bool noLength) : HttpMessageHandler
    {
        public bool DisposedContent => content?.Disposed == true;
        private PayloadContent? content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            content = new PayloadContent(bytes, noLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class PayloadContent(int bytes, bool noLength) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length)
        {
            length = bytes;
            return !noLength;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[bytes]).AsTask();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LateClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatResponse> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            var result = await Release.Task;
            Returned.TrySetResult();
            return result;
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
