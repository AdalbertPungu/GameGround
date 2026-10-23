# Multi-agent contracts and runtime - Phases 3 through 4C

## 1. Objectives and current diagnostic

The pipeline is:

```text
Core domain / validated event prefix
  -> FootballIntelligenceEngine (the only football calculator)
  -> immutable, window-scoped AgentContext
  -> Analyst observations
  -> deterministic ClaimVerifier
  -> Narrator claims
  -> deterministic ClaimVerifier + approved-handoff check
  -> Completed SharedState / future presentation
```

The reference dataset remains synthetic and unchanged. Its full analysis exposes
47 calculated facts, 12 control sequences and 10 moments/candidates. The
[Phase 2 metric contract](../football-intelligence/football-intelligence.md) remains authoritative.
No football formula, dataset schema or domain rule changes in this phase.

Existing evidence identifies a manifest, match, rules version, prefix cutoff and
ordered source IDs. It does not establish the truth of arbitrary prose, and fact,
moment and sequence ordinals are not global identifiers.

`GameGround.Agents` references Intelligence (and transitively Core), not the JSON
loader, CLI, HTTP, storage or a model SDK. Dataset slicing happens only at the
trusted context-building boundary. Agents receive no raw events or MatchDataset,
and do not replay events or recalculate statistics.

## 2. Retained roles

| Role | Decision | Distinct value |
|---|---|---|
| Match Analyst | Retained | Select measurable observations from authoritative facts and moments. |
| Pattern / Sequence Analyst | Merged into Analyst | Sequences are already deterministic. No separate pattern metric or justified tactical inference currently requires another agent. |
| Narrator | Retained | Select and order approved observations for a future audience; cannot add facts. |
| Verification / Critic | Retained as deterministic service | Independently checks payload, provenance, numbers, scope and wording; can reject. |
| Orchestrator | Deterministic application code | Routing, immutable handoffs, history, bounded revisions and failure status, not football analysis. |

The default local workflow has one Analyst adapter and one Narrator adapter.
Multiple analyst adapters are supported for deduplication tests, not required for
the demo. No additional specialist agent is instantiated automatically.

These are **local simulated roles**, not autonomous LLM agents. The simulations
prove contracts, not the superiority of an already deployed multi-agent model.
Future separation is justified by independent observation selection, restricted
presentation and independent approval, not by an artificial agent count.

## 3. Responsibilities and access

| Component | Input / state | Output | Tools / restrictions |
|---|---|---|---|
| Context factory | Trusted dataset and requested window | Immutable AgentContext | Calls Intelligence on a prefix; never exposes raw events. |
| Analyst | Context, own previous batch, checks and revision reasons | Proposed claims | EvidenceQuery; select facts/moments, no new metrics. |
| Narrator | Same bounded context, verified observations, own revision feedback | Proposed narrative claims | Only meanings in approved handoff; no new facts even if available in context. |
| Verifier | Context + proposed claim | VerificationResult | Canonical lookups and exact templates; no model. |
| Orchestrator | Adapters, options, context | SharedState | Controls calls, provenance, handoff, deduplication and failures. |

Adapters have an `AgentIdentity(Id, Role)`. The orchestrator checks that returned
producers match the invoked identity. Roles alone are not globally unique IDs.
Agents do not write the context, evidence, facts, verification results or global
workflow state. Each invocation receives an immutable request.

`SimulatedAnalyst` selects per-team goals, shots and non-null pass completion, plus
available moment candidates. It does not synthesize recurrence statistics.
An empty observed prefix produces no automatic observations and explicitly skips
the narrator.
`SimulatedNarrator` preserves the canonical wording and order of the approved
observations. It is deliberately not a creative narrative generator.

## 4. Shared state

`SharedState` contains:

- `Context.Identity`: source-content digest, AnalysisScope and AnalysisWindow.
- `AnalysisScope`: match ID, manifest/dataset ID/schema/synthetic flag,
  Intelligence rules version and authoritative prefix cutoff.
- `Context.From` / `Through`: actual visible source positions, including period
  and period clock; null for the empty prefix.
- Teams, available facts, canonical evidence entries, sequences, moments and
  insight candidates, all immutable.
- Verified `Observations`, verified `NarrativeClaims`.
- `Verifications`: every checked claim, producer, attempt and result, including
  rejected attempts.
- Global `Status` and ordered `History` with decisions and explanations.

Statuses are Created, Analyzing, Narrating, Completed, Rejected, Failed, Cancelled.
RunAsync returns a terminal state; Created and running statuses describe the
contract, not persisted streaming notifications.

Only a **Completed** workflow may be published. Rejected/Failed/Cancelled states
are diagnostic, even if they retain observations from an earlier successful
stage. No rejected batch is partially accepted. There is no database or durable
runtime in this phase; a caller receives the immutable final state.

Minimal host-side usage (the dataset is loaded outside the agents):

```csharp
var context = AgentContextFactory.Through(dataset, dataset.Events.Length);
var workflow = new LocalOrchestrator(
    [new SimulatedAnalyst()], new SimulatedNarrator());
SharedState state = await workflow.RunAsync(context);
```

## 5. Claim contract

`Claim` contains:

- `ClaimId`, producer identity and role, text.
- `ClaimType`: Metric or RecordedMoment.
- Exact `ContextIdentity`, including the requested window and content digest.
- Ordered `FactIds`, `EvidenceReference` entries and `CoveredSourceEvents`.
- Either `MetricAssertion` or `MomentId`, never both.
- Proposed / Verified / Rejected status.

A `MetricAssertion` specifies FactKind, team (or match), value, unit and optional
numerator/denominator. It references exactly one canonical fact. A RecordedMoment
references exactly the fact set and statement of an existing candidate.

Claim IDs are local to `(agent identity, stage attempt)`. They must be positive
and unique within a batch; the orchestrator stores producer and attempt with
each verification. Revisions may reuse a claim ID. Semantic deduplication ignores
claim IDs and producer, but includes context, type, metric/moment payload, exact
fact/evidence references and covered source IDs.

There is **no probabilistic confidence score**. Passing deterministic support
checks is not calibrated confidence about a real-world football interpretation.
The text is never evidence and no claim can self-approve.

## 6. Evidence contract and identity

`AvailableEvidence` binds a FactId to the original Intelligence `Evidence` and an
`EvidenceReference(EvidenceId, ContextIdentity)`. EvidenceId uses the fact's local
ordinal; its meaning is inseparable from the context. Two facts may cover the
same source event. References must exactly equal the canonical ordered reference
set for the claim's facts.

The content ID is SHA-256 over an invariant System.Text.Json projection of
AnalysisScope, window, team labels and all **typed source events in the prefix**.
It includes the event type discriminator and subtype fields, not merely the
MatchEvent base properties. Pressure and Tackle have identical field shapes but
remain different source types. Different
visible source content cannot silently reuse the same local fact references.
An unseen suffix is excluded from both computation and identity.

This is a content binding, not a signature, user authorization mechanism,
secret protection or cross-process sandbox. Do not treat an adapter executing
arbitrary .NET code as untrusted code contained by these interfaces.

Zero-count facts have a canonical evidence entry with an empty source list.
They describe absence only in an observed prefix, not proof of a positive event.
They are allowed with their scoped evidence reference. Undefined percentages
remain null and cannot be asserted.

## 7. Verification contract

`VerificationResult` exposes claimId, status, named checks, supported/unsupported
fact references, canonical source events and explicit rejection reasons.
SupportedFacts identifies resolvable referenced facts; it does **not** imply the
claim is accepted. UnsupportedFacts identifies unknown fact IDs. Overall status
and checks decide acceptance, including when all fact IDs exist but values fail.

Checks:

1. WellFormedClaim: initialized arrays, identity, positive ID, known role,
   nonempty text, complete context, Proposed status.
2. HasEvidence: nonempty fact and evidence references.
3. EvidenceExists: canonical references exactly match the known fact set,
   without missing, extra or duplicate facts/evidence.
4. SameMatch: manifest, dataset, match and Intelligence version agree.
5. SameWindow: exact window, prefix cutoff and source-content identity agree.
6. ReferencedMetricMatches: type, metric, unit, team and exact fact set agree.
7. NumericValuesConsistent: value, numerator, denominator and printed numeric
   tokens match the authoritative canonical statement.
8. WordingWithinKnownData: exact canonical metric template or candidate wording.
9. NoUnsupportedTacticalInference: the same closed-language constraint excludes
   unverified inferences; this is not a keyword blacklist.
10. NoFutureEventLeakage: no foreign context or unproven covered source ID.
11. SourceCoverageMatches: declared indirect coverage exactly matches the ordered
    canonical evidence union.

Malformed claims fail before unsafe traversal. Other well-formed claims receive
all checks. The orchestrator also rejects a narrator's claim whose semantic
meaning was not present in the approved observation handoff.

Free prose is **not verifiable** with this deterministic implementation. A later
LLM must initially select structured payloads whose text is rendered by the host,
or a separately designed language-verification contract must be approved.

## 8. Handoffs and success/rejection conditions

| Handoff | Input / allowed data | Output / success | Rejection |
|---|---|---|---|
| Host -> Context | Trusted dataset, window | Validated prefix and safe projections | Invalid visible domain data or window: explicit exception. |
| Orchestrator -> Analyst | Bounded context, own feedback | Initialized Proposed observation batch | Malformed output, foreign producer or failed checks. |
| Analyst -> Verifier | Proposed claims + canonical context | Entire batch passes | Any unsupported value, evidence, scope or wording. |
| Orchestrator -> Narrator | Verified observations + same context | Proposed claims restricted to approved meanings | New facts, even if individually correct, or invalid claims. |
| Narrator -> Verifier | Proposed narrative claims + approved handoff | Entire nonempty batch passes | Failed checks or unapproved meaning. |
| Orchestrator -> Future host | Completed immutable state | Verified, deduplicated narrative claims | Any non-Completed status is not publishable. |

The verifier runs **before and after** narration, not only at the end.
The invocation reason is recorded in History. If the analyst selects no
observations, the narrator is explicitly skipped. Narrative output must be
nonempty when observations have been handed off.

## 9. Failure recovery

| Failure | Behavior |
|---|---|
| Unsupported claim / unknown or foreign evidence / wrong number / overbroad wording | Record rejected claim and checks; request replacement batch. |
| Malformed batch, duplicate claim ID, false producer identity | Explicit MalformedOutput history and textual revision reasons; no unsafe payload forwarded. |
| Duplicate meaning from one or multiple analysts | Keep first in configured order; record DuplicateRemoved. |
| Repeatedly rejected or malformed output | At most MaxRevisions extra attempts, then Rejected. |
| Agent timeout | Failed + Timeout history, cancel invocation, no automatic overlapping retry. |
| Explicit AgentExecutionException | Failed + AgentFailure history with original reason; no fallback narrative. |
| Caller cancellation | Cancelled state and history; no publishing. |
| Unexpected programming exception | Propagates, not disguised as an approved or empty result. |

Default: one revision, five-second timeout. Bounds: 0-10 revisions; timeout
greater than zero and at most five minutes. At most `MaxRevisions + 1` calls per
stage. Each revision receives its previous batch, verification results and
explicit reasons; malformed batches receive reasons without invalid objects.

Adapters are invoked on a task so a synchronously blocked adapter does not block
the host's wait. Cancellation is cooperative: an in-process timeout cannot kill
arbitrary .NET code. No second invocation is automatically started after timeout,
and late output has no route into SharedState. Stronger process isolation and
runtime resource budgets are future decisions, not implemented guarantees.

## 10. Analysis windows and no future leakage

Canonical windows are inclusive sequence ranges, because period clocks reset
and several events may share a clock. The empty prefix is `(1, 0)`.

- Whole supplied match: `(1, dataset.Events.Length)`.
- Prefix: `Through(dataset, cutoff)`.
- Period: `Period(dataset, period, cutoff)`.
- Time range: `TimeWindow(dataset, period, fromClock, throughClock, cutoff)`.
- Before an event/moment: `Before(dataset, EventPosition)` is exclusive and
  validates the position against the identified source event.

Clock boundaries are inclusive, with an additional sequence cutoff for tied
clocks. A time/period request with no observed events fails explicitly; it is
not converted to the whole match or to invented zero statistics.

The factory cuts the dataset **before** calling Intelligence. No future event
is domain-validated, counted, included in evidence, serialized into context identity
or used to close an interval. Only an observed prefix is analyzed.

For a window starting after sequence 1:

- Do not reuse cumulative counts, ratios or durations as window statistics.
- Only occurrence facts with all their evidence inside the window are exposed.
- Only sequences starting inside the window and fully supported inside it are
  exposed; do not clip an interval and silently retain its old duration.
- A moment with partially outside evidence is omitted. Dangling references to
  unavailable control sequences are removed from moment projections.
- Candidates must have a corresponding available moment and fact set.

This is intentionally conservative. Period/window summaries are **not yet**
computed. Adding them requires a separate approved deterministic Intelligence
contract, not agent-side arithmetic or silently changed Phase 2 semantics.
The context's original prefix scope is retained for traceability.

The host supplies the authorized cutoff. This phase prevents leakage through
the scoped tools; it does not decide whether a caller is authorized to request
the full match. Future authentication must enforce that policy outside agents.

## 11. Evidence Query tools

`EvidenceQuery` is bound once to an immutable AgentContext:

- `GetFacts()` returns only the context's facts.
- `GetEvidence(factIds)` returns their exact canonical evidence entries.
- `GetSequence(sequenceId)` returns only an available sequence.
- `GetMoment(momentId)` returns only an available moment.

It has no raw-JSON operation or parameter that can broaden the window or switch
matches. Unknown/unavailable references raise explicit ArgumentException; they
are not silently resolved from a full-match cache. Raw source payloads remain
at the trusted ingestion/Intelligence boundary. Ordered source IDs provide
traceability for a future privileged evidence viewer.

## 12. Prohibitions

No real Premier League data, invented xG, spatial signal, formation, heatmap,
tracking, speed, distance, tactical causality, danger label, inferred recovery
or unmeasured dominance. Pressure means recorded pressure events, not a spatial
pressing model. Sequence count includes restarts, not just regains.

Phase 3 introduced no LLM call, model credential, Microsoft SDK, Azure deployment,
Foundry resource, frontend, HTTP API, database or external agent runtime.
The isolated Phase 4A SDK compatibility tests are described in section 16;
they do not change this local workflow.

## 13. Accepted / rejected examples

Reference match, full observed window `(1,20)`:

```text
ACCEPT:
Meadow United: recorded pass completion = 100% (1/1), within sequences 1-20.

Metric: PassCompletion, Team 2, value 100, Percent, numerator 1, denominator 1.
Evidence: canonical entry for that fact; source EventId 8.

ACCEPT:
Team 2 scored

RecordedMoment: the engine's first goal candidate; source EventId 9.

REJECT:
Meadow United: recorded pass completion = 90% (1/1), within sequences 1-20.
Reason: printed number and wording disagree with the canonical fact.

REJECT:
Meadow United completed 100% of all its passes (1/1).
Reason: drops the recorded-data and observation-window qualification.

REJECT:
Meadow United dominated the midfield.
Reason: no supported metric or permitted wording establishes this assertion.

REJECT:
Any otherwise correct claim from cutoff 20 submitted to context cutoff 8.
Reason: context/evidence cutoff and content binding mismatch.
```

The local reference workflow produces 16 observations and 16 verified narrative
claims: six selected team metrics and ten existing moment candidates. It does
not pretend to generate a broadcaster-quality recap.

## 14. Test strategy and reproducibility

The 241 Phase 1/2 cases remain unchanged. Phase 3 adds 80 cases, for 321 passing
cases in total. The build has no warnings or errors and full format verification
passes. Phase 3 tests cover:

- Correct claims, wrong structured and printed values/ratios, absent/unknown
  evidence, foreign match/manifest/content, future context and source IDs.
- Unverifiable tactical language and broader wording despite correct numbers.
- Every available metric and moment template, scoped zeroes and null ratios.
- Every reference prefix (including empty), period/time windows, equal clocks,
  exclusive before-moment queries and invalid requests.
- Changed unseen suffix having no effect on context or content identity.
- Query denial of unavailable facts, moments and sequences.
- Whole-batch rejection, both stages' correction handoffs, bounded revisions,
  semantic deduplication across two producers, malformed output and spoofing.
- Explicit timeouts, cancellation, known adapter failure and propagated bugs.
- Entire serialized-state equality across two successful local executions.

No random IDs, wall-clock history stamps or unordered routing are generated.
Given identical contexts, configuration and successful simulated responses,
the entire state is deterministic. Timeout/cancellation outcomes depend on the
execution environment; they are explicit failure states, not deterministic
football facts.

Run the existing solution commands:

```powershell
dotnet restore GameGround.sln
dotnet build GameGround.sln --no-restore
dotnet test GameGround.sln --no-build --no-restore
dotnet format GameGround.sln --verify-no-changes --no-restore
```

For a concrete machine-readable demonstration in test output:

```powershell
dotnet test tests\GameGround.Tests\GameGround.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Reference_workflow_is_verified_end_to_end" --logger "console;verbosity=detailed"
```

No new CLI, network call or dependency is needed.

## 15. Future Microsoft mapping and decisions before Phase 4

These are conceptual seams, not claims that particular SDK APIs exist:

- Microsoft Agent Framework: possible future adapter/orchestration integration
  behind ILocalAgent. Preserve context projection, host verification and bounds.
- Microsoft Foundry: possible model/agent execution, evaluation and tracing.
  Never replace canonical evidence with a model's self-assessment.
- Azure Container Apps: possible future host for the application boundary.
- Azure Functions: possible event-triggered host if lifecycle and workload fit.
- A2A: not required for two in-process local roles; only reconsider for a real
  independently deployed interoperability requirement.

No package name or cloud API was asserted or installed in Phase 3. At integration time,
verify official current documentation, availability, authentication, observability,
costs, data handling and exact APIs before selecting an SDK.

Before Phase 4 decide:

1. Whether model output remains structured selection + host rendering, or needs
   a separately specified and tested free-language verification contract.
2. Whether partial-window aggregate statistics are needed, requiring explicit
   deterministic Intelligence extensions.
3. Audience, ranking and narrative length; do not invent audience preference
   data or football importance labels.
4. Runtime/model provider and verified SDK versions, only after approval.
5. Authorization of match/window requests, adapter isolation, budgets, telemetry,
   privacy and durable recovery requirements.

The Phase 3 runtime remains the behavioral reference and rollback path.

## 16. Phase 4A: isolated Agent Framework API spike

In Phase 4A, the [runtime project](../../src/GameGround.AgentFramework/GameGround.AgentFramework.csproj)
was a dependency anchor only. No production executor, model adapter or migrated
orchestrator was introduced. All executable spike code lives in the separate
[compatibility tests](../../tests/GameGround.AgentFramework.Tests/ApiCompatibilityTests.cs)
with a [scripted client](../../tests/GameGround.AgentFramework.Tests/ScriptedChatClient.cs).
Removing these two projects and their solution/package entries leaves existing
GameGround behavior intact.

Central package versions are pinned to the stable `Microsoft.Agents.AI` and
`Microsoft.Agents.AI.Workflows` 1.25.0 packages, using their native `net10.0` assets.
`Microsoft.Extensions.AI` 10.10.0 and its Abstractions 10.10.1 are resolved
transitively; no extra direct reference is needed. No Foundry, Azure, Semantic
Kernel or AutoGen package is added.

Dependency direction:

```text
GameGround.AgentFramework.Tests -> GameGround.AgentFramework
  -> GameGround.Agents -> GameGround.Intelligence -> GameGround.Core
```

Only the new runtime project introduces the SDK dependencies. Existing projects
do not reference it. AgentContext, EvidenceQuery, Claim, ClaimVerifier,
LocalOrchestrator, SimulatedAgents, Core, Intelligence and the dataset are unchanged.

Seven offline test cases compile and exercise:

- `WorkflowBuilder`, `Executor<TInput,TOutput>`, `HandleAsync`,
  `AddEdge`, `WithOutputFrom`, `InProcessExecution.RunAsync` and typed output events.
- `AddEdge<ValidationDecision>` with complementary conditions: approved and
  rejected inputs execute only their selected branch.
- `RunStreamingAsync` and `StreamingRun.CancelRunAsync`: executor entry is
  coordinated with a TaskCompletionSource before cancellation, and the handler
  observes its CancellationToken. There are no sleeps or latency-based triggers.
  A ten-second watchdog only bounds a broken test.
- `AIAgent`, `ChatClientAgent`, `CreateSessionAsync`,
  `RunAsync<TestAgentOutput>` and `AgentResponse<TestAgentOutput>.Result`.
  The scripted client asserts that a JSON-schema response format reaches it.
- `AIFunctionFactory.Create`, tool registration, a scripted FunctionCallContent
  round trip, and FunctionResultContent. The sole function serves hard-coded
  test fact 8; direct invocation of an unavailable ID raises an explicit error.

The structured response is a scripted fixture, not an LLM result or evidence
verification. These tests do not establish provider/schema compatibility with
any real model. No transport, endpoint, credential or network-capable client
is constructed. NuGet restore itself needs package access; test execution does not.

### Cancellation finding that must inform Phase 4B

In the 1.25.0 default off-thread environment, cancelling the token supplied to
`RunAsync` did not cancel an already-running handler. A pre-cancelled invocation
also did not throw the expected OperationCanceledException. The implementation
uses that token while consuming events; it is not sufficient as a host-level
execution cancellation guarantee. The SDK documents that cancelling
`WatchStreamAsync` stops observation, not workflow execution.

The passing spike explicitly calls `StreamingRun.CancelRunAsync`, verifies
handler cancellation, and disposes every run. Before Phase 4B, implement and test
the host's pre-cancellation check and explicit bridging from caller cancellation
and deadlines to active-run cancellation. Do not equate an ended event stream
with a cancelled or approved GameGround workflow.

Sources: [versioned execution implementation](https://github.com/microsoft/agent-framework/blob/dotnet-1.25.0/dotnet/src/Microsoft.Agents.AI.Workflows/InProc/InProcessExecutionEnvironment.cs),
[streaming cancellation contract](https://github.com/microsoft/agent-framework/blob/dotnet-1.25.0/dotnet/src/Microsoft.Agents.AI.Workflows/StreamingRun.cs),
and [public net10.0 signatures](https://github.com/microsoft/agent-framework/blob/dotnet-1.25.0/dotnet/src/Microsoft.Agents.AI.Workflows/PublicAPI/net10.0/PublicAPI.Shipped.txt).

Phase 4A adds seven passing cases to the unchanged 321-case baseline: 328 total.
It did not migrate the runtime or enable a model. The separately approved Phase
4B implementation below preserves the deterministic verification and publication gates.

## 17. PHASE 4B: Agent Framework with deterministic simulated agents

### Purpose and topology

The SDK now owns step execution, conditional transitions and revision back-edges.
The [FrameworkWorkflowRunner](../../src/GameGround.AgentFramework/FrameworkWorkflowRunner.cs)
constructs a fresh `WorkflowBuilder` graph for each run; it does not invoke
`LocalOrchestrator`. The latter is unchanged and remains the behavioral oracle
and explicit rollback path. No CLI default or other existing production entry
point is switched implicitly.

```text
AgentContext
  -> initialize
  -> analyst
  -> analyst-verification
       -> rejected, revision available -> analyst
       -> approved, next analyst       -> analyst
       -> approved observations        -> narrator
       -> empty observations / exhausted / failed -> publication decision
  -> narrator
  -> claim-verification
       -> rejected, revision available -> narrator
       -> verified / exhausted / failed -> publication decision
```

These are six explicit executors: initialization, Analyst invocation, Analyst
verification, Narrator invocation, claim verification and publication decision.
All are typed `Executor<TInput,TOutput>` stages. Context initialization accepts
`AgentContext`; internal stages exchange an immutable
[WorkflowFrame](../../src/GameGround.AgentFramework/WorkflowFrame.cs), not dictionaries
or chat history. The publication executor emits the existing `SharedState`.

Frames carry the shared state, current analyst index, attempt, proposed batch,
previous batch, verification feedback, revision reasons and route. Each
execution has its own journal containing only the latest immutable frame, so
the host can construct a diagnostic cancellation result after SDK termination.
It is not a cross-run store or a replacement for authoritative context.

### Agent inputs and deterministic gates

Analysts use the existing `ILocalAgent` contract, including `SimulatedAnalyst`,
with bounded context. `EvidenceQuery` remains available only over that context;
the runtime has no data-loader, file, raw event, network or model tool.

The runtime-specific
[NarrativeRequest / INarrativeAgent](../../src/GameGround.AgentFramework/NarrativeAgent.cs)
deliberately expose only verified observations and revision feedback, without
AgentContext or query capability. `SimulatedNarrativeAgent` copies the approved
claims with its own identity and proposed status, matching Phase 3 simulated
narration. This adapter does not alter or replace the existing SimulatedNarrator.
Failed proposals can be returned to their producer for revision; they are not
approved handoff data.

[WorkflowBatchGate](../../src/GameGround.AgentFramework/WorkflowBatchGate.cs) invokes
the existing deterministic `ClaimVerifier` at both verification stages. It also
preserves the application policies previously enforced by LocalOrchestrator:

- Initialized batches, correct producer, unique batch claim IDs and nonempty
  narration when observations exist.
- No additional narrative meaning beyond approved observations, even when a
  different fact would independently pass ClaimVerifier.
- Whole-batch acceptance, not publication of the valid subset of a rejected batch.
- Semantic deduplication within batches and across analysts, preserving the
  first configured producer.
- At most `MaxRevisions + 1` calls per stage, with explicit replacement-batch
  feedback and history.

The protected orchestrator's private comparison/policy code cannot be extracted
without changing it. The runtime therefore contains an equivalent local policy,
with oracle parity tests guarding against drift. ClaimVerifier itself is reused,
not reimplemented or weakened.

Rejections retain VerificationAttempt entries containing the producer, attempted
claim, result and reason. Malformed batches have explicit history and retain
available affected claim IDs; null/default batches have no valid claim to verify.
Rejected, failed and cancelled states have no NarrativeClaims. Approved
observations retained in a failed state are diagnostic, not a partial publication.
An empty selection completes with an explicit Narrator-skipped decision.

The publication executor alone completes a nonempty narrative, requiring the
whole batch to be verified. The host accepts exactly one typed SharedState from
that executor. An ended stream, output from another executor or an unexpected
output type is not publication success. Intermediate SDK events are not forwarded
as public narratives.

### Cancellation, failure and timeout

The Phase 4A cancellation finding above remains applicable.
[StreamingWorkflowHost](../../src/GameGround.AgentFramework/StreamingWorkflowHost.cs)
checks for pre-cancellation before starting the SDK. It registers a caller-token
signal, observes the stream, and explicitly awaits `StreamingRun.CancelRunAsync`
when cancellation wins. It then awaits observation termination and disposes the
run. The runner reports `Cancelled`, not `Completed`, including when cancellation
races a final output.

Cancellation registration only signals a task; it does not run async disposal in
a token callback. SDK error events propagate their exceptions; unexpected bugs
are not disguised as domain rejections or success. Missing trusted terminal
output fails explicitly.

The existing default one-revision/five-second policy and option bounds remain.
Each agent invocation uses a linked token and bounded wait. A timeout cancels the
agent token, records Failed/Timeout, does not retry, and never makes late output
available to the graph. Known AgentExecutionException failures are explicit.
Cooperative SDK cancellation is awaited; arbitrary noncooperative in-process
agent work cannot be forcibly killed. Such work has no journal/publication
access, and its late result is not consumed.

### Equivalence and tests

Phase 4B adds 35 offline cases: 32 runtime/gate cases and three isolated host
cases. Together with 321 unchanged Phase 1-3 cases and seven unchanged Phase 4A
cases, the solution has 363 passing cases.

Coverage includes:

- Happy path and only-approved Narrator inputs.
- Rejection before Narrator, unsupported narrative, incorrect numbers, whole-batch
  rejection and independently valid facts not authorized by the handoff.
- Revisions and bounded exhaustion for both roles, malformed output, empty
  narration, semantic deduplication, known failures and propagated programming bugs.
- Equivalent status, ordered observations, narrative claims and verification
  attempts against LocalOrchestrator, including every reference prefix 0-20 and
  rejected-then-corrected claims. Internal SDK event ordering is not compared.
- Full-window versus cutoff-8 output, bounded fact/evidence queries, rejection of
  a future-context proposal, repeated and concurrent context isolation.
- Pre-cancelled caller/host, coordinated cancellation during Analyst and Narrator,
  runtime reuse after cancellation, timeout without retry, and late output after
  timeout having no effect on publication or a subsequent run.
- Ended streams without a trusted output, and output with the wrong type, failing
  closed instead of yielding successful workflow results.

The test project alone references GameGround.Data and copies the unchanged
synthetic fixture for loading tests. No runtime data-loader dependency is added.
An internal friend-assembly declaration permits direct cancellation-host tests;
it is not a reverse project reference.

```text
GameGround.AgentFramework.Tests -> GameGround.AgentFramework
  -> GameGround.Agents -> GameGround.Intelligence -> GameGround.Core
GameGround.AgentFramework.Tests -> GameGround.Data -> GameGround.Core
```

No package version changes are required. The stable 1.25.0 SDK remains confined
to the new runtime and its tests. Core, Intelligence, Agents, their contracts and
original tests remain unchanged.

### Intentionally unimplemented

No AIAgent/ChatClientAgent is instantiated by the Phase 4B runtime. There is no
real model, Foundry, Azure credential/resource, free-language narration,
frontend, HTTP endpoint, deployment, durable checkpoint store or demo telemetry.
The Phase 4A scripted agent API tests remain separate from this workflow.
Deterministic simulated behavior isolates orchestration correctness from model
quality. Phase 4B stops here; Phase 4C requires separate authorization.

## 18. PHASE 4C: opt-in model-backed Analyst, simulated Narrator

### Real model boundary and unchanged workflow

[ModelBackedAnalyst](../../src/GameGround.AgentFramework/ModelBackedAnalyst.cs)
implements the existing `ILocalAgent` boundary. It can be passed directly to the
unchanged Phase 4B FrameworkWorkflowRunner:

```csharp
using var model = LocalAnalystModel.FromEnvironment();
var evaluations = new System.Collections.Concurrent.ConcurrentQueue<AnalystEvaluation>();
var analyst = new ModelBackedAnalyst(model, evaluations.Enqueue);
var runner = new FrameworkWorkflowRunner([analyst], new SimulatedNarrativeAgent());
SharedState state = await runner.RunAsync(context, cancellationToken);
```

This is explicit opt-in composition, not a production entry-point change.
Constructing the provider does not call it; executing this composition does.
Dispose the model only after the workflow/model calls have ended. The original
SimulatedAnalyst, LocalOrchestrator and all Phase 4B graph, frame, gate, host and
publication implementations are unchanged.

```text
Synthetic events -> deterministic intelligence -> bounded context
  -> ModelBackedAnalyst -> IAnalystModel -> ChatClientAgent / local model
  -> unchanged deterministic Analyst gate
  -> unchanged simulated Narrator
  -> unchanged claim verification and publication gate
```

No real Narrator is introduced. A model never owns statistics, verification,
routing, revision limits, authorization or publication.

### Provider-independent typed abstraction

[IAnalystModel](../../src/GameGround.AgentFramework/AnalystModelContracts.cs) exposes
`AnalyzeAsync(AnalystPrompt, CancellationToken)` and a provider/model/version
identity. Its result is `AnalystModelOutput`, containing typed observations:

- Positive, batch-unique observation ID.
- Type: Metric or RecordedMoment.
- Canonical statement selected from supplied allowed observations.
- Match ID and content identity.
- Referenced fact IDs, evidence IDs and exact covered source event IDs.
- Nullable moment ID.

The output does not repeat metric values, ratios or confidence estimates.
The adapter obtains an assertion's metric payload from the referenced
authoritative fact. It does not calculate a new value. The model's statement,
IDs and coverage are preserved, not replaced with corrected references or
canonical text. A wrong number in the statement therefore fails verification.
Wrong match/content IDs remain wrong in the proposed Claim and are rejected.

Host-owned producer identity, scoped evidence wrappers and Proposed status are
constructed at the boundary; the model cannot set Verified status.

### Bounded input and prompt version

[AnalystSystemPrompt](../../src/GameGround.AgentFramework/AnalystSystemPrompt.cs)
has version `gameground-analyst-v1`. It explicitly requires synthetic-only
evidence, no invented statistics/IDs, no unsupported tactical inference or future
sources, and an empty result instead of guessing.

The prompt snapshot is built through bounded EvidenceQuery operations:

- Facts and evidence for the current context.
- Available sequences and moments only.
- Allowed canonical metric/moment observations derived from those facts.
- Match/dataset/rules/window/content identity, attempt and rejection reasons.

No MatchDataset, raw event collection, filesystem, endpoint configuration or
credential is serialized into model input. This is a bounded snapshot equivalent
to the existing query operations, not model-accessible dynamic tools. The model
has no operation that can widen its window or change its match.

The first model version selects and orders supported observations. It cannot
publish a new free-language explanation or establish tactical/pattern causality.
Selecting several related facts does not create a new verified pattern metric.
This restriction deliberately preserves the current closed-wording verifier.

### Structured-output enforcement and deterministic gates

[ChatClientAnalystModel](../../src/GameGround.AgentFramework/ChatClientAnalystModel.cs)
uses the real `AIAgent`/`ChatClientAgent`, a fresh AgentSession per call, and
`RunAsync<AnalystModelOutput>` with `AgentResponse<T>.Result`. The JSON-schema
format is generated by Agent Framework; no prose regex parsing is used.

Serializer options require declared fields, disallow unknown and duplicate
properties, and respect nullable annotations. Null/scalar roots, malformed
JSON, invalid batches, unknown types and incomplete finish reasons fail
explicitly. A syntactically invalid response becomes AgentExecutionException,
which the existing workflow records as Failed/AgentFailure; it is not repaired
or converted into an empty successful response.

For a well-shaped response, the adapter returns proposed Claims even if they
have false references or statements. The unchanged WorkflowBatchGate invokes
ClaimVerifier and records Rejected results. Only wholly verified batches
proceed. Semantic rejections participate in the existing bounded revision flow;
the prompt includes deterministic rejection reasons on subsequent attempts.
Structural/provider failures terminate without an automatic model retry.

An explicitly empty observation array is a valid selection and causes the
existing Narrator-skipped path, without an invented fallback.

### Local provider and configuration

The chosen provider is an OpenAI-compatible **local loopback endpoint**, not
OpenAI's hosted service or Microsoft Foundry. There was no live model call in
this phase, by user choice; no model weights or model server were installed.

The stable `Microsoft.Extensions.AI.OpenAI` 10.10.1 package supplies the supported
`IChatClient` connector (`OpenAI.Chat.ChatClient.AsIChatClient`). It resolves
OpenAI 2.14.0 and System.ClientModel 1.15.0 transitively. The two Agent Framework
packages stay pinned at 1.25.0. No Azure or Foundry package/resource is introduced.

[LocalAnalystModel](../../src/GameGround.AgentFramework/LocalAnalystModel.cs) reads:

| Variable | Requirement |
|---|---|
| GAMEGROUND_ANALYST_ENDPOINT | Required explicit HTTP(S) loopback base URL, normally ending in `/v1`. |
| GAMEGROUND_ANALYST_MODEL | Required locally served model name. |
| GAMEGROUND_ANALYST_MODEL_VERSION | Optional declared revision; metadata says `unreported` if absent. |
| GAMEGROUND_ANALYST_API_KEY | Optional local-server credential; never persisted or logged. |

For example, configure the endpoint/model in the launching shell, not in
source-controlled files:

```powershell
$env:GAMEGROUND_ANALYST_ENDPOINT = "http://127.0.0.1:1234/v1"
$env:GAMEGROUND_ANALYST_MODEL = "your-locally-served-model"
```

Missing configuration, non-loopback endpoints, embedded URL credentials, query
strings and fragments fail before inference. The SDK requires a key object, so
an unauthenticated local server receives the explicit non-secret `local-no-auth`
placeholder when no key is configured. There is no cloud endpoint/credential
discovery or automatic provider fallback. Redirects and proxy use are disabled;
provider retries are zero, leaving revision decisions to the workflow.

Transport/provider failures are recorded with sanitized messages, without
exception response bodies or keys. Request timeout is distinct from caller
cancellation. The unchanged Phase 4B timeout and explicit StreamingRun
cancellation bridge remain authoritative. Default workflow timeout remains five
seconds; suitability for a particular local model is not claimed or measured.

### Evaluation records, not public narration

An optional `Action<AnalystEvaluation>` sink receives a record per invoked model
attempt containing provider/model/declared version, prompt version, context and
analysis window, input evidence IDs, available output observation IDs,
per-observation deterministic verification status/reason, invocation outcome and
latency. Supply a thread-safe sink when running concurrently.

The adapter invokes the same ClaimVerifier for these evaluation checks, but
never approves publication: Proposed in evaluation means only that the
prechecks passed. Final whole-batch verification/history/publication status
comes from the unchanged workflow SharedState. A timed-out model may record
Cancelled when its token is cancelled, while SharedState correctly records
Failed/Timeout. Correlate records by context and attempt.

No evaluation exporter, logging backend or prompt/response persistence is added.
The sink is caller-owned and optional; without it no durable evaluation record
is retained. Records omit prompts, response text, endpoint URLs and credentials.
Provider/model/version values are declared configuration, not cryptographically
verified server provenance. An absent version is never fabricated.

### Offline tests and limitations

46 new Phase 4C cases run without a server, network or credentials. They cover
valid selections, bad evidence/facts/match/content, cutoff-8 future references,
unsupported xG/speed/distance/dominance, numeric hallucinations, atomic batches,
empty selections, revisions, malformed JSON/schema, duplicate fields/IDs,
incomplete responses, fresh sessions, cancellation, timeout and configuration.

Deterministic fake selections match Phase 4B observation types, evidence/source
references, verification status and publication status for every reference
prefix 0-20. Natural-language equality is not the comparison criterion.

One additional protocol test uses the real OpenAI connector and an in-memory
HttpMessageHandler, asserting the `/v1/chat/completions` request and JSON-schema
format and returning a synthetic protocol response. This validates transport
serialization without network traffic; it is not live model validation.

The existing 363 cases remain unchanged, for 409 cases total. No credentials or
credential files are created. Core, Intelligence, Agents, dataset, LocalOrchestrator,
simulated agents and all Phase 4B runtime implementations remain unchanged.

Known limitations:

- A real local server/model must support the required structured-output format;
  OpenAI compatibility alone does not guarantee JSON-schema support.
- No live inference, latency, model quality or model-version accuracy is verified.
- Narration remains simulated and canonical; no free-language factual entailment
  verifier, new football metric or causal pattern detector is introduced.
- In-process cancellation remains cooperative and cannot forcibly kill arbitrary
  provider work. Late results have no publication route.

This phase introduces **no Microsoft Foundry or Azure hosting**, no production
entry-point switch, frontend, deployment, real Narrator or demo instrumentation.
Phase 4C stops here.

## 19. Phase 4D - model-backed Narrator, unchanged publication authority

Phase 4D adds an explicitly selected local-model Narrator at the existing
`INarrativeAgent` constructor seam. It does not change the typed workflow,
Analyst implementation, simulated agents, `LocalOrchestrator` oracle,
`WorkflowFrame`, `WorkflowBatchGate`, `StreamingWorkflowHost` or ClaimVerifier.
The CLI and other existing entry points still use their previous defaults.

```text
Deterministic intelligence -> Analyst -> unchanged Analyst verification gate
                                              |
                                    approved observations only
                                              |
                                  ModelBackedNarrator / INarratorModel
                                              |
                                 typed ordered narrative claim drafts
                                              |
                                  deterministic NarratorGroundingGate
                                    |                   |
                             proposed Claims     one bounded revision
                                    |             (safe error codes only)
                         unchanged ClaimVerifier + WorkflowBatchGate
                                    |
                       existing single trusted publication executor
```

### Narrow contracts, stable IDs and verifiable narration

[NarratorModelContracts](../../src/GameGround.AgentFramework/NarratorModelContracts.cs)
defines the typed `INarratorModel`, request, response, limits and evaluation
records. A small dedicated task interface is necessary: `IAnalystModel` accepts
facts, evidence, sequences and moments that must **not** reach the Narrator.
Both real adapters reuse the existing provider-neutral `IChatClient`, installed
OpenAI connector and Agent Framework structured-output API. There is no second
cloud-provider abstraction, package addition or replacement SDK.

[ModelBackedNarrator](../../src/GameGround.AgentFramework/ModelBackedNarrator.cs)
checks the handoff is bounded, synthetic, verified, Analyst-produced and scoped
to one context. Its projected model request contains only:

- Match ID, content identity and inclusive sequence window.
- Approved observation text, existing claim category and typed numeric payload.
- Host-assigned stable narrative claim ID and composite observation reference.
- Prompt version, attempt and bounded deterministic feedback on revision.

The composite reference is `(AgentId, ClaimId)`: claim IDs alone are not unique
across multiple Analysts. Narrative IDs are assigned from the approved handoff
order, starting at one, and must remain tied to the same observation even if the
model reorders or selects a subset. The response array determines recap order;
joining verified narrative texts in this order is deterministic.

The model receives no raw JSON dataset, events, source-event IDs, EvidenceQuery,
filesystem, previous rejected text, unrestricted context or provider settings.
Provenance stays host-side and is inherited only from the referenced approved
observation after validation. Invalid output is never silently repaired.

Each draft requires ID, text, supporting references, category, match/window
scope and nullable `MetricAssertion`. The existing domain `Claim`, `ClaimType`
and `MetricAssertion` are reused. In this version **exactly one** supporting
observation is permitted per claim; composite assertions are not verifiable by
the existing final contract. Repeated references, including reuse across
different claims, are rejected along with unknown/unapproved references.

[NarratorGroundingGate](../../src/GameGround.AgentFramework/NarratorGroundingGate.cs)
requires exact canonical approved wording and exact numeric payload, category
and scope. It rejects missing/duplicate/unstable claim IDs, null/empty output,
unsupported scope, added numbers, teams, players, chronology, outcomes and
tactical/causal language. This is a deliberately closed claim grammar, **not**
a claim that arbitrary natural-language truth can be established by string
matching. Free paraphrasing, connective factual prose and combined claims fail
closed until a richer verifiable contract is designed.

Only validated drafts become **Proposed**, never Verified, domain Claims.
The unchanged final verifier rechecks their full host-side facts, evidence,
source-event coverage, numeric values, context and wording. The unchanged batch
policy independently requires the same meaning as an approved observation.
No adapter or model publishes directly.

### Structured output, failure, revision and cancellation

[ChatClientNarratorModel](../../src/GameGround.AgentFramework/ChatClientNarratorModel.cs)
uses a fresh `ChatClientAgent`/session per invocation and
`RunAsync<NarratorModelOutput>` with `AgentResponse<T>.Result`. The generated
JSON schema is requested through the working installed SDK. Strict JSON
deserialization requires constructor fields (including nested numeric fields),
disallows unknown/duplicate properties and integer/unknown claim categories,
and checks nonnull object roots and an explicit normal `stop` finish reason.
Valid JSON alone is not factual approval.

[NarratorSystemPrompt](../../src/GameGround.AgentFramework/NarratorSystemPrompt.cs)
is versioned `gameground-narrator-v1`. It specifies synthetic-only narration,
approved-only selection/order, canonical wording, stable references and no
invented statistics, entities, chronology, outcomes, causality or unsupported
football concepts. No tools are attached.

Structural/grounding rejection gets **at most one adapter-local revision**.
The replacement response is a complete batch; no accepted subset is published.
Feedback contains only enum error codes and, when known, host-approved narrative
claim IDs. Rejected response text, foreign IDs and arbitrary reason strings are
not forwarded. A second failed validation raises `AgentExecutionException`;
the unchanged workflow records Failed/AgentFailure with no narrative publication.
Transport/provider errors fail immediately, without revision.

This local revision budget avoids manufacturing invalid domain Claims for errors
such as an unknown observation reference. It does not add workflow edges or
weaken gates. The unchanged graph still owns scheduling, agent deadline,
cancellation and publication. The adapter refuses an outer Narrator attempt
above zero, so even a graph configured with ten revisions cannot multiply its
two-call maximum. Analyst and simulated-agent revision policies remain unchanged.
The local revision budget is independent of the graph's `MaxRevisions`; setting
that option to zero does not disable the model adapter's single model revision.

Limits are explicit and tested at their thresholds:

| Boundary | Maximum |
|---|---:|
| Approved observations / output claims | 128 each |
| Canonical text per claim | 1,024 .NET string characters |
| Identity/configuration label | 256 .NET string characters |
| Serialized request | 131,072 UTF-8 bytes |
| Structured response text | 65,536 UTF-8 bytes |
| Local HTTP response envelope | 262,144 bytes |
| Requested completion token budget | 8,192 tokens |

[NarratorResponseLimitHandler](../../src/GameGround.AgentFramework/NarratorResponseLimitHandler.cs)
caps both declared and actually read HTTP body bytes before the OpenAI SDK
buffers/deserializes a local-provider response. Oversized/error responses are
disposed. The text byte cap is checked before typed result deserialization.
The transport cap applies to the provided local factory; a caller injecting an
arbitrary `IChatClient` is responsible for its own underlying transport cap.

The configured per-model-call timeout cancels the provider token and stops
waiting even for an uncooperative client. The unchanged workflow's agent timeout
covers the entire invocation, including revision, and can be shorter. Caller
cancellation remains distinct from provider timeout. Late output is checked
against cancellation and has no publication route; arbitrary in-process provider
work cannot be forcibly killed.

### Explicit local configuration, no cloud fallback

[LocalNarratorModel](../../src/GameGround.AgentFramework/LocalNarratorModel.cs)
is opt-in and reads configuration only when explicitly constructed:

| Variable | Requirement |
|---|---|
| GAMEGROUND_NARRATOR_ENDPOINT | Required HTTP(S) loopback base URL, normally `http://127.0.0.1:1234/v1`. |
| GAMEGROUND_NARRATOR_MODEL | Required locally served model name, at most 256 characters. |
| GAMEGROUND_NARRATOR_MODEL_VERSION | Optional declared model revision; `unreported` when absent. |
| GAMEGROUND_NARRATOR_API_KEY | Optional local-server credential; never logged, serialized into a prompt or persisted. |
| GAMEGROUND_NARRATOR_TIMEOUT_SECONDS | Optional positive per-call timeout up to 300 seconds, invariant-culture number; default 5. |

The factory follows the existing Analyst's loopback connector restrictions,
without modifying the protected Analyst files. It adds a bounded response
transport and Narrator deadline. Missing/unsafe configuration fails clearly
before inference. Embedded URL credentials, queries, fragments and non-loopback
endpoints are rejected. Proxies, redirects and provider retries are disabled;
there is no hosted endpoint discovery or fallback. An unauthenticated local
server receives only the non-secret `local-no-auth` SDK placeholder.

An explicit composition at the existing seam is sufficient:

```csharp
using var model = LocalNarratorModel.FromEnvironment();
var runner = new FrameworkWorkflowRunner(
    [new SimulatedAnalyst()], new ModelBackedNarrator(model));
// A model-backed Analyst can also be supplied through the same unchanged seam.
var state = await runner.RunAsync(boundedContext, cancellationToken);
```

This is opt-in library composition, not a new executable mode or a change to
production entry points. Keep the adapter alive until the workflow and any
caller-owned provider cleanup complete. The per-call deadline and whole-agent
deadline should be selected explicitly for any future authorized live test;
no live model latency or quality has been measured here.

### Evaluation and offline regression evidence

An optional `Action<NarratorEvaluation>` receives provider/model/declared
version, prompt version, match/content/window, attempt, input observation
references, bounded output claim IDs, deterministic rejection codes, outcome
and elapsed time. The sink must be thread-safe if shared across workflows.
`Proposed` means pre-gate success, **not** final verification or publication;
correlate by scope with unchanged SharedState verifications/status for the
authoritative result. Failed/Cancelled records cannot authorize publication.
Provider failures use sanitized errors without response bodies or credentials.
No prompt/response persistence, exporter or new telemetry backend is added.

Tests use deterministic `INarratorModel` fakes, scripted `IChatClient` instances,
and the real OpenAI connector over an **in-memory** HTTP handler. No standard
test requires a local model server, model key, internet, Azure or Foundry.
They cover valid ordered/subset-compatible claims, malformed/missing output,
reference/ID collisions, numeric/scope/entity/chronology rejection, atomic
revision, both model-backed stages together, provider and read errors, exact
size thresholds, cancellation/timeouts, late output and concurrent isolation.
Reference prefixes 0-20 are compared with the unchanged LocalOrchestrator for
types, evidence/source references, numeric/moment payload, verification and
publication status. An empty Analyst result still skips the Narrator.

The pre-edit baseline was 409 passed, zero failed/skipped, with clean build and
format verification. Phase 4D adds 118 offline cases, for **527 passed, zero
failed, zero skipped** across the complete solution. Release build has zero
warnings/errors and whole-solution formatting verification passes. All 62
pre-existing source/test/data files in the baseline hash manifest are unchanged.
The reference dataset SHA-256 remains
`5BE8CCAEC5169D61F47AC184B59BD22060B12DC4AEE6A61A542B657871218B77`.
No NuGet dependencies or version declarations change in this phase.

Known limitations:

- This is real model plumbing, not authorization or evidence of live model
  correctness. No live inference, model download or server startup occurred.
- The selected local model must implement the requested JSON-schema format.
- Canonical selection/ordering is supported, not unconstrained prose generation,
  compound factual claims, new football calculations or causal inference.
- Configured provider/model/version labels are declarations, not attested
  server identity. No specific local model weights are prescribed.
- Evaluation prechecks and final workflow verification have distinct authority.
  An optional failing evaluation sink propagates an error, not success.
- Cancellation is cooperative for underlying work; ignored late results do not
  gain a route to publication.

Phase 4D introduces **no Microsoft Foundry, Azure hosting, frontend, deployment,
authentication, billing or demo recording**. All pre-existing runtime and test
files and the synthetic reference dataset remain protected and unchanged.
Phase 4D itself stopped at this boundary; the following section documents the
separately authorized Phase 4E evaluation infrastructure.

## 20. Phase 4E - explicit local-model evaluation

Phase 4E provides a separate CLI evaluation operation. The existing CLI
`validate`/`replay` commands, simulated adapters and all standard tests remain
offline. The live operation is **never run by build or test**:

```text
validated synthetic match-001 fixture
       -> deterministic AgentContextFactory / intelligence
       -> ModelBackedAnalyst
       -> existing Analyst ClaimVerifier + WorkflowBatchGate
       -> approved observations only
       -> ModelBackedNarrator
       -> existing deterministic narrative/final publication gates
       -> bounded JSON metadata report
```

No integration change to protected files or oracle is needed:
`FrameworkWorkflowRunner` already accepts an `ILocalAgent` Analyst array and an
`INarrativeAgent` Narrator. `ModelBackedAnalyst` implements the former;
`ModelBackedNarrator` implements the latter. The explicit command constructs
both concrete local adapters and feeds them through this existing seam. It
does not duplicate the deterministic pipeline or invoke `LocalOrchestrator`.

### Invoke live evaluation explicitly

The command first requires configuration for **both** stages, validates both
endpoint URLs as loopback HTTP(S) without userinfo/query/fragment, constructs
both clients, loads/validates the specified dataset and verifies the exact
synthetic fixture SHA-256 and 1-1 replay score before any model request. Client
construction itself performs no network request. A connection error during
inference produces a failed, non-published report with a sanitized
`analyst_local_endpoint_unavailable` or `narrator_local_endpoint_unavailable`
reason; it is not treated as a successful evaluation.

Use the Phase 4C/4D environment variables unchanged:

```powershell
$env:GAMEGROUND_ANALYST_ENDPOINT = "http://127.0.0.1:1234/v1"
$env:GAMEGROUND_ANALYST_MODEL = "<locally-served-model>"
$env:GAMEGROUND_NARRATOR_ENDPOINT = "http://127.0.0.1:1234/v1"
$env:GAMEGROUND_NARRATOR_MODEL = "<locally-served-model>"
# Optional, only if this local server requires them:
# GAMEGROUND_ANALYST_API_KEY
# GAMEGROUND_NARRATOR_API_KEY
# GAMEGROUND_ANALYST_MODEL_VERSION
# GAMEGROUND_NARRATOR_MODEL_VERSION
# GAMEGROUND_NARRATOR_TIMEOUT_SECONDS

dotnet run --project tools\GameGround.Cli -- evaluate-models `
  data\synthetic\match-001.json `
  "$env:TEMP\gameground-phase4e-evaluation.json"
```

The command is the only live entry point. This task did not execute it because
none of the required Analyst or Narrator endpoint/model settings are present
and no local server was configured. No endpoint was contacted. No model was
downloaded or started.

### Report and evaluation policy

[ModelEvaluationRunner](../../src/GameGround.AgentFramework/ModelEvaluationRunner.cs)
reuses `DatasetLoader`, fixture replay, `AgentContextFactory`,
`FrameworkWorkflowRunner`, the model-backed adapters and all unchanged
deterministic gates. The output report contains bounded metadata only:

- configured provider/model/version labels and loopback scheme/host/port identity, with
  no API keys, authorization headers, raw prompts or model response text;
- Analyst and Narrator prompt versions;
- total pipeline/command duration and summed per-agent model-call latency;
- call success/failure and malformed/incomplete response counts;
- proposed, approved, revised and rejected observation/claim counts;
- invalid evidence/observation reference counts;
- workflow status, publication outcome and a finite sanitized
  non-publication/failure category;
- dataset ID, match ID, fixture SHA-256, expected and replayed score;
- mechanical checks for Analyst numeric verification, approved-only Narrator
  input, published claim provenance and numeric parity.

`SuccessfulCalls` counts a call whose output was returned and typed but may
still have failed grounding; malformed structured responses and provider,
transport, timeout or cancellation failures count as failed calls. Rejected
claims/observations are reported separately from call transport/format success.

The final score expectation is Harbor FC 1-1 Meadow United. Only the exact,
synthetic `match-001` content hash documented in prior phases is eligible.
Client/provider errors are converted to bounded categories before report
persistence. The report does not contain narrative claim strings or arbitrary
exception messages. The selected report path is supplied explicitly by the
caller; choose a private/ignored path. No report from a real model call exists
for this phase.

Mechanical verification demonstrates provenance, canonical numerical
consistency and gate execution only. The current closed wording contract cannot
establish general semantic or qualitative narrative correctness. The report
states this limitation and does not grade a recap as correct solely because the
JSON is valid or the deterministic gates pass.

Offline tests exercise the same evaluator with scripted model implementations:
success/publication, Analyst rejection blocking the Narrator, Narrator-reference
rejection/revision, malformed output, provider failure, cancellation, timeout,
fixture hash rejection, configuration preflight and rejection of a remote
endpoint. They make no HTTP/network calls. The existing connector protocol
tests continue to use in-memory HTTP.

The complete solution currently has **541 passing tests, zero failures and zero
skips**, including 14 new offline evaluation-runner tests. Release build
succeeds with zero warnings/errors, and whole-solution formatting verification
passes. The dataset hash and protected `LocalOrchestrator`, Core, Intelligence
and deterministic verification files are unchanged. No package version or
dependency was added.

### Phase 4E status and boundaries

- Offline evaluation infrastructure and report tests are validated by the
  standard suite.
- **Live evaluation: NOT RUN.** No Analyst or Narrator model identity,
  endpoint, latency, success/rejection counts or live publication outcome can
  be reported until the explicit command is run with a real local server.
- The current environment has no required model environment settings, so
  configured model/endpoint identities and live-call/rejection counts are
  unavailable.
- The fixture SHA-256 remains
  `5BE8CCAEC5169D61F47AC184B59BD22060B12DC4AEE6A61A542B657871218B77`.
- No new NuGet dependency, cloud fallback, Foundry, Azure deployment,
  frontend, authentication, billing or demo instrumentation is introduced.

Next step: provision/launch a compatible local OpenAI-style server and model
outside this command, configure both stage identities using the existing
variables, then explicitly run the command above. Inspect the resulting
bounded report before drawing any claim about model quality. Phase 4E stops
here.
