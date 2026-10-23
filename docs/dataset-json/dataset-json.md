# Synthetic dataset JSON contract - version 1

The reference file is [match-001.json](../../data/synthetic/match-001.json).
It exactly reproduces the independent Phase 1A C# fixture: two fictional teams,
four fictional players, twenty events, two sixty-second periods, final score 1-1.
It contains no real Premier League data. This miniature tests the contract;
it does not claim to simulate the statistical realism of a full match.

## Document structure

All listed constructor fields are required, including nullable `team` in a
control state. Files use strict UTF-8, with or without a UTF-8 BOM; UTF-16 is
not automatically accepted. Property names are case-sensitive camelCase. Unknown properties,
duplicate properties, comments and trailing commas are rejected. There are no
implicit defaults for omitted fields. Null collection elements are rejected
by domain validation; null required objects are rejected during parsing.

| Object | Required fields |
| --- | --- |
| Root | `manifest`, `match`, `events` |
| Manifest | `datasetId` (string), `schemaVersion` (integer, currently 1), `isSynthetic` (boolean, must be true) |
| Match | `matchId` (string), `homeTeam`, `awayTeam`, `periods` |
| Team | `id` (integer), `name` (string), `players` (array) |
| Player | `id` (integer), `name` (string) |
| Period | `number` (integer), `duration` (duration string) |
| Every event | `type`, `id`, `sequence`, `period`, `clock` |
| Control state | `kind` (`deadBall`, `contested`, `team`), `team` (integer or null) |

IDs are JSON integers mapped to distinct domain ID types. Numeric strings,
fractional IDs and integers outside Int32 are rejected. Positivity and
referential integrity are domain rules, not JSON parsing rules.

`isSynthetic: true` is mandatory provenance metadata, not independent proof of
the origin of an untrusted input. Schema evolution requires an explicit
contract/version decision; unsupported versions are rejected, never migrated
silently.

## Time

`duration` and `clock` use the invariant .NET TimeSpan `c` representation:
`[-][d.]hh:mm:ss[.fffffff]`, for example `"00:01:00"` or `"00:00:05.1234567"`.
The clock is relative to the numbered period, not a timestamp or a global
match minute. A configured period duration includes its simulated added time.
The domain rejects negative or out-of-bounds clocks. Subsecond ticks survive
serialization without rounding.

## Event discriminators and payloads

`type` may appear anywhere in its object. These are the only supported types:

| `type` | Additional required fields |
| --- | --- |
| `periodStarted` | None |
| `periodEnded` | None |
| `pass` | `team`, `player`, `recipient`, `outcome` (`completed`, `incomplete`) |
| `shot` | `team`, `player`, `outcome` (`goal`, `saved`, `offTarget`, `blocked`) |
| `tackle` | `team`, `player` |
| `foul` | `team`, `player` |
| `pressure` | `team`, `player` |
| `controlChanged` | `from`, `to` (control state objects) |

Enum values are written in camelCase. The System.Text.Json enum reader also
accepts their case-insensitive spellings, but unknown names and numeric JSON
enum tokens are rejected. A goal exists only as `shot` with `outcome: "goal"`.
There is no separately supplied score or goal event.

## Minimal valid example

This is a valid *prefix*, not a completed match. The empty event array is
allowed by the Phase 1A contract.

```json
{
  "manifest": {
    "datasetId": "synthetic-minimal-v1",
    "schemaVersion": 1,
    "isSynthetic": true
  },
  "match": {
    "matchId": "fictional-miniature",
    "homeTeam": {
      "id": 1,
      "name": "Harbor FC",
      "players": [{ "id": 11, "name": "Alex Harbor" }]
    },
    "awayTeam": {
      "id": 2,
      "name": "Meadow United",
      "players": [{ "id": 21, "name": "Sam Meadow" }]
    },
    "periods": [{ "number": 1, "duration": "00:01:00" }]
  },
  "events": []
}
```

## Pipeline and responsibilities

1. `DatasetLoader.Load(path)` reads a local file.
2. `DatasetJson.Deserialize(json)` parses shape/types and constructs immutable
   domain records. It deliberately does **not** validate football rules.
3. `DatasetValidator.Validate(dataset)` checks the complete supplied sequence.
4. The loader returns only a validated `MatchDataset`.
5. Callers invoke `MatchReducer.Replay(dataset)` to obtain a `MatchSnapshot`.

Validation and replay share one internal `DatasetEvaluator`, extracted without
changing the Phase 1A rules. Validation necessarily evaluates transitions in
order; it discards the computed snapshot. Replay returns that snapshot.
No football rule is copied into the loader, codec or CLI.

`DatasetJson.Serialize(dataset)` supports object -> JSON -> object round-trips
and does not imply business validity; callers validate separately.
Immutable arrays are compared element by element in the round-trip tests
rather than relying on collection backing-store identity.

The loader reads the whole document into memory. It is intended for small,
trusted local datasets, not unbounded streams. JSON nesting uses the serializer's
default depth limit. No network, cloud or LLM is involved.

## Errors

| Stage | Error |
| --- | --- |
| File access | Standard `IOException` / `UnauthorizedAccessException` (not disguised as bad JSON) |
| JSON or structural contract | `DatasetJsonException`, code `InvalidJson`, JSON path when provided by the serializer, original exception retained |
| Football contract | Existing `DomainValidationException`, stable `ValidationError` code, event ID when applicable |

A missing manifest is a structural error. A present manifest with false
provenance or an unsupported version is a domain `InvalidManifest` error.
Unknown event types are structural errors; unknown players/teams referenced
by otherwise well-formed events retain the domain's `UnknownPlayer` /
`UnknownTeam` codes. Processing stops at the first error in deterministic order.

## CLI

From the workspace root:

```powershell
dotnet run --project tools\GameGround.Cli -- validate data\synthetic\match-001.json
dotnet run --project tools\GameGround.Cli -- replay data\synthetic\match-001.json
```

Both commands fully load, validate and reconstruct the score. `replay` also
prints the final snapshot position and completion state. It is not an
interactive or time-paced replay.

Expected summary:

```text
Dataset: synthetic-miniature-v1
Validation: valid (synthetic)
Events: 20
Score: Harbor FC 1-1 Meadow United
```

Exit codes: `0` success; `1` file, JSON or domain error; `2` usage error.
Errors go to stderr, successes to stdout. A failure never prints a valid
summary. Diagnostics are in English; underlying OS file messages may be
localized. The CLI does not modify the input file.

## Verification

```powershell
dotnet restore GameGround.sln
dotnet build GameGround.sln --no-restore
dotnet test GameGround.sln --no-build --no-restore
```

The original Phase 1A fixture and tests are preserved. New tests load the actual
reference file copied to the test output, compare every event and business
field against that fixture, and compare every prefix snapshot. Invalid JSON
variants are written to isolated temporary files and removed after each test.
