using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using GameGround.AgentFramework;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Data;

namespace GameGround.AgentFramework.Tests;

public sealed class ModelEvaluationRunnerTests
{
    private static readonly string DatasetPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json");
    private const string LocalEndpoint = "http://127.0.0.1:1234/v1";

    [Fact]
    public async Task Offline_runner_composes_both_models_reports_ground_truth_and_publishes_only_verified_claims()
    {
        var analyst = new ScriptedAnalyst((prompt, _) => Task.FromResult(new AnalystModelOutput(
            prompt.AllowedObservations.Take(3).ToArray())));
        var narrator = new ScriptedNarrator((prompt, _) => Task.FromResult(ToNarrative(prompt)));

        var report = await Run(analyst, narrator,
            LocalEndpoint, "http://localhost:5678/v1");

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("synthetic-miniature-v1", report.DatasetId);
        Assert.Equal("harbor-meadow-miniature", report.MatchId);
        Assert.Equal(ModelEvaluationRunner.ExpectedDatasetSha256, report.DatasetSha256);
        Assert.Equal("scripted", report.AnalystModel.Provider);
        Assert.Equal("analyst-offline-fixture", report.AnalystModel.Model);
        Assert.Equal("http://127.0.0.1:1234", report.AnalystModel.Endpoint);
        Assert.Equal("scripted", report.NarratorModel.Provider);
        Assert.Equal("narrator-offline-fixture", report.NarratorModel.Model);
        Assert.Equal("gameground-analyst-v1", report.AnalystPromptVersion);
        Assert.Equal("gameground-narrator-v1", report.NarratorPromptVersion);
        Assert.Equal("Completed", report.WorkflowStatus);
        Assert.True(report.PublicationOccurred);
        Assert.Null(report.NonPublicationReason);
        Assert.Equal(1, report.GroundTruth.ExpectedHomeScore);
        Assert.Equal(1, report.GroundTruth.ExpectedAwayScore);
        Assert.Equal(1, report.GroundTruth.ActualHomeScore);
        Assert.Equal(1, report.GroundTruth.ActualAwayScore);
        Assert.True(report.GroundTruth.ScoreMatches);
        Assert.True(report.GroundTruth.AnalystNumericFactsVerified);
        Assert.True(report.GroundTruth.NarratorReceivedOnlyApprovedObservations);
        Assert.True(report.GroundTruth.PublishedClaimsHaveValidProvenance);
        Assert.True(report.GroundTruth.PublishedNumericClaimsMatchApprovedObservations);
        Assert.Contains("not established", report.GroundTruth.SemanticCorrectnessAssessment);
        Assert.Equal(1, report.Analyst.Calls);
        Assert.Equal(1, report.Analyst.SuccessfulCalls);
        Assert.Equal(0, report.Analyst.FailedCalls);
        Assert.Equal(3, report.Analyst.ObservationsOrClaimsProposed);
        Assert.Equal(3, report.Analyst.Approved);
        Assert.Equal(1, report.Narrator.Calls);
        Assert.Equal(1, report.Narrator.SuccessfulCalls);
        Assert.Equal(0, report.Narrator.FailedCalls);
        Assert.Equal(3, report.Narrator.ObservationsOrClaimsProposed);
        Assert.Equal(3, report.Narrator.Approved);
        Assert.True(report.TotalExecutionMilliseconds >= 0);
        Assert.True(report.Analyst.TotalLatencyMilliseconds >= 0);
        Assert.True(report.Narrator.TotalLatencyMilliseconds >= 0);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Authorization", serialized);
        Assert.DoesNotContain("API_KEY", serialized);
        Assert.DoesNotContain("SourceEvents", serialized);
        Assert.DoesNotContain("\"Facts\":", serialized);
    }

    [Fact]
    public async Task Deterministic_analyst_verification_rejection_blocks_narrator_and_publication()
    {
        var analyst = new ScriptedAnalyst((prompt, _) =>
        {
            var invalid = prompt.AllowedObservations[0] with
            {
                EvidenceIds = [int.MaxValue]
            };
            return Task.FromResult(new AnalystModelOutput([invalid]));
        });
        var narrator = new ScriptedNarrator((_, _) => throw new InvalidOperationException("Narrator must not run."));

        var report = await Run(analyst, narrator,
            LocalEndpoint, LocalEndpoint);

        Assert.Equal("Rejected", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("deterministic_verification_rejected", report.NonPublicationReason);
        Assert.Equal(2, report.Analyst.Calls);
        Assert.Equal(2, report.Analyst.ObservationsOrClaimsProposed);
        Assert.Equal(1, report.Analyst.Revised);
        Assert.Equal(2, report.Analyst.Rejected);
        Assert.Equal(2, report.Analyst.InvalidReferences);
        Assert.Equal(0, report.Narrator.Calls);
        Assert.True(report.GroundTruth.NarratorReceivedOnlyApprovedObservations);
        Assert.Null(report.GroundTruth.PublishedClaimsHaveValidProvenance);
    }

    [Fact]
    public async Task Narrator_reference_rejection_is_reported_after_bounded_revision_without_publication()
    {
        var analyst = SuccessfulAnalyst();
        var narrator = new ScriptedNarrator((prompt, _) => Task.FromResult(
            new NarratorModelOutput([ToNarrative(prompt).Claims[0] with
            {
                ObservationIds = [new ObservationReference("unknown-agent", 777)]
            }])));

        var report = await Run(analyst, narrator,
            LocalEndpoint, LocalEndpoint);

        Assert.Equal("Failed", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("narrator_output_rejected", report.NonPublicationReason);
        Assert.Equal(2, report.Narrator.Calls);
        Assert.Equal(2, report.Narrator.SuccessfulCalls);
        Assert.Equal(0, report.Narrator.FailedCalls);
        Assert.Equal(1, report.Narrator.Revised);
        Assert.Equal(2, report.Narrator.Rejected);
        Assert.Equal(2, report.Narrator.InvalidReferences);
        Assert.Equal(0, report.Narrator.Approved);
        Assert.True(report.GroundTruth.NarratorReceivedOnlyApprovedObservations);
        Assert.Null(report.GroundTruth.PublishedClaimsHaveValidProvenance);
    }

    [Fact]
    public async Task Malformed_analyst_output_is_counted_as_failed_and_never_published()
    {
        var analyst = new ScriptedAnalyst((_, _) =>
            throw new AnalystModelOutputException("Do not persist this provider content."));
        var narrator = new ScriptedNarrator((_, _) => throw new InvalidOperationException("Must not run."));

        var report = await Run(analyst, narrator,
            LocalEndpoint, LocalEndpoint);

        Assert.Equal("Failed", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("analyst_malformed_structured_output", report.NonPublicationReason);
        Assert.Equal(1, report.Analyst.Calls);
        Assert.Equal(1, report.Analyst.FailedCalls);
        Assert.Equal(1, report.Analyst.MalformedOrIncompleteResponses);
        Assert.Equal(0, report.Narrator.Calls);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Do not persist", json);
    }

    [Fact]
    public async Task Empty_analyst_selection_skips_narrator_and_records_nonpublication()
    {
        var analyst = new ScriptedAnalyst((_, _) =>
            Task.FromResult(new AnalystModelOutput([])));
        var narrator = new ScriptedNarrator((_, _) =>
            throw new InvalidOperationException("Narrator must not run."));

        var report = await Run(analyst, narrator, LocalEndpoint, LocalEndpoint);

        Assert.Equal("Completed", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("no_approved_analyst_observations", report.NonPublicationReason);
        Assert.Equal(1, report.Analyst.Calls);
        Assert.Equal(0, report.Analyst.Approved);
        Assert.Equal(0, report.Narrator.Calls);
        Assert.True(report.GroundTruth.NarratorReceivedOnlyApprovedObservations);
        Assert.Null(report.GroundTruth.PublishedNumericClaimsMatchApprovedObservations);
    }

    [Fact]
    public async Task Provider_failure_is_counted_without_persisting_exception_details()
    {
        var analyst = new ScriptedAnalyst((_, _) =>
            throw new AgentExecutionException("Local Analyst endpoint is unavailable."));
        var narrator = new ScriptedNarrator((_, _) => throw new InvalidOperationException("Must not run."));

        var report = await Run(analyst, narrator,
            LocalEndpoint, LocalEndpoint);

        Assert.Equal(1, report.Analyst.FailedCalls);
        Assert.Equal(0, report.Analyst.MalformedOrIncompleteResponses);
        Assert.Equal("analyst_local_endpoint_unavailable", report.NonPublicationReason);
        Assert.Equal("local_endpoint_unavailable", report.Analyst.FailureCategory);
    }

    [Fact]
    public async Task Unavailable_narrator_endpoint_is_reported_as_a_prerequisite_failure()
    {
        var analyst = SuccessfulAnalyst();
        var narrator = new ScriptedNarrator((_, _) =>
            throw new AgentExecutionException("Local Narrator endpoint is unavailable."));
        var report = await Run(analyst, narrator, LocalEndpoint, LocalEndpoint);

        Assert.Equal("Failed", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("narrator_local_endpoint_unavailable", report.NonPublicationReason);
        Assert.Equal("local_endpoint_unavailable", report.Narrator.FailureCategory);
        Assert.Equal(1, report.Narrator.FailedCalls);
        Assert.DoesNotContain("Local Narrator endpoint is unavailable.", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Malformed_narrator_response_is_counted_and_never_published()
    {
        var analyst = SuccessfulAnalyst();
        var narrator = new ScriptedNarrator((_, _) =>
            throw new NarratorOutputException(NarratorRejection.InvalidStructure));

        var report = await Run(analyst, narrator, LocalEndpoint, LocalEndpoint);

        Assert.Equal("Failed", report.WorkflowStatus);
        Assert.False(report.PublicationOccurred);
        Assert.Equal("narrator_malformed_structured_output", report.NonPublicationReason);
        Assert.Equal(2, report.Narrator.Calls);
        Assert.Equal(0, report.Narrator.SuccessfulCalls);
        Assert.Equal(2, report.Narrator.FailedCalls);
        Assert.Equal(2, report.Narrator.MalformedOrIncompleteResponses);
        Assert.Equal(0, report.Narrator.Revised);
        Assert.Equal(2, report.Narrator.Rejected);
        Assert.Empty(narrator.Prompts.Where(prompt => prompt.Attempt == 0)
            .SelectMany(prompt => prompt.Feedback));
        Assert.All(narrator.Prompts.Where(prompt => prompt.Attempt == 1),
            prompt => Assert.Contains(prompt.Feedback, item => item.Reason == NarratorRejection.InvalidStructure));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_timeout_never_appear_as_successful_publication(bool cancel)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyst = new ScriptedAnalyst(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var narrator = new ScriptedNarrator((_, _) => throw new InvalidOperationException("Must not run."));
        using var cancellation = new CancellationTokenSource();
        var pending = ModelEvaluationRunner.RunAsync(Dataset(), DatasetHash(), analyst, narrator,
            LocalEndpoint, LocalEndpoint, cancellation.Token,
            cancel ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(100));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel)
            cancellation.Cancel();
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(report.PublicationOccurred);
        Assert.Equal(cancel ? "Cancelled" : "Failed", report.WorkflowStatus);
        Assert.Equal(cancel ? "cancelled" : "timed_out", report.NonPublicationReason);
        Assert.Equal(1, report.Analyst.Calls);
        Assert.Equal(1, report.Analyst.FailedCalls);
        Assert.Equal(0, report.Narrator.Calls);
    }

    [Fact]
    public async Task Changed_fixture_hash_fails_before_any_model_call()
    {
        var analyst = SuccessfulAnalyst();
        var narrator = new ScriptedNarrator((prompt, _) => Task.FromResult(ToNarrative(prompt)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModelEvaluationRunner.RunAsync(Dataset(), new string('0', 64),
                analyst, narrator, LocalEndpoint, LocalEndpoint));

        Assert.Empty(analyst.Prompts);
        Assert.Empty(narrator.Prompts);
    }

    [Fact]
    public void Local_evaluation_configuration_requires_all_four_nonsecret_identity_settings_before_creation()
    {
        var reads = new List<string>();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalModelEvaluationConfiguration.Create(name => { reads.Add(name); return null; }));
        Assert.Contains("GAMEGROUND_ANALYST_ENDPOINT", exception.Message);
        Assert.Equal(["GAMEGROUND_ANALYST_ENDPOINT"], reads);
    }

    [Fact]
    public void Local_evaluation_configuration_rejects_remote_hosts_before_creating_any_provider()
    {
        var values = new Dictionary<string, string?>
        {
            ["GAMEGROUND_ANALYST_ENDPOINT"] = "https://api.example.com/v1",
            ["GAMEGROUND_ANALYST_MODEL"] = "fixture",
            ["GAMEGROUND_NARRATOR_ENDPOINT"] = "http://127.0.0.1:1234/v1",
            ["GAMEGROUND_NARRATOR_MODEL"] = "fixture"
        };
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalModelEvaluationConfiguration.Create(name => values.GetValueOrDefault(name)));
        Assert.Contains("loopback", exception.Message);
        Assert.DoesNotContain("api.example.com", exception.Message);
    }

    [Fact]
    public void Local_evaluation_configuration_validates_both_endpoints_and_only_reports_loopback_identity()
    {
        var values = new Dictionary<string, string?>
        {
            ["GAMEGROUND_ANALYST_ENDPOINT"] = "http://127.0.0.1:1234/v1",
            ["GAMEGROUND_ANALYST_MODEL"] = "offline-analyst",
            ["GAMEGROUND_ANALYST_API_KEY"] = "not-for-reporting",
            ["GAMEGROUND_NARRATOR_ENDPOINT"] = "http://localhost:5678/v1",
            ["GAMEGROUND_NARRATOR_MODEL"] = "offline-narrator",
            ["GAMEGROUND_NARRATOR_API_KEY"] = "also-not-for-reporting"
        };
        using var configuration = LocalModelEvaluationConfiguration.Create(name => values.GetValueOrDefault(name));

        Assert.Equal("http://127.0.0.1:1234", configuration.AnalystEndpointIdentity);
        Assert.Equal("http://localhost:5678", configuration.NarratorEndpointIdentity);
        Assert.DoesNotContain("not-for-reporting", configuration.AnalystEndpointIdentity);
        Assert.DoesNotContain("also-not-for-reporting", configuration.NarratorEndpointIdentity);
    }

    private static ScriptedAnalyst SuccessfulAnalyst() =>
        new((prompt, _) => Task.FromResult(new AnalystModelOutput(prompt.AllowedObservations.Take(2).ToArray())));

    private static Task<ModelEvaluationReport> Run(
        IAnalystModel analyst, INarratorModel narrator, string analystEndpoint, string narratorEndpoint) =>
        ModelEvaluationRunner.RunAsync(Dataset(), DatasetHash(), analyst, narrator,
            analystEndpoint, narratorEndpoint);

    private static MatchDataset Dataset() => new DatasetLoader().Load(DatasetPath);
    private static string DatasetHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DatasetPath)));

    private static NarratorModelOutput ToNarrative(NarratorPrompt prompt) =>
        new(prompt.Observations.Select(item => new NarrativeClaimDraft(item.NarrativeClaimId, item.Text,
            [item.Id], item.Category, prompt.Scope, item.Metric)).ToArray());

    private sealed class ScriptedAnalyst(
        Func<AnalystPrompt, CancellationToken, Task<AnalystModelOutput>> analyze) : IAnalystModel
    {
        public AnalystModelIdentity Identity { get; } = new("scripted", "analyst-offline-fixture", "v1");
        public ConcurrentQueue<AnalystPrompt> Prompts { get; } = [];

        public Task<AnalystModelOutput> AnalyzeAsync(AnalystPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return analyze(prompt, cancellationToken);
        }
    }

    private sealed class ScriptedNarrator(
        Func<NarratorPrompt, CancellationToken, Task<NarratorModelOutput>> narrate) : INarratorModel
    {
        public NarratorModelIdentity Identity { get; } = new("scripted", "narrator-offline-fixture", "v1");
        public ConcurrentQueue<NarratorPrompt> Prompts { get; } = [];

        public Task<NarratorModelOutput> NarrateAsync(NarratorPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return narrate(prompt, cancellationToken);
        }
    }
}
