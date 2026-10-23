namespace GameGround.Core;

internal static class DatasetEvaluator
{
    internal static MatchSnapshot Evaluate(MatchDataset dataset, Action<MatchSnapshot>? onSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var players = ValidateDefinition(dataset);
        var match = dataset.Match;
        var state = new MatchSnapshot(
            match.MatchId, 0, 0, TimeSpan.Zero, PeriodStatus.NotStarted,
            0, 0, BallControl.DeadBall, false);
        var seen = new HashSet<EventId>();
        onSnapshot?.Invoke(state);

        foreach (var item in dataset.Events)
        {
            Require(item is not null, ValidationError.InvalidEvent, "An event cannot be null.");
            Require(item.Id.Value > 0, ValidationError.InvalidEvent, "Event IDs must be positive.", item);
            Require(seen.Add(item.Id), ValidationError.DuplicateEvent, "Event ID already replayed.", item);
            Require(item.Sequence == state.LastSequence + 1, ValidationError.InvalidSequence,
                "Sequences must be contiguous and start at 1.", item);
            Require(item.Period >= 1 && item.Period <= match.Periods.Length,
                ValidationError.InvalidPeriod, "Period is not defined by the match.", item);
            var period = match.Periods[item.Period - 1];
            Require(item.Clock >= TimeSpan.Zero && item.Clock <= period.Duration,
                ValidationError.InvalidClock, "Clock is outside the configured period duration.", item);

            if (item is PeriodStarted)
            {
                Require(state.PeriodStatus != PeriodStatus.Active && !state.IsComplete
                    && item.Period == state.Period + 1,
                    ValidationError.InvalidPeriodBoundary, "Periods must start once, in order.", item);
                Require(item.Clock == TimeSpan.Zero, ValidationError.InvalidClock,
                    "A period must start at clock zero.", item);
                state = state with
                {
                    Period = item.Period,
                    Clock = item.Clock,
                    LastSequence = item.Sequence,
                    PeriodStatus = PeriodStatus.Active,
                    Control = BallControl.DeadBall
                };
                onSnapshot?.Invoke(state);
                continue;
            }

            Require(state.PeriodStatus == PeriodStatus.Active && item.Period == state.Period,
                ValidationError.InvalidPeriodBoundary, "Event requires its period to be active.", item);
            Require(item.Clock >= state.Clock, ValidationError.InvalidClock,
                "Clock cannot move backwards within a period.", item);

            if (item is PlayerEvent action)
            {
                ValidatePlayer(action.Team, action.Player, players, match, item);
            }

            state = Apply(state, item, match, players);
            state = state with { LastSequence = item.Sequence, Clock = item.Clock };
            onSnapshot?.Invoke(state);
        }

        return state;
    }

    private static MatchSnapshot Apply(
        MatchSnapshot state, MatchEvent item, MatchDefinition match,
        Dictionary<PlayerId, TeamId> players)
    {
        switch (item)
        {
            case PeriodEnded:
                Require(item.Clock == match.Periods[item.Period - 1].Duration,
                    ValidationError.InvalidClock, "Period end must match its configured duration.", item);
                return state with
                {
                    PeriodStatus = PeriodStatus.Ended,
                    Control = BallControl.DeadBall,
                    IsComplete = item.Period == match.Periods.Length
                };

            case ControlChanged change:
                ValidateControl(change.From, match, item);
                ValidateControl(change.To, match, item);
                Require(change.From == state.Control && change.To != change.From,
                    ValidationError.InvalidControl,
                    "Control transition must match current control and change it.", item);
                return state with { Control = change.To };

            case Pass pass:
                Require(Enum.IsDefined(pass.Outcome), ValidationError.InvalidOutcome,
                    "Unknown pass outcome.", item);
                ValidatePlayer(pass.Team, pass.Recipient, players, match, item);
                Require(pass.Recipient != pass.Player, ValidationError.InvalidAction,
                    "A player cannot pass to themselves.", item);
                RequireTeamControl(state, pass.Team, item);
                return state;

            case Shot shot:
                Require(Enum.IsDefined(shot.Outcome), ValidationError.InvalidOutcome,
                    "Unknown shot outcome.", item);
                RequireTeamControl(state, shot.Team, item);
                if (shot.Outcome != ShotOutcome.Goal)
                {
                    return state;
                }

                return state with
                {
                    HomeScore = state.HomeScore + (shot.Team == match.HomeTeam.Id ? 1 : 0),
                    AwayScore = state.AwayScore + (shot.Team == match.AwayTeam.Id ? 1 : 0),
                    Control = BallControl.DeadBall
                };

            case Foul:
                return state with { Control = BallControl.DeadBall };

            case Tackle tackle:
                RequireOpponentControl(state, tackle.Team, item);
                return state;

            case Pressure pressure:
                RequireOpponentControl(state, pressure.Team, item);
                return state;

            default:
                throw new DomainValidationException(
                    ValidationError.UnsupportedEvent, "Unsupported event type.", item.Id);
        }
    }

    private static Dictionary<PlayerId, TeamId> ValidateDefinition(MatchDataset dataset)
    {
        var manifest = dataset.Manifest;
        Require(manifest is not null
            && !string.IsNullOrWhiteSpace(manifest.DatasetId)
            && manifest.SchemaVersion == DatasetManifest.SupportedSchemaVersion
            && manifest.IsSynthetic,
            ValidationError.InvalidManifest,
            "A named, explicitly synthetic dataset using the supported schema is required.");
        var match = dataset.Match;
        Require(match is not null && !string.IsNullOrWhiteSpace(match.MatchId),
            ValidationError.InvalidDefinition, "A named match is required.");
        Require(match.HomeTeam is not null && match.AwayTeam is not null,
            ValidationError.InvalidDefinition, "Two teams are required.");
        Require(match.HomeTeam.Id != match.AwayTeam.Id,
            ValidationError.InvalidDefinition, "Home and away teams must differ.");
        Require(!match.Periods.IsDefaultOrEmpty, ValidationError.InvalidDefinition,
            "At least one period is required.");
        for (var index = 0; index < match.Periods.Length; index++)
        {
            var period = match.Periods[index];
            Require(period is not null && period.Number == index + 1 && period.Duration > TimeSpan.Zero,
                ValidationError.InvalidDefinition,
                "Periods must be numbered from 1 in order and have positive durations.");
        }

        Require(!dataset.Events.IsDefault, ValidationError.InvalidEvent,
            "Events must be initialized; an empty replay is allowed.");
        var players = new Dictionary<PlayerId, TeamId>();
        foreach (var team in new[] { match.HomeTeam, match.AwayTeam })
        {
            Require(team.Id.Value > 0 && !string.IsNullOrWhiteSpace(team.Name)
                && !team.Players.IsDefaultOrEmpty,
                ValidationError.InvalidDefinition, "Teams require positive IDs, names and players.");
            foreach (var player in team.Players)
            {
                Require(player is not null && player.Id.Value > 0
                    && !string.IsNullOrWhiteSpace(player.Name),
                    ValidationError.InvalidDefinition, "Players require positive IDs and names.");
                Require(players.TryAdd(player.Id, team.Id), ValidationError.InvalidDefinition,
                    "Player IDs must be unique across both teams.");
            }
        }

        return players;
    }

    private static void ValidatePlayer(
        TeamId team, PlayerId player, Dictionary<PlayerId, TeamId> players,
        MatchDefinition match, MatchEvent item)
    {
        Require(IsMatchTeam(team, match), ValidationError.UnknownTeam,
            "Team does not belong to this match.", item);
        Require(players.TryGetValue(player, out var actualTeam), ValidationError.UnknownPlayer,
            "Player does not belong to this match.", item);
        Require(actualTeam == team, ValidationError.PlayerTeamMismatch,
            "Player does not belong to the event team.", item);
    }

    private static void ValidateControl(BallControl control, MatchDefinition match, MatchEvent item)
    {
        Require(control is not null && Enum.IsDefined(control.Kind),
            ValidationError.InvalidControl, "Unknown ball control state.", item);
        Require(control.Kind == BallControlKind.Team
                ? control.Team is { } team && IsMatchTeam(team, match)
                : control.Team is null,
            ValidationError.InvalidControl,
            "Team control requires a match team; neutral control must not specify a team.", item);
    }

    private static bool IsMatchTeam(TeamId team, MatchDefinition match) =>
        team == match.HomeTeam.Id || team == match.AwayTeam.Id;

    private static void RequireTeamControl(MatchSnapshot state, TeamId team, MatchEvent item) =>
        Require(state.Control == BallControl.By(team), ValidationError.InvalidAction,
            "A pass or shot requires control by the acting team.", item);

    private static void RequireOpponentControl(MatchSnapshot state, TeamId team, MatchEvent item) =>
        Require(state.Control.Kind == BallControlKind.Team && state.Control.Team != team,
            ValidationError.InvalidAction, "A tackle or pressure requires opponent control.", item);

    private static void Require(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        ValidationError code, string message, MatchEvent? item = null)
    {
        if (!condition)
        {
            throw new DomainValidationException(code, message, item?.Id);
        }
    }
}
