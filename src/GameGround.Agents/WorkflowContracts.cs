using System.Collections.Immutable;

namespace GameGround.Agents;

public enum WorkflowStatus
{
    Created,
    Analyzing,
    Narrating,
    Completed,
    Rejected,
    Failed,
    Cancelled
}

public enum WorkflowDecisionKind
{
    Invoked,
    Verified,
    Rejected,
    DuplicateRemoved,
    RevisionRequested,
    MalformedOutput,
    Timeout,
    AgentFailure,
    Cancelled,
    Skipped
}

public sealed record WorkflowDecision(
    int Step, AgentIdentity Agent, int Attempt, WorkflowDecisionKind Kind,
    string Reason, ImmutableArray<ClaimId> ClaimIds);

public sealed record VerificationAttempt(
    AgentIdentity Agent, int Attempt, Claim Claim, VerificationResult Result);

public sealed record SharedState(
    AgentContext Context,
    ImmutableArray<Claim> Observations,
    ImmutableArray<Claim> NarrativeClaims,
    ImmutableArray<VerificationAttempt> Verifications,
    WorkflowStatus Status,
    ImmutableArray<WorkflowDecision> History);

public sealed record AgentRequest(
    AgentContext Context,
    ImmutableArray<Claim> ApprovedObservations,
    ImmutableArray<Claim> PreviousClaims,
    ImmutableArray<VerificationResult> Feedback,
    ImmutableArray<string> RevisionReasons,
    int Attempt);

public sealed record AgentOutput(ImmutableArray<Claim> Claims);

public interface ILocalAgent
{
    AgentIdentity Identity { get; }
    Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowOptions(int MaxRevisions, TimeSpan AgentTimeout)
{
    public static WorkflowOptions Default { get; } = new(1, TimeSpan.FromSeconds(5));
}

public sealed class AgentExecutionException(string message) : Exception(message);
