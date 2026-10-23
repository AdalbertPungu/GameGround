using System.Collections.Immutable;
using GameGround.Agents;

namespace GameGround.AgentFramework;

internal static class NarratorGroundingGate
{
    internal static ImmutableArray<NarratorFeedback> Validate(NarratorPrompt prompt, NarratorModelOutput? output)
    {
        if (output?.Claims is null)
            return [new(NarratorRejection.InvalidStructure, null)];
        if (output.Claims.Length > NarratorLimits.MaxClaims)
            return [new(NarratorRejection.SizeLimit, null)];
        if (output.Claims.Length == 0)
            return [new(NarratorRejection.EmptyNarrative, null)];
        var failures = ImmutableArray.CreateBuilder<NarratorFeedback>();
        var ids = new HashSet<int>();
        var references = new HashSet<ObservationReference>();
        foreach (var claim in output.Claims)
        {
            if (claim is null || string.IsNullOrWhiteSpace(claim.Text) || claim.Scope is null)
            {
                failures.Add(new(NarratorRejection.InvalidStructure, claim?.Id));
                continue;
            }
            if (claim.Text.Length > NarratorLimits.MaxTextCharacters)
                failures.Add(new(NarratorRejection.SizeLimit, claim.Id));
            if (claim.Id <= 0 || !ids.Add(claim.Id))
                failures.Add(new(NarratorRejection.InvalidClaimId, claim.Id));
            if (claim.Scope != prompt.Scope)
                failures.Add(new(NarratorRejection.ScopeMismatch, claim.Id));
            if (claim.ObservationIds is not { Length: 1 } || claim.ObservationIds[0] is not { } reference
                || !NarratorLimits.ValidIdentity(reference.AgentId) || !references.Add(reference))
            {
                failures.Add(new(NarratorRejection.InvalidObservationReference, claim.Id));
                continue;
            }
            var observation = prompt.Observations.SingleOrDefault(item => item.Id == reference);
            if (observation is null)
            {
                failures.Add(new(NarratorRejection.InvalidObservationReference, claim.Id));
                continue;
            }
            if (claim.Id != observation.NarrativeClaimId)
                failures.Add(new(NarratorRejection.InvalidClaimId, claim.Id));
            if (claim.Category != observation.Category)
                failures.Add(new(NarratorRejection.CategoryMismatch, claim.Id));
            if (claim.Metric != observation.Metric)
                failures.Add(new(NarratorRejection.NumericMismatch, claim.Id));
            if (claim.Text != observation.Text)
                failures.Add(new(NarratorRejection.UnsupportedWording, claim.Id));
        }
        return failures.ToImmutable();
    }

    internal static AgentOutput ToClaims(
        NarratorModelOutput output, ImmutableArray<Claim> approved, AgentIdentity producer) =>
        new(output.Claims.Select(draft =>
        {
            var reference = draft.ObservationIds[0];
            var source = approved.Single(item => item.Id.Value == reference.ClaimId
                && item.Producer.Id == reference.AgentId);
            return source with
            {
                Id = new ClaimId(draft.Id),
                Producer = producer,
                Text = draft.Text,
                Type = draft.Category,
                Metric = draft.Metric,
                Status = ClaimStatus.Proposed
            };
        }).ToImmutableArray());
}
