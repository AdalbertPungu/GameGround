using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameGround.Agents;

namespace GameGround.AgentFramework;

public sealed record NarratorModelIdentity(string Provider, string Model, string Version);

public sealed record NarrativeScope(
    [property: JsonRequired] string MatchId,
    [property: JsonRequired] string ContextId,
    [property: JsonRequired] int FromSequence,
    [property: JsonRequired] int ThroughSequence);

public sealed record ObservationReference(
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] int ClaimId);

public sealed record ApprovedNarrativeObservation(
    int NarrativeClaimId, ObservationReference Id, string Text, ClaimType Category, MetricAssertion? Metric);

public sealed record NarratorPrompt(
    string Version,
    NarrativeScope Scope,
    ImmutableArray<ApprovedNarrativeObservation> Observations,
    ImmutableArray<NarratorFeedback> Feedback,
    int Attempt);

public sealed record NarrativeClaimDraft(
    [property: JsonRequired] int Id,
    [property: JsonRequired] string Text,
    [property: JsonRequired] ObservationReference[] ObservationIds,
    [property: JsonRequired] ClaimType Category,
    [property: JsonRequired] NarrativeScope Scope,
    [property: JsonRequired] MetricAssertion? Metric);

public sealed record NarratorModelOutput(
    [property: JsonRequired] NarrativeClaimDraft[] Claims);

public interface INarratorModel
{
    NarratorModelIdentity Identity { get; }
    Task<NarratorModelOutput> NarrateAsync(NarratorPrompt prompt, CancellationToken cancellationToken);
}

public enum NarratorRejection
{
    InvalidStructure,
    SizeLimit,
    EmptyNarrative,
    InvalidClaimId,
    InvalidObservationReference,
    ScopeMismatch,
    CategoryMismatch,
    NumericMismatch,
    UnsupportedWording
}

public sealed record NarratorFeedback(NarratorRejection Reason, int? ClaimId);

public sealed class NarratorOutputException(NarratorRejection reason)
    : Exception($"Narrator structured output rejected: {reason}.")
{
    public NarratorRejection Reason { get; } = reason;
}

public sealed record NarratorEvaluation(
    NarratorModelIdentity Model,
    string PromptVersion,
    NarrativeScope Scope,
    int Attempt,
    ImmutableArray<ObservationReference> InputObservationIds,
    ImmutableArray<int> OutputClaimIds,
    ImmutableArray<NarratorFeedback> Rejections,
    string Outcome,
    TimeSpan Latency);

public static class NarratorLimits
{
    public const int MaxObservations = 128;
    public const int MaxClaims = 128;
    public const int MaxTextCharacters = 1024;
    public const int MaxIdentityCharacters = 256;
    public const int MaxPromptBytes = 131072;
    public const int MaxResponseBytes = 65536;
    public const int MaxTransportBytes = 262144;
    public const int MaxOutputTokens = 8192;

    internal static JsonSerializerOptions Serialization { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Converters =
        {
            new JsonStringEnumConverter<ClaimType>(allowIntegerValues: false)
        }
    };

    internal static bool ValidIdentity([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentityCharacters;
}
