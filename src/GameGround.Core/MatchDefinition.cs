using System.Collections.Immutable;

namespace GameGround.Core;

public readonly record struct TeamId(int Value);
public readonly record struct PlayerId(int Value);
public readonly record struct EventId(int Value);

public sealed record Player(PlayerId Id, string Name);

public sealed record Team(TeamId Id, string Name, ImmutableArray<Player> Players);

public sealed record MatchPeriod(int Number, TimeSpan Duration);

public sealed record MatchDefinition(
    string MatchId,
    Team HomeTeam,
    Team AwayTeam,
    ImmutableArray<MatchPeriod> Periods);
