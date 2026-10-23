using System.Collections.Immutable;
using System.Diagnostics;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Intelligence;

namespace GameGround.AgentFramework;

public sealed class ModelBackedAnalyst : ILocalAgent
{
    private readonly IAnalystModel model;
    private readonly Action<AnalystEvaluation>? evaluation;

    public ModelBackedAnalyst(IAnalystModel model, Action<AnalystEvaluation>? evaluation = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        this.evaluation = evaluation;
    }

    public AgentIdentity Identity { get; } = new("model-analyst", AgentRole.Analyst);

    public async Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = CreatePrompt(request);
        var watch = Stopwatch.StartNew();
        ImmutableArray<int> outputIds = [];
        ImmutableArray<AnalystObservationEvaluation> checks = [];
        var outcome = "Failed";
        string? failure = "Analyst model invocation did not complete.";
        try
        {
            var response = await model.AnalyzeAsync(prompt, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response?.Observations is { } observations)
                outputIds = observations.Where(item => item is not null).Select(item => item.Id).ToImmutableArray();
            response = ValidateShape(response);
            var claims = response.Observations.Select(item => ToClaim(request.Context, item)).ToImmutableArray();
            var verifier = new ClaimVerifier();
            checks = claims.Select(claim =>
            {
                var result = verifier.Verify(request.Context, claim);
                return new AnalystObservationEvaluation(claim.Id.Value, result.Status, result.RejectionReason);
            }).ToImmutableArray();
            outcome = checks.Any(check => check.Status == ClaimStatus.Rejected) ? "Rejected" : "Proposed";
            failure = null;
            return new AgentOutput(claims);
        }
        catch (AnalystModelOutputException)
        {
            failure = "Analyst model output violated the structured observation contract.";
            throw new AgentExecutionException(failure);
        }
        catch (AgentExecutionException)
        {
            failure = "Analyst provider invocation failed; no model response published.";
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "Cancelled";
            failure = "Analyst model invocation was cancelled.";
            throw;
        }
        finally
        {
            evaluation?.Invoke(new AnalystEvaluation(model.Identity, prompt.Version, prompt.Context, request.Attempt,
                prompt.Evidence.Select(item => item.Reference.Id.Value).ToImmutableArray(),
                outputIds, checks, outcome, failure, watch.Elapsed));
        }
    }

    private AnalystPrompt CreatePrompt(AgentRequest request)
    {
        var context = request.Context;
        var query = new EvidenceQuery(context);
        var facts = query.GetFacts();
        var allowed = ImmutableArray.CreateBuilder<AnalystObservation>();
        foreach (var fact in facts.Where(fact => fact.Value.HasValue
            && fact.Kind is not (FactKind.GoalOccurrence or FactKind.ControlTransitionOccurrence
                or FactKind.FoulInterruptionOccurrence)))
            allowed.Add(ToObservation(ClaimText.ForFact(context, fact.Id, new ClaimId(allowed.Count + 1), Identity)));
        foreach (var candidate in context.Candidates)
            allowed.Add(ToObservation(ClaimText.ForMoment(context, candidate.MomentId,
                new ClaimId(allowed.Count + 1), Identity)));
        return new AnalystPrompt(AnalystSystemPrompt.Version, context.Identity, facts,
            query.GetEvidence(facts.Select(fact => fact.Id).ToImmutableArray()),
            context.Sequences.Select(sequence => query.GetSequence(sequence.Id)).ToImmutableArray(),
            context.Moments.Select(moment => query.GetMoment(moment.Id)).ToImmutableArray(),
            allowed.ToImmutable(), request.RevisionReasons, request.Attempt);
    }

    private static AnalystObservation ToObservation(Claim claim) => new(
        claim.Id.Value, claim.Type.ToString(), claim.Text, claim.Context.Scope.MatchId, claim.Context.ContentId,
        claim.FactIds.Select(id => id.Value).ToArray(), claim.Evidence.Select(item => item.Id.Value).ToArray(),
        claim.CoveredSourceEvents.Select(id => id.Value).ToArray(), claim.Moment?.Value);

    private Claim ToClaim(AgentContext context, AnalystObservation item)
    {
        var type = item.Type switch
        {
            nameof(ClaimType.Metric) => ClaimType.Metric,
            nameof(ClaimType.RecordedMoment) => ClaimType.RecordedMoment,
            _ => throw new AnalystModelOutputException("Unknown observation type.")
        };
        var identity = context.Identity with
        {
            ContentId = item.ContextId,
            Scope = context.Identity.Scope with { MatchId = item.MatchId }
        };
        var facts = item.FactIds.Select(value => new FactId(value)).ToImmutableArray();
        var fact = facts.Length == 1 ? context.Facts.SingleOrDefault(value => value.Id == facts[0]) : null;
        var metric = type == ClaimType.Metric && fact?.Value is { } value
            ? new MetricAssertion(fact.Kind, fact.Team, value, fact.Unit, fact.Numerator, fact.Denominator) : null;
        return new Claim(new ClaimId(item.Id), Identity, item.Statement, type, identity, facts,
            item.EvidenceIds.Select(id => new EvidenceReference(new EvidenceId(id), identity)).ToImmutableArray(),
            item.SourceEventIds.Select(id => new EventId(id)).ToImmutableArray(), metric,
            item.MomentId is { } moment ? new MomentId(moment) : null);
    }

    private static AnalystModelOutput ValidateShape(AnalystModelOutput? output)
    {
        if (output?.Observations is null || output.Observations.Any(item => item is null
            || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Statement)
            || string.IsNullOrWhiteSpace(item.MatchId) || string.IsNullOrWhiteSpace(item.ContextId)
            || item.FactIds is null || item.EvidenceIds is null || item.SourceEventIds is null)
            || output.Observations.Select(item => item.Id).Distinct().Count() != output.Observations.Length)
            throw new AnalystModelOutputException("Malformed structured observation batch.");
        return output;
    }
}
