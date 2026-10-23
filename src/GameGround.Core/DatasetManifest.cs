using System.Collections.Immutable;

namespace GameGround.Core;

public sealed record DatasetManifest(string DatasetId, int SchemaVersion, bool IsSynthetic)
{
    public const int SupportedSchemaVersion = 1;
}

public sealed record MatchDataset(
    DatasetManifest Manifest,
    MatchDefinition Match,
    ImmutableArray<MatchEvent> Events);
