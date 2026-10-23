using Microsoft.Agents.AI.Workflows;

namespace GameGround.AgentFramework.Tests;

public sealed class StreamingWorkflowHostTests
{
    [Fact]
    public async Task Ended_stream_without_output_from_the_trusted_executor_is_not_success()
    {
        var executor = new OutputExecutor();
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StreamingWorkflowHost.RunAsync<int, string>(workflow, 1, "publication", CancellationToken.None));

        Assert.Equal("Workflow ended without exactly one trusted publication decision.", error.Message);
    }

    [Fact]
    public async Task Output_of_the_wrong_type_is_not_a_publication_decision()
    {
        var executor = new OutputExecutor();
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StreamingWorkflowHost.RunAsync<int, int>(workflow, 1, "other-output", CancellationToken.None));
    }

    [Fact]
    public async Task Precancelled_host_does_not_start_the_workflow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new OutputExecutor();
        var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StreamingWorkflowHost.RunAsync<int, string>(workflow, 1, "other-output", cancellation.Token));

        Assert.Equal(0, executor.CallCount);
    }

    private sealed class OutputExecutor() : Executor<int, string>("other-output")
    {
        public int CallCount { get; private set; }
        public override ValueTask<string> HandleAsync(
            int message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult("Not a GameGround publication.");
        }
    }
}
