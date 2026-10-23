using System.Collections.Immutable;

namespace GameGround.Core;

public static class MatchReducer
{
    public static MatchSnapshot Replay(MatchDataset dataset) => DatasetEvaluator.Evaluate(dataset);

    public static ImmutableArray<MatchSnapshot> ReplayTimeline(MatchDataset dataset)
    {
        var snapshots = ImmutableArray.CreateBuilder<MatchSnapshot>();
        DatasetEvaluator.Evaluate(dataset, snapshots.Add);
        return snapshots.ToImmutable();
    }
}
