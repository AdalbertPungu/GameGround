using System.Collections.Immutable;
using GameGround.Core;
using GameGround.Data;
using GameGround.Tests.Fixtures;

namespace GameGround.Tests.Intelligence;

internal static class IntelligenceTestData
{
    internal static MatchDataset Reference() => new DatasetLoader().Load(
        Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json"));

    internal static MatchDataset Prefix(MatchDataset dataset, int count) =>
        dataset with { Events = dataset.Events.Take(count).ToImmutableArray() };

    internal static MatchDataset WithEvents(params MatchEvent[] events) =>
        SyntheticMatch.Create() with { Events = events.ToImmutableArray() };
}
