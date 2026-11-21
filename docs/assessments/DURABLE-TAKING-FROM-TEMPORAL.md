> **Assessment — PO, 2026-11-21.** Read-only. Produced by the PO role under this repository's standing canvas policy; no scope, denominator, issue, commit or spend was changed. Companion: [`docs/TEMPORAL_MAF_COMPARISON.md`](../TEMPORAL_MAF_COMPARISON.md), [`docs/assessments/TEMPORAL-CONTRIBUTION-ASSESSMENT.md`](TEMPORAL-CONTRIBUTION-ASSESSMENT.md).

# Durable should take three things from Temporal, and refuse the rest

**Assessment basis.** Upstream read at `temporalio/ai-integrations` `7607956c80b945891e3aa96eff9fde3dcdb9f326` (2026-11-20), the same commit `docs/TEMPORAL_MAF_COMPARISON.md` cites. Local citation is this tree at `4420dfa`, `Copilot.Durable` **0.5.0** (`lib/Copilot.Durable.csproj:17`) with the external-event park already landed in `[Unreleased]` (`CHANGELOG.md`). Read-only: no file, issue, PR, commit or spend was created. `issue-work-queue` reports no PO-owned issue for this request; the eligible queue is #165 and #224–#230, none of which is this deliverable.

The headline: **Temporal's transferable value to Durable is almost entirely in the *authoring* layer — how a durable system classifies failure, binds a human decision, and proves its own claims — not in its *execution* layer.** The execution layer is exactly what Durable must not borrow. Three things are worth taking. Six are worth taking narrowly or deferring. Five should be refused outright.

---

## 1. What makes Temporal great — the named list

Not the UI, not the service. These are the properties that make people accept a Temporal Service as a dependency, with mechanism and what each buys.

| # | Property | Mechanism in the clone (file:line) | The property it buys |
|---|---|---|---|
| T1 | **Determinism + replay as the recovery mechanism** | Workflow code is re-executed from Event History; the plugin re-implements the loop because `AIAgent.RunAsync` awaits `ConfigureAwait(false)` and would stall the scheduler (`Temporalio.Extensions.MicrosoftAgentFramework/TemporalAgent.cs:21-27`, `:347-363`) | A crash costs re-execution, not lost work. The engine, not your error handling, is the safety net. |
| T2 | **Authored provider-error classification** | HTTP status rule (408/409/429/5xx retryable, other 4xx not), `x-should-retry` override, `retry-after-ms`/`Retry-After` in three forms capped at 60 s (`Internal/ModelErrorClassifier.cs:33-58`, `:112-141`), status read reflectively from any provider exception (`:82-110`); 22 facts in `FailureTests.cs:18-144`, `:286-305` | Retry decisions are a code-reviewed policy, not a provider SDK's private default. It is testable, auditable, and shared across plugins. |
| T3 | **Activity heartbeats and timeouts** | 5-minute start-to-close, 10-second heartbeat, heartbeat at ≥3 s while the provider call runs, recomputed against the configured timeout (`Activities/ModelActivities.cs:24-25`, `:67-82`, `:143-158`); README §Retries | A dead worker is noticed in seconds and the work is retried elsewhere. Liveness is a first-class property, not a TCP keepalive. |
| T4 | **Parked waits at zero cost** | `await Workflow.WaitConditionAsync(...)` holds no Activity and no timer while the human decides; survives worker restart (README §Approvals, final paragraph; `tests/.../ApprovalWorkflow.workflow.cs`) | A run can wait for a person for a week and cost nothing. This is the single most economically important property of durable execution. |
| T5 | **Request-id-bound approvals** | Request id is `ficc_` + call id (`Internal/ApprovalProcessor.cs:18`, `:217`); responses bind to *the surfaced request*, and the response's own `ToolCall` is read only for its call id (`:100-130`); a surfaced-but-unanswered request and a forged id both fail the run (`:131`, `:264-274`); `HeldApproval` records the copy of the call that actually runs (`Internal/HeldApproval.cs:9-11`) | A tampered response cannot redirect the call the human saw. A dropped decision fails loudly rather than silently. The human's authority is bound to a *specific* recorded call. |
| T6 | **Fail-fast misuse detection** | Call through `AIAgent`-typed reference → named `WorkflowError` (`TemporalAgent.cs:362-363`); context provider that awaits workflow work → named error (README §Context and history providers); credential-shaped key in `AdditionalProperties` → run fails *before any Activity is scheduled* (README §Chat clients and credentials); 10 misuse cases in one theory (`FailureTests.cs:321-340`) | Silent hazards become loud, named failures at the earliest possible moment. The most expensive bug class in a durable system is the one that looks fine and is wrong. |
| T7 | **Tested replay compatibility per major** | `ReplayMajor = 0`, ≥11 recorded histories replayed through the plugin (`tests/.../ReplayTests.cs:12-40`); plus a construction-time API guard naming the tested version and accepted range (`Internal/AgentFrameworkApiGuard.cs:17-59`) | An upgrade does not silently invalidate recorded runs. "Replay works within a major" is a tested contract, not a hope. |
| T8 | **Secret boundary at admission** | Credential-shaped keys refused *before* the Activity input is recorded, top-level or nested headers (README §Chat clients and credentials; `FailureTests.cs:326-327`) | Event History is a permanent, replicated artifact. Anything that lands there is effectively published. The boundary is enforced at the recording point, not by convention. |
| T9 | **Activity-count-level test assertions** | `MaxCachedWorkflows = 0` forces no history cache, then exact per-name Activity counts and exact total (`tests/.../SideEffectTests.cs:16-35`, `:39-55`) | The test proves *the behaviour claimed*, not that the code ran. Two model Activities, two tool Activities, nothing else. |
| T10 | **Payload/size accounting** | Whole transcript is input to every model Activity; Temporal caps Activity input at 2 MiB; continue-as-new bounds **history but not payload** (README §Limits) | The growth surface is named, measured and bounded at the one place it must be. |
| T11 | **Continue-as-new** | Serialize session into next run's input, `ContinueAsNewSuggested`, `CreateContinueAsNewException` (README §Sessions and continue-as-new) | A workflow can live for years while its history stays small. |
| T12 | **Query for pending, Update with validator to decide** | `PendingApprovals()` Query, `DecideAsync` Update, `ValidateDecision` validator that runs *before* the Workflow sees the reply so bad input never enters history (README §Approvals) | Read state and decide state are separate, typed, validated operations — not one overloaded "send a message". |

Two properties worth naming that the comparison matrix does not foreground: **T13, an attempt-limited default is a *decision the caller must make*** — Temporal's own default has no attempt limit and the README has to warn about it (§Retries) — and **T14, shipped test doubles** (`FakeChatClient`, `ScriptedResponse`, `InMemoryMcpServer`, README §Testing) so a user can test their own workflow without a provider or network.

---

## 2. Candidate adoption table

One row per candidate. "Durable today" cites the tree; "Temporal does" cites the clone. Verdicts: 3 adopt, 5 adopt narrowly/defer, 5 reject.

### 2.1 Provider-error retry classification — **Adopt (narrowly, as a shared policy value)**

| Column | Content |
|---|---|
| **Temporal does** | Classifies by HTTP status only, never message text; `x-should-retry` overrides; `Retry-After` honoured in three forms capped at 60 s; unreadable status propagates unchanged and is retried by the engine (`ModelErrorClassifier.cs:33-58`, `:112-141`; README §Retries) |
| **Durable today** | **No retry policy at all.** `grep -rni 'retry\|backoff' lib/Durable.cs lib/ts/durable.ts lib/py/durable.py` returns zero hits. Recovery is re-entry from a committed checkpoint; a phase handler that throws is committed as `TaskCommit.Failed(exception.Message)` and the task ends (`lib/Durable.cs:5539-5545`). There is no timeout on a phase handler anywhere (`Timeout` appears only in the model-wait paths, `lib/Durable.cs:3945-4068`). |
| **Smallest adoptable slice** | A **pure classifier function** — status + headers in, `(retryable, delay?)` out — with *no* engine attached, plus a caller-supplied `RetryPolicy` on `DurableTaskDefinition` phases (`MaximumAttempts`, `MaximumInterval`, initial interval) whose classification feeds `TaskCommit.Failed` vs a re-committed phase with `attempt` in the checkpoint. The classifier itself is ~150 lines of dependency-free C#. |
| **What it would break** | Nothing structural *if* the classifier is a value and the policy is opt-in. Two hazards: (a) the attempt counter must live in the **checkpoint**, not the record, or a rollback restores a stale count — the D2 "put back both copies" lesson (`.squad/decisions.md:939-980`); (b) a retry loop inside a phase handler is a *new* unbounded execution path in a system whose whole claim is "bounded durable state". |
| **Evidence required** | 22-fact analogue: status table, `x-should-retry` override, three `Retry-After` forms, 60 s cap, unreadable-status-propagates. Plus a crash test: kill between attempt 2 and 3, reopen, confirm the attempt counter survived and the delay is honoured. |
| **Verdict** | **Adopt narrowly.** The *classifier* is high-value, cheap, and portable. The *engine* is already Durable's — do not import a second one. |

**Why narrow and not full.** Temporal's retry is safe because the engine owns the loop and the Activity has no side effects beyond its recorded result. In Durable, a phase handler *does* have side effects (`MemoAsync`, `CommitAsync` writing documents), so a naive retry re-runs them. That is why the memo must be written *in the same transaction as the checkpoint* (`lib/Durable.cs:2676-2690`) — and the retry slice must require exactly that, or it is a side-effect amplifier.

### 2.2 Attempt-limited defaults — **Adopt (as a required explicit choice, not a default)**

| Column | Content |
|---|---|
| **Temporal does** | Default retry policy has **no attempt limit**; the README warns you must set `MaximumAttempts` or the run retries until the Workflow closes (§Retries). The tested example sets 5 (`README.md:143-152`). |
| **Durable today** | No policy to have a default for (§2.1). A phase handler that throws fails the task once (`lib/Durable.cs:5543`); `Restarts` counts process re-entries (`lib/Durable.cs:219`, `:5482-5488`), not attempts. |
| **Smallest slice** | The policy's `MaximumAttempts` is **required when a retry policy is supplied** — no default. The self-owned runner's stance differs correctly from Temporal's: a store-backed runner with a human operator should fail to a terminal `failed` outcome with the last error, never retry forever. |
| **What it would break** | A required field is a public-surface change; it lands with the policy type, not after. |
| **Evidence required** | A test asserting that omitting the limit is a construction-time error (T6 discipline against our own API), and one asserting a terminal `failed` outcome after N attempts with `restarts` unchanged. |
| **Verdict** | **Adopt.** This is the one place Durable should deliberately *invert* a Temporal default rather than match it, and say so in the changelog. |

### 2.3 Request-id-bound approvals — **Adopt narrowly (binding without wire change)**

| Column | Content |
|---|---|
| **Temporal does** | Response binds to the surfaced request; the response's own `ToolCall` is read only for its call id (`ApprovalProcessor.cs:100-130`); unknown or unanswered → run fails (`:131`, `:264-274`) |
| **Durable today** | A task parks with `TaskCommit.WaitingForEvent(cp, "external:<name>")` (`lib/Durable.cs:294-305`); delivery writes the payload as **one** memo under one key and clears the name (`lib/Durable.cs:2229-2244`, `ExternalEventMemoKey` at `:2275`). The payload's *shape* and the decision's *identity* are entirely the app's. First delivery wins; a second wakes nobody. |
| **Smallest adoptable slice** | **No wire change.** Convention plus a helper: a decision payload `{"requestId","approved","reason"}` and a `TaskCommit.DecisionRequest(checkpoint, requestId)` helper that parks on `external:<name>` and writes `requestId` into the checkpoint. The resumed phase reads the memo, compares `requestId` to the checkpoint's, and a mismatch is a **named terminal failure** (`decision-mismatch`) rather than a silent apply. This is binding *by agreement*, checkable at re-entry, with zero bytes added to the store. |
| **What it would break** | Nothing on the wire. It does add public surface (helper + error name). It cannot reproduce the engine's guarantee: the *call that runs* is not snapshotted, because Durable does not hold the tool call across the park — the app re-derives it. Claiming parity here would be false (§5). |
| **Evidence required** | The D2-style three-defect test set: a mismatched id fails terminally and *does not* apply; a redelivery changes nothing; a decision arriving before the park commits is not lost (the "wait for the surface" problem the plugin solves with `await Workflow.WaitConditionAsync(() => !awaitingReply)`, README §Approvals). |
| **Verdict** | **Adopt narrowly.** High value, no compatibility cost, honest about what it does not buy. |

### 2.4 Fail-fast misuse detection — **Adopt (three of six hazards only)**

| Column | Content |
|---|---|
| **Temporal does** | Ten named misuse failures in one theory (`FailureTests.cs:321-340`), each a `WorkflowError` with the offending name in the message |
| **Durable today — the silent hazards, ranked** | 1. **`MemoAsync` inside a `CommitAsync` callback** — documented as a hang ("the gate is not reentrant and the callback cannot return… it is a hang", `lib/Durable.cs:2676-2692`, `:385-392`). Not detectable; it deadlocks. 2. **`RefreshAsync` on a live persistent store** — a documented no-op (`lib/Durable.cs:2546-2572`, `DESIGN.md:213`). Silent by design, so a caller who *expected* a refresh gets stale data and no signal. 3. **Harness closed without disposing a borrowed store** — `DurableHarness.DisposeAsync` disposes the store only when `OwnsStore` (`lib/Durable.cs:6452-6455`), and `DESIGN.md:209-211` states the reopening rule as prose. 4. A **second writer** — already an `InvalidOperationException` naming file, lock, and pid (`lib/Durable.cs:1261-1290`) — **already good**. 5. A **memo resurrected after a failed effect** — the transaction memo is the correct pattern (`lib/Durable.cs:2676-2690`) but nothing detects the inverse (two memos for one effect). 6. A **phase that throws** — becomes a string `Failed` (`lib/Durable.cs:5543`), losing the exception type. |
| **Smallest adoptable slice** | (a) A `[ThreadStatic]`/`AsyncLocal` "commit in progress" flag that makes `TaskRuntime.MemoAsync` **throw** when called from inside a commit callback instead of hanging — this is a pure bug-class fix, not a feature. (b) A named `DurableMisuseException` with stable codes (`memo-in-transaction`, `store-not-owned`, `refresh-on-live-store`) so a host can assert on it. (c) Preserve the exception *type* in the terminal outcome (`{"error":"…","errorType":"…"}`), additive. |
| **What it would break** | (a) no behaviour change for correct code; (b) new exception type is additive; (c) adds a field to an outcome payload — additive, old stores read back unchanged if the reader tolerates absence. |
| **Evidence required** | A test that the hang becomes a throw (with a timeout so a regression fails fast), and one per named code. |
| **Verdict** | **Adopt.** This is the highest quality-per-line item on the list and it fixes a real hazard rather than adding a feature. |

### 2.5 Replay compatibility testing — **Adopt narrowly (a fixture corpus, not an engine)**

| Column | Content |
|---|---|
| **Temporal does** | 11 recorded Event Histories replayed through the replayer per major (`ReplayTests.cs:12-40`); construction-time API guard naming the tested version (`AgentFrameworkApiGuard.cs:17-59`) |
| **Durable today** | `tools/ReplayRuntime/fixtures/` has **4** fixtures for the *runtime* (session shape), not for the *store* format; `validate.sh --deterministic` proves output determinism and effective-model agreement (`DESIGN.md:1088-1130`) but does **not** replay a recorded store against a new library version. `DurableStoreFormat` has one version (`"v":1`, `DESIGN.md:107-111`). |
| **The analogue** | The store's JSONL *is* Durable's history. The analogue is a **versioned store corpus**: committed `.jsonl` files (one per format generation) that a test opens, replays, and asserts a canonical projection of state — documents, versions, tasks, memos, `WaitingOn`. That is a replay test in everything but name, and it is *cheaper* than Temporal's because the store is declarative, not executable. |
| **What it would break** | Nothing at runtime. Cost is real: three ports each need a reader test over the same corpus, and the corpus becomes a compatibility commitment (a change to `"v":1` gains a migration note). |
| **Evidence required** | One corpus directory, a reader in each of C#/TS/Python, asserting the *same* canonical projection. A deliberate format change must fail the test until the corpus is extended and the migration recorded. |
| **Verdict** | **Adopt narrowly.** It is the cheapest way to make the existing "a store written before this change reads back unchanged" claim testable instead of asserted in five separate decision entries. |

### 2.6 Secret boundary at admission — **Adopt (one boundary, one rule)**

| Column | Content |
|---|---|
| **Temporal does** | Credential-shaped key in `AdditionalProperties`/headers fails the run **before any Activity is scheduled** (README §Chat clients and credentials; `FailureTests.cs:326-327`) |
| **Durable today** | **No credential-shape check anywhere.** `grep -rni 'authorization\|api-key\|secret' src/Durable.Runtime/*.cs lib/Durable.cs` returns zero. `AgentDefinitionValidator` validates name, extensions, tools, sandbox, permissions (`src/Durable.Runtime/AgentDefinitionValidator.cs:36-45`) — not secrets. `AgentChoices` (`lib/Durable.cs`, class `AgentChoices`) records model, effort, instructions, permissions, compaction — a free-form `Instructions` string is the realistic leak path into the store and the RPC input. |
| **Smallest adoptable slice** | **One rule at three recording boundaries**, not a scanner: reject a *key* (not a value) matching `^(authorization|api-key|x-api-key|apikey|cookie|set-cookie)$` case-insensitively in (a) agent-definition import, (b) RPC/HTTP request body, (c) any structured object the layer writes to a store record. Values cannot be reliably detected; keys can, and key-shape is what the Temporal rule uses. |
| **What it would break** | A definition that today imports with a stray header now fails. That is a breaking change for a narrow population → **minor** version, changelog entry, one-release warning before it becomes an error. |
| **Evidence required** | A test per boundary, positive and negative, plus a test that an *unrelated* key (`x-request-id`) still passes. |
| **Verdict** | **Adopt.** It is the only item on the list where Durable is genuinely *behind* on a safety property rather than on a convenience, and Band D's hosted position makes the store a durable artifact other people read. |

### 2.7 Activity-count-level test assertions — **Adopt (already partly there), narrow the gap**

| Column | Content |
|---|---|
| **Temporal does** | `MaxCachedWorkflows = 0` + exact counts per Activity name and exact total (`SideEffectTests.cs:16-35`), plus an explicit zero for `WorkflowTaskFailed` |
| **Durable today** | Exactness exists for some things: `Assert.Equal(1, VersionCount(...))` / `versionsBefore + 1` (`tests/Durable.Tests/FacadeContractTests.cs:137`, `:212`, `:371-402`), reopen-mints-past-every-sequence (`DurableStoreTests.cs:266-335`). But there is **no exact record-count assertion** over the JSONL envelope, no exact **memo** count, and model admissions are asserted by identity not by *count* (`FacadeDurabilityTests.cs:148-178`). |
| **Smallest slice** | A single `StoreAssert.RecordCounts(store)` helper that counts records by `type`, and a handful of tests that assert the exact multiset for one representative flow (one conversation, one turn, one tool round, one commit) — the Durable analogue of `Assert.Equal(2, counts.Count)`. |
| **What it would break** | Nothing. Test-only. |
| **Evidence required** | The tests themselves; a mutation run that adds one spurious record and goes red. |
| **Verdict** | **Adopt narrowly.** Cheap, and it is the discipline that makes §2.5's corpus meaningful. |

### 2.8 Payload/size accounting — **Defer (measure first, do not cap)**

| Column | Content |
|---|---|
| **Temporal does** | 2 MiB Activity input cap; transcript is input to *every* model call; continue-as-new bounds history, not payload (README §Limits) |
| **Durable today** | Growth surfaces, all **unmeasured**: transcript entries (`AppendEntry`, `lib/Durable.cs:2616`), document versions (`FacadeContractTests.cs` counts them, nothing bounds them), task records with `Restarts`, memo payloads (arbitrary `JsonElement`, `lib/Durable.cs:2241`), and the JSONL file itself, which is append-only and never compacted. `grep 'MiB\|MaxBytes'` finds only a byte-offset calculation (`lib/Durable.cs:1542`). |
| **Why defer and not adopt** | A cap is a behaviour change with no evidence it is needed, and the honest first step is the measurement. The measuring command exists: `wc -c` over the store after a representative run, plus a per-`type` byte breakdown from the same records §2.7 counts. **Not measured today; the command is the deliverable, not a number.** |
| **Verdict** | **Defer.** Adopt only after a measurement shows a real unbounded surface. |

### 2.9 Query/Update surface — **Defer (the transport is the wrong shape for it)**

| Column | Content |
|---|---|
| **Temporal does** | Query lists pending approvals; Update decides with a validator that keeps bad input out of history (README §Approvals) |
| **Durable today** | `serve --http` is `POST /` or `POST /turns` only; it serves one durable turn per request and streams events (`DESIGN.md:629-695`). There is **no** read-only route, no pending-approval listing, no validator concept. `serve --rpc` is JSON-lines turns (`DESIGN.md:804+`). The store *can* be read for a parked task, but only by a process holding the write lock. |
| **Why defer** | Adding Query/Update means a **read path that does not take the single-writer lock**, or a second process that can only read — a new tier with its own compatibility and staleness contract (`RefreshAsync`'s own remarks anticipate it, `lib/Durable.cs:2551-2555`). That is a design decision in its own right, not a slice. And the honest consumer of "pending approvals" in Durable today is a human reading a document, which already works. |
| **Verdict** | **Defer.** Belongs with the "read-only tier" question, which should be its own gap-proofed issue. |

### 2.10 Continue-as-new analogue — **Reject the primitive, keep the policy**

| Column | Content |
|---|---|
| **Temporal does** | Serialize session into next input, `ContinueAsNewSuggested`, new execution with bounded history (README §Sessions and continue-as-new) |
| **Durable today** | Compaction is **native SDK compaction** exposed as `InfiniteSessions` (`lib/Durable.cs:3366`, `:3497`, `:6240`), not a layer primitive; example `24-compaction.cs` demonstrates the SDK's. The store has no history bound. |
| **Why reject the primitive** | Continue-as-new exists because Temporal's history is *unbounded input to replay* and replay cost grows with it. Durable's replay cost is a file read at open; there is no equivalent forcing function. A "bounded history" primitive in a store whose contract is "the file is the state" would be a compaction/GC mechanism with its own crash-safety requirements — a large change to the thing the library most must not destabilise. |
| **What to keep instead** | The *policy*: a documented, measured guidance that a transcript should be summarised via the SDK's compaction, and that document versioning has an explicit retention decision the app owns. That is documentation, not code. |
| **Verdict** | **Reject the primitive; adopt the documented retention policy only if §2.8 measures growth.** |

### 2.11 Provider-neutral chat client — **Reject (this is the product's identity, not a gap)**

| Column | Content |
|---|---|
| **Temporal does** | Any `IChatClient`, credentials on the worker, model name crosses the Activity boundary (README §Chat clients and credentials) |
| **Durable today** | Copilot-only by construction: one SDK pinned at `1.0.28` in TS and Python (`lib/ts/package.json:13`, `lib/py/pyproject.toml:6`), the harness creates and owns a `CopilotClient` (`lib/Durable.cs:6467+`), session identity is the SDK session (`DESIGN.md:327`), permissions are the SDK's twelve request variants (`lib/Durable.cs:2979-2995`), sandbox is the CLI's. |
| **Why reject** | Every one of Durable's differentiating properties — permission grants, sandbox policy, session identity, extension hot-reload, the fork that copies transcript *and* documents — is expressed in Copilot SDK terms. Provider neutrality would not be an addition; it would be a second implementation of the layer's core with a lowest-common-denominator abstraction underneath, and it would delete the reason the library exists (`TEMPORAL_MAF_COMPARISON.md` §6 items 5–6, §9). |
| **Verdict** | **Reject.** State it once in the README's non-goals so it stops being re-proposed. |

### 2.12 Task queues / scale-out / multi-worker — **Reject (incompatible), with the honest alternative**

| Column | Content |
|---|---|
| **Temporal does** | Workers poll task queues; scale-out is the server's job; the Workflow is single-threaded by construction (README §Installation; matrix row "Concurrent writers") |
| **Durable today** | **One writer per store file, enforced by an exclusive lock**, refused at open with file/lock/pid (`lib/Durable.cs:1261-1290`, `DESIGN.md:199-215`). Turns are serialised (`DESIGN.md:629+`, "Turns are serialized"); Band A's concurrent-clients row shows the commit gate serialising two writers (`REAL_WORLD_MATRIX.md:41-79`). |
| **Why incompatible** | A second worker is a second writer. The lock is not a tuning parameter; it is the mechanism that makes the in-memory read model correct and torn-tail recovery safe (`DESIGN.md:107-120`). Adding a queue would mean a central lock service — i.e. a Temporal-shaped server — which is the one thing the library may not become. |
| **The honest alternative** | **One writer per store, many stores.** Shard by store path; a host that needs more throughput runs N independent stores each with its own worker and its own lock. Document that as the scale-out story, and make `StoreFileLock`'s refusal message point at it. |
| **Verdict** | **Reject.** Write the alternative into the README non-goals. |

### 2.13 Additional candidates found while reading

| Candidate | Verdict | Note |
|---|---|---|
| **T3 heartbeats/liveness** | **Reject** | A heartbeat exists to detect a *worker* that died while an *Activity* is open. Durable's equivalent is a committed checkpoint and a `Restarts` counter (`lib/Durable.cs:219`); there is no open call to heartbeat. A phase-level timeout is a *different* feature (§2.1's policy can carry one) and should be scoped there, not imported as "heartbeats". |
| **T12 Query/Update validators** | **Adopt narrowly, later** | The *validator concept* — reject bad input before it enters the store — is transferable to RPC request bodies (`DESIGN.md:629+` already refuses bad requests) and to `AgentDefinitionValidator`. It does not require the Query/Update transport. |
| **T14 shipped test doubles** | **Defer** | `tools/ReplayRuntime` is already Durable's fake runtime; a `FakeChatClient`-equivalent public testing package would be useful but is a packaging decision, not a durability one. |
| **T7's API guard (version check at construction)** | **Adopt narrowly** | Durable pins the SDK at `1.0.28` in three files but has no construction-time check that the installed SDK exposes the members it calls (`grep 'VersionGuard\|guard'` finds only unrelated guards). A named `DurableSdkVersionException` naming the tested version is cheap and directly analogous to `AgentFrameworkApiGuard.cs:17-59`. |

---

## 3. Sequenced roadmap

Constraints honoured: gap-proof first; wire format untouched; three ports in step; semver; **two active implementation issues** at a time (the standing implementation budget).

### Step 1 — Misuse detection becomes typed and loud (0.5.x, patch-to-minor)

- **User-visible outcome:** calling `MemoAsync` from inside a commit callback throws `memo-in-transaction` instead of hanging forever; a borrowed store closed by a harness carries a named remedy; a phase failure keeps its exception type in the outcome.
- **Gap proof:** the hang is documented at `lib/Durable.cs:2676-2692` but has no failing test. The PR's first commit is that test (with a 5 s timeout so a regression fails rather than hangs).
- **Acceptance evidence:** three named codes, each with a positive and negative test; a kill-restart test proving the outcome payload reads back in an older store.
- **Ports:** C# first (it owns the gate), TS/Python in the **same release** — the memo deadlock exists in all three by construction (parity contract, `DESIGN.md:897+`).
- **Release:** minor. Changelog "Changed" + "Fixed". No store-format change; no migration note.

### Step 2 — Provider-error classifier + opt-in attempt-limited policy (0.6.0, minor)

- **User-visible outcome:** a phase handler can supply a retry policy; the layer classifies a provider error into retryable/non-retryable by status + headers, honours `Retry-After` capped at 60 s, and **requires** `MaximumAttempts`.
- **Gap proof:** Band A's "the turn is re-sent, not replayed" and Band C's duplicate-side-effect row are the recorded failures; the issue must cite one and reproduce "no policy exists" with the grep in §2.1.
- **Acceptance evidence:** the 22-fact analogue (status table, override, three `Retry-After` forms, cap, unreadable-status propagates); a crash test proving the attempt counter survives reopen; a test that omitting `MaximumAttempts` is a construction error.
- **Ports:** classifier is pure and ports verbatim; the policy type is identical in all three. **In step** — a policy present in C# only would be a parity violation.
- **Release:** minor. Changelog "Added". The inverted default (required, not unlimited) gets its own paragraph and a decision entry.

### Step 3 — Store-compatibility corpus + record-count exactness (0.6.x, patch)

- **User-visible outcome:** none at runtime. The claim "a store written before this change reads back unchanged" becomes a test three language readers must pass.
- **Gap proof:** the claim is asserted in five decision entries and no test. The gap is demonstrated by deleting a corpus file and showing the suite goes red.
- **Acceptance evidence:** one corpus directory, three readers, one canonical projection; a mutation meta-test (the pattern already exists in `tests/parity/test_mutations.py:1-25`).
- **Ports:** C#/TS/Python **must** all land — the point is cross-language agreement.
- **Release:** patch. Changelog "Internal"/"Tests". No public surface.

### Step 4 — Decision binding for external events (0.7.0, minor)

- **User-visible outcome:** `TaskCommit.DecisionRequest(checkpoint, requestId)` + a documented decision payload; a mismatched id fails the task terminally with `decision-mismatch` and applies nothing.
- **Gap proof:** D2's three defects are the precedent that this class of bug is silent (`.squad/decisions.md:939-980`); Band G's "decision is a value, not a delivery" is the motivating shape.
- **Acceptance evidence:** mismatch fails and does not apply; redelivery is a no-op; a decision that arrives before the park commits is not lost.
- **Ports:** helper + error name in all three; **no wire change** (`WaitingOn` and `ExternalEventMemoKey` unchanged).
- **Release:** minor. Changelog "Added". Compatibility note: existing parks keep working because the helper is additive.

### Step 5 — Secret boundary at admission (0.8.0, minor, with a deprecation release)

- **User-visible outcome:** an agent definition or request body carrying a credential-shaped **key** is refused at import/serve with a named error.
- **Gap proof:** zero hits for any credential check in the runtime (§2.6); the measurement is the grep, and the test is a definition that today imports and should not.
- **Acceptance evidence:** one test per boundary, positive and negative, plus a non-secret key that still passes.
- **Ports:** the runtime host is C#-only today; the port obligation is the **store record boundary**, which each port can enforce at its own write path.
- **Release:** minor, with one release of warning-before-error for the narrow population that has a stray header today.

**Sequencing rationale.** Step 1 first because it fixes a hazard with no new surface. Step 2 next because it is the largest product gap and needs the misuse machinery from Step 1. Step 3 rides alongside Step 2 as the second of the two budget slots — it is test-only and does not contend for design attention. Steps 4 and 5 are later because each adds public surface and benefits from the corpus existing first.

---

## 4. What Durable should NOT take

1. **A separate orchestration service.** Reject. The library's entire proposition is that one file and one writer replace the service (`DESIGN.md:199-221`, `TEMPORAL_MAF_COMPARISON.md` §6 item 3). A service is not an upgrade path; it is a different product.
2. **Multi-writer stores / task queues / worker fleets.** Reject, for the reasons in §2.12. The alternative — many stores, one writer each — should be documented rather than apologised for.
3. **Importing `ConfigureAwait(false)`-style engine semantics.** Reject. Temporal has to own the loop because `AIAgent.RunAsync` leaves the scheduler (`TemporalAgent.cs:21-27`). Durable deliberately leaves the loop to the runtime and pays a *different* price — a resumed turn is re-sent (`REAL_WORLD_MATRIX.md:41-79`) — and that trade is honest. Importing the constraint without the engine buys the tax and not the guarantee.
4. **Provider neutrality at the cost of the Copilot runtime integration.** Reject (§2.11). Permission grants, sandbox policy, session identity, extension hot-reload and combined transcript/document forks are all expressed in SDK terms; abstracting them would hollow out the layer's differentiators.
5. **A per-scenario or per-plugin private fork.** Reject, and note that `.github/copilot-instructions.md` already names this as **the** failure mode the temporary revision scope exists to prevent. Any scenario that cannot be expressed must change the shared layer or file a gap.
6. **Any feature that weakens crash safety to simplify a scenario.** Reject. The torn-tail/`InvalidDataException`/double-pending invariants (`DESIGN.md:107-120`, D2's "a status without its supporting state is not a status") are the thing being demonstrated.
7. **Continue-as-new as a layer primitive** (§2.10) and **activity heartbeats** (§2.13) — both are shaped by an engine Durable does not have.

**Where Temporal's guarantees are engine-backed and cannot be reproduced by a store at any reasonable cost.** This must be stated plainly, because it caps every claim in §5:

- **T1 determinism + replay:** a store can replay *records*; it cannot replay *executed code* deterministically. Durable never claims to.
- **T3 liveness detection:** a heartbeat needs an open, owned call. A committed checkpoint has no heartbeat to miss.
- **T5 the strongest half of approval binding:** Temporal can guarantee "the call that runs is the one the human saw" because the engine holds the call across the park (`HeldApproval.cs:9-11`). A store holds a *decision*; the app re-derives the call. Decision binding is reproducible; **call snapshotting is not**.
- **T11 continue-as-new:** bounded history is meaningful only where history is the replay input.
- **T2's *engine* half:** classification is portable; *retry-until-closed* is not, because Durable has no engine to close the run.

---

## 5. Product risk and narrative

**The current honest position** (state it, keep it): *Durable owns application state; Temporal owns execution durability.* `TEMPORAL_MAF_COMPARISON.md` §5 and §9 already say this and should not be softened.

**After each step, the claim Durable may make:**

| After | May claim | May **not** claim |
|---|---|---|
| Step 1 | "Misuse fails loudly with named errors; a memo-in-transaction is an error, not a hang." | "We detect misuse like Temporal does" — Durable detects *six* of a different catalogue. |
| Step 2 | "Provider errors are classified by an authored, tested policy; retries are opt-in and attempt-limited." | "We retry like Temporal" — no engine closes the run, and a retried phase re-runs side effects unless the memo commits with the checkpoint. |
| Step 3 | "A store written by an earlier version reads back, proven across three languages." | "Replay compatibility like Temporal's" — the store replays *data*; Temporal replays *execution*. |
| Step 4 | "A decision is bound to the request it answers, and a mismatched decision fails instead of applying." | "Tamper-proof approvals" — the call is not snapshotted. |
| Step 5 | "Credential-shaped keys are refused before they reach the store." | "Secrets cannot leak" — values are not scanned. |

**The competitive-narrative risk is real and it cuts one way.** A half-adopted retry policy is *worse than none*: today, "no policy" is a clean, defensible statement. A policy that classifies correctly but retries a phase handler whose side effect was not memoised produces duplicate effects under a name that sounds like Temporal's — the exact failure Band C already measured once (`REAL_WORLD_MATRIX.md`, Band C memo/ledger caveat) and Band A doubled (`2 enquiry documents, 2 model admissions`). **The mitigation is structural, not editorial: Step 2's slice must make the memo-and-checkpoint-same-transaction requirement a precondition of the retry policy, not documentation beside it.**

Partial adoption also risks the comparison document going stale in the direction that flatters Durable. Every step that lands must update the affected matrix cell in the same PR, and no cell may be upgraded from "not implemented" to "implemented" on the strength of the narrow slice alone.

---

## 6. ETA/risk ranges

Ranges are effort envelopes in *one engineer's focused hours*, not dates and not capacity commitments. Confidence is stated per step.

| Step | Range | Confidence | What would move it |
|---|---|---|---|
| 1. Misuse detection | 2–4 days | **High** — three named codes, one gate flag, one outcome field. | The `AsyncLocal` flag interacting with the TS/Python ports' different async models. |
| 2. Classifier + policy | 5–9 days | **Medium** — the classifier ports verbatim; the policy's interaction with the checkpoint attempt counter is the risk. | A decision that the attempt counter belongs on the task record rather than in the checkpoint (rollback semantics). |
| 3. Corpus + counts | 3–5 days | **High** — test-only, pattern exists. | Discovering that a port's reader normalises differently (a real finding, not a delay). |
| 4. Decision binding | 3–5 days | **Medium** — small surface, but the "decision arrives before the park commits" case needs care. | The pre-park race proving harder than the D2 precedent suggests. |
| 5. Secret boundary | 2–4 days + one warning release | **Medium** — the rule is trivial; the compatibility window is the work. | A user with a legitimate credential-shaped *non-secret* key; the allow-list would need a documented escape. |

**Cross-cutting.** Each step's range assumes the **two-active-issue budget** holds and does not include release mechanics. A step that requires a port to lag deliberately must say so and add a parity-document entry; a step that changes the store wire format (none of these do) would invalidate every range above.

---

## 7. Canvas handoff

**Canvas: Core Engineers — affected sections: Product priorities and MVP denominator.** Result: **UNCHANGED.** Source identity: `tools/core-engineers-canvas/scope.json` at this tree, plus `lib/Copilot.Durable.csproj` version `0.5.0` and the `issue-work-queue` output for `matthewcorven/github-copilot-sdk-durable`. No refresh was run because this is a read-only assessment that proposes no scope change; **next action** — if the user promotes any of §3's steps into scope, the owner (PO) must update `scope.json`'s MVP denominator and goals, then invoke `node tools/core-engineers-canvas/server.mjs --refresh` and verify the snapshot SHA/time/errors before reporting the sections current. Nothing in this document should be read as a cached-percentage or denominator change.