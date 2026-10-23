using System.Collections.Immutable;
using GameGround.Agents;

namespace GameGround.AgentFramework;

internal enum WorkflowRoute { Analyst, Narrator, Publication }

internal sealed record WorkflowFrame(SharedState State)
{
    internal int AnalystIndex { get; init; }
    internal int Attempt { get; init; }
    internal AgentOutput? Output { get; init; }
    internal ImmutableArray<Claim> Previous { get; init; } = [];
    internal ImmutableArray<VerificationResult> Feedback { get; init; } = [];
    internal ImmutableArray<string> RevisionReasons { get; init; } = [];
    internal WorkflowRoute Route { get; init; } = WorkflowRoute.Analyst;
    internal bool Accepted { get; init; }

    internal WorkflowFrame Log(
        AgentIdentity producer, WorkflowDecisionKind kind, string reason, ImmutableArray<ClaimId> ids) =>
        this with
        {
            State = State with
            {
                History = State.History.Add(new WorkflowDecision(
                    State.History.Length + 1, producer, Attempt, kind, reason, ids))
            }
        };
}
