using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework.Tests;

public sealed class ApiCompatibilityTests
{
    public sealed record TestAgentOutput(string Kind, IReadOnlyList<int> FactIds);
    public sealed record TestFact(int FactId, string Description);
    private sealed record SpikeInput(int Value);
    private sealed record IntermediateValue(int Value);
    private sealed record SpikeOutput(int Value);
    private sealed record ValidationDecision(int Value, bool Approved);
    private sealed record RouteOutput(string Route, int Value);

    [Fact]
    public async Task Typed_workflow_passes_input_through_two_executors_to_output()
    {
        var increment = new IncrementExecutor();
        var doubleValue = new DoubleExecutor();
        var workflow = new WorkflowBuilder(increment)
            .AddEdge(increment, doubleValue)
            .WithOutputFrom(doubleValue)
            .Build();

        await using var run = await InProcessExecution.RunAsync(workflow, new SpikeInput(3));

        var output = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>());
        Assert.Equal(new SpikeOutput(8), Assert.IsType<SpikeOutput>(output.Data));
    }

    [Theory]
    [InlineData(1, "approved")]
    [InlineData(-1, "rejected")]
    public async Task Conditional_edges_invoke_only_the_matching_branch(int value, string expectedRoute)
    {
        var validation = new ValidationExecutor();
        var success = new RouteExecutor("approved");
        var failure = new RouteExecutor("rejected");
        var workflow = new WorkflowBuilder(validation)
            .AddEdge<ValidationDecision>(validation, success, decision => decision is { Approved: true })
            .AddEdge<ValidationDecision>(validation, failure, decision => decision is { Approved: false })
            .WithOutputFrom(success, failure)
            .Build();

        await using var run = await InProcessExecution.RunAsync(workflow, new SpikeInput(value));

        var output = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>());
        Assert.Equal(new RouteOutput(expectedRoute, value), Assert.IsType<RouteOutput>(output.Data));
        Assert.Equal(expectedRoute == "approved" ? 1 : 0, success.CallCount);
        Assert.Equal(expectedRoute == "rejected" ? 1 : 0, failure.CallCount);
    }

    [Fact]
    public async Task Workflow_cancellation_reaches_the_running_executor()
    {
        var executor = new CancellableExecutor();
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new SpikeInput(1));

        // The watchdog only bounds a broken test; cancellation is coordinated by executor entry.
        try
        {
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await run.CancelRunAsync();

            await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(executor.Completed);
        }
        finally
        {
            await run.CancelRunAsync();
        }
    }

    [Fact]
    public async Task Scripted_agent_returns_a_strongly_typed_structured_response()
    {
        var expected = new TestAgentOutput("recorded", [1, 8]);
        using var client = new ScriptedChatClient((_, options) =>
        {
            var format = Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            Assert.Contains("FactIds", format.Schema.ToString(), StringComparison.OrdinalIgnoreCase);
            return TextResponse(JsonSerializer.Serialize(expected));
        });
        AIAgent agent = new ChatClientAgent(client, name: "structured-api-spike");
        var session = await agent.CreateSessionAsync();

        AgentResponse<TestAgentOutput> response =
            await agent.RunAsync<TestAgentOutput>("Return the scripted test fixture.", session);

        Assert.Equal(expected.Kind, response.Result.Kind);
        Assert.Equal(expected.FactIds, response.Result.FactIds);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, client.RemainingResponses);
    }

    [Fact]
    public async Task Scripted_agent_invokes_only_the_registered_local_function_tool()
    {
        var requestedIds = new List<int>();
        TestFact GetFact(int factId)
        {
            requestedIds.Add(factId);
            return LookupTestFact(factId);
        }

        AIFunction tool = AIFunctionFactory.Create(GetFact, name: "GetFact");
        using var client = new ScriptedChatClient(
            (_, options) =>
            {
                Assert.Same(tool, Assert.Single(Assert.IsAssignableFrom<IList<AITool>>(options?.Tools)));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-fact-8", "GetFact",
                        new Dictionary<string, object?> { ["factId"] = 8 })
                ]));
            },
            (messages, _) =>
            {
                var result = Assert.Single(messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>());
                Assert.Equal("call-fact-8", result.CallId);
                Assert.Null(result.Exception);
                Assert.Contains("Hard-coded API spike fixture", JsonSerializer.Serialize(result.Result));
                return TextResponse("Local tool completed.");
            });
        AIAgent agent = new ChatClientAgent(client, name: "function-api-spike", tools: [tool]);
        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync("Invoke the local test function for fact 8.", session);

        Assert.Equal("Local tool completed.", response.Text);
        Assert.Equal([8], requestedIds);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(0, client.RemainingResponses);
    }

    [Fact]
    public async Task Local_function_tool_explicitly_rejects_an_unavailable_fact()
    {
        AIFunction tool = AIFunctionFactory.Create(LookupTestFact, name: "GetFact");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["factId"] = 9 }));
    }

    private static ChatResponse TextResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static TestFact LookupTestFact(int factId) => factId == 8
        ? new TestFact(8, "Hard-coded API spike fixture; not football intelligence.")
        : throw new ArgumentOutOfRangeException(nameof(factId), factId, "Only test fact 8 is available.");

    private sealed class IncrementExecutor() : Executor<SpikeInput, IntermediateValue>("increment")
    {
        public override ValueTask<IntermediateValue> HandleAsync(
            SpikeInput message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IntermediateValue(message.Value + 1));
    }

    private sealed class DoubleExecutor() : Executor<IntermediateValue, SpikeOutput>("double")
    {
        public override ValueTask<SpikeOutput> HandleAsync(
            IntermediateValue message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SpikeOutput(message.Value * 2));
    }

    private sealed class ValidationExecutor() : Executor<SpikeInput, ValidationDecision>("validate")
    {
        public override ValueTask<ValidationDecision> HandleAsync(
            SpikeInput message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ValidationDecision(message.Value, message.Value >= 0));
    }

    private sealed class RouteExecutor : Executor<ValidationDecision, RouteOutput>
    {
        private readonly string _route;

        public RouteExecutor(string route) : base(route) => _route = route;

        public int CallCount { get; private set; }

        public override ValueTask<RouteOutput> HandleAsync(
            ValidationDecision message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new RouteOutput(_route, message.Value));
        }
    }

    private sealed class CancellableExecutor() : Executor<SpikeInput, SpikeOutput>("cancellable")
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed { get; private set; }

        public override async ValueTask<SpikeOutput> HandleAsync(
            SpikeInput message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                Completed = true;
                return new SpikeOutput(message.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }
}
