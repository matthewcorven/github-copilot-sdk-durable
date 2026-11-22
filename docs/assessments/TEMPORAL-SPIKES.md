# Spike results — integrating surfaces and delivery viability

Four engineering spikes, run against the real stacks, to test the claims in
[`TEMPORAL-CONTRIBUTION-ASSESSMENT.md`](TEMPORAL-CONTRIBUTION-ASSESSMENT.md). Each spike was asked a
narrow question, was required to separate **already supported** from **needs work**, and had to
produce measurements rather than recommendations.

The headline: **three of the four candidate contributions changed shape under test.** One was
falsified outright, one was already implemented upstream, one was downgraded from "highest priority"
to a metadata fix, and one turned into a *bug fix* with a much lower bar. Only the local-activity
work survived as originally framed — and even that must be a two-plugin change.

---

## 0. Method and environment

| | |
|---|---|
| Upstream | `temporalio/ai-integrations` @ `7607956` (2026-11-20), sparse clone |
| SDK | `temporalio/sdk-dotnet` @ `0f47b6b` (2026-11-19) plus the published 1.20.0 / 1.22.0 packages |
| Runtime | **live** Temporal dev server (`temporalio/temporal:latest`, container `temporal-spike`), health `SERVING`, UI :8233 |
| Toolchain | .NET SDK 11.0.100 (10.0.103 and 10.0.404 also present) |
| Published versions used | `Microsoft.Agents.AI` **1.28.0**; `Temporalio` **1.20.0** and **1.22.0**; `Temporalio.Extensions.WorkflowStreams` **1.22.0** |
| Models | none. `FakeChatClient` / `ScriptedResponse` from the plugin's `Testing` package — no provider credentials, no live model calls |
| Write scope | `/tmp/spikes/*` only. No repository file, issue, PR, commit or spend |

Every number below is a measured value with the command that produced it. Where something could not
be measured it says so.

---

## 1. Verdict summary

| # | Candidate | Verdict after testing | Shape | Release |
|---|---|---|---|---|
| 1 | **Local Activity tools** | **Real, valuable, and must be opt-in.** 3.5× faster; 371 → 171 events | small code, **two plugins** | minor (opt-in) |
| 2 | **`Microsoft.Agents.AI` 1.28 window** | **No gap.** 398/398 green at 1.28.0 with zero source changes | patch (metadata) | patch |
| 3 | **External storage for long transcripts** | **Real, but blocked by the dependency floor** | code + **double bump** + docs | minor |
| 4 | **`TMPRL1101` diagnostics** | **Premise falsified.** The hazard never reaches that code path | needs redesign | — |
| 5 | **A different core gap (new)** | Empty *successful* completion is invisible to every diagnostic | design + test | — |
| 6 | **DI / hosting** | **Already supported**; the gap is a sample | docs | none |
| 7 | **Provider-error classification** | **A real bug** — unclassifiable errors retry forever | small code (fix) | patch/minor |
| 8 | **Approval binding** | **Already implemented and tested upstream** | no contribution | — |

---

## 2. Spike 1 — Local Activities as tool calls

**Question.** Can a tool call run as a Local Activity, and is that contribution real and shippable?

**Already supported.** `Workflow.ExecuteLocalActivityAsync` exists in `Temporalio` 1.20.0 with an
**identical** surface in 1.22.0 (10 overloads; `diff` of the member list is empty).
`LocalActivityOptions` carries `ScheduleToClose`, `ScheduleToStart`, `StartToClose`, `RetryPolicy`,
`CancellationType`, `CancellationToken`, `ActivityId`, `LocalRetryThreshold`,
`IncludeArgumentsInMarker`, `Summary`. 1.22.0 adds only `EventGroups`. The plugin has **zero**
`LocalActivity` references; `Internal/ActivityFunction.cs:85,94` schedules full Activities.

**Measured.** Same workflow, one tool, two model rounds, three implementations:

| | full Activity | Local Activity | user space |
|---|---|---|---|
| total events | **23** | **18** | 18 |
| `ActivityTaskScheduled` | 3 | 2 | 2 |
| `MarkerRecorded` | 0 | 1 | 1 |
| `WorkflowTaskCompleted` | 4 | 3 | 3 |
| tool result reached the model | `sunny in City0` | `sunny in City0` | `sunny in City0` |
| transported tool schema | identical | identical | identical |
| replay of own history | OK | OK | OK |

20 rounds × 2 tools:

| | full | local |
|---|---|---|
| events | **371** | **171** |
| `ActivityTaskScheduled` | 61 | 21 |
| `WorkflowTaskCompleted` | 62 | 22 |
| `MarkerRecorded` | 0 | 40 |
| median wall time (4 runs) | **2 465 ms** | **695 ms** (**3.5×**) |
| history bytes | 460 653 | 382 687 (−17 %) |

The byte saving is small because the transcript is the input to every model call (265 822 bytes in
both) — the Local Activity saves the 40 tool round trips and 40 workflow tasks, not the transcript.
That is worth stating, because it bounds the benefit.

**The blocking constraint.** Cross-variant replay fails in **both** directions:

```
REPLAY[full]: FAILED  [TMPRL1100] Nondeterminism error: No command scheduled for event
                      HistoryEvent(id: 11, ActivityTaskScheduled)
REPLAY[local]: FAILED [TMPRL1100] Nondeterminism error: Missing associated machine for LocalActivity(2)
```

So an ungated swap breaks every in-flight run. `AGENTS.md` agrees: *"A behavior change that can alter
recorded histories must be gated with a patch, or called out as breaking in the PR."* The design must
be a **per-tool opt-in defaulting to today's behaviour** — which needs no `Workflow.Patched` (the
plugin uses neither `Patched` nor `DeprecatePatch` today).

**Semantic deltas that must be documented.** Local Activities have no heartbeat; retry defaults to
*forever* and long backoff becomes a timer (`LocalRetryThreshold`, default 1 m); a crash before the
enclosing workflow task completes re-runs the tool (**at-least-once** — only safe for idempotent
tools); no task-queue routing, priority or versioning; the tool name appears **nowhere** in history
(`grep -c "get_weather_0" history-local.json` → 0), only bare `MarkerRecorded` rows, so `Summary` is
the only labelling hook. Observed failure mode: a deterministically failing tool under the default
policy spun workflow tasks until `TimedOut`.

**Delivery.** One new file (~180 lines) mirroring `ActivityAsTool.cs` + `ActivityFunction.cs`;
`AgentLoop` needs no change (`Internal/AgentLoop.cs:344-374` invokes any `AIFunction`). Tests:
exact-count assertions in the `SideEffectTests` style (tool is *not* an Activity; one marker; zero
`WorkflowTaskFailed`), a recorded history for `ReplayTests`, and a negative test that the default
still schedules a full Activity. **Two plugins**: `AGENTS.md` says *"Before you add a behavior that a
sibling plugin already has, read the sibling and follow it, or say in the PR why this plugin
differs."* The Python MAF plugin has the same gap (`activity_as_tool` is full-Activity only), so the
rule points at one option name in both — matching `use_local_activity` already used for **model**
calls in `python/openai_agents` and `typescript/openai-agents`.

**Verdict: viable, production-ready with the opt-in gate, and the highest-value item that survived.**

---

## 3. Spike 2 — the version window and external storage

### 3a. Is the `Microsoft.Agents.AI` range honest? **Yes.**

| step | result |
|---|---|
| restore as-is | resolves **1.26.0** (lock + `RestoreLockedMode`) |
| bump to 1.28.0 | csproj change only; `Microsoft.Extensions.AI` 10.10.0 → 10.11.0, `Microsoft.Agents.AI.Abstractions` 1.26.0 → 1.28.0 |
| build | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `PluginTests` | 38 / 38 |
| `AgentRunTests` | 56 / 56 |
| **full suite** | **398 / 398 passed** (1 m 17 s) |

So the declared `[1.26.0,2.0.0)` range is **honest**, and the earlier assessment's rank-1 item is a
**metadata fix**: `AgentFrameworkApiGuard.TestedVersion` still says `1.26.0`, so the guard's error
message and the README name a version the suite no longer represents.

The guard's protection is narrower than its name: it runs six
`Type.GetConstructor(Type[])` lookups on the history/context provider context types. It proves they
*exist*, not that they behave the same. It cannot see return types, property setters, enum values, or
anything in `Microsoft.Extensions.AI`. It converts "mid-run `MissingMethodException`" into a
construction-time non-retryable failure — useful, but not a contract.

### 3b. Does external storage compose? **Yes — but it needs the floor raised.**

Measured, with a 1.8 MB / 2 MB / 12 MB transcript:

| case | result |
|---|---|
| default converter, 1 800 000 B | `WARN [TMPRL1103] size=1800034 limit=524288` then **succeeded** |
| default converter, 2 097 000 B | succeeded (warn only) |
| default converter, 2 098 000 B | **`RpcException: Blob data size exceeds limit.`** |
| default converter, 2 700 000 B | same failure |
| `ExternalStorage`, 2 700 000 B | `[driver] stored 1 payload(s)`, **succeeded**, 2 files |
| `ExternalStorage`, 12 000 000 B | **succeeded** |

Two corrections to the README's framing: the 2 MiB figure is **exactly 2 097 152 bytes and is
enforced server-side**, not a client-side cap — and `PayloadLimitsOptions` is **warn-only**
(`PayloadsWarnSize` default 524 288, `MemoWarnSize` 2 048) with no error threshold.

The plugin never touches `DataConverter` (zero references in `src/`), so a user converter wins —
verified by running, not inferred. But `Temporalio.Converters.IStorageDriver` **does not exist in
1.20.0** (0 hits in the 1.20.0 `Temporalio.xml`; 43 hits in 1.22.0, including
`ExternalStorage.#ctor(IStorageDriver)`) — and `Temporalio.Extensions.WorkflowStreams` **1.22.0
declares a hard dependency on `Temporalio 1.22.0`**. So the recipe is a **coordinated double bump**,
and a consumer following the README today hits a *compile* error, not a runtime one. The suite is
green at 1.22.0 (398/398), so the bump has no regression evidence against it.

**Verdict: viable, but it is a dependency-floor change (minor release + migration note), not a docs
tweak.** A `PayloadCodec` + `ExternalStorage` combination was **not** tested; the docs state the
codec is not applied to references, so encryption-plus-offload is unverified.

---

## 4. Spike 3 — the diagnostics premise is falsified

This is the most important result of the four, because it inverts the earlier ranking.

**The claim tested:** `ConfigureAwait(false)` off the workflow scheduler produces a stall that ends
in `TMPRL1101` after 2 s, so enriching that message is the core contribution.

**What actually happens.** There are two distinct handoffs:

| shape | caught? | how |
|---|---|---|
| `Task.Delay(...).ConfigureAwait(false)` | **yes, in microseconds** | `WorkflowTracingEventListener.cs:192-201` → `InvalidWorkflowSchedulerException` **with a user frame** (`ThirdPartyLib.DelayConfigureAwaitFalseAsync … Program.cs:line 14`) |
| `Workflow.DelayAsync(...).ConfigureAwait(false)` — *the shape of `AIAgent.RunAsync`* | **no. Silent for 400 s** | `successful: {}` |

The 400-second run: 134 polls, `status=Running` throughout, and
`grep -ci "TMPRL1101|deadlock|fail:|error:"` → **0**. The trace log shows why:

```
trce: Sending workflow completion: { "runId": "01a1fcf9-…", "successful": { } }
```

An **empty successful completion**. The workflow task *succeeds*, so the 2-second deadlock timer never
fires — **enriching `TMPRL1101` would not touch the case that matters.** `TMPRL1101` only fires for a
hard block (reproduced: 2.0 s, `stack_trace` = `at Temporalio.Worker.WorkflowWorker.HandleActivationAsync`
— no user frame is reachable). `__stack_trace` also cannot answer during the stall
(`query timed out before a worker could process it`, twice) though it works on a healthy workflow.

**What works today.** The activity route: `RESULT=completed`, `REPLAY=ok`, `FINAL status=Completed`.
So the *fix* is already available to integrators; what is missing is detection and discoverability.

**What does not work.** A `DeterministicAwait` wrapper: every attempt that used `Task.Run` was
rejected by the listener, and disabling the listener does not help, because the failure is a
*successful* task with no result — nothing can retroactively repair it. `Workflow.Unsafe.SideEffect`
does not exist in .NET (`grep -rn SideEffect` → 0). `WorkflowTaskCompleted` events cannot distinguish
a stall from a normal wait (`failure=<none>`).

**Two findings that were not in the brief:**

1. **`TemporalCommunity.Extensions.Analyzers` 0.6.0 already ships `TEMP001`** —
   *"`ConfigureAwait(false)` leaves Temporal's workflow task scheduler"* — with a code fix. Verified
   on the exact repro: it fires for the in-workflow call site and produces **nothing** for the
   transitive helper (`TemporalWorkflowAnalyzer.cs:654-665` is a lexical ancestor check; the docs say
   it does not inspect external helper libraries). So `sdk-dotnet#576` is not greenfield — the
   remaining gap is precisely the transitive call-graph case, which is what `#576` itself calls "the
   key/hard part".
2. **The `__stack_trace` query path produced a malformed completion** in three runs:
   `Workflow completion failure: Lang SDK sent us a malformed workflow completion … legacy query
   response along with other commands. This is not allowed`. Reproduced in 3 of 3 attempts that
   issued the query during the stall; **not** independently attributed by the spike. This looks like
   a real SDK bug worth its own reproduction before any claim.

**Revised shape of the contribution:** a **detection sink** — an observer that flags "workflow task
completed, zero commands, workflow still open" — not a better message on a code path that never
executes. That is a design problem with no prototype yet, so it should not be ranked as ready work.

---

## 5. Spike 4 — hosting, classification, approvals

### 5a. Hosting: already supported; the gap is a sample

`Temporalio.Extensions.Hosting` 1.22 exposes `ConfigureOptions()` returning a public
`OptionsBuilder<TemporalWorkerServiceOptions>`, and `PostConfigure<IServiceProvider>` reaches the
container. Both wirings ran a workflow through `FakeChatClient` against the live server:

```csharp
.AddHostedTemporalWorker(TaskQueue)
.AddWorkflow<HostedAgentWorkflow>()
.ConfigureOptions()
.PostConfigure<IServiceProvider>((options, provider) =>
    options.Plugins = [provider.GetRequiredService<MicrosoftAgentFrameworkPlugin>()]);
```

Two undocumented traps worth putting in the sample: `.ConfigureOptions(o => { })` binds the wrong
overload (`CS1929`, `ChatClientBuilder.ConfigureOptions` wins), and
`AddHostedTemporalWorker(host, ns, queue)` registers no `ITemporalClient`. **Verdict: README section +
sample. Code not required** (an `AddMicrosoftAgentFrameworkPlugin()` helper is ergonomics only).

### 5b. Provider classification: a real bug, and therefore a low bar

26/26 table cases pass — every status shape (`HttpRequestException`, `Status`, `StatusCode`,
`HttpStatusCode`, `AggregateException` flattening), `x-should-retry` in both directions and both
casings, `Retry-After` as seconds, fraction, HTTP date (+30 s → 29.688 s), `retry-after-ms`, garbage,
and the 60 s cap. The classifier is well built.

The defect is the **null case**. An exception with no readable status returns `null`, propagates, and
— because the plugin sets **no** `RetryPolicy` (verified: zero hits in `src/`) — runs under the
server default of **unlimited attempts**:

```
nostatus: FAKE CALLS at T+30.0s = 5     (attempts at 0.2, 1.2, 3.2, 7.2, 15.2, 31.3 s)
          terminal: none — cancelled by hand
http400:  FAKE CALLS at T+5.0s = 1      nonRetryable=True
```

The plugin's own contract line reads: *"A provider error that the framework marks as final is a
non-retryable failure. Other errors are left to the retry policy."* A permanent 400 from a provider
whose exception shape the classifier cannot read is **not** made non-retryable — so the first
sentence fails even on its own terms, and the second presumes a policy that terminates. **Read as a
bug against the intent of the contract, which lowers the contribution bar from "feature" to "fix".**

Prototyped fix, and it works: a per-worker `StatusExtractor` registration point tried before the
reflection default (a static was the smallest diff; the right public shape is a property on
`MicrosoftAgentFrameworkPluginOptions`). With a user-supplied extractor, both the permanent and
throttled third-party errors classify correctly; an extractor returning `null` falls through.
`ModelErrorClassifier` is `internal`, so this is a public-surface decision and needs a version bump,
a changelog entry and a parity note for the sibling plugins. The recommended pair is the extractor
**plus a bounded default** for unclassified failures. A framework-side fix is not deliverable
(MAF core is 404).

### 5c. Approval binding: nothing to contribute

All four cases behave exactly as claimed:

| case | outcome |
|---|---|
| normal approval | tool ran once, result returned |
| rejection | tool did not run; model told `Tool call invocation rejected. not now` |
| tampered **arguments** | ran the surfaced call — `to=ops@example.com`, attacker sent `attacker@example.com` |
| tampered **name** | ran the surfaced call — `send_email`, attacker sent `get_weather/Kingston` |
| forged request id | workflow failed with `MicrosoftAgentFrameworkWorkflowError`, zero tool executions |

**And it is already tested upstream:** `ApprovalTests.cs:128`
`TamperedResponse_RunsTheCallTheSessionSurfaced` is a theory over `arguments` and `name`, and
`ApprovalTests.cs:158` covers the forged id. The independent reproduction matched. **No contribution
is needed** — the earlier assessment listed this as a candidate, and the spike retired it.

---

## 6. Revised ranking

The earlier assessment ranked the version-window check first and left diagnostics high. The evidence
reorders both.

| Rank | Contribution | Why it moved | Effort | Release | Production-ready when |
|---|---|---|---|---|---|
| **1** | **Provider-error retry-forever fix** (`StatusExtractor` + bounded default) | Reclassified from *feature* to *bug*: measured 5→6 attempts and climbing, no terminal state, against the plugin's own contract | S–M | patch/minor + changelog | extractor is per-worker, a null-returning extractor falls through, a bounded default is asserted, parity note for Python/TS |
| **2** | **Local Activity tools, both MAF plugins** | Only item that survived unchanged; 3.5× measured | M (two languages) | minor, opt-in | per-tool opt-in defaults off; exact-count test; recorded history replayed; the five semantic deltas documented; Python option shares the name |
| **3** | **External storage recipe + raise the floor to `Temporalio` 1.22.0** | Reclassified from *docs-only* to *dependency-floor change*: the type does not exist below 1.22.0 | S–M | minor + migration note | double bump (`Temporalio` + `WorkflowStreams`) with 398/398 green; README qualifies "2 MiB" as server-side and default-only; `IStorageDriver` recipe; codec interaction either tested or stated untested |
| **4** | **Hosting sample** | Confirmed already supported; the gap is a sample | S | none | README section using `PostConfigure<IServiceProvider>`, both traps documented |
| **5** | **`TestedVersion` → 1.28.0 (and consider narrowing the range)** | Downgraded from rank 1: 398/398 at 1.28.0, no source changes needed | S | patch | the literal matches what the suite proves; the guard's blind spots stated in the README |
| **6** | **Empty-completion detection sink** | New, from a falsified premise; no prototype | L | — | a reproduction that is *not* also a `stack_trace` query artifact; an observer design; a decision on whether it belongs in the SDK or the plugin |
| **7** | **`TMPRL1101` enrichment** | **Demoted**: the code path does not fire for the hazard it was proposed for | M | — | only if paired with (6), and then as a secondary improvement |
| — | **Approval binding** | **Retired**: already implemented and tested | — | — | — |
| — | **Analyzer (`#576`)** | Partly shipped as `TEMP001`; remaining gap is the transitive call graph | L | — | a call-graph prototype that does not produce false confidence |

---

## 7. Cross-cutting findings

1. **The integration surface is better than the roadmap assumed.** Four of eight candidates were
   *already supported*: hosting, approval binding, the version range, and the SDK's Local Activity API.
   The genuinely missing pieces are one bug fix and one opt-in feature.
2. **The dependency floor is a real adoption barrier.** `Temporalio` 1.20.0 predates `IStorageDriver`,
   so a documented escape hatch is unreachable for consumers. Raising the floor is a product decision,
   not a docs edit.
3. **The framework is not contributable.** `microsoft/agent-framework` — the URL in the package's own
   nuspec — returns 404 authenticated and unauthenticated, while `agent-framework-durable-extension`
   and `Agent-Framework-Samples` are public. Every "fix it upstream" path collapses to a plugin-side
   refusal, a pinned ceiling, or an issue nobody outside can file.
4. **Detection, not diagnosis, is the real core gap.** The silent failure is an *empty successful
   completion*, invisible to `TMPRL1101`, the tracing listener, `WorkflowTaskCompleted` and
   `__stack_trace`. Naming the call site is the wrong goal; noticing that the task completed without
   doing anything is the right one.
5. **Opt-in is mandatory for anything that changes history shape.** Cross-variant replay fails in both
   directions with `TMPRL1100`. The plugin has no `Workflow.Patched` today, so an opt-in flag that
   defaults off is both the correct design and the cheapest one.

---

## 8. Not established

| Item | Why it matters | Next step |
|---|---|---|
| Cancellation reaching a running Local Activity tool | one of the semantic deltas is documented but unmeasured | cancel a workflow mid-tool and inspect `WORKFLOW_EXECUTION_CANCELED` |
| A tool that exceeds the workflow task timeout | Local Activity heartbeat is workflow-task-level; a slow tool's behaviour is unmeasured | a 15 s tool, then count `WORKFLOW_TASK_COMPLETED` |
| `ExternalStorage` + `PayloadCodec` | encryption plus offload may be a real requirement | add a codec to the spike's converter and re-run |
| Why `__stack_trace` produced a malformed completion | possible SDK bug, reproduced 3/3 but not attributed | dedicated reproduction before filing |
| Whether 1.28.0 is contract-stable | the guard only checks constructor existence | not answerable by testing alone; state it as a residual risk |
| Retry of a `TMPRL1101` failure | could not distinguish long backoff from terminal | server logs or a longer observation window |
| Whether the two-plugin local-activity PR would be accepted | the argument rests on `AGENTS.md` and the sibling gap | open a design comment before code |
