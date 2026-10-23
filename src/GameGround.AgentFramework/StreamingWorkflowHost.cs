using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI.Workflows;

namespace GameGround.AgentFramework;

internal static class StreamingWorkflowHost
{
    internal static async Task<TOutput> RunAsync<TInput, TOutput>(
        Workflow workflow, TInput input, string outputExecutorId, CancellationToken cancellationToken)
        where TInput : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input);
        var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancellationRequested.TrySetResult());
        var outputs = new List<TOutput>();
        var observation = ObserveAsync();

        if (await Task.WhenAny(observation, cancellationRequested.Task) == cancellationRequested.Task)
        {
            await run.CancelRunAsync();
        }

        try
        {
            await observation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return outputs.Count == 1
            ? outputs[0]
            : throw new InvalidOperationException("Workflow ended without exactly one trusted publication decision.");

        async Task ObserveAsync()
        {
            await foreach (var item in run.WatchStreamAsync())
            {
                if (item is WorkflowErrorEvent error)
                    ExceptionDispatchInfo.Capture(error.Exception
                        ?? new InvalidOperationException("Workflow reported an error without an exception.")).Throw();
                if (item is ExecutorFailedEvent failure)
                    ExceptionDispatchInfo.Capture(failure.Data
                        ?? new InvalidOperationException("Executor reported failure without an exception.")).Throw();
                if (item is WorkflowOutputEvent output && output.ExecutorId == outputExecutorId
                    && output.Data is TOutput value)
                    outputs.Add(value);
            }
        }
    }
}
