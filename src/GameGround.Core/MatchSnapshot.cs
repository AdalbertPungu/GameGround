namespace GameGround.Core;

public enum BallControlKind
{
    DeadBall,
    Contested,
    Team
}

public sealed record BallControl(BallControlKind Kind, TeamId? Team)
{
    public static BallControl DeadBall { get; } = new(BallControlKind.DeadBall, null);
    public static BallControl Contested { get; } = new(BallControlKind.Contested, null);
    public static BallControl By(TeamId team) => new(BallControlKind.Team, team);
}

public enum PeriodStatus
{
    NotStarted,
    Active,
    Ended
}

public sealed record MatchSnapshot(
    string MatchId,
    int LastSequence,
    int Period,
    TimeSpan Clock,
    PeriodStatus PeriodStatus,
    int HomeScore,
    int AwayScore,
    BallControl Control,
    bool IsComplete);
