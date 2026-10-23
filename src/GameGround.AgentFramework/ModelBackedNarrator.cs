using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using GameGround.Agents;

namespace GameGround.AgentFramework;

public sealed class ModelBackedNarrator : INarrativeAgent
{
    private readonly INarratorModel model;
    private readonly Action<NarratorEvaluation>? evaluation;

    public ModelBackedNarrator(INarratorModel model, Action<NarratorEvaluation>? evaluation = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        this.evaluation = evaluation;
    }

    public AgentIdentity Identity { get; } = new("model-narrator", AgentRole.Narrator);

    public async Task<AgentOutput> GenerateAsync(NarrativeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Revisions are local to this adapter; an outer retry must never grant another model budget.
        if (request.Attempt != 0)
            throw new AgentExecutionException("Narrator model revision budget already exhausted.");
        var prompt = CreatePrompt(request);
        for (var attempt = 0; attempt <= 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            ImmutableArray<int> ids = [];
            ImmutableArray<NarratorFeedback> rejected = [];
            var outcome = "Failed";
            try
            {
                NarratorModelOutput? output = null;
                try
                {
                    output = await model.NarrateAsync(prompt, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output?.Claims is { Length: <= NarratorLimits.MaxClaims } claims)
                        ids = claims.Where(item => item is not null).Select(item => item.Id).ToImmutableArray();
                    rejected = NarratorGroundingGate.Validate(prompt, output);
                }
                catch (NarratorOutputException error)
                {
                    rejected = [new(error.Reason, null)];
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (rejected.IsEmpty && output is not null)
                {
                    var result = NarratorGroundingGate.ToClaims(output, request.ApprovedObservations, Identity);
                    cancellationToken.ThrowIfCancellationRequested();
                    outcome = "Proposed";
                    return result;
                }
                outcome = "Rejected";
                if (attempt == 1)
                    throw new AgentExecutionException(
                        $"Narrator rejected after one revision: {string.Join(", ", rejected.Select(item => item.Reason).Distinct())}.");
                // Do not forward rejected text, foreign IDs, arbitrary reasons or unknown claim IDs.
                prompt = prompt with
                {
                    Attempt = 1,
                    Feedback = rejected.Select(item => new NarratorFeedback(item.Reason,
                        prompt.Observations.Any(observation => observation.NarrativeClaimId == item.ClaimId)
                            ? item.ClaimId : null)).Distinct().ToImmutableArray()
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "Cancelled";
                throw;
            }
            finally
            {
                evaluation?.Invoke(new NarratorEvaluation(model.Identity, NarratorSystemPrompt.Version,
                    prompt.Scope, attempt, prompt.Observations.Select(item => item.Id).ToImmutableArray(),
                    ids, rejected, outcome, watch.Elapsed));
            }
        }
        throw new InvalidOperationException("Narrator revision loop ended without a decision.");
    }

    private static NarratorPrompt CreatePrompt(NarrativeRequest request)
    {
        var approved = request.ApprovedObservations;
        if (approved.IsDefaultOrEmpty || approved.Length > NarratorLimits.MaxObservations)
            throw new AgentExecutionException("Narrator requires a bounded nonempty approved handoff.");
        var context = approved[0]?.Context;
        if (context?.Scope is null || context.Window is null
            || !NarratorLimits.ValidIdentity(context.ContentId) || !NarratorLimits.ValidIdentity(context.Scope.MatchId)
            || !context.Scope.Manifest.IsSynthetic || context.Window.FromSequence < 1
            || context.Window.ThroughSequence < context.Window.FromSequence)
            throw new AgentExecutionException("Narrator requires an identified synthetic analysis window.");
        var keys = new HashSet<ObservationReference>();
        foreach (var claim in approved)
        {
            if (claim is null || claim.Status != ClaimStatus.Verified || claim.Context != context
                || claim.Producer is null || claim.Producer.Role != AgentRole.Analyst
                || !NarratorLimits.ValidIdentity(claim.Producer.Id) || claim.Id.Value <= 0
                || string.IsNullOrWhiteSpace(claim.Text) || claim.Text.Length > NarratorLimits.MaxTextCharacters
                || claim.FactIds.IsDefaultOrEmpty || claim.Evidence.IsDefaultOrEmpty || claim.CoveredSourceEvents.IsDefault
                || claim.Evidence.Any(item => item is null || item.Context != context)
                || !keys.Add(new ObservationReference(claim.Producer.Id, claim.Id.Value)))
                throw new AgentExecutionException("Narrator handoff must contain unique verified Analyst observations in one scope.");
        }
        var prompt = new NarratorPrompt(NarratorSystemPrompt.Version,
            new NarrativeScope(context.Scope.MatchId, context.ContentId,
                context.Window.FromSequence, context.Window.ThroughSequence),
            approved.Select((claim, index) => new ApprovedNarrativeObservation(
                index + 1, new ObservationReference(claim.Producer.Id, claim.Id.Value),
                claim.Text, claim.Type, claim.Metric)).ToImmutableArray(), [], 0);
        if (JsonSerializer.SerializeToUtf8Bytes(prompt, NarratorLimits.Serialization).Length > NarratorLimits.MaxPromptBytes)
            throw new AgentExecutionException("Narrator approved handoff exceeds the input size limit.");
        return prompt;
    }
}
