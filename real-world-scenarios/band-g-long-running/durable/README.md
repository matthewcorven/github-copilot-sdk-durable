# Band G — Durable variant

The same long-running workflow, the same human plan-review gate, on Durable. Issue #177, against the
baseline that is #176.

Band G's baseline already resumes across processes, so restart/resume is **not** where this variant
wins. What it changes is what the wait *is*, what a resume costs, and what a decision is worth once it
has been recorded. Read the three-way table below before the feature list: this band's value is the
comparison, and the middle column is not a straw man.

## What is here

| Path | What it is |
| --- | --- |
| `band-g-durable.cs` | The driver: one verb per process step. `#:project`-references `lib/Copilot.Durable.csproj`. |
| `BriefWorkflow.cs` | The workflow's five phases, including the memoized model steps and the gate. |
| `Gate.cs` | The approval gate task, and the only writer of the decision document. |
| `Model.cs` | The turn plus the memo plus the call ledger. |
| `Workflow.cs` | The run's input, checkpoint, and outcome types. |
| `run.sh` | Process 1: create the run, drive it to the gate, exit. |
| `resume.sh` | Every later process: re-enter, approve or reject, finish. |
| `evidence.sh` | Every claim below, measured. Six flows with their controls. |
| `evidence/durable-gate-restart.md` | The captured output, lifted from `evidence.sh`'s recordings. Re-running reproduces it except for wall-clock timestamps. |
| `evidence/remove-reentry-wait.py` | The mutation that produces the before/after control. |

## Two processes, the way the baseline does it

```sh
cd real-world-scenarios/band-g-long-running/durable
./run.sh                        # process 1: plan, research, write, then stop at the gate
APPROVE=1 ./resume.sh           # process 2: approve, then post-approval and completion
```

They share exactly two things: the run directory (the store, the scene the run writes, and `run.json`),
and this scenario's source. Process 2 constructs its own store handle, harness, gateway, and task
definitions. The only in-memory state it has is what the store hands back.

`run.sh` prints the directory, so the two chain with `RUN_DIR`. Neither script deletes a directory it
did not create: `run.sh` marks what it makes with an empty `.band-g-durable-scratch`, and `resume.sh`
removes a directory only when that marker is present. A pre-populated `RUN_DIR` is refused with exit 2
rather than emptied. This is the rule #189 added to the baseline's scripts, applied here from the start.

## The five requirements, and where each is shown

All five are exercised by `./evidence.sh`; the captured output is in
`evidence/durable-gate-restart.md`.

1. **The gate is a task waiting on an external event with a durable deadline, not a blocking read.**
   The workflow creates the gate as a child task and commits `TaskCommit.Waiting` on it
   (`BriefWorkflow.cs:127`), then returns. The parent holds **no invocation** while it waits, which is
   what lets process 1 exit with the run half-done. The child records its deadline once, at open
   (`Gate.cs:47`), and sleeps to it with `SleepUntilAsync` (`Gate.cs:68`) — so a gate restarted after
   five minutes of a ten-minute window still has five minutes, and one restarted after eleven wakes
   immediately and records a timeout. §1 of the evidence shows process 1 exiting with the parent
   `Waiting` and the gate `Running`; process 2 drives both.
2. **Work done before the gate is memoized, proven by counting model calls.** Every step is a memo
   read, a turn, and a memo write, keyed by the step's position in the run (`Model.cs:113`,
   `Model.cs:154`). §2 kills the process before the `write` step commits and resumes: the ledger reads
   `3 calls [plan, research, write], 1 memo hits`, so the resume added **no** call. §2a is the same
   script with the memo bypassed, and it reads `4 calls [plan, research, write, write], 0 memo hits` —
   the resume re-called a step the store already held. The difference between the two runs is the
   memo's whole contribution, stated as a number. **Scope, stated plainly:** the kill point is *before*
   the gate, which is the case the baseline also avoids by not having run those steps yet — so this
   measures the memo **within this variant**, not against the baseline. The baseline-relative claim is
   in the three-way table, and it is marked as likely rather than measured.
3. **A decision cannot be overwritten by a late wakeup or a duplicate approval.** The decision is a
   durable document and the first write wins: a second caller finds the first and returns it
   (`Gate.cs`, `GateCommands.RecordAsync`). All three captures are needed, because the two refusals are
   different guards and only one of them is the one that matters:

   - **§3, a duplicate against a live gate.** `alice` approves, then `mallory` approves, and only then
     is the run driven. The second write is refused with `ignored: a decision is already on file
     (alice, approved)` — the document's own rule — and the evidence asserts the refusal did **not**
     come from the terminal check. This is the half that proves a duplicate cannot overwrite a
     decision.
   - **§4a, a late wakeup.** The gate's four-second window expires in a process that did not set it;
     the gate wakes at once and records `timed-out`. An approval afterwards cannot turn that into an
     approval, because the run is terminal.
   - **§4b, a decision after the run finished.** Refused as late, by the same terminal guard.

   The gate itself never writes a decision — it reads one — so a stale timer has nothing to overwrite
   with.
4. **Aborting mid-wait cascades, and terminal state is final.** §5 aborts the parent while it is
   `Waiting` and its gate is `Running`. The ordering is **recorded, not inferred**: the parent's abort
   handler reads its child at the moment it runs, so the capture shows

   ```
   observed:   t59 was Terminal (aborted) when the abort handler ran
   ```

   That is the difference between "both ended terminal" — which any ordering produces, including no
   cascade at all — and "the child was already settled when the parent concluded". The parent's verdict
   is `aborted` and an approval afterwards is refused. The cascade itself is the layer's
   (`AbortTaskAsync` aborts the children first); the scenario's handler records what it observed.
5. **`lib/Durable.cs`, through the project reference.** `band-g-durable.cs:19` is
   `#:project ../../../lib/Copilot.Durable.csproj`. No store, task runner, memo, or scheduler is
   copied into this folder.

## The three-way comparison

The middle column is the one that has to be honest. **It was not run.** Its behaviour is taken from
the product's own documentation, which is cited; if this band's comparison is used as evidence
anywhere, that distinction matters.

| | File checkpoint (baseline, #176) | Durable Task (agent-framework-durable-extension) | This variant |
| --- | --- | --- | --- |
| **Restart/resume** | **Yes, demonstrated.** Process 1 stops at the gate, process 2 resumes from the recorded checkpoint id and finishes. | **Yes, event-sourced.** Documented: completed agent and executor steps are not repeated after a restart, and a workflow can "pause for human input or external events". **Not run here.** | **Yes**, demonstrated in §1. The store is the record; the parent is re-entered from a checkpoint it committed while `Waiting`. |
| **Re-does model calls on resume** | **Likely, unless memoized by the app.** The checkpoint is the framework's workflow state; the port adds no memo of its own, and a step that had not reached a checkpoint is re-executed. The baseline's own README says pre-gate calls are not repeated at *that* gate — because they had not run yet — which is not the same claim. | **No, on replay.** Documented: "completed agent executions are not repeated or lost if a failure occurs", and durable orchestrations "execute the same way when replayed". **Not run here.** | **No, on a memo**, and measured both ways in §2 and §2a: a resume adds no call for a completed step, and the same flow with the memo bypassed adds one. |
| **Domain state transactional** | **No.** The checkpoint is workflow state. Anything the workflow wrote to the outside world is outside it, and a resume cannot undo or re-derive it. | **Partial.** Workflow and conversation state are checkpointed; a side effect an executor performs is not in that transaction. | **Yes, within the run.** A step's checkpoint, its memo, and its ledger entry commit in one transaction, so a call that happened is never uncounted and a count never names a call that did not happen. The scene files the run writes are outside it — see the limitations. |
| **Operational weight** | **None.** A directory on disk. One writer: the store takes an exclusive lock, so a second process on one directory is refused. | **DTS + emulator.** Documented prerequisites for a self-hosted run: a Durable Task Scheduler connection (or the emulator container) and, for Azure Functions hosting, Azurite as well. | **A store file.** A JSONL file and one writer, no server. A second process on one file is refused for the same reason. |
| **What a decision is** | A request/response pair inside the workflow. The checkpoint holds the pending request; answering it is what resume does. | An external event raised on an orchestration instance, with a timer for the timeout (`WaitForExternalEvent` / `wait_for_external_event`). | A document with a first-writer-wins rule, plus a gate that reads it. Idempotent by construction: a duplicate and a late write both end at the answer already on file. |
| **Abort mid-wait** | Not part of the shape. The port stops at the gate and resumes; there is no abort tree. | Orchestration termination exists; the documentation does not describe a cascade over child work in these terms. **Not run here.** | **Cascades**, demonstrated in §5: aborting the waiting parent aborts its gate child first, and both end terminal. |

### If the middle column already covers what we do, say it

Mostly, it does, and the band should say so plainly:

- **Restart/resume is a tie.** The baseline already does it and the Durable Task extension already
  does it, event-sourced, with a scheduler in front. Nothing here is new.
- **Memoization on resume is a tie on the claim and not on the mechanism.** The extension replays an
  orchestration's history, so completed agent calls do not re-run. This variant keeps an explicit
  memo per step. The extension's approach is stronger: it is the whole orchestration, not the step.
- **A durable wait with a timeout is a tie.** `WaitForExternalEvent` with a timer is the same idea as
  this gate, with more behind it.
- **The one thing that is genuinely different** is the decision: a first-writer-wins document rather
  than an event queue. An event raised twice on an orchestration instance is two events to the
  framework; here the second approval is refused because the decision is a value, not a delivery. That
  is a real difference in what an approval *is*, and it is a small one — a durable function can
  deduplicate too, by writing to storage under a key. **It is not worth adopting a store file over a
  scheduler for.** What this variant demonstrates is that the durable layer is enough for the shape,
  at the cost of the scheduler.

So: the band's value is the comparison, and the comparison says the extension covers the operational
story better and this covers the decision story slightly more directly. Neither is a win.

## The layer changes this needed

Two gaps had to be fixed before requirement 1 could hold at all. Both were reproduced first, and both
are in one commit with a regression test that fails without the change. See
**#203** for the reproduction and the reasoning.

1. **`ResumeAsync` skipped a task left `Waiting`** (`lib/Durable.cs`, `ResumeAsync`). It re-entered
   `Pending` and `Running` only. A parent suspended on its children was never resumed after a restart:
   the children finished, the parent stayed `Waiting`, nothing raised an error. `lib/ts/durable.ts`
   and `lib/py/durable.py` already included `Waiting`, so this brought C# into line.
2. **A task re-entered while `Waiting` ran its next phase without waiting** (all three ports). The
   recorded wait was honoured only after a commit made in the same process, so a parent re-entered
   from a checkpoint it committed before the process ended started its post-gate phase immediately and
   read a child that had not run.

§6 of the evidence is the control for the second: with the re-entry wait removed, the gate still ends
`approved` and the parent still fails with `the gate produced no verdict` — it read the gate before the
gate finished. With the wait, the same flow completes approved (§4).

The `Waiting` filter is a three-line change; the re-entry wait is six. Neither changes the wire
format: a status is already persisted and already round-trips, so a store written before the change
still reads.

## What the model is, and what the call count proves

No model endpoint was configured for these captures, so the scenario supplies each turn's answer
(`--deterministic`). The **turn** is not synthesised: a conversation is created, the prompt is admitted
to the durable store, it crosses the runtime boundary to the repository's recorded runtime, an
assistant message comes back, and the turn is checkpointed. What is supplied is the assistant text.

So the ledger proves **how many turns the run issued**, and — because the count commits with the memo —
that a resumed process issued none for a step already on disk. It proves nothing about model quality,
and it is not evidence that a provider was reachable. The alternative was a run nobody could repeat,
which would have made the memoization claim unrepeatable rather than unproven.

`--live` is the explicit opposite: it takes whatever the configured runtime answered, and changes
nothing else — not the turn, not the memo, not the count. The two are alternatives, and naming both is
a usage error rather than a precedence rule. Note that the repository's recorded runtime is not a
model: `--live` against *it* serves a fixed transcript, so `--live` is only the "take the model's
words" path when a real runtime is behind `DURABLE_RUNTIME_PATH`.

## The deterministic fault points

| Seam | What it does |
| --- | --- |
| `FAULT=<step>` (or `--fault`) | `Environment.Exit(42)` inside the step, after its turn and before its checkpoint commits. The store keeps the last commit, so the next process is the only one that can move the run. |
| `--no-memo` | The control: ignore the memo a step finds, so every attempt calls the model. Used by §2a to measure what the memo saves. |
| `GATE_SECONDS=<n>` | The gate window, recorded in the run's input. A resume cannot supply a fresh one. |

`Environment.Exit` rather than `throw` on purpose: an exception is caught by the runner and committed
as a `failed` outcome, which would make the store say the run failed. A process that is gone commits
nothing.

## What was executed, and what was not

**Executed.** All six flows in `evidence.sh`, from a clean checkout, on this machine: `dotnet` 10.0.402
at `/opt/homebrew/bin/dotnet`, the repository's recorded runtime, no model endpoint. Thirty claims
checked, all held — a number counted from the script (`grep -c '^  claim \|^  rejects '`), not from
memory.

The suites, with an honest caveat about the counts:

| Suite | With the change | Without the change |
| --- | --- | --- |
| C# (`tests/Durable.Tests`) | 220 tests, 200 pass, 20 fail | 218 tests, 200 pass, 20 fail |
| TypeScript (`lib/ts`) | 126 tests, 120 pass, 2 fail | 125 tests, 119 pass, 2 fail (same two) |
| Python (`lib/py`) | 75 tests, 1 fail | 74 tests, 1 fail (same one) |

Each suite gains exactly the tests this change adds, and they pass: two in C# (one per fix) and one
each in TypeScript and Python for the re-entry wait. Each of the three port tests was checked against
the guard being removed, and each fails without it. **The pre-existing failures are not caused by it
and are not a clean baseline either:** most are the bundled-runtime cases CI's download covers, and at
least one — `LateJoinExampleTests.BarrierReadinessFailuresCleanupAndSourceMutations` — is a timing
flake that fails on repeated runs of its own, with and without this change. The counts move by one or
two between runs for that reason. Nothing here should be read as "the suite was green".

**Not executed.** The middle column. The Durable Task extension's `DurableAIAgent`, `WaitForExternalEvent`
and Durable Task Scheduler were not installed, run, or measured — Docker is available on this machine,
but standing up the emulator, a Functions host, and a configured model was out of scope for a
comparison whose conclusion is "it already does this". Every claim about it above is documentation, and
is marked as such.

**Not executed.** A live model. No endpoint was configured.

## Known limitations

- **The scene files are outside the transaction.** `research.md`, `draft-v1.md` and `brief.md` are
  written by the phase *before* its checkpoint commits. A kill between the write and the commit leaves
  a file the store does not know about, and the resumed step rewrites it. The store is the authority;
  the files are for a reader. Making the files transactional would need content-addressed writes and a
  reconciler, which is a different scenario.
- **One writer per store.** A second process opening the store while the first holds it is refused,
  not serialized. The pause/resume shape does not need more, and the baseline has the same rule.
- **A decision is refused once the run is terminal,** even if it names the right brief. That is what
  "terminal is final" means here, but it also means a reviewer who answers a second too late is told
  no rather than having their answer recorded for a human to read. The refusal is in the ledger.
- **No gate queue, and no authorization.** One approval per run. Who is allowed to approve is not
  modelled; the decision records who did.
- **`TaskRuntime` still exposes no store,** so a phase that reads a document closes over the store.
  The gate does exactly that (`ApprovalGate.Define(store)`). This is the same gap Band A recorded
  (#193's sibling note), and it is workable rather than blocking.

## What `lib/Durable.cs` did not need

Worth stating because the revision scope for this program allowed a layer change: **only the two
resume defects above were needed.** The gate, the deadline, the memo, the cascade, and the
first-writer-wins decision are all expressible with `TaskCommit.Waiting`, `SleepUntilAsync`,
`MemoAsync`, `AbortTaskAsync`, and a document. No new API was added for this scenario.
