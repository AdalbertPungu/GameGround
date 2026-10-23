using System.Collections.Concurrent;
using System.Diagnostics;
using GameGround.Agents;
using GameGround.Core;

namespace GameGround.AgentFramework;

public sealed record EvaluationModelIdentity(
    string Provider, string Model, string Version, string Endpoint);

public sealed record EvaluationAgentMetrics(
    int Calls,
    int SuccessfulCalls,
    int FailedCalls,
    int MalformedOrIncompleteResponses,
    double TotalLatencyMilliseconds,
    int ObservationsOrClaimsProposed,
    int Approved,
    int Revised,
    int Rejected,
    int InvalidReferences,
    string? FailureCategory);

public sealed record EvaluationGroundTruth(
    int ExpectedHomeScore,
    int ExpectedAwayScore,
    int ActualHomeScore,
    int ActualAwayScore,
    bool ScoreMatches,
    bool AnalystNumericFactsVerified,
    bool NarratorReceivedOnlyApprovedObservations,
    bool? PublishedClaimsHaveValidProvenance,
    bool? PublishedNumericClaimsMatchApprovedObservations,
    string SemanticCorrectnessAssessment);

public sealed record ModelEvaluationReport(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    double TotalExecutionMilliseconds,
    string DatasetId,
    string MatchId,
    string DatasetSha256,
    EvaluationModelIdentity AnalystModel,
    EvaluationModelIdentity NarratorModel,
    string AnalystPromptVersion,
    string NarratorPromptVersion,
    string WorkflowStatus,
    bool PublicationOccurred,
    string? NonPublicationReason,
    EvaluationAgentMetrics Analyst,
    EvaluationAgentMetrics Narrator,
    EvaluationGroundTruth GroundTruth);

public static class ModelEvaluationRunner
{
    public const string ExpectedDatasetSha256 = "5BE8CCAEC5169D61F47AC184B59BD22060B12DC4AEE6A61A542B657871218B77";
    public const string ExpectedDatasetId = "synthetic-miniature-v1";
    public const string ExpectedMatchId = "harbor-meadow-miniature";
    public const int ExpectedHomeScore = 1;
    public const int ExpectedAwayScore = 1;

    public static async Task<ModelEvaluationReport> RunAsync(
        MatchDataset dataset,
        string datasetSha256,
        IAnalystModel analystModel,
        INarratorModel narratorModel,
        string analystEndpointAuthority,
        string narratorEndpointAuthority,
        CancellationToken cancellationToken = default,
        TimeSpan? agentTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetSha256);
        ArgumentNullException.ThrowIfNull(analystModel);
        ArgumentNullException.ThrowIfNull(narratorModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(analystEndpointAuthority);
        ArgumentException.ThrowIfNullOrWhiteSpace(narratorEndpointAuthority);
        var analystEndpoint = ValidateLocalEndpoint(analystEndpointAuthority);
        var narratorEndpoint = ValidateLocalEndpoint(narratorEndpointAuthority);
        ValidateModelIdentity(analystModel.Identity.Provider, analystModel.Identity.Model, analystModel.Identity.Version);
        ValidateModelIdentity(narratorModel.Identity.Provider, narratorModel.Identity.Model, narratorModel.Identity.Version);
        var effectiveTimeout = agentTimeout ?? TimeSpan.FromMinutes(5);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(agentTimeout), "Evaluation agent timeout must be positive and at most five minutes.");

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var hash = datasetSha256.ToUpperInvariant();
        if (!string.Equals(hash, ExpectedDatasetSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Controlled evaluation requires the unchanged synthetic match-001 fixture.");
        if (!dataset.Manifest.IsSynthetic)
            throw new InvalidOperationException("Model evaluation accepts synthetic datasets only.");
        if (dataset.Manifest.DatasetId != ExpectedDatasetId || dataset.Match.MatchId != ExpectedMatchId
            || dataset.Events.IsDefault || dataset.Events.Length != 20)
            throw new InvalidOperationException("Controlled evaluation accepts only the unchanged synthetic miniature fixture.");
        var snapshot = MatchReducer.Replay(dataset);
        if (snapshot.HomeScore != ExpectedHomeScore || snapshot.AwayScore != ExpectedAwayScore)
            throw new InvalidOperationException("Synthetic evaluation fixture score must be 1-1; no model call was made.");

        var context = AgentContextFactory.Through(dataset, dataset.Events.Length);
        var analystEvaluations = new ConcurrentQueue<AnalystEvaluation>();
        var narratorEvaluations = new ConcurrentQueue<NarratorEvaluation>();
        var trackedAnalystModel = new CountingAnalystModel(analystModel);
        var trackedNarratorModel = new CountingNarratorModel(narratorModel);
        var workflow = new FrameworkWorkflowRunner(
            [new ModelBackedAnalyst(trackedAnalystModel, analystEvaluations.Enqueue)],
            new ModelBackedNarrator(trackedNarratorModel, narratorEvaluations.Enqueue),
            new WorkflowOptions(1, effectiveTimeout));
        var state = await workflow.RunAsync(context, cancellationToken);
        stopwatch.Stop();

        var analystRecords = analystEvaluations.ToArray();
        var narratorRecords = narratorEvaluations.ToArray();
        var published = state.Status == WorkflowStatus.Completed && !state.NarrativeClaims.IsEmpty;
        var analystMetrics = SummarizeAnalyst(analystRecords, state, trackedAnalystModel.CallCount);
        var narratorMetrics = SummarizeNarrator(narratorRecords, state, trackedNarratorModel.CallCount);
        var expectedReferences = state.Observations
            .Select(claim => new ObservationReference(claim.Producer.Id, claim.Id.Value)).ToHashSet();
        var narratorReceivedOnlyApproved = narratorRecords
            .SelectMany(record => record.InputObservationIds)
            .All(expectedReferences.Contains)
            && state.Observations.All(claim => claim.Status == ClaimStatus.Verified);
        var publishedProvenanceValid = !published || state.NarrativeClaims.All(claim =>
            claim.Status == ClaimStatus.Verified
            && state.Verifications.Any(attempt => attempt.Agent.Role == AgentRole.Narrator
                && attempt.Claim.Id == claim.Id && attempt.Result.Status == ClaimStatus.Verified
                && attempt.Result.SourceEvents.SequenceEqual(claim.CoveredSourceEvents)));
        var analystNumericFactsVerified = state.Observations.All(claim => claim.Metric is null
            || state.Verifications.Any(attempt => attempt.Agent.Role == AgentRole.Analyst
                && attempt.Claim.Id == claim.Id && attempt.Result.Status == ClaimStatus.Verified
                && attempt.Result.Checks.Any(check =>
                    check.Kind == VerificationCheckKind.NumericValuesConsistent && check.Passed)));
        var approvedByReference = state.Observations.ToDictionary(
            claim => new ObservationReference(claim.Producer.Id, claim.Id.Value));
        var numericClaimsMatch = !published || state.NarrativeClaims.All(claim =>
            claim.Metric is null || approvedByReference.Values.Any(observation =>
                observation.Metric == claim.Metric && observation.FactIds.SequenceEqual(claim.FactIds)
                && observation.Evidence.SequenceEqual(claim.Evidence)));
        var report = new ModelEvaluationReport(
            1, startedAt, stopwatch.Elapsed.TotalMilliseconds, dataset.Manifest.DatasetId,
            dataset.Match.MatchId, hash,
            new(analystModel.Identity.Provider, analystModel.Identity.Model, analystModel.Identity.Version,
                analystEndpoint),
            new(narratorModel.Identity.Provider, narratorModel.Identity.Model, narratorModel.Identity.Version,
                narratorEndpoint),
            AnalystSystemPrompt.Version, NarratorSystemPrompt.Version, state.Status.ToString(), published,
            NonPublicationReason(state, analystMetrics, narratorMetrics), analystMetrics, narratorMetrics,
            new(ExpectedHomeScore, ExpectedAwayScore, snapshot.HomeScore, snapshot.AwayScore,
                snapshot.HomeScore == ExpectedHomeScore && snapshot.AwayScore == ExpectedAwayScore,
                analystNumericFactsVerified, narratorReceivedOnlyApproved,
                published ? publishedProvenanceValid : null, published ? numericClaimsMatch : null,
                "Mechanical checks verify provenance and canonical numeric facts; semantic or qualitative narrative correctness is not established."));
        return report;
    }

    private static EvaluationAgentMetrics SummarizeAnalyst(
        AnalystEvaluation[] records, SharedState state, int callCount)
    {
        var verifications = state.Verifications.Where(item => item.Agent.Role == AgentRole.Analyst).ToArray();
        var unrecordedCalls = Math.Max(0, callCount - records.Length);
        return new EvaluationAgentMetrics(
            callCount,
            records.Count(IsCompletedCall),
            records.Count(IsFailedCall) + unrecordedCalls,
            records.Count(item => item.FailureReason == "Analyst model output violated the structured observation contract."),
            records.Sum(item => item.Latency.TotalMilliseconds),
            records.Sum(item => item.OutputObservationIds.Length),
            state.Observations.Length,
            records.Where(item => item.Attempt > 0).Sum(item => item.OutputObservationIds.Length),
            verifications.Count(item => item.Result.Status == ClaimStatus.Rejected),
            verifications.Count(item => item.Result.Checks.Any(check =>
                check.Kind == VerificationCheckKind.EvidenceExists && !check.Passed)),
            FailureCategory(state, AgentRole.Analyst));
    }

    private static EvaluationAgentMetrics SummarizeNarrator(
        NarratorEvaluation[] records, SharedState state, int callCount)
    {
        var verifications = state.Verifications.Where(item => item.Agent.Role == AgentRole.Narrator).ToArray();
        var adapterRejected = records.Sum(record => record.Rejections.IsEmpty
            ? 0
            : Math.Max(1, record.Rejections.Select(item => item.ClaimId).Distinct().Count()));
        var unrecordedCalls = Math.Max(0, callCount - records.Length);
        return new EvaluationAgentMetrics(
            callCount,
            records.Count(IsCompletedCall),
            records.Count(IsFailedCall) + unrecordedCalls,
            records.Sum(item => item.Rejections.Count(reason => reason.Reason == NarratorRejection.InvalidStructure)),
            records.Sum(item => item.Latency.TotalMilliseconds),
            records.Sum(item => item.OutputClaimIds.Length),
            state.NarrativeClaims.Length,
            records.Where(item => item.Attempt > 0).Sum(item => item.OutputClaimIds.Length),
            adapterRejected + verifications.Count(item => item.Result.Status == ClaimStatus.Rejected),
            records.Sum(record => record.Rejections.Count(reason =>
                reason.Reason == NarratorRejection.InvalidObservationReference)),
            FailureCategory(state, AgentRole.Narrator,
                records.Any(item => item.Rejections.Any(rejection =>
                    rejection.Reason == NarratorRejection.InvalidStructure))));
    }

    private static bool IsCompletedCall(AnalystEvaluation item) => item.Outcome is "Proposed" or "Rejected";
    private static bool IsCompletedCall(NarratorEvaluation item) =>
        (item.Outcome is "Proposed" or "Rejected")
        && !item.Rejections.Any(reason => reason.Reason == NarratorRejection.InvalidStructure);
    private static bool IsFailedCall(AnalystEvaluation item) => item.Outcome is "Failed" or "Cancelled";
    private static bool IsFailedCall(NarratorEvaluation item) => (item.Outcome is "Failed" or "Cancelled")
        || item.Rejections.Any(reason => reason.Reason == NarratorRejection.InvalidStructure);

    private static string? NonPublicationReason(
        SharedState state, EvaluationAgentMetrics analyst, EvaluationAgentMetrics narrator)
    {
        if (state.Status == WorkflowStatus.Completed && !state.Observations.IsEmpty && !state.NarrativeClaims.IsEmpty)
            return null;
        if (state.Status == WorkflowStatus.Cancelled)
            return "cancelled";
        if (state.History.Any(item => item.Kind == WorkflowDecisionKind.Timeout))
            return "timed_out";
        if (state.History.Any(item => item.Kind == WorkflowDecisionKind.AgentFailure))
        {
            var role = state.History.Last(item => item.Kind == WorkflowDecisionKind.AgentFailure).Agent.Role;
            var category = role == AgentRole.Analyst ? analyst.FailureCategory : narrator.FailureCategory;
            return category switch
            {
                "local_endpoint_unavailable" => role == AgentRole.Analyst
                    ? "analyst_local_endpoint_unavailable" : "narrator_local_endpoint_unavailable",
                "provider_rejected_request" => role == AgentRole.Analyst
                    ? "analyst_provider_rejected_request" : "narrator_provider_rejected_request",
                "deterministic_narrator_rejection" => "narrator_output_rejected",
                "malformed_structured_output" => role == AgentRole.Analyst
                    ? "analyst_malformed_structured_output" : "narrator_malformed_structured_output",
                _ => role == AgentRole.Analyst ? "analyst_call_failed" : "narrator_call_failed"
            };
        }
        if (state.Verifications.Any(item => item.Result.Status == ClaimStatus.Rejected))
            return "deterministic_verification_rejected";
        if (state.Observations.IsEmpty)
            return "no_approved_analyst_observations";
        if (state.NarrativeClaims.IsEmpty)
            return "no_approved_narrative_claims";
        return "workflow_did_not_complete";
    }

    private static string? FailureCategory(SharedState state, AgentRole role, bool malformedNarratorOutput = false)
    {
        if (state.Status == WorkflowStatus.Cancelled)
            return "cancelled";
        if (state.History.Any(item => item.Agent.Role == role && item.Kind == WorkflowDecisionKind.Timeout))
            return "timeout";
        var decision = state.History.LastOrDefault(item =>
            item.Agent.Role == role && item.Kind == WorkflowDecisionKind.AgentFailure);
        if (decision is null)
            return null;
        if (decision.Reason is "Local Analyst endpoint is unavailable." or "Local Narrator endpoint is unavailable.")
            return "local_endpoint_unavailable";
        if (decision.Reason.StartsWith("Local Analyst provider rejected", StringComparison.Ordinal)
            || decision.Reason.StartsWith("Local Narrator provider rejected", StringComparison.Ordinal))
            return "provider_rejected_request";
        if (decision.Reason == "Analyst model output violated the structured observation contract.")
            return "malformed_structured_output";
        if (decision.Reason.StartsWith("Narrator rejected after one revision:", StringComparison.Ordinal))
            return malformedNarratorOutput ? "malformed_structured_output" : "deterministic_narrator_rejection";
        if (decision.Reason is "Local Narrator provider request timed out."
            or "Local Analyst provider request timed out.")
            return "provider_timeout";
        return "model_call_failed";
    }

    private static string ValidateLocalEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Evaluation endpoint identity must be an explicit local loopback HTTP(S) URL.");
        return endpoint.GetLeftPart(UriPartial.Authority);
    }

    private static void ValidateModelIdentity(string provider, string model, string version)
    {
        if (!NarratorLimits.ValidIdentity(provider) || !NarratorLimits.ValidIdentity(model)
            || !NarratorLimits.ValidIdentity(version))
            throw new ArgumentException("Evaluation model identity labels must be present and bounded.");
    }

    private sealed class CountingAnalystModel(IAnalystModel inner) : IAnalystModel
    {
        private int calls;
        public AnalystModelIdentity Identity => inner.Identity;
        public int CallCount => Volatile.Read(ref calls);
        public Task<AnalystModelOutput> AnalyzeAsync(AnalystPrompt prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return inner.AnalyzeAsync(prompt, cancellationToken);
        }
    }

    private sealed class CountingNarratorModel(INarratorModel inner) : INarratorModel
    {
        private int calls;
        public NarratorModelIdentity Identity => inner.Identity;
        public int CallCount => Volatile.Read(ref calls);
        public Task<NarratorModelOutput> NarrateAsync(NarratorPrompt prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return inner.NarrateAsync(prompt, cancellationToken);
        }
    }
}
