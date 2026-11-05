# Band G Durable variant — a durable approval gate across processes

Captured output of `real-world-scenarios/band-g-long-running/durable/` (issue #177), run on this
machine. **Every console block below is a whole recording**: the command, its output, and its exit
status, written by `durable/evidence.sh` and lifted verbatim by `durable/evidence/render.py` — the
script that wrote this file. A block is one invocation, not a command retyped beside output.

That invariant is enforced rather than promised: `render.py` fails without writing the file when a
recording is missing, empty, or does not end with an `exit=` line. Re-run `./evidence.sh` to
regenerate.

| Field | Value |
| --- | --- |
| Worktree | `squad-177-g2-durable` |
| `dotnet` | `10.0.402` |
| Runtime | `tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime` — the repository's recorded runtime |
| Model | `replay-model`; each answer is supplied by the scenario (`--deterministic`) |
| Driver | `durable/band-g-durable.cs` |
| Runners | `durable/run.sh` (process 1), `durable/resume.sh` (every later process) |
| Recorder | `durable/evidence.sh` |
| Assembler | `durable/evidence/render.py` |

## What the model is, and what the call count therefore proves

**What a turn is.** Every "model call" in these recordings is a real Copilot SDK turn through the
durable harness: a conversation is created, the prompt is admitted to the durable store, the prompt
crosses the runtime boundary, an assistant message comes back, and the turn is checkpointed. Nothing
in that path is the scenario's.

**What the scenario supplies.** Only the assistant text. `--deterministic` replaces what the model
said after the turn returns, so a run needs no endpoint and two runs of the same prompt are
identical. `--live` is its explicit counterpart: it takes whatever the configured runtime answered,
so a run against a real model differs only in which bytes come back. Neither flag changes the turn,
the memo, or the count.

**So the counter proves** how many turns this run of the workflow issued, and — because the count and
the memo commit together — that a resumed process issued none for a step whose memo the store already
held. It does **not** prove anything about model quality, and it is not evidence that a provider was
reachable. The alternative was a run nobody could repeat, which would have made the memoization claim
unrepeatable rather than merely unproven.

## The five requirements, and where each is shown

| Requirement | Where it is demonstrated |
| --- | --- |
| **1. The gate is a task waiting on an external event with a durable deadline, not a blocking read.** | §1. The workflow commits `TaskCommit.Waiting` on a child gate task and returns; the child commits its deadline once, at open. Process 1 exits while the parent is `Waiting` and the gate is `Running`, and process 2 drives both. §4 leaves the run at its gate with a four-second window, then opens the store in a **fresh** process
after that window has passed: the gate wakes at once, records a timeout, and the run ends on it. |
| **2. Pre-gate work is memoized, proven by counting calls.** | §2 and §2a. The variant resumes without re-calling a step the store already held (`3 calls ..., 1 memo hits`), and the same flow with the memo bypassed re-calls it (`4 calls [plan, research, write, write], 0 memo hits`). Both halves are the same script. |
| **3. A decision cannot be overwritten by a late wakeup or a duplicate approval.** | §3 approves a **live** gate twice and the second write is refused by the decision document's own first-writer-wins rule (`ignored: a decision is already on file`), with a `rejects` assertion that the refusal did **not** come from the terminal check. §4a leaves a gate to time out in a fresh process and shows a later approval cannot turn the timeout into an approval. §4b shows a decision arriving after the run finished refused as late — the same guard, a different route, and the capture says which. |
| **4. Aborting mid-wait cascades, and terminal state is final.** | §5. The parent is `Waiting` and its gate is `Running`; the abort leaves both terminal. The ordering is recorded rather than inferred: the parent's abort handler reads its child at the moment it runs and the capture shows `t59 was Terminal (aborted) when the abort handler ran` — so the child was already settled, which "both ended terminal" alone would not establish. An approval afterwards is refused. |
| **5. `lib/Durable.cs` is used, not copied.** | `durable/band-g-durable.cs` has `#:project ../../../lib/Copilot.Durable.csproj` and no store, task runner, or memo implementation of its own. The scenario's own code is the workflow definition, the gate, and the model ledger. |

## The layer change this needed, and its control

Two gaps had to be fixed for requirement 1 to hold at all. Both were reproduced first, and both are
in the same commit as a regression test that fails without the change.

1. **`ResumeAsync` skipped a task left `Waiting`.** It re-entered `Pending` and `Running` only, while
   `lib/ts/durable.ts` and `lib/py/durable.py` both included `Waiting`. A parent suspended on its
   children was therefore never resumed after a restart: the children finished, the parent stayed
   `Waiting`, and nothing raised an error.
2. **A task re-entered while `Waiting` ran its next phase without waiting.** The recorded wait was
   honoured only after a commit made inside the same process. A parent re-entered from a checkpoint it
   committed before the process ended started its post-gate phase immediately — reading a child that
   had not run. §6 is this one: with the wait removed the gate still ends `approved` and the parent
   still fails with `the gate produced no verdict`, because it read the gate before the gate finished.

The `Waiting` filter was already right in TypeScript and Python, so the first fix brings C# into
line. The second is new in all three: the recorded wait is recovered on re-entry in C#, TypeScript, and
Python alike, in one commit, because a semantic change to the layer that lands in one port and not the
others is a cross-port discrepancy rather than a fix. The wire format did not change — a status is
already persisted and already round-trips — so a store written before the change still reads.

## Running it

```sh
cd real-world-scenarios/band-g-long-running/durable
./run.sh                      # process 1: to the gate, then exit
APPROVE=1 ./resume.sh         # process 2: approve, then to completion
./evidence.sh                 # every flow above, then this file
```

A run needs no model endpoint. `DURABLE_RUNTIME_PATH` selects a real runtime when there is one; the
scripts build the repository's recorded runtime when there is not.

## What this does not show

- **A live model.** No endpoint was configured for these captures, so every answer is the scenario's.
  The turns are real; the words are not a model's.
- **Concurrency across processes.** One store has one writer, and a second process opening it while
  the first holds it is refused rather than serialized. The two-process shape here is sequential by
  construction, which is what the baseline's pause/resume shape needs.
- **A gate queue.** There is one approval per run and no notion of who is allowed to answer it. The
  decision document records who did.
- **The middle column, run.** §"Three-way comparison" in the band's README states exactly what was
  and was not executed for the Agent Framework durable extension.

---

## 1. Two-process restart/resume

**Process 1 — run to the gate and exit.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/happy GATE_SECONDS=120 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/happy
store:     .scratch/band-g-evidence/happy/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 120s)
outcome:    waiting at the gate (task t1 is Waiting on t59 Running)
gate:       t59 Running
model:      3 calls [plan, research, write], 0 memo hits

the run is at its gate; the directory holds it.
approve it with: RUN_DIR=.scratch/band-g-evidence/happy ./resume.sh
kept: .scratch/band-g-evidence/happy
exit=0
```

**Process 2 — approve and finish.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/happy APPROVE=1 AS=alice COMMENT=ship it KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh
run:       .scratch/band-g-evidence/happy
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

decision:   recorded: alice approved the plan
on file:    approved by alice at 2026-11-05T16:22:18.4420260+00:00
run:        Waiting

task:       t1 (resuming)
status:     Waiting, restarts before this process: 0
gate:       t59 Running
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (approved)
decision:   approved by alice: ship it
model:      4 calls [plan, research, write, brief], 0 memo hits
outcome:    completed
verdict:    approved

kept: .scratch/band-g-evidence/happy
exit=0
```

**The store's view of the finished run.**

```console
$ drive .scratch/band-g-evidence/happy inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (approved)
decision:   approved by alice: ship it
model:      4 calls [plan, research, write, brief], 0 memo hits
outcome:    completed
verdict:    approved
exit=0
```

## 2. A resume does not re-call the model for a step already done

**Process 1 variant — leave the process before the write step commits.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo FAULT=write GATE_SECONDS=120 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/memo
store:     .scratch/band-g-evidence/memo/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 120s)
fault:      armed at step 'write'
fault:      leaving the process before write commits its checkpoint

the driver left the process at the armed fault (exit 42); the store holds the last commit.
resume it with: RUN_DIR=.scratch/band-g-evidence/memo ./resume.sh
kept: .scratch/band-g-evidence/memo
exit=42
```

**The store after the kill: three steps on disk.**

```console
$ drive .scratch/band-g-evidence/memo inspect
task:       t1 Running (restarts 0)
gate:       (not created)
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
exit=0
```

**Process 2 variant — resume, hitting the memo.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh
run:       .scratch/band-g-evidence/memo
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

task:       t1 (resuming)
status:     Running, restarts before this process: 0
gate:       (not created)
outcome:    waiting at the gate (task t1 is Waiting on t62 Running)
gate:       t62 Running
model:      3 calls [plan, research, write], 1 memo hits

kept: .scratch/band-g-evidence/memo
exit=0
```

**Approve and finish.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo APPROVE=1 AS=alice COMMENT=approved after the restart KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh
run:       .scratch/band-g-evidence/memo
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

decision:   recorded: alice approved the plan
on file:    approved by alice at 2026-11-05T16:22:27.8193090+00:00
run:        Waiting

task:       t1 (resuming)
status:     Waiting, restarts before this process: 1
gate:       t62 Running
task:       t1 Terminal (restarts 1)
gate:       t62 Terminal (approved)
decision:   approved by alice: approved after the restart
model:      4 calls [plan, research, write, brief], 1 memo hits
outcome:    completed
verdict:    approved

kept: .scratch/band-g-evidence/memo
exit=0
```

## 2a. The control: the same flow with the memo bypassed

**Process 1 control — the same kill, in the same place.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo-control FAULT=write GATE_SECONDS=120 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh --no-memo
run:       .scratch/band-g-evidence/memo-control
store:     .scratch/band-g-evidence/memo-control/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
memo:       bypassed (the control run: every step attempt calls the model)
task:       t1 (created, gate window 120s)
fault:      armed at step 'write'
fault:      leaving the process before write commits its checkpoint

the driver left the process at the armed fault (exit 42); the store holds the last commit.
resume it with: RUN_DIR=.scratch/band-g-evidence/memo-control ./resume.sh
kept: .scratch/band-g-evidence/memo-control
exit=42
```

**Process 2 control — the resume re-calls the step the store already held.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo-control KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh --no-memo
run:       .scratch/band-g-evidence/memo-control
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

task:       t1 (resuming)
status:     Running, restarts before this process: 0
gate:       (not created)
outcome:    waiting at the gate (task t1 is Waiting on t78 Running)
gate:       t78 Running
model:      4 calls [plan, research, write, write], 0 memo hits

kept: .scratch/band-g-evidence/memo-control
exit=0
```

**The control reaches the same gate.**

```console
$ drive .scratch/band-g-evidence/memo-control inspect
task:       t1 Waiting (restarts 1)
gate:       t78 Running
decision:   (none)
model:      4 calls [plan, research, write, write], 0 memo hits
exit=0
```

**The control finishes on the same verdict.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/memo-control APPROVE=1 AS=alice COMMENT=approved after the restart KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh --no-memo
run:       .scratch/band-g-evidence/memo-control
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

decision:   recorded: alice approved the plan
on file:    approved by alice at 2026-11-05T16:22:36.7619320+00:00
run:        Waiting

task:       t1 (resuming)
status:     Waiting, restarts before this process: 1
gate:       t78 Running
task:       t1 Terminal (restarts 1)
gate:       t78 Terminal (approved)
decision:   approved by alice: approved after the restart
model:      5 calls [plan, research, write, write, brief], 0 memo hits
outcome:    completed
verdict:    approved

kept: .scratch/band-g-evidence/memo-control
exit=0
```

## 3. A duplicate approval cannot overwrite the first

**Process 1 — to the gate.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/duplicate GATE_SECONDS=120 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/duplicate
store:     .scratch/band-g-evidence/duplicate/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 120s)
outcome:    waiting at the gate (task t1 is Waiting on t59 Running)
gate:       t59 Running
model:      3 calls [plan, research, write], 0 memo hits

the run is at its gate; the directory holds it.
approve it with: RUN_DIR=.scratch/band-g-evidence/duplicate ./resume.sh
kept: .scratch/band-g-evidence/duplicate
exit=0
```

**Process 2 — approve and finish.**

```console
$ drive .scratch/band-g-evidence/duplicate approve --deterministic --as alice --comment first approval
decision:   recorded: alice approved the plan
on file:    approved by alice at 2026-11-05T16:22:41.3762230+00:00
run:        Waiting
exit=0
```

**Process 3 — a second approval from a different reviewer.**

```console
$ drive .scratch/band-g-evidence/duplicate approve --deterministic --as mallory --comment second approval
decision:   ignored: a decision is already on file (alice, approved)
on file:    approved by alice at 2026-11-05T16:22:41.3762230+00:00
run:        Waiting
exit=4
```

**The decision on file is still the first one.**

```console
$ drive .scratch/band-g-evidence/duplicate inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (approved)
decision:   approved by alice: first approval
model:      4 calls [plan, research, write, brief], 0 memo hits
outcome:    completed
verdict:    approved
exit=0
```

## 4. A late decision cannot overwrite a verdict

**Process 1 — a four-second window, then exit at the gate.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/late GATE_SECONDS=4 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/late
store:     .scratch/band-g-evidence/late/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 4s)
outcome:    waiting at the gate (task t1 is Waiting on t59 Running)
gate:       t59 Running
model:      3 calls [plan, research, write], 0 memo hits

the run is at its gate; the directory holds it.
approve it with: RUN_DIR=.scratch/band-g-evidence/late ./resume.sh
kept: .scratch/band-g-evidence/late
exit=0
```

**Process 2 — started after the window has passed: the gate wakes at once and times out.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/late KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh
run:       .scratch/band-g-evidence/late
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

task:       t1 (resuming)
status:     Waiting, restarts before this process: 0
gate:       t59 Running
resumed:    2 task(s) re-entered (failed, completed)
outcome:    completed
model:      3 calls [plan, research, write], 0 memo hits

kept: .scratch/band-g-evidence/late
exit=0
```

**The store: a timeout, and a run that ended on it.**

```console
$ drive .scratch/band-g-evidence/late inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (timed-out)
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
outcome:    failed — the plan was not approved (timed-out): no decision before the gate's deadline
exit=0
```

**An approval after the timeout.**

```console
$ drive .scratch/band-g-evidence/late approve --deterministic --as alice --comment approved too late
decision:   refused: the workflow is Terminal, so this decision is late
on file:    rejected by finished at 2026-11-05T16:22:58.1924280+00:00
run:        Terminal
exit=4
```

**The verdict is still the timeout.**

```console
$ drive .scratch/band-g-evidence/late inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (timed-out)
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
outcome:    failed — the plan was not approved (timed-out): no decision before the gate's deadline
exit=0
```

**A decision after the run finished.**

```console
$ drive .scratch/band-g-evidence/late approve --deterministic --as mallory --comment after the run
decision:   refused: the workflow is Terminal, so this decision is late
on file:    rejected by finished at 2026-11-05T16:23:02.1495810+00:00
run:        Terminal
exit=4
```

## 5. Aborting mid-wait cascades to the children

**Process 1 — to the gate.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/abort GATE_SECONDS=300 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/abort
store:     .scratch/band-g-evidence/abort/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 300s)
outcome:    waiting at the gate (task t1 is Waiting on t59 Running)
gate:       t59 Running
model:      3 calls [plan, research, write], 0 memo hits

the run is at its gate; the directory holds it.
approve it with: RUN_DIR=.scratch/band-g-evidence/abort ./resume.sh
kept: .scratch/band-g-evidence/abort
exit=0
```

**The run before the abort: parent Waiting, gate Running.**

```console
$ drive .scratch/band-g-evidence/abort inspect
task:       t1 Waiting (restarts 0)
gate:       t59 Running
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
exit=0
```

**Abort the parent.**

```console
$ drive .scratch/band-g-evidence/abort abort
abort:      t1 and its children
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (aborted)
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
outcome:    completed
verdict:    aborted
observed:   t59 was Terminal (aborted) when the abort handler ran
exit=0
```

**Both ends terminal, and the parent's verdict is an abort.**

```console
$ drive .scratch/band-g-evidence/abort inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (aborted)
decision:   (none)
model:      3 calls [plan, research, write], 0 memo hits
outcome:    completed
verdict:    aborted
observed:   t59 was Terminal (aborted) when the abort handler ran
exit=0
```

**An approval after the abort.**

```console
$ drive .scratch/band-g-evidence/abort approve --deterministic --as mallory --comment after the abort
decision:   refused: the workflow is Terminal, so this decision is late
on file:    rejected by finished at 2026-11-05T16:23:07.5239430+00:00
run:        Terminal
exit=4
```

## 6. The before/after control for the layer's re-entry wait

**Process 1 of the control: the same run, to the same gate.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/control GATE_SECONDS=120 KEEP=1 real-world-scenarios/band-g-long-running/durable/run.sh
run:       .scratch/band-g-evidence/control
store:     .scratch/band-g-evidence/control/store.jsonl
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

model:      deterministic (the scenario supplies each answer after its turn)
task:       t1 (created, gate window 120s)
outcome:    waiting at the gate (task t1 is Waiting on t59 Running)
gate:       t59 Running
model:      3 calls [plan, research, write], 0 memo hits

the run is at its gate; the directory holds it.
approve it with: RUN_DIR=.scratch/band-g-evidence/control ./resume.sh
kept: .scratch/band-g-evidence/control
exit=0
```

**Process 2 of the control: approve — with the wait removed, the parent reads the gate early.**

```console
$ env RUN_DIR=.scratch/band-g-evidence/control APPROVE=1 AS=alice COMMENT=approved KEEP=1 real-world-scenarios/band-g-long-running/durable/resume.sh
run:       .scratch/band-g-evidence/control
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

decision:   recorded: alice approved the plan
on file:    approved by alice at 2026-11-05T16:23:10.9076350+00:00
run:        Waiting

task:       t1 (resuming)
status:     Waiting, restarts before this process: 0
gate:       t59 Running
resumed:    2 task(s) re-entered (failed, completed)
outcome:    completed
model:      3 calls [plan, research, write], 0 memo hits

kept: .scratch/band-g-evidence/control
exit=0
```

**The control's store: the gate finished approved, and the parent failed anyway.**

```console
$ drive .scratch/band-g-evidence/control inspect
task:       t1 Terminal (restarts 0)
gate:       t59 Terminal (approved)
decision:   approved by alice: approved
model:      3 calls [plan, research, write], 0 memo hits
outcome:    failed — the plan was not approved (the gate produced no verdict): no comment
exit=0
```

