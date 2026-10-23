# GameGround football domain

This library contains no AI, networking, storage or real Premier League data.
All match inputs must explicitly declare synthetic provenance. That declaration
is required metadata, not proof of the origin of an arbitrary external input.

## Contract

- A match has exactly two distinct teams. Players are nested in their team;
  player IDs are unique across the match. IDs are positive, typed integers.
- Periods are numbered consecutively from 1. Each duration includes any
  simulated added time. The clock is period-relative, not wall-clock time.
- Event sequences start at 1 and are contiguous. Event IDs are unique across
  the replay. Equal timestamps are allowed; sequence establishes their order.
- Each period starts at zero and ends at its declared duration. Periods cannot
  overlap, be skipped, restart, or receive actions after ending.
- An empty replay or a valid prefix is supported. `IsComplete` becomes true
  only when the final configured period ends.
- Ball control is dead ball, contested, or owned by one match team.
  An explicit transition must name the current state and a different valid
  destination. Dead ball and contested states cannot have a team.
- Period boundaries, goals and fouls make the ball dead. Restarting requires
  an explicit control transition. Other actions do not implicitly transfer
  control: even an incomplete pass, saved shot or tackle requires a separate
  observed transition if control changes.
- Passes and shots require the acting team's control. Pass recipients must be
  different players on that team. Tackles and pressure require opponent control.
  A foul records its offender and makes the ball dead.
- A goal is exclusively `ShotOutcome.Goal` and credits the shooter's team.
  There is no second goal event or externally supplied score.
- Replaying duplicate event IDs is an error, not a silently ignored delivery.
  A second shot without restarting after a goal is also invalid.
- Replay is stateless and reconstructs the score from zero on every call.
  Snapshots contain immutable, value-comparable state and no wall-clock values.
- Invalid inputs throw `DomainValidationException` with a stable `Code`, a
  diagnostic message and, for event failures, the event ID.

## Scope and limitations

This is a deliberately small event simulation, not a complete referee engine.
It does not implement own goals, cards, substitutions, offside, penalty shootouts,
kick-off entitlement, video, tracking, xG, speed, formations or possession metrics
inside the core domain.
No action implies knowledge of off-ball movement. The miniature demonstration
fixture uses two players per team and shortened periods; it is not a full match
realism benchmark. Repeated real-world actions with different IDs cannot be
identified as duplicate deliveries without additional source identity.

## Demonstration and verification

The reproducible in-code dataset is in the test project's
`Fixtures\SyntheticMatch.cs`. It includes passes, pressure, a tackle, explicit
control changes, goals, a foul, and two shortened periods. Invalid variants are
constructed from it by the tests.

Phase 1B adds the equivalent versioned
[JSON reference dataset](../../data/synthetic/match-001.json), a separate local
loader and a CLI. See the [JSON contract and commands](../../docs/dataset-json/dataset-json.md).
The independent C# fixture remains a regression oracle.

`DatasetValidator.Validate` and `MatchReducer.Replay` use the same internal
`DatasetEvaluator`. Validation evaluates the supplied transitions and discards
the snapshot; replay returns it. There is no duplicate set of football rules.

Phase 2 adds `MatchReducer.ReplayTimeline`: index 0 is the initial state, and
each subsequent index is the post-event state for that sequence. It uses the
same validator/evaluator in one pass and returns no partial result on failure.
The separate [Football Intelligence layer](../../docs/football-intelligence/football-intelligence.md)
derives descriptive metrics, observed control intervals and source-linked facts.
It does not change the source JSON or the domain's football rules.

From the solution root, using the SDK selected by `global.json`:

```powershell
dotnet restore GameGround.sln
dotnet build GameGround.sln --no-restore
dotnet test GameGround.sln --no-build --no-restore
```
