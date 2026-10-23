using System.Collections.Immutable;
using GameGround.Agents;
using Microsoft.Agents.AI.Workflows;

namespace GameGround.AgentFramework;

public sealed class FrameworkWorkflowRunner
{
    private readonly ImmutableArray<ILocalAgent> analysts;
    private readonly INarrativeAgent narrator;
    private readonly WorkflowOptions options;

    public FrameworkWorkflowRunner(
        ImmutableArray<ILocalAgent> analysts, INarrativeAgent narrator, WorkflowOptions? options = null)
    {
        if (analysts.IsDefaultOrEmpty || analysts.Any(agent => agent is null
            || agent.Identity is null || string.IsNullOrWhiteSpace(agent.Identity.Id)
            || agent.Identity.Role != AgentRole.Analyst))
            throw new ArgumentException("At least one identified analyst is required.", nameof(analysts));
        ArgumentNullException.ThrowIfNull(narrator);
        if (narrator.Identity is null || string.IsNullOrWhiteSpace(narrator.Identity.Id)
            || narrator.Identity.Role != AgentRole.Narrator)
            throw new ArgumentException("An identified narrator is required.", nameof(narrator));
        if (analysts.Select(agent => agent.Identity.Id).Append(narrator.Identity.Id).Distinct().Count() != analysts.Length + 1)
            throw new ArgumentException("Agent identities must be unique.", nameof(analysts));
        this.options = options ?? WorkflowOptions.Default;
        if (this.options.MaxRevisions is < 0 or > 10 || this.options.AgentTimeout <= TimeSpan.Zero
            || this.options.AgentTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), "Use 0-10 revisions and a positive timeout up to 5 minutes.");
        this.analysts = analysts;
        this.narrator = narrator;
    }

    public async Task<SharedState> RunAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = new WorkflowSession(analysts, narrator, options, context);
        if (cancellationToken.IsCancellationRequested)
            return session.Cancel();
        var initialize = new InitializeExecutor(session);
        var analyst = new AnalystExecutor(session);
        var analystGate = new AnalystVerificationExecutor(session);
        var narrate = new NarratorExecutor(session);
        var narrativeGate = new NarrativeVerificationExecutor(session);
        var publication = new PublicationExecutor(session);
        var workflow = new WorkflowBuilder(initialize)
            .AddEdge(initialize, analyst)
            .AddEdge(analyst, analystGate)
            .AddEdge<WorkflowFrame>(analystGate, analyst, frame => frame?.Route == WorkflowRoute.Analyst)
            .AddEdge<WorkflowFrame>(analystGate, narrate, frame => frame?.Route == WorkflowRoute.Narrator)
            .AddEdge<WorkflowFrame>(analystGate, publication, frame => frame?.Route == WorkflowRoute.Publication)
            .AddEdge(narrate, narrativeGate)
            .AddEdge<WorkflowFrame>(narrativeGate, narrate, frame => frame?.Route == WorkflowRoute.Narrator)
            .AddEdge<WorkflowFrame>(narrativeGate, publication, frame => frame?.Route == WorkflowRoute.Publication)
            .WithOutputFrom(publication)
            .Build();
        try
        {
            return await StreamingWorkflowHost.RunAsync<AgentContext, SharedState>(
                workflow, context, "publication", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return session.Cancel();
        }
    }

    private sealed class WorkflowSession(
        ImmutableArray<ILocalAgent> analysts, INarrativeAgent narrator, WorkflowOptions options, AgentContext context)
    {
        private WorkflowFrame current = new(new SharedState(context, [], [], [], WorkflowStatus.Created, []));
        private AgentIdentity activeAgent = analysts[0].Identity;

        internal WorkflowFrame Save(WorkflowFrame frame) => current = frame;
        internal WorkflowFrame Initialize() => Save(current with
        {
            State = current.State with { Status = WorkflowStatus.Analyzing }
        });

        internal SharedState Cancel()
        {
            if (current.State.Status != WorkflowStatus.Cancelled)
            {
                current = current.Log(activeAgent, WorkflowDecisionKind.Cancelled, "Caller cancelled workflow.", []);
            }
            return Save(current with
            {
                State = current.State with { Status = WorkflowStatus.Cancelled, NarrativeClaims = [] }
            }).State;
        }

        internal async Task<WorkflowFrame> InvokeAsync(WorkflowFrame frame, bool narrative, CancellationToken token)
        {
            var identity = narrative ? narrator.Identity : analysts[frame.AnalystIndex].Identity;
            activeAgent = identity;
            frame = Save(frame.Log(identity, WorkflowDecisionKind.Invoked, narrative
                ? "Narrate only previously verified observations."
                : "Select observations from window-scoped intelligence, without recomputing metrics.", []));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var invocationToken = linked.Token;
            try
            {
                var output = await Task.Run(() => narrative
                    ? narrator.GenerateAsync(new NarrativeRequest(frame.State.Observations,
                        frame.Previous, frame.Feedback, frame.RevisionReasons, frame.Attempt), invocationToken)
                    : analysts[frame.AnalystIndex].GenerateAsync(new AgentRequest(frame.State.Context, [],
                        frame.Previous, frame.Feedback, frame.RevisionReasons, frame.Attempt), invocationToken), invocationToken)
                    .WaitAsync(options.AgentTimeout, token);
                token.ThrowIfCancellationRequested();
                return Save(frame with { Output = output, Accepted = false });
            }
            catch (TimeoutException)
            {
                linked.Cancel();
                return Fail(WorkflowStatus.Failed, WorkflowDecisionKind.Timeout,
                    "Agent timed out; no automatic overlapping retry. No output approved.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                linked.Cancel();
                return Fail(WorkflowStatus.Cancelled, WorkflowDecisionKind.Cancelled, "Caller cancelled workflow.");
            }
            catch (AgentExecutionException exception)
            {
                return Fail(WorkflowStatus.Failed, WorkflowDecisionKind.AgentFailure, exception.Message);
            }

            WorkflowFrame Fail(WorkflowStatus status, WorkflowDecisionKind kind, string reason)
            {
                frame = frame.Log(identity, kind, reason, []);
                return Save(frame with
                {
                    State = frame.State with { Status = status },
                    Output = null,
                    Route = WorkflowRoute.Publication
                });
            }
        }

        internal WorkflowFrame VerifyAnalyst(WorkflowFrame frame)
        {
            if (frame.State.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                return Save(frame);
            var identity = analysts[frame.AnalystIndex].Identity;
            frame = WorkflowBatchGate.Verify(frame, identity, options.MaxRevisions, narrative: false);
            if (!frame.Accepted)
                return Save(frame);
            var observations = frame.State.Observations;
            foreach (var claim in frame.Output!.Claims)
                (frame, observations) = WorkflowBatchGate.AddUnique(frame, observations, claim, identity);
            var state = frame.State with { Observations = observations };
            if (frame.AnalystIndex + 1 < analysts.Length)
                return Save(new WorkflowFrame(state) { AnalystIndex = frame.AnalystIndex + 1 });
            if (observations.IsEmpty)
            {
                frame = new WorkflowFrame(state with { Status = WorkflowStatus.Completed })
                    .Log(narrator.Identity, WorkflowDecisionKind.Skipped, "No supported observations; no narrative requested.", []);
                return Save(frame with { Route = WorkflowRoute.Publication });
            }
            return Save(new WorkflowFrame(state with { Status = WorkflowStatus.Narrating })
            {
                AnalystIndex = frame.AnalystIndex,
                Route = WorkflowRoute.Narrator
            });
        }

        internal WorkflowFrame VerifyNarrative(WorkflowFrame frame)
        {
            if (frame.State.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                return Save(frame);
            frame = WorkflowBatchGate.Verify(frame, narrator.Identity, options.MaxRevisions, narrative: true);
            if (frame.Accepted)
                frame = frame with
                {
                    State = frame.State with { NarrativeClaims = frame.Output!.Claims },
                    Route = WorkflowRoute.Publication
                };
            return Save(frame);
        }

        internal SharedState Publish(WorkflowFrame frame)
        {
            if (frame.State.Status == WorkflowStatus.Narrating && frame.Accepted)
            {
                if (frame.State.NarrativeClaims.IsEmpty
                    || frame.State.NarrativeClaims.Any(claim => claim.Status != ClaimStatus.Verified))
                    throw new InvalidOperationException("Publication requires a nonempty, wholly verified narrative.");
                frame = frame with { State = frame.State with { Status = WorkflowStatus.Completed } };
            }
            if (frame.State.Status is not (WorkflowStatus.Completed or WorkflowStatus.Rejected
                or WorkflowStatus.Failed or WorkflowStatus.Cancelled))
                throw new InvalidOperationException("Publication gate received a nonterminal workflow.");
            if (frame.State.Status != WorkflowStatus.Completed)
                frame = frame with { State = frame.State with { NarrativeClaims = [] } };
            return Save(frame).State;
        }
    }

    private sealed class InitializeExecutor(WorkflowSession session) : Executor<AgentContext, WorkflowFrame>("initialize")
    {
        public override ValueTask<WorkflowFrame> HandleAsync(
            AgentContext message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(session.Initialize());
    }

    private sealed class AnalystExecutor(WorkflowSession session) : Executor<WorkflowFrame, WorkflowFrame>("analyst")
    {
        public override async ValueTask<WorkflowFrame> HandleAsync(
            WorkflowFrame message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            await session.InvokeAsync(message, narrative: false, cancellationToken);
    }

    private sealed class AnalystVerificationExecutor(WorkflowSession session)
        : Executor<WorkflowFrame, WorkflowFrame>("analyst-verification")
    {
        public override ValueTask<WorkflowFrame> HandleAsync(
            WorkflowFrame message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(session.VerifyAnalyst(message));
    }

    private sealed class NarratorExecutor(WorkflowSession session) : Executor<WorkflowFrame, WorkflowFrame>("narrator")
    {
        public override async ValueTask<WorkflowFrame> HandleAsync(
            WorkflowFrame message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            await session.InvokeAsync(message, narrative: true, cancellationToken);
    }

    private sealed class NarrativeVerificationExecutor(WorkflowSession session)
        : Executor<WorkflowFrame, WorkflowFrame>("claim-verification")
    {
        public override ValueTask<WorkflowFrame> HandleAsync(
            WorkflowFrame message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(session.VerifyNarrative(message));
    }

    private sealed class PublicationExecutor(WorkflowSession session) : Executor<WorkflowFrame, SharedState>("publication")
    {
        public override ValueTask<SharedState> HandleAsync(
            WorkflowFrame message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(session.Publish(message));
    }
}
