using System.Collections.Immutable;
using GameGround.Agents;

namespace GameGround.AgentFramework;

internal static class WorkflowBatchGate
{
    internal static WorkflowFrame Verify(
        WorkflowFrame frame, AgentIdentity producer, int maxRevisions, bool narrative)
    {
        var output = frame.Output;
        var malformed = output is null || output.Claims.IsDefault
            || output.Claims.Any(claim => claim is null || claim.Producer != producer)
            || output.Claims.Select(claim => claim.Id).Distinct().Count() != output.Claims.Length
            || (narrative && output.Claims.IsEmpty);
        if (malformed)
        {
            const string reason = "Expected initialized claims, unique batch IDs, the invoked producer identity and nonempty narration.";
            var ids = output is null || output.Claims.IsDefault ? []
                : output.Claims.Where(claim => claim is not null).Select(claim => claim.Id).ToImmutableArray();
            frame = frame.Log(producer, WorkflowDecisionKind.MalformedOutput, reason, ids) with
            {
                Previous = [],
                Feedback = [],
                RevisionReasons = [reason]
            };
        }
        else
        {
            var claims = output!.Claims;
            var verifier = new ClaimVerifier();
            var results = claims.Select(claim => verifier.Verify(frame.State.Context, claim)).ToArray();
            for (var index = 0; index < claims.Length; index++)
            {
                var result = results[index];
                if (narrative && result.Status == ClaimStatus.Verified
                    && !frame.State.Observations.Any(observation => SameMeaning(observation, claims[index])))
                {
                    var check = new VerificationCheck(VerificationCheckKind.ReferencedMetricMatches, false,
                        "Narrative introduces a fact not handed off as an approved observation.");
                    result = result with
                    {
                        Status = ClaimStatus.Rejected,
                        Checks = result.Checks.Add(check),
                        RejectionReason = check.Detail
                    };
                    results[index] = result;
                }

                var checkedClaim = claims[index] with { Status = result.Status };
                frame = frame with
                {
                    State = frame.State with
                    {
                        Verifications = frame.State.Verifications.Add(
                            new VerificationAttempt(producer, frame.Attempt, checkedClaim, result))
                    }
                };
                frame = frame.Log(producer,
                    result.Status == ClaimStatus.Verified ? WorkflowDecisionKind.Verified : WorkflowDecisionKind.Rejected,
                    result.RejectionReason ?? "All deterministic checks passed.", [checkedClaim.Id]);
            }

            frame = frame with
            {
                Previous = claims,
                Feedback = results.ToImmutableArray(),
                RevisionReasons = results.Where(result => result.Status == ClaimStatus.Rejected)
                    .Select(result => result.RejectionReason!).ToImmutableArray()
            };
            if (results.All(result => result.Status == ClaimStatus.Verified))
            {
                var unique = ImmutableArray<Claim>.Empty;
                foreach (var claim in claims)
                    (frame, unique) = AddUnique(frame, unique, claim with { Status = ClaimStatus.Verified }, producer);
                return frame with { Output = new AgentOutput(unique), Accepted = true };
            }
        }

        if (frame.Attempt < maxRevisions)
        {
            return frame.Log(producer, WorkflowDecisionKind.RevisionRequested,
                "Replace the entire batch; rejected or malformed batches are not partially published.", []) with
            {
                Attempt = frame.Attempt + 1,
                Route = narrative ? WorkflowRoute.Narrator : WorkflowRoute.Analyst,
                Output = null,
                Accepted = false
            };
        }

        return frame with
        {
            State = frame.State with { Status = WorkflowStatus.Rejected },
            Route = WorkflowRoute.Publication,
            Output = null,
            Accepted = false
        };
    }

    internal static (WorkflowFrame Frame, ImmutableArray<Claim> Claims) AddUnique(
        WorkflowFrame frame, ImmutableArray<Claim> destination, Claim claim, AgentIdentity producer)
    {
        if (!destination.Any(existing => SameMeaning(existing, claim)))
            return (frame, destination.Add(claim));
        return (frame.Log(producer, WorkflowDecisionKind.DuplicateRemoved,
            "Same context, semantic payload, facts and evidence; first deterministic producer wins.", [claim.Id]), destination);
    }

    private static bool SameMeaning(Claim left, Claim right) => left.Context == right.Context
        && left.Type == right.Type && left.Metric == right.Metric && left.Moment == right.Moment
        && left.FactIds.SequenceEqual(right.FactIds) && left.Evidence.SequenceEqual(right.Evidence)
        && left.CoveredSourceEvents.SequenceEqual(right.CoveredSourceEvents);
}
