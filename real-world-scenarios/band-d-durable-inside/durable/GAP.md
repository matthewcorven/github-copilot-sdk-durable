# The gap this scenario found in the layer, and the revision that closes it

`.github/copilot-instructions.md` allows a scenario to revise `lib/` while this program is
open, on three conditions: **prove the gap**, **keep the ports in step**, and **land a test
that fails without the change**. This file is the proof. The layer change is commit
`8a27683` on this branch, and it is a separate commit from the scenario on purpose.

## What the scenario needed

A human-in-the-loop approval that **survives a container restart and resumes at the
approval**, where the wait is a durable record and not a loop. In the language of the layer
that is: a task that parks on a signal from outside the store, with a deadline that is the
same instant for every process afterwards.

## What the layer offered

`TaskCommit.Waiting(checkpoint, on)` and `TaskStatus.Waiting`. Both are real, and both were
written for a different thing: `WaitingOn` is **child task ids**, and the runner waits for
each id with `WaitForTaskAsync`. `docs/COMPARATIVE_ANALYSIS.md` says so: "Parent waits on
children". There was no way to say "wait for something that is not in this store".

`SleepUntilAsync` is a durable *timer*, which is the right shape for a delay and the wrong
shape for an event: a deadline is when to stop waiting, not when the answer arrives, and
sleeping until the deadline would burn a container for 24 hours to discover no one answered.

## The reproduction, before any change

The scenario was written the obvious way first: park with `TaskCommit.Waiting(checkpoint,
["external:approval/PA-D2-0001"])`, read the answer from a memo, and sleep until the deadline
otherwise. Four things went wrong, and each is a separate defect.

### 1. An event name was read as a child task id

A run that parks on `"external:approval/PA-D2-0001"` makes the runner call
`WaitForTaskAsync("external:approval/PA-D2-0001")`. No task has that id, so:

```
wait_for_task_async → TaskOutcome("failed", error="unknown task")
```

and the *parent* fails. The task that was meant to park was failed on the spot. The store
then holds a terminal task with `outcome.status == "failed"` and the message `unknown task`,
which is a lie about what happened.

### 2. The non-transaction commit dropped the names

The scenario's park was a plain commit — `runtime.CommitAsync(() => TaskCommit.Waiting(...))`
— and the task record afterwards was:

```
process 1: status=Waiting waitingOn=[] checkpoint={"phase":"await","deadline":"…"}
```

`waitingOn` is **empty**. `CommitTaskAsync(string, Func<TaskCommit>)` wrote the checkpoint,
the status and the outcome, and never the list. The transaction overload writes it; the
non-transaction one did not. So a task that committed `Waiting` through that arm was stored
as a *running* task — no child to wait for, no event to wake, nothing to resume by — and only
`Checkpoint` kept the fact that it had parked at all. The same hole exists in the TypeScript
port (`commitTaskCheckpointAsync`) and in Python (`_commit_task_direct`), because both were
ported from that arm.

### 3. A run that ends at a park reported `failed`

With the park working, a resumed task settles as:

```
{"status":"failed","error":"task did not finish", …}
```

`RunTaskCoreAsync`'s final fallback. The task is `Waiting` in the store and perfectly
resumable, but every caller is told the run failed. A host that retries on failure would
retry a task that is waiting by design; a host that reports failures to a user would tell
them the approval failed when it is simply unanswered.

### 4. The second run got the first run's completion

The runner cached one `TaskCompletionSource` per task id and never cleared it. So:

1. run 1 parks, settles the cache with `failed`;
2. the event arrives, the task is woken, run 2 starts — and `RunTaskAsync` finds the
   *settled* source in the cache and hands its waiter run 1's outcome.

The second `WaitForTaskAsync` returned `failed` while the store held `completed`, and the
third returned the stale terminal outcome. This one is the subtle one: it is invisible until
a task runs twice, and a task only runs twice when something outside the process woke it —
which is exactly the shape this scenario needed.

## What changed

One commit, three defects fixed plus the primitive that made the shape expressible. It is
additive: no existing signature changed, and a store written before it reads back unchanged,
because the event namespace is a prefix inside the list that already existed.

| Change | Where | Why the shape |
| --- | --- | --- |
| `TaskCommit.WaitingForEvent(checkpoint, "external:<name>")` | `lib/Durable.cs`, `lib/ts/durable.ts`, `lib/py/durable.py` | The park is a commit, like `Waiting` — not a new status. |
| `TaskCommit.IsExternalEvent`, `TaskCommit.ExternalEventPrefix` | all three | One stored list, two namespaces. A store written before this change holds only child ids, and every one of them still reads as a child id. No wire-format migration, no version bump of the record. |
| `DurableStore.RaiseEventAsync(name, payload)` | all three | The other half of the commit, on the store because an event outlives the process that parked on it. Writes the payload as the memo `ExternalEventMemoKey` and clears the name, **in one transaction**; a second delivery finds no waiter and never overwrites the first answer. |
| The runner ignores external names when waiting for children, and ends the run when nothing else is left | all three | The park is "no invocation", not "wait for a child". |
| A run ending at a park settles as `pending` | all three | `failed` is for a run with nowhere to continue. `pending` is what the store already reports for a task with no outcome. |
| `WaitingOn` is written on **both** commit arms | all three | Defect 2. |
| A completion is per run and dropped when the run settles | all three | Defect 4. |

**Compatibility.** `pending` is not a new status string — it is what `TaskOutcome` already
uses in `outcomes_async` for a child with no outcome. No public signature changed; the three
new members are additive; `WaitingOn` semantics are unchanged for every name without the
`external:` prefix. The only observable behaviour change for existing callers is defect 4's
fix, which is a bug fix: a second run of a task now settles with the second run's outcome.

**Tests that fail without it.** `tests/Durable.Tests/ExternalEventTests.cs` (5 facts; the suite
went 223 → 228),
the `external event` block in `lib/ts/durable.test.ts`, and
`test_external_event_wakes_a_parked_approval_at_its_checkpoint` in `lib/py/test_sdk.py`.
The .NET file does not compile against the unmodified layer — the before/after control is in
`README.md` §"The layer change, and the control".

## What this is not

- **Not a general event bus.** One payload under one memo key, delivered to every task parked
  on that exact name. No fan-out to a handler, no ordering guarantee beyond the store's
  sequence, no delivery to a task that is not waiting.
- **Not a scheduler.** `RaiseEventAsync` does not start the run. The host that owns the task
  runner calls `ResumeAsync` or `RunTaskAsync`; this is why the scenario's `deliver` and
  `resume` are two calls, and why `record_approval` does them in that order.
- **Not a substitute for `SleepUntilAsync`.** A deadline is still the caller's: the
  scenario puts it in the checkpoint and checks it on the run the event starts, so an
  expired approval fails instead of waiting forever.
- **Not a change to the crash-safety guarantees.** The delivery is one transaction and the
  memo is written with the wait cleared, so a crash leaves either "still waiting" or
  "woken with an answer". Nothing was weakened to make the scenario simpler.

## Why this is the layer's shape and not the scenario's

The alternative was to keep the wait inside the scenario: a `while` loop over
`runtime.MemoAsync(key)`, sleeping between reads. It was rejected because:

- it holds a container for the length of a human's deliberation — the platform bills that,
  and D1's README prices it;
- it cannot survive a restart without re-entering the loop from the top, so the "resume at
  the approval" claim would be false;
- it is a private convention that every other scenario would have to reinvent, which is the
  fork this program exists to prevent.

The layer's job is to hold state that outlives a process. A wait for an external event is
exactly that, so it belongs in the layer — and the defects it exposed (2 and 4) were
already silently corrupting any caller who used `Waiting` from the non-transaction arm or
ran a task twice.