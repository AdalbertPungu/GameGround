using GameGround.Core;

namespace GameGround.Tests.Fixtures;

internal static class SyntheticMatch
{
    public static TeamId Home { get; } = new(1);
    public static TeamId Away { get; } = new(2);
    public static PlayerId HomeOne { get; } = new(11);
    public static PlayerId HomeTwo { get; } = new(12);
    public static PlayerId AwayOne { get; } = new(21);
    public static PlayerId AwayTwo { get; } = new(22);
    public static TimeSpan At(int seconds) => TimeSpan.FromSeconds(seconds);

    public static MatchDataset Create() => new(
        new DatasetManifest("synthetic-miniature-v1", DatasetManifest.SupportedSchemaVersion, true),
        new MatchDefinition(
            "harbor-meadow-miniature",
            new Team(Home, "Harbor FC",
            [
                new Player(HomeOne, "Alex Harbor"),
                new Player(HomeTwo, "Robin Harbor")
            ]),
            new Team(Away, "Meadow United",
            [
                new Player(AwayOne, "Sam Meadow"),
                new Player(AwayTwo, "Charlie Meadow")
            ]),
            [new MatchPeriod(1, At(60)), new MatchPeriod(2, At(60))]),
        [
            new PeriodStarted(new(1), 1, 1, At(0)),
            new ControlChanged(new(2), 2, 1, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Pass(new(3), 3, 1, At(5), Home, HomeOne, HomeTwo, PassOutcome.Completed),
            new Pressure(new(4), 4, 1, At(6), Away, AwayOne),
            new Tackle(new(5), 5, 1, At(7), Away, AwayTwo),
            new ControlChanged(new(6), 6, 1, At(7), BallControl.By(Home), BallControl.Contested),
            new ControlChanged(new(7), 7, 1, At(8), BallControl.Contested, BallControl.By(Away)),
            new Pass(new(8), 8, 1, At(10), Away, AwayTwo, AwayOne, PassOutcome.Completed),
            new Shot(new(9), 9, 1, At(12), Away, AwayOne, ShotOutcome.Goal),
            new ControlChanged(new(10), 10, 1, At(13), BallControl.DeadBall, BallControl.By(Home)),
            new Foul(new(11), 11, 1, At(14), Away, AwayTwo),
            new ControlChanged(new(12), 12, 1, At(15), BallControl.DeadBall, BallControl.By(Home)),
            new Pass(new(13), 13, 1, At(16), Home, HomeOne, HomeTwo, PassOutcome.Incomplete),
            new ControlChanged(new(14), 14, 1, At(17), BallControl.By(Home), BallControl.By(Away)),
            new Shot(new(15), 15, 1, At(18), Away, AwayOne, ShotOutcome.Saved),
            new PeriodEnded(new(16), 16, 1, At(60)),
            new PeriodStarted(new(17), 17, 2, At(0)),
            new ControlChanged(new(18), 18, 2, At(0), BallControl.DeadBall, BallControl.By(Home)),
            new Shot(new(19), 19, 2, At(5), Home, HomeTwo, ShotOutcome.Goal),
            new PeriodEnded(new(20), 20, 2, At(60))
        ]);
}
