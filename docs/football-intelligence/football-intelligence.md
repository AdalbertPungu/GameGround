# Deterministic football intelligence - rules version 1

## Scope and architecture

This phase uses only the version 1 event contract. It introduces no JSON schema
change, networking, AI, HTTP, UI or cloud dependencies. The reference dataset
[match-001.json](../../data/synthetic/match-001.json) is unchanged.

```text
MatchDataset
  -> MatchReducer.ReplayTimeline (validate and evaluate once)
  -> FootballIntelligenceEngine
     -> match / control / shooting summaries
     -> control sequences and shot chronology
     -> calculated facts + evidence
     -> deterministic moments + literal insight candidates
```

`GameGround.Intelligence` references only `GameGround.Core` and has no NuGet
dependencies. Loading a file remains the responsibility of `GameGround.Data`.
No intelligence calculation is added to the JSON parser or CLI.

The reducer's new `ReplayTimeline` uses the **same** domain evaluator as
`Replay` and `DatasetValidator`. It returns the initial snapshot at index 0
and the post-event snapshot at index `event.Sequence`. An invalid dataset
throws the existing domain error before a timeline or analysis is returned.
The pre-event state for an event at sequence `s` is timeline index `s - 1`.
No football transition logic is duplicated in the intelligence layer.

The engine performs ordered analysis of the supplied events only. Its event,
shot and sequence indexes avoid replaying every prefix for every metric.
Memory is proportional to input and evidence size; this is an in-memory local
analysis, not an unbounded live-stream processor.

## What is and is not observable

| Event | Available data | Supported calculation | Absent data / forbidden inference |
| --- | --- | --- | --- |
| `periodStarted`, `periodEnded` | Period, period-relative clock, sequence, ID | Started/completed periods, observed period time | Wall-clock duration or real broadcast time |
| `pass` | Team, player, recipient, completed/incomplete, clock | Attempts, completions, completion percentage | Distance, direction, pass difficulty, assist |
| `shot` | Team, player, outcome, clock | Shot counts, goals, outcome breakdown, conversion, chronology | xG, ball speed, danger, location |
| `tackle` | Team, player, clock | Recorded tackle count | Success, forced turnover or defensive quality |
| `foul` | Offending team/player, clock | Fouls, interruption of existing team control | Victim, severity, card or sanction |
| `pressure` | Acting team/player, clock | Recorded pressure-event count | Spatial pressing, intensity, effectiveness |
| `controlChanged` | Source/destination control state, clock | Explicit transitions, continuous control intervals | Cause, tactical success or "regain" after every restart |

There are no coordinates, tracking frames, speed, distance, formations, heatmaps,
calibrated probabilities or external football history. They are not synthesized
by this engine.

## Observation scope and provenance

Every `Evidence` includes an `AnalysisScope`:

- the original `DatasetManifest` (including synthetic declaration and schema);
- match ID;
- last supplied sequence (`ThroughSequence`);
- deterministic intelligence `RulesVersion`.

Every computed ID (`FactId`, `MomentId`, `ControlSequenceId`) is local to that
scope, assigned in deterministic order starting at 1. IDs are not global
identities. The scope is not a content hash or a cryptographic integrity proof;
consumers must retain the actual source dataset/analysis. The original source
event objects are retained, immutably, in `MatchIntelligence.SourceEvents`.

Only observed periods appear in the output. An empty event array means a valid
not-started prefix, not a completed match with no football actions.
`Summary.Snapshot.IsComplete` and scope must accompany any displayed statistic.
To analyze a prefix, supply the corresponding dataset event prefix; the engine
does not inspect or extrapolate unseen events. No future period duration is
added merely because it is configured in the match definition.

## Metric catalogue

All counts below refer to the supplied observation scope, not automatically
to a whole match. A count with no matching events is 0.

| Metric / `FactKind` | Formula and sources | Limitations / unavailable cases |
| --- | --- | --- |
| `Events` | Count all events | Includes boundaries and control changes, not just player actions |
| `PeriodsStarted` / `PeriodsEnded` | Count respective boundary events | Completed periods only if an end was supplied |
| Score / `Goals` | Count `shot` with `outcome=goal` by shooting team | No own-goal model; reducer score is authoritative and independently cross-checked by tests |
| `Shots` | Count all `shot` events for a team | Not an assessment of danger |
| `SavedShots`, `OffTargetShots`, `BlockedShots` | Count shots with the corresponding outcome | The four shot outcomes, including goal, partition all shots |
| `ShotsOnTarget` | Goals + saved shots | Explicit operational convention; blocked shots are excluded; not a claim of official provider statistics |
| `ShotConversion` | `100 * goals / shots` | Null when shots=0, not zero; numerator and denominator retained |
| `Passes` / `CompletedPasses` | Count all passes / only completed passes | Incomplete is the only other outcome |
| `PassCompletion` | `100 * completed / attempted` | Null when attempted=0 |
| `Fouls` | Count `foul` events by offending team | Says nothing about sanctions |
| `Tackles` | Count `tackle` events by acting team | No successful-tackle metric |
| `PressureEvents` | Count `pressure` events by acting team | No pressing effectiveness metric |
| `ExplicitControlChanges` | Count `controlChanged` events | Does not include implicit dead-ball effects of goals, fouls or period ends |
| `ControlSequences` | Count team-owned continuous control sequences | Includes restarts and zero-duration sequences; not a count of opponent-ball recoveries |
| `ControlDuration` | Sum observed interval lengths for one team's sequences | Exact only within this piecewise-constant synthetic control model |
| `ContestedDuration` / `DeadBallDuration` | Sum interval lengths of corresponding states | Not attributed to either team |
| `ObservedDuration` | Sum all observed sequence lengths within periods | No halftime or wall-clock gap; null percentages are not replaced by invented values |
| `GoalOccurrence` | 1 for a sourced goal moment | Single-event fact, not a team aggregate |
| `ControlTransitionOccurrence` | 1 for a sourced explicit control transition | No tactical importance implied |
| `FoulInterruptionOccurrence` | 1 when a foul changes pre-event team control to dead ball | Only team-control interruption; no such moment on already-dead or contested control |

Percentages use decimal arithmetic and are not rounded by the engine. For
example, one goal from three shots is `100m / 3`. Rendering may round later,
but the exact numerator/denominator remains available. Numeric fact units are
`Count`, `Seconds` or `Percent`. Duration facts convert integer ticks to decimal
seconds (`ticks / TimeSpan.TicksPerSecond`) without floating-point rounding.
Summary and sequence durations remain `TimeSpan`.

`MatchSummary.Teams` includes pass, goal, foul, tackle and pressure counts.
`Shooting` includes outcome counts for each team; `ShotTimeline` stores period,
clock, event ID, player, outcome, evidence and the associated control-sequence ID.
`PeriodSummary` stores its observed clock, completion status and source events.

## Control intervals and coherent event sequences

An `EventPosition` is (sequence, period, period-relative clock), never an
invented global timestamp.

For a continuous control interval:

```text
observed duration = end.Clock - start.Clock
```

- A period starts with a dead-ball interval at clock 0.
- A state change in the reducer closes the current sequence and starts a new
  one at the same event. This covers explicit transitions and implicit
  dead-ball changes after a goal or foul.
- A period end closes its sequence; no new post-period interval is created.
- If the input ends mid-period, the final interval stops at the last supplied
  event. `IsClosed=false` indicates that a later event could extend it.
- A goal at the end of a prefix starts an open, zero-duration dead-ball
  interval. It is not extended to the configured period end.
- A foul while already dead does not create another dead-ball sequence.
- Events with identical clocks remain ordered by sequence. Zero-duration
  intervals are retained, not discarded or counted as positive time.
- Team A -> contested -> Team B and Team A -> dead -> Team B preserve the
  neutral interval. Direct Team A -> Team B creates no invented neutral gap.
- Team A -> Team A as an explicit no-op remains a domain error. Consecutive
  actions under Team A control do not split a sequence.
- Team A -> contested -> Team A creates two team sequences, but is not labelled
  a recovery from the opposing team.
- A saved shot, incomplete pass or tackle does not itself transfer control.
  This follows the existing domain contract exactly. For example, Meadow's
  state persists from clock 17 through the saved shot at 18 to period end at
  60: 43 seconds of *modelled control*, not a new real-world possession claim.

Intervals are temporally half-open `[start, end)`. Evidence includes the start
event, intervening events, **and the closing boundary event**. Adjacent
sequences can therefore share a boundary event; their time is never double
counted. Team sequence evidence may include an opponent's pressure, tackle or
foul. Event membership does not imply the acting team owned control.

`ControlSequence` is the coherent `EventSequence` model for this phase. A shot
links to its controlling team sequence, preserving the preceding passes and
terminal shot when present. This means "observed in the same control sequence",
not "the preceding pass caused the goal" or an assist attribution. No separate
pass-chain taxonomy is added without a need for additional interruption rules.

## Deterministic moments and candidates

There are exactly three rules, evaluated in event order:

1. `Goal`: a shot with `outcome=goal`. Evidence is that shot.
2. `ControlTransition`: any explicit `controlChanged` event. Evidence is that
   event, which carries both source and destination. The associated team is
   the destination team, or null for a neutral destination.
3. `FoulInterruption`: a foul whose validated pre-event state is team control.
   Evidence is the interrupted sequence through the foul. The moment's team
   is the **offending team**, not necessarily the controlling team.

Every moment has a typed ID, event position, source evidence, fact IDs and,
where applicable, a control-sequence reference.

Each moment produces one `InsightCandidate` containing its moment ID, kind,
team, position, priority, literal statement, fact IDs and evidence.
`ScoreUpdate` priority is reserved for a goal; other rules use `ContextUpdate`.
These are editorial routing categories, not learned importance scores or
claims that an event is decisive.

Statements use invariant, fixed English templates and numeric team IDs:

```text
Team 2 scored at period 1, 00:00:12.
Control changed from team 1 to contested at period 1, 00:00:07.
A foul by team 2 interrupted team 1 control at period 1, 00:00:14.
```

No rule calls a shot dangerous, declares domination or tactical superiority,
detects an unusual sequence, or applies an arbitrary long-possession threshold.
The dataset and contract lack the signals/baselines needed to justify these.

## Evidence and traceability

```text
InsightCandidate.MomentId -> MatchMoment
InsightCandidate.FactIds  -> CalculatedFact
CalculatedFact.Evidence  -> AnalysisScope + ordered SourceEventIds
SourceEventIds            -> original MatchEvent objects
```

`CalculatedFact` includes a typed kind, optional team, decimal value (nullable
when unavailable), unit, optional numerator/denominator and evidence.
Count evidence contains matching events. Ratio evidence contains all events
in its denominator. Duration evidence contains the union of all relevant
interval source events in original order, with duplicate boundaries removed.

Zero counts have empty positive-source arrays and an explicit complete
observation scope. They are evidence of absence **within that scope**, not
positive events or proof about unseen play. The denominator=0 percentage
value is null; it does not claim 0% efficiency.

`EvidenceResolver.Resolve(analysis, evidence)` resolves source objects from
that analysis. It rejects a different scope, missing sources, sources outside
the cutoff, duplicates and reordered references with explicit argument errors.
No file access is needed. Resolution proves reference integrity, not that an
arbitrary caller-authored sentence is entailed by those references. Future
narrative validation must still check meaning.

## Determinism

Outputs are immutable records and immutable arrays. Ordering is fixed: teams
home then away, periods chronologically, sequences/moments/candidates by
events, facts by the documented engine construction order. IDs are ordinal
within a scope. There is no wall-clock, randomness, culture-dependent
formatting, global mutable cache or external call.

Independent `ImmutableArray` instances do not have structural record `==`
semantics. Determinism means equality of **all contents**, not backing-array
reference identity. Tests compare the complete serialized output field values
and, independently, every strongly typed source event. Culture changes,
concurrent runs, prefix analysis and repeat runs are covered.

This phase introduces no report JSON wire contract. The source dataset's
polymorphic JSON contract remains in `GameGround.Data`; a default serializer of
the report's base-typed source events is not a replacement for that codec.

## Verified example: match-001.json

| Measure | Harbor FC (team 1) | Meadow United (team 2) |
| --- | --- | --- |
| Goals | 1 | 1 |
| Shots | 1 | 2 |
| Saved / off-target / blocked | 0 / 0 / 0 | 1 / 0 / 0 |
| On target (goals + saved) | 1 | 2 |
| Goal conversion | 100% (1/1) | 50% (1/2) |
| Passes completed / attempted | 1 / 2 | 1 / 1 |
| Pass completion | 50% | 100% |
| Fouls / tackles / pressures | 0 / 0 / 0 | 1 / 1 / 1 |
| Team control sequences | 4 | 2 |
| Modelled observed control | 15 s | 47 s |

Global: 20 events, two ended periods, seven explicit control transitions,
twelve control sequences including neutral ones, one second contested,
57 seconds dead, 120 seconds observed. The durations sum exactly:
`15 + 47 + 1 + 57 = 120`.

Three shots: event 9 (team 2, period 1 at 12 s, goal), event 15 (team 2,
period 1 at 18 s, saved), event 19 (team 1, period 2 at 5 s, goal).
The first shot's control sequence has source events 7, 8 and 9.

There are 47 facts and ten moments/candidates: two goals, seven explicit
control transitions and one foul interrupting team control (sources 10, 11).
No "four regains" claim is made about Harbor's four control sequences.

The fixture has only three passes and three shots. It demonstrates arithmetic,
control and evidence but cannot establish statistical trends, long-term
baselines, pressing intensity or tactical causality. More varied synthetic
scenarios would be needed for those claims; the reference dataset is not edited
to create them.

## Verification

The original 162 Phase 1A/1B cases and reference JSON are retained unchanged.
Additional tests verify exact reference totals and interval boundaries,
all shot outcomes, no-op and contradictory transitions, zero observations,
zero denominators, same-clock actions, fractional time, period boundaries,
foul/goal interruptions, evidence resolution, all prefix cutoffs, determinism
and concurrency.

```powershell
dotnet restore GameGround.sln
dotnet build GameGround.sln --no-restore
dotnet test GameGround.sln --no-build --no-restore
dotnet format GameGround.sln --verify-no-changes --no-restore
```

To print the tested example without adding a CLI command:

```powershell
dotnet test tests\GameGround.Tests --no-build --no-restore --filter "FullyQualifiedName~Example_analysis_of_reference_json" --logger "console;verbosity=detailed"
```
