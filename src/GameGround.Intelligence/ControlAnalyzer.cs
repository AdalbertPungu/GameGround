using System.Collections.Immutable;
using GameGround.Core;

namespace GameGround.Intelligence;

internal static class ControlAnalyzer
{
    internal static ImmutableArray<ControlSequence> Analyze(
        MatchDataset dataset, ImmutableArray<MatchSnapshot> timeline, AnalysisScope scope)
    {
        var result = ImmutableArray.CreateBuilder<ControlSequence>();
        int? start = null;
        var control = BallControl.DeadBall;
        for (var index = 0; index < dataset.Events.Length; index++)
        {
            var item = dataset.Events[index];
            var after = timeline[index + 1];
            if (item is PeriodStarted)
            {
                start = index;
                control = after.Control;
            }
            else if (item is PeriodEnded)
            {
                Close(index, isClosed: true);
                start = null;
            }
            else if (control != after.Control)
            {
                Close(index, isClosed: true);
                start = index;
                control = after.Control;
            }
        }

        if (start is not null)
        {
            Close(dataset.Events.Length - 1, isClosed: false);
        }

        return result.ToImmutable();

        void Close(int end, bool isClosed)
        {
            if (start is not { } first)
            {
                throw new InvalidOperationException("A validated active period must have a control sequence.");
            }

            var firstEvent = dataset.Events[first];
            var lastEvent = dataset.Events[end];
            result.Add(new ControlSequence(
                new ControlSequenceId(result.Count + 1),
                control,
                Position(firstEvent),
                Position(lastEvent),
                isClosed,
                new Evidence(scope, dataset.Events[first..(end + 1)].Select(item => item.Id).ToImmutableArray())));
        }
    }

    internal static EventPosition Position(MatchEvent item) => new(item.Sequence, item.Period, item.Clock);

    internal static TimeSpan Duration(IEnumerable<ControlSequence> sequences) =>
        TimeSpan.FromTicks(sequences.Sum(sequence => sequence.ObservedDuration.Ticks));
}
