using System.Text.Json;
using GameGround.AgentFramework;
using GameGround.Core;
using GameGround.Data;

namespace GameGround.Cli;

public static class ModelEvaluationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: GameGround.Cli evaluate-models <synthetic-dataset.json> <report.json>");
            return 2;
        }

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            EnsureDifferentFiles(args[0], args[1]);
            using var models = LocalModelEvaluationConfiguration.FromEnvironment();
            var dataset = new DatasetLoader().Load(args[0]);
            var bytes = await File.ReadAllBytesAsync(args[0], cancellationToken);
            var datasetHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var report = await ModelEvaluationRunner.RunAsync(dataset, datasetHash, models.Analyst, models.Narrator,
                models.AnalystEndpointIdentity, models.NarratorEndpointIdentity, cancellationToken);
            stopwatch.Stop();
            report = report with
            {
                StartedAtUtc = startedAt,
                TotalExecutionMilliseconds = stopwatch.Elapsed.TotalMilliseconds
            };
            var json = JsonSerializer.Serialize(report, JsonOptions);
            var reportPath = Path.GetFullPath(args[1]);
            var directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(reportPath, json, CancellationToken.None);
            output.WriteLine(json);
            if (!report.PublicationOccurred)
            {
                error.WriteLine($"Evaluation did not publish: {report.NonPublicationReason ?? "workflow_not_completed"}.");
                return 1;
            }
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine($"Evaluation prerequisite failed: {exception.Message}");
            return 1;
        }
        catch (DatasetJsonException exception)
        {
            error.WriteLine($"Synthetic dataset is invalid [{exception.Code}]: {exception.Message}");
            return 1;
        }
        catch (DomainValidationException exception)
        {
            error.WriteLine($"Synthetic dataset failed domain validation [{exception.Code}].");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Evaluation cancelled before a report could be produced.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"Evaluation input/output failed [{exception.GetType().Name}]; details omitted.");
            return 1;
        }
    }

    private static void EnsureDifferentFiles(string datasetPath, string reportPath)
    {
        var dataset = Path.GetFullPath(datasetPath);
        var report = Path.GetFullPath(reportPath);
        if (string.Equals(dataset, report, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Report path must not overwrite the synthetic dataset.");
    }
}
