using System.Globalization;
using GameGround.Core;
using GameGround.Data;

namespace GameGround.Cli;

public static class DatasetCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || args[0] is not ("validate" or "replay")
            || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: GameGround.Cli <validate|replay> <dataset.json>");
            return 2;
        }

        try
        {
            var dataset = new DatasetLoader().Load(args[1]);
            var snapshot = MatchReducer.Replay(dataset);
            output.WriteLine($"Dataset: {dataset.Manifest.DatasetId}");
            output.WriteLine("Validation: valid (synthetic)");
            output.WriteLine(FormattableString.Invariant($"Events: {dataset.Events.Length}"));
            output.WriteLine(FormattableString.Invariant(
                $"Score: {dataset.Match.HomeTeam.Name} {snapshot.HomeScore}-{snapshot.AwayScore} {dataset.Match.AwayTeam.Name}"));
            if (args[0] == "replay")
            {
                output.WriteLine(FormattableString.Invariant(
                    $"Snapshot: sequence={snapshot.LastSequence}; period={snapshot.Period}; clock={snapshot.Clock.ToString("c", CultureInfo.InvariantCulture)}; status={snapshot.PeriodStatus}; complete={snapshot.IsComplete}"));
            }

            return 0;
        }
        catch (DatasetJsonException exception)
        {
            error.WriteLine($"Validation: invalid [{exception.Code}] {exception.Message}");
            return 1;
        }
        catch (DomainValidationException exception)
        {
            var eventContext = exception.EventId is { } id
                ? FormattableString.Invariant($" event={id.Value}")
                : string.Empty;
            error.WriteLine($"Validation: invalid [{exception.Code}]{eventContext} {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            error.WriteLine($"File error [FileReadError]: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"File error [AccessDenied]: {exception.Message}");
            return 1;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"File error [InvalidPath]: {exception.Message}");
            return 1;
        }
        catch (NotSupportedException exception)
        {
            error.WriteLine($"File error [InvalidPath]: {exception.Message}");
            return 1;
        }
    }
}
