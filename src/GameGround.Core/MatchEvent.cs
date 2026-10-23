namespace GameGround.Core;

public enum PassOutcome
{
    Completed,
    Incomplete
}

public enum ShotOutcome
{
    Goal,
    Saved,
    OffTarget,
    Blocked
}

public abstract record MatchEvent(EventId Id, int Sequence, int Period, TimeSpan Clock);

public sealed record PeriodStarted(EventId Id, int Sequence, int Period, TimeSpan Clock)
    : MatchEvent(Id, Sequence, Period, Clock);

public sealed record PeriodEnded(EventId Id, int Sequence, int Period, TimeSpan Clock)
    : MatchEvent(Id, Sequence, Period, Clock);

public abstract record PlayerEvent(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player)
    : MatchEvent(Id, Sequence, Period, Clock);

public sealed record Pass(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player,
    PlayerId Recipient, PassOutcome Outcome)
    : PlayerEvent(Id, Sequence, Period, Clock, Team, Player);

public sealed record Shot(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player,
    ShotOutcome Outcome)
    : PlayerEvent(Id, Sequence, Period, Clock, Team, Player);

public sealed record Tackle(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player)
    : PlayerEvent(Id, Sequence, Period, Clock, Team, Player);

public sealed record Foul(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player)
    : PlayerEvent(Id, Sequence, Period, Clock, Team, Player);

public sealed record Pressure(
    EventId Id, int Sequence, int Period, TimeSpan Clock, TeamId Team, PlayerId Player)
    : PlayerEvent(Id, Sequence, Period, Clock, Team, Player);

public sealed record ControlChanged(
    EventId Id, int Sequence, int Period, TimeSpan Clock, BallControl From, BallControl To)
    : MatchEvent(Id, Sequence, Period, Clock);
