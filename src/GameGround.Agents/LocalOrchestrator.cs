using System.Collections.Immutable;

namespace GameGround.Agents;

public sealed class LocalOrchestrator
{
    private readonly ImmutableArray<ILocalAgent> analysts;
    private readonly ILocalAgent narrator;
    private readonly WorkflowOptions options;
    private readonly ClaimVerifier verifier = new();

    public LocalOrchestrator(
        ImmutableArray<ILocalAgent> analysts, ILocalAgent narrator, WorkflowOptions? options = null)
    {
        if (analysts.IsDefaultOrEmpty || analysts.Any(agent => agent is null
            || agent.Identity is null || string.IsNullOrWhiteSpace(agent.Identity.Id)
            || agent.Identity.Role != AgentRole.Analyst))
        {
            throw new ArgumentException("At least one identified analyst is required.", nameof(analysts));
        }

        ArgumentNullException.ThrowIfNull(narrator);
        if (narrator.Identity is null || string.IsNullOrWhiteSpace(narrator.Identity.Id)
            || narrator.Identity.Role != AgentRole.Narrator)
        {
            throw new ArgumentException("An identified narrator is required.", nameof(narrator));
        }

        if (analysts.Select(agent => agent.Identity.Id).Append(narrator.Identity.Id).Distinct().Count()
            != analysts.Length + 1)
        {
            throw new ArgumentException("Agent identities must be unique.", nameof(analysts));
        }

        this.options = options ?? WorkflowOptions.Default;
        if (this.options.MaxRevisions is < 0 or > 10
            || this.options.AgentTimeout <= TimeSpan.Zero
            || this.options.AgentTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Use 0-10 revisions and a positive timeout up to 5 minutes.");
        }

        this.analysts = analysts;
        this.narrator = narrator;
    }

    public async Task<SharedState> RunAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var history = ImmutableArray.CreateBuilder<WorkflowDecision>();
        var verifications = ImmutableArray.CreateBuilder<VerificationAttempt>();
        var observations = ImmutableArray.CreateBuilder<Claim>();
        var narratives = ImmutableArray.CreateBuilder<Claim>();
        var status = WorkflowStatus.Analyzing;
        foreach (var analyst in analysts)
        {
            var output = await RunStage(analyst, []);
            if (output is null)
            {
                return State();
            }

            foreach (var claim in output.Value.Claims)
            {
                AddUnique(observations, claim, analyst.Identity, output.Value.Attempt);
            }
        }

        if (observations.Count == 0)
        {
            status = WorkflowStatus.Completed;
            Log(narrator.Identity, 0, WorkflowDecisionKind.Skipped, "No supported observations; no narrative requested.", []);
            return State();
        }

        status = WorkflowStatus.Narrating;
        var narrative = await RunStage(narrator, observations.ToImmutable());
        if (narrative is null)
        {
            return State();
        }

        narratives.AddRange(narrative.Value.Claims);
        status = WorkflowStatus.Completed;
        return State();

        async Task<(ImmutableArray<Claim> Claims, int Attempt)?> RunStage(
            ILocalAgent agent, ImmutableArray<Claim> approved)
        {
            ImmutableArray<Claim> previous = [];
            ImmutableArray<VerificationResult> feedback = [];
            ImmutableArray<string> revisionReasons = [];
            for (var attempt = 0; attempt <= options.MaxRevisions; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    status = WorkflowStatus.Cancelled;
                    Log(agent.Identity, attempt, WorkflowDecisionKind.Cancelled, "Caller cancelled workflow.", []);
                    return null;
                }

                Log(agent.Identity, attempt, WorkflowDecisionKind.Invoked,
                    agent.Identity.Role == AgentRole.Analyst
                        ? "Select observations from window-scoped intelligence, without recomputing metrics."
                        : "Narrate only previously verified observations.", []);
                var request = new AgentRequest(context, approved, previous, feedback, revisionReasons, attempt);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                AgentOutput output;
                try
                {
                    output = await Task.Run(() => agent.GenerateAsync(request, linked.Token), linked.Token)
                        .WaitAsync(options.AgentTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    linked.Cancel();
                    status = WorkflowStatus.Failed;
                    Log(agent.Identity, attempt, WorkflowDecisionKind.Timeout,
                        "Agent timed out; no automatic overlapping retry. No output approved.", []);
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    linked.Cancel();
                    status = WorkflowStatus.Cancelled;
                    Log(agent.Identity, attempt, WorkflowDecisionKind.Cancelled, "Caller cancelled workflow.", []);
                    return null;
                }
                catch (AgentExecutionException exception)
                {
                    status = WorkflowStatus.Failed;
                    Log(agent.Identity, attempt, WorkflowDecisionKind.AgentFailure, exception.Message, []);
                    return null;
                }

                var malformed = output is null || output.Claims.IsDefault
                    || output.Claims.Any(claim => claim is null || claim.Producer != agent.Identity)
                    || output.Claims.Select(claim => claim.Id).Distinct().Count() != output.Claims.Length
                    || (agent.Identity.Role == AgentRole.Narrator && output.Claims.IsEmpty);
                if (malformed)
                {
                    const string reason = "Expected initialized claims, unique batch IDs, the invoked producer identity and nonempty narration.";
                    Log(agent.Identity, attempt, WorkflowDecisionKind.MalformedOutput,
                        reason, []);
                    previous = [];
                    feedback = [];
                    revisionReasons = [reason];
                }
                else
                {
                    previous = output!.Claims;
                    var results = previous.Select(claim => verifier.Verify(context, claim)).ToArray();
                    if (agent.Identity.Role == AgentRole.Narrator)
                    {
                        for (var index = 0; index < previous.Length; index++)
                        {
                            if (results[index].Status == ClaimStatus.Verified
                                && !approved.Any(observation => SameMeaning(observation, previous[index])))
                            {
                                var check = new VerificationCheck(VerificationCheckKind.ReferencedMetricMatches,
                                    false, "Narrative introduces a fact not handed off as an approved observation.");
                                results[index] = results[index] with
                                {
                                    Status = ClaimStatus.Rejected,
                                    Checks = results[index].Checks.Add(check),
                                    RejectionReason = check.Detail
                                };
                            }
                        }
                    }

                    feedback = results.ToImmutableArray();
                    revisionReasons = results.Where(result => result.Status == ClaimStatus.Rejected)
                        .Select(result => result.RejectionReason!).ToImmutableArray();
                    for (var index = 0; index < previous.Length; index++)
                    {
                        var result = results[index];
                        var checkedClaim = previous[index] with { Status = result.Status };
                        verifications.Add(new VerificationAttempt(agent.Identity, attempt, checkedClaim, result));
                        Log(agent.Identity, attempt,
                            result.Status == ClaimStatus.Verified ? WorkflowDecisionKind.Verified : WorkflowDecisionKind.Rejected,
                            result.RejectionReason ?? "All deterministic checks passed.", [checkedClaim.Id]);
                    }

                    if (results.All(result => result.Status == ClaimStatus.Verified))
                    {
                        var unique = ImmutableArray.CreateBuilder<Claim>();
                        foreach (var claim in previous)
                        {
                            AddUnique(unique, claim with { Status = ClaimStatus.Verified }, agent.Identity, attempt);
                        }

                        return (unique.ToImmutable(), attempt);
                    }
                }

                if (attempt < options.MaxRevisions)
                {
                    Log(agent.Identity, attempt, WorkflowDecisionKind.RevisionRequested,
                        "Replace the entire batch; rejected or malformed batches are not partially published.", []);
                }
            }

            status = WorkflowStatus.Rejected;
            return null;
        }

        void AddUnique(
            ImmutableArray<Claim>.Builder destination, Claim claim, AgentIdentity agent, int attempt)
        {
            if (destination.Any(existing => SameMeaning(existing, claim)))
            {
                Log(agent, attempt, WorkflowDecisionKind.DuplicateRemoved,
                    "Same context, semantic payload, facts and evidence; first deterministic producer wins.", [claim.Id]);
            }
            else
            {
                destination.Add(claim);
            }
        }

        void Log(
            AgentIdentity agent, int attempt, WorkflowDecisionKind kind, string reason, ImmutableArray<ClaimId> ids) =>
            history.Add(new WorkflowDecision(history.Count + 1, agent, attempt, kind, reason, ids));

        SharedState State() => new(context, observations.ToImmutable(), narratives.ToImmutable(),
            verifications.ToImmutable(), status, history.ToImmutable());
    }

    private static bool SameMeaning(Claim left, Claim right) => left.Context == right.Context
        && left.Type == right.Type && left.Metric == right.Metric && left.Moment == right.Moment
        && left.FactIds.SequenceEqual(right.FactIds) && left.Evidence.SequenceEqual(right.Evidence)
        && left.CoveredSourceEvents.SequenceEqual(right.CoveredSourceEvents);
}
