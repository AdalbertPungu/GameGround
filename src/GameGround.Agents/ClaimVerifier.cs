using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GameGround.Core;
using GameGround.Intelligence;

namespace GameGround.Agents;

public enum VerificationCheckKind
{
    WellFormedClaim,
    HasEvidence,
    EvidenceExists,
    SameMatch,
    SameWindow,
    ReferencedMetricMatches,
    NumericValuesConsistent,
    WordingWithinKnownData,
    NoUnsupportedTacticalInference,
    NoFutureEventLeakage,
    SourceCoverageMatches
}

public sealed record VerificationCheck(VerificationCheckKind Kind, bool Passed, string Detail);

public sealed record VerificationResult(
    ClaimId ClaimId,
    ClaimStatus Status,
    ImmutableArray<VerificationCheck> Checks,
    ImmutableArray<FactId> SupportedFacts,
    ImmutableArray<FactId> UnsupportedFacts,
    ImmutableArray<EventId> SourceEvents,
    string? RejectionReason);

public sealed partial class ClaimVerifier
{
    public VerificationResult Verify(AgentContext context, Claim claim)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claim);
        var checks = ImmutableArray.CreateBuilder<VerificationCheck>();
        var shaped = claim.Id.Value > 0 && claim.Producer is not null
            && !string.IsNullOrWhiteSpace(claim.Producer.Id) && Enum.IsDefined(claim.Producer.Role)
            && !string.IsNullOrWhiteSpace(claim.Text) && claim.Context is not null
            && claim.Context.Scope is not null && claim.Context.Window is not null
            && !string.IsNullOrWhiteSpace(claim.Context.ContentId)
            && !claim.FactIds.IsDefault && !claim.Evidence.IsDefault
            && !claim.CoveredSourceEvents.IsDefault && claim.Status == ClaimStatus.Proposed
            && claim.Evidence.All(reference => reference is not null && reference.Context is not null
                && reference.Context.Scope is not null && reference.Context.Window is not null);
        Add(VerificationCheckKind.WellFormedClaim, shaped, "Initialized, proposed, identified claim required.");
        if (!shaped || claim.Context is null)
        {
            return Result([], [], []);
        }

        var facts = claim.FactIds.Select(id => context.Facts.SingleOrDefault(fact => fact.Id == id)).ToArray();
        var known = facts.Where(fact => fact is not null).Select(fact => fact!).ToArray();
        var supported = known.Select(fact => fact.Id).Distinct().ToImmutableArray();
        var unsupported = claim.FactIds.Where(id => !supported.Contains(id)).Distinct().ToImmutableArray();
        var entries = claim.Evidence.Select(reference =>
            context.Evidence.SingleOrDefault(entry => entry.Reference == reference)).ToArray();
        var complete = unsupported.IsEmpty && claim.FactIds.Length > 0
            && claim.FactIds.Distinct().Count() == claim.FactIds.Length;
        Add(VerificationCheckKind.HasEvidence, claim.Evidence.Length > 0 && claim.FactIds.Length > 0,
            "Positive evidence reference required; a zero-count fact may have no source events.");
        var expectedRefs = known.Select(fact =>
            context.Evidence.Single(entry => entry.FactId == fact.Id).Reference).ToArray();
        Add(VerificationCheckKind.EvidenceExists, complete
            && entries.All(entry => entry is not null)
            && claim.Evidence.SequenceEqual(expectedRefs),
            "References must exactly match the canonical facts and their evidence.");
        Add(VerificationCheckKind.SameMatch,
            SameDataset(claim.Context.Scope, context.Identity.Scope)
            && claim.Evidence.All(reference => SameDataset(reference.Context.Scope, context.Identity.Scope)),
            "Dataset, manifest, match and intelligence rules must agree.");
        Add(VerificationCheckKind.SameWindow, claim.Context == context.Identity
            && claim.Evidence.All(reference => reference.Context == context.Identity),
            "Window, cutoff and source-content identity must agree.");

        string? canonicalText = null;
        var semanticMatch = false;
        var numericMatch = false;
        if (claim.Type == ClaimType.Metric && claim.Moment is null
            && claim.Metric is { } metric && known.Length == 1 && complete)
        {
            var fact = known[0];
            semanticMatch = ClaimText.IsMetric(fact.Kind) && metric.Kind == fact.Kind
                && metric.Team == fact.Team && metric.Unit == fact.Unit;
            numericMatch = fact.Value.HasValue && metric.Value == fact.Value
                && metric.Numerator == fact.Numerator && metric.Denominator == fact.Denominator;
            if (semanticMatch && fact.Value is { } value)
            {
                canonicalText = ClaimText.RenderMetric(context,
                    new MetricAssertion(fact.Kind, fact.Team, value, fact.Unit, fact.Numerator, fact.Denominator));
            }
        }
        else if (claim.Type == ClaimType.RecordedMoment && claim.Metric is null && claim.Moment is { } moment)
        {
            var candidate = context.Candidates.SingleOrDefault(item => item.MomentId == moment);
            semanticMatch = candidate is not null && complete && claim.FactIds.SequenceEqual(candidate.FactIds);
            if (semanticMatch && candidate is not null)
            {
                canonicalText = candidate.Statement;
                numericMatch = true;
            }
        }

        var canonicalSources = known.SelectMany(fact => fact.Evidence.SourceEventIds)
            .Distinct().ToImmutableArray();
        Add(VerificationCheckKind.ReferencedMetricMatches, semanticMatch,
            "Claim kind, metric, unit, team and exact fact set must match.");
        Add(VerificationCheckKind.NumericValuesConsistent, numericMatch && canonicalText is not null
            && Numbers(claim.Text).SequenceEqual(Numbers(canonicalText)),
            "Structured values, ratio and printed numbers must match the engine.");
        var wording = canonicalText is not null && claim.Text == canonicalText;
        Add(VerificationCheckKind.WordingWithinKnownData, wording,
            "Only canonical, explicitly scoped metric or engine moment wording is verifiable.");
        Add(VerificationCheckKind.NoUnsupportedTacticalInference, wording,
            "Closed wording excludes tactical inference; this is not a free-language entailment test.");
        Add(VerificationCheckKind.NoFutureEventLeakage, claim.Context == context.Identity
            && claim.Evidence.All(reference => reference.Context == context.Identity)
            && claim.CoveredSourceEvents.All(canonicalSources.Contains),
            "No foreign cutoff, future reference or unproven source ID is permitted.");
        Add(VerificationCheckKind.SourceCoverageMatches,
            claim.CoveredSourceEvents.SequenceEqual(canonicalSources),
            "Declared indirect source coverage must equal canonical evidence coverage.");
        return Result(supported, unsupported, canonicalSources);

        void Add(VerificationCheckKind kind, bool passed, string detail) =>
            checks.Add(new VerificationCheck(kind, passed, detail));

        VerificationResult Result(
            ImmutableArray<FactId> supportedFacts,
            ImmutableArray<FactId> unsupportedFacts,
            ImmutableArray<EventId> sources)
        {
            var failed = checks.Where(check => !check.Passed).Select(check => check.Kind.ToString()).ToArray();
            return new VerificationResult(claim.Id,
                failed.Length == 0 ? ClaimStatus.Verified : ClaimStatus.Rejected,
                checks.ToImmutable(), supportedFacts, unsupportedFacts, sources,
                failed.Length == 0 ? null : string.Join(", ", failed));
        }
    }

    private static bool SameDataset(AnalysisScope? left, AnalysisScope? right) =>
        left is not null && right is not null && left.MatchId == right.MatchId
        && left.Manifest == right.Manifest && left.RulesVersion == right.RulesVersion;

    private static IEnumerable<string> Numbers(string text) =>
        NumberPattern().Matches(text).Select(match => match.Value);

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
