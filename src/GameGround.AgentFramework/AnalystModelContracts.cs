using System.Collections.Immutable;
using System.Text.Json.Serialization;
using GameGround.Agents;
using GameGround.Intelligence;

namespace GameGround.AgentFramework;

public sealed record AnalystModelIdentity(string Provider, string Model, string Version);

public sealed record AnalystObservation(
    [property: JsonRequired] int Id,
    [property: JsonRequired] string Type,
    [property: JsonRequired] string Statement,
    [property: JsonRequired] string MatchId,
    [property: JsonRequired] string ContextId,
    [property: JsonRequired] int[] FactIds,
    [property: JsonRequired] int[] EvidenceIds,
    [property: JsonRequired] int[] SourceEventIds,
    [property: JsonRequired] int? MomentId);

public sealed record AnalystModelOutput(
    [property: JsonRequired] AnalystObservation[] Observations);

public sealed record AnalystPrompt(
    string Version,
    ContextIdentity Context,
    ImmutableArray<CalculatedFact> Facts,
    ImmutableArray<AvailableEvidence> Evidence,
    ImmutableArray<ControlSequence> Sequences,
    ImmutableArray<MatchMoment> Moments,
    ImmutableArray<AnalystObservation> AllowedObservations,
    ImmutableArray<string> RevisionReasons,
    int Attempt);

public interface IAnalystModel
{
    AnalystModelIdentity Identity { get; }
    Task<AnalystModelOutput> AnalyzeAsync(AnalystPrompt prompt, CancellationToken cancellationToken);
}

public sealed class AnalystModelOutputException(string message) : Exception(message);

public sealed record AnalystObservationEvaluation(
    int ObservationId, ClaimStatus Status, string? RejectionReason);

public sealed record AnalystEvaluation(
    AnalystModelIdentity Model,
    string PromptVersion,
    ContextIdentity Context,
    int Attempt,
    ImmutableArray<int> InputEvidenceIds,
    ImmutableArray<int> OutputObservationIds,
    ImmutableArray<AnalystObservationEvaluation> Verifications,
    string Outcome,
    string? FailureReason,
    TimeSpan Latency);
