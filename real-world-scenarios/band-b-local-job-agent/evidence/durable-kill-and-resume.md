# Band B Durable variant — kill and resume

Captured output of the Durable variant of the Band B job agent (issue #150), run on this machine
against a live Copilot CLI. Nothing below is retyped: each block is the command shown, with the
output it produced.

| Field | Value |
| --- | --- |
| Worktree | `/Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2` |
| Branch | `squad/150-b2-durable` (from `origin/dev` @ `5c6d167`) |
| `dotnet` | `/opt/homebrew/bin/dotnet` 10.0.402 |
| Copilot CLI | 1.0.105 |
| Model | `gpt-5.1-codex-mini` (the baseline's pinned model) |
| Loop source | `durable/ralph-loop-durable.cs` |
| Runner | `durable/run.sh` (process 1), `durable/resume.sh` (every later process) |
| Scratch root | `.scratch/band-b-evidence/` in the worktree root; kept here with `KEEP=1` |
| Verifier | `baseline/verify.sh` — the baseline's, reused, not re-written |

Seven flows were run, each in its own scratch directory, each with a store of its own:

| Directory | Flow |
| --- | --- |
| `reference/target` | Uninterrupted. One process, cap 10, runs to completion. |
| `kill-resume/target` | Process 1 stops after iteration 3 commits; process 2 resumes and finishes. |
| `mid-turn/target` | Process 1 stops between iteration 2's turn commit and its step memo; process 2 resumes. |
| `memo-hit/target` | Process 1 stops after iteration 2's step memo is written but before it settles; process 2 resumes and hits the memo. |
| `cap/target` | Cap 3, process 1 stops after iteration 2, process 2 resumes and stops at the run's cap. |
| `divergence/target` | `PLAN.md` edited by hand between the two processes. |
| `in-flight/target` | Process 1 stops inside iteration 2's turn; process 2 resumes into the failure path. |

Every directory below was produced by one driver script, in the order listed, against the source
this file is committed with. The §7 controls are part of the same run; two of them leave no
directory behind, which is the point of the control.

---

## 1. The headline: kill after iteration N, resume at N+1

### Process 1 — `STOP_AFTER_ITERATION=3 ./run.sh 10`

```console
$ RUN_DIR=.scratch/band-b-evidence/kill-resume KEEP=1 STOP_AFTER_ITERATION=3 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
target:    .scratch/band-b-evidence/kill-resume/target
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

target:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target
store:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target/.durable/store.jsonl
model:     gpt-5.1-codex-mini (pinned)
plan:      /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target/PLAN.md
stopping: after iteration 3 commits (STOP_AFTER_ITERATION)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
max:       10 iterations
run:       new durable task t8 (cap 10, written to the store)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 1/10 ===
  plan digest: 3e60cf2491efcc1c
  agent-turn: no memo step/1/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/1/agent-turn written
  reconcile: no plan item changed; 10 item(s) still open
iteration 1 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 2/10 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/2/agent-turn written
  reconcile: no plan item changed; 8 item(s) still open
iteration 2 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/3/agent-turn written
  reconcile: formatted 2 section heading(s); 6 item(s) still open
iteration 3 complete.
stopping: iteration 3 is committed (STOP_AFTER_ITERATION=3).

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.

stopped at the seam: iteration 3 is committed, the run is not finished.
resume with: KEEP=1 RUN_DIR=.scratch/band-b-evidence/kill-resume ./resume.sh
target: .scratch/band-b-evidence/kill-resume/target
kept: .scratch/band-b-evidence/kill-resume/target

```

The process is a real process and it exits on its own. `STOP_AFTER_ITERATION` is not a signal
handler: the phase commits iteration 3, prints the line above, and throws on its own cancellation
token, so the runner unwinds and the process exits 0 with the task left `Running` at the
checkpoint. Nothing is shared with the next process but the store file.

### Process 2 — `./resume.sh`

```console
$ RUN_DIR=.scratch/band-b-evidence/kill-resume KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
target:    .scratch/band-b-evidence/kill-resume/target
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

target:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target
store:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target/.durable/store.jsonl
model:     gpt-5.1-codex-mini (pinned)
plan:      /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/kill-resume/target/PLAN.md
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 4
last stage: iteration-complete, plan digest 603155e8dfaf31ae
  recorded step: agent-turn — I finished both items for this pass and ticked them in `PLAN.md`. (3 tool calls)
  recorded step: reconcile — formatted 2 section heading(s); 6 item(s) still open
memos on record: 6
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 4/10 ===
  plan digest: 603155e8dfaf31ae
  agent-turn: no memo step/4/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/4/agent-turn written
  reconcile: no plan item changed; 4 item(s) still open
iteration 4 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 5/10 ===
  plan digest: 07a6a447bbae9275
  agent-turn: no memo step/5/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/5/agent-turn written
  reconcile: formatted 2 section heading(s); 2 item(s) still open
iteration 5 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 6/10 ===
  plan digest: 936e4e5d11031cb7
  agent-turn: no memo step/6/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/6/agent-turn written
  reconcile: no plan item changed; plan has no open item
iteration 6 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
plan reports complete after 6 iteration(s).

verified: the target matches the plan
kept: .scratch/band-b-evidence/kill-resume/target

```

`resuming at iteration 4` is read from the store's checkpoint. The resume process is given no
iteration number, no cap, and no plan digest on its command line.

---

## 2. Idempotency: the resumed run's file mutations equal an uninterrupted run's

### The reference run

```console
$ RUN_DIR=.scratch/band-b-evidence/reference KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
...
iteration 6 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
plan reports complete after 6 iteration(s).

verified: the target matches the plan
```

### The diff

```console
$ cd .scratch/band-b-evidence
$ diff -r --exclude=.durable reference/target kill-resume/target
$ echo "exit=$?"
exit=0
```

**Empty.** The kill-and-resume flow produced a target byte-identical to the uninterrupted flow's:
`PLAN.md`, `release-notes.md`, and all ten `docs/section-*.md` files. The `.durable/` store is
excluded because it is the record, not the product — the two stores differ by construction (one
had two processes open it, the other had one).

`baseline/verify.sh` also passes on both, and it is the same verifier: the Durable variant reuses
it rather than restating the contract.

### What the store holds

The task record at the end of the kill-and-resume flow, read straight out of the JSONL store:

```console
$ python3 - <<'EOF'
import json
last=None
for line in open('kill-resume/target/.durable/store.jsonl'):
    rec=json.loads(line)
    if rec.get('type')=='task':
        last=rec['payload']
print("memo keys:", sorted(last['memos'].keys()))
print("checkpoint:", json.dumps(last['checkpoint'], indent=2))
EOF
memo keys: ['step/1/agent-turn', 'step/1/reconcile', 'step/2/agent-turn', 'step/2/reconcile',
            'step/3/agent-turn', 'step/3/reconcile', 'step/4/agent-turn', 'step/4/reconcile',
            'step/5/agent-turn', 'step/5/reconcile', 'step/6/agent-turn', 'step/6/reconcile']
checkpoint: {
  "phase": "iteration",
  "iteration": 6,
  "stage": "iteration-complete",
  "planDigest": "c79976668a4d09e9",
  "steps": [
    {
      "step": "agent-turn",
      "outcome": "Both items are done: `docs/section-09.md` and `docs/section-10.md` now start with their H1 (`# Section 09`). (10 tool calls)"
    },
    {
      "step": "reconcile",
      "outcome": "no plan item changed; plan has no open item"
    }
  ]
}
```

Twelve keys for six iterations — one `agent-turn` and one `reconcile` per iteration, numbered from
1. They span two processes: iterations 1–3 were run by process 1, 4–6 by process 2, and the key for
iteration *n* is the same string whichever process wrote it.

---

## 3. The memo boundary: present, hit, and the honest limit

### 3a. A stop between the turn's commit and its memo

The interesting crash is not "between iterations"; it is "after the turn mutated files, before its
step memo was written". `STOP_BEFORE_MEMO=1` puts the seam exactly there.

```console
$ RUN_DIR=.scratch/band-b-evidence/mid-turn KEEP=1 STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
...
=== Iteration 2/10 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
stopping: iteration 2's turn is complete, the step memo is not written.

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.
```

```console
$ RUN_DIR=.scratch/band-b-evidence/mid-turn KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 2
last stage: turn-complete, plan digest af36a70803b9c0e2
  recorded step: agent-turn — I added the H1 to both files and confirmed on disk that each is now the first line. `docs/section-01.md` starts with `# … (6 tool calls)
memos on record: 2
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 2/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: memo step/2/agent-turn is missing but the checkpoint records the turn
  agent-turn: restoring the memo from the checkpoint — not run again
  reconcile: no plan item changed; 8 item(s) still open
iteration 2 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/3/agent-turn written
  reconcile: formatted 2 section heading(s); 6 item(s) still open
iteration 3 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 4/10 ===
  plan digest: 603155e8dfaf31ae
  agent-turn: no memo step/4/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/4/agent-turn written
  reconcile: no plan item changed; 4 item(s) still open
iteration 4 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 5/10 ===
  plan digest: 07a6a447bbae9275
  agent-turn: no memo step/5/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/5/agent-turn written
  reconcile: no plan item changed; 2 item(s) still open
iteration 5 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 6/10 ===
  plan digest: 936e4e5d11031cb7
  agent-turn: no memo step/6/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/6/agent-turn written
  reconcile: no plan item changed; plan has no open item
iteration 6 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
plan reports complete after 6 iteration(s).

verified: the target matches the plan
```

The turn is **not** re-run. The checkpoint written immediately after the turn carries the turn's
outcome, so the resume reconstructs `step/2/agent-turn` from the checkpoint instead of re-issuing
it. That is why the checkpoint-after-the-turn exists at all: it is what makes the memo recoverable
when the process dies between the commit and the memo write.

And its files are identical to the reference run's:

```console
$ diff -r --exclude=.durable reference/target mid-turn/target
$ echo "exit=$?"
exit=0
```

### 3b. A memo hit: the guard working, not merely present

3a shows the memo being *rebuilt*. This flow shows it being *read*: the seam stops after the
turn's memo is written and before the iteration settles, so the resume re-enters iteration 2 with
the memo already in the store and skips the step.

```console
$ RUN_DIR=.scratch/band-b-evidence/memo-hit KEEP=1 STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
...
=== Iteration 2/10 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ view
  ⚙ view
  ⚙ edit
  agent-turn: done; memo step/2/agent-turn written
stopping: iteration 2's turn memo is written, the iteration is not settled.

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.
```

```console
$ RUN_DIR=.scratch/band-b-evidence/memo-hit KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 2
last stage: turn-complete, plan digest af36a70803b9c0e2
  recorded step: agent-turn — I added the H1 to `docs/section-01.md` (`# Section 01`) and `docs/section-02.md` (`# Section 02`). I re-read both files … (8 tool calls)
memos on record: 3
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 2/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: memo step/2/agent-turn is present — not run again
  reconcile: no plan item changed; 8 item(s) still open
iteration 2 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/3/agent-turn written
  reconcile: no plan item changed; 6 item(s) still open
iteration 3 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 4/10 ===
  plan digest: 603155e8dfaf31ae
  agent-turn: no memo step/4/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/4/agent-turn written
  reconcile: formatted 2 section heading(s); 4 item(s) still open
iteration 4 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 5/10 ===
  plan digest: 07a6a447bbae9275
  agent-turn: no memo step/5/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/5/agent-turn written
  reconcile: formatted 2 section heading(s); 2 item(s) still open
iteration 5 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 6/10 ===
  plan digest: 936e4e5d11031cb7
  agent-turn: no memo step/6/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/6/agent-turn written
  reconcile: no plan item changed; plan has no open item
iteration 6 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
plan reports complete after 6 iteration(s).

verified: the target matches the plan
```

The line `agent-turn: memo step/2/agent-turn is present — not run again` is the branch at
`ralph-loop-durable.cs:315`: the memo was read, the turn was not issued, and the recorded outcome
was carried into the settle checkpoint. The iteration's files are still identical to the
reference run's:

```console
$ diff -r --exclude=.durable reference/target memo-hit/target
$ echo "exit=$?"
exit=0
```

Two things are worth separating here, because they are different mechanisms:

- **3a is recovery.** No memo exists; the checkpoint proves the turn happened; the memo is rebuilt
  from it. The guard is what stops that rebuild from becoming a second turn.
- **3b is the guard itself.** The memo exists; the step is skipped on the strength of it alone.

### 3c. The limit: a turn that was issued and never finished

The layer records each **completed** tool call as a `tool` checkpoint on the conversation's
`durable.checkpoint` document (`RecordToolCheckpoint`, `lib/Durable.cs:4271`), and that document is
`DocumentHistory.Rewindable` (`lib:4590`), so a finished turn's calls stay readable through
`DurableStore.Documents` / `DocumentState.Versions` (`lib:2072`, `lib:110`). What the layer will not
do is *continue* a half-finished turn: the SDK has no per-tool-call replay, and the turn's own
checkpoint is written only when the turn reports success.

So a resume into `Stage = "turn-started"` fails the run, and says what the store knows before it
does. `STOP_MID_TURN=1` reproduces the state end to end: the process leaves while iteration 2's
turn is running.

```console
$ RUN_DIR=.scratch/band-b-evidence/in-flight KEEP=1 STOP_AFTER_ITERATION=2 STOP_MID_TURN=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
...
=== Iteration 2/10 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ view
  ⚙ bash
stopping: iteration 2's turn is in flight, after 1 completed tool call(s).
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
durable: run did not complete: failed task did not finish

the durable variant exited with status 1
target: .scratch/band-b-evidence/in-flight/target
```

Process 1 leaves the task at `turn-started` and exits 1 — the runner reports `failed task did not
finish`, which is the run being torn down mid-phase rather than an outcome the loop chose. The
store is whole and closed normally, so the next process opens it like any other run.

```console
$ RUN_DIR=.scratch/band-b-evidence/in-flight KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 2
last stage: turn-started, plan digest af36a70803b9c0e2
  recorded step: agent-turn — I finished both items for this pass and ticked them in `PLAN.md`. I re-read `release-notes.md` on disk after editing, an… (4 tool calls)
  recorded step: reconcile — no plan item changed; 10 item(s) still open
memos on record: 2
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  iteration 2's turn is recorded as issued and never finished.
  the store holds no completed tool call(s) for it:
  the turn's tool writes were queued and not committed: the process left first.
  that is the whole of what the store can say: the turn is observable, not replayable.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
durable: run did not complete: failed iteration 2 was in flight: the turn was issued (plan digest af36a70803b9c0e2) and its outcome was never recorded, so it may have mutated files. Re-running it would duplicate those mutations. Recreate the target from baseline/target/ and re-run.

the durable variant exited with status 1
target: .scratch/band-b-evidence/in-flight/target
```

The message is accurate about the boundary in both directions:

- **Observable.** The store knows the turn was issued (the `turn-started` checkpoint the phase
  wrote before it), and it holds whatever tool checkpoints had committed by the time the process
  left. In this capture, none had: the layer queues a tool checkpoint per conversation and awaits
  the queue at the turn's completion barrier (`CheckpointBarrierAsync`, `lib:4150`), so a process
  that leaves mid-turn can exit with its last calls unwritten. The report says exactly that rather
  than claiming a partial record it does not have.
- **Not replayable.** The turn cannot be continued, so re-issuing it would repeat whatever it did.
  The run fails instead, naming the iteration, and asks for the target to be recreated.

Re-entering a *finished* turn is a different case and the store answers it directly: the turn's
checkpoint carries its outcome, so the resume reports what the dead turn did. That is 3a, and it is
the demonstrable half. 3c is the half that cannot be recovered, and it is the reason the variant
fails rather than retries.

What would close it is a primitive the layer does not have: per-tool-call replay in the SDK, or a
tool record written *before* the call and settled after it, so a resume could tell an interrupted
call from a completed one. The layer's current record is written after the call and settled in a
batch, which makes it a history rather than a cursor. That is a real feature request and it is
written up in the PR for #150, not attempted in this tree.

---

## 4. Bounded: a resumed run does not get a fresh cap

### Process 1 — cap 3, stop after iteration 2

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 STOP_AFTER_ITERATION=2 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 3
...
=== Iteration 2/3 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/2/agent-turn written
  reconcile: no plan item changed; 8 item(s) still open
iteration 2 complete.
stopping: iteration 2 is committed (STOP_AFTER_ITERATION=2).

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.
```

### Process 2 — resume

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
max:       3 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 3
last stage: iteration-complete, plan digest bbef84d1f745f802
  recorded step: agent-turn — I added the H1 to both files and ticked both items in `PLAN.md`. (2 tool calls)
  recorded step: reconcile — no plan item changed; 8 item(s) still open
memos on record: 4
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/3 ===
  plan digest: bbef84d1f745f802
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/3/agent-turn written
  reconcile: no plan item changed; 6 item(s) still open
iteration 3 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
iteration cap reached: 3 iterations and the plan is not complete.
first open item: - [ ] `docs/section-05.md`: add the missing H1

iteration cap reached; the plan is still open. See the loop output above.
target: .scratch/band-b-evidence/cap/target
```

It stops at 3, not at 5 (2 + 3). The cap lives in the task's stored input, which the first process
wrote once.

A resumed process that tries to supply a different cap is told so and the stored value wins:

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh 9
max:       3 iterations (from the run's stored input)
ignored:   -- 9 is not this run's cap; the store says 3.
run:       task t8 is already finished; nothing to resume.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
iteration cap reached: 3 iterations and the plan is not complete.
first open item: - [ ] `docs/section-05.md`: add the missing H1

iteration cap reached; the plan is still open. See the loop output above.
target: .scratch/band-b-evidence/cap/target
```

So the run is bounded by its own budget, and a finished run stays finished: re-entering a terminal
task reports the recorded outcome instead of starting new work.

---

## 5. The plan file is input, not the record

Between process 1 and process 2 of the divergence flow, `PLAN.md` was edited by hand. The store was
not touched.

**The plan at the seam** (what process 1 left, items ticked in order):

```markdown
Status: in-progress

- [x] `release-notes.md`: demote every heading one level (H2->H3, H3->H4, H4->H5), leaving the title and fenced blocks alone
- [x] `release-notes.md`: end every bullet under `### New` with `<!-- NEW -->`
- [x] `docs/section-01.md`: add the missing H1
- [x] `docs/section-02.md`: add the missing H1
- [ ] `docs/section-03.md`: add the missing H1 and repoint the broken `./runner.md` link to `./section-01.md`
- [ ] `docs/section-04.md`: add the missing H1
- [ ] `docs/section-05.md`: add the missing H1
- [ ] `docs/section-06.md`: add the missing H1
- [ ] `docs/section-07.md`: add the missing H1
- [ ] `docs/section-08.md`: add the missing H1
- [ ] `docs/section-09.md`: add the missing H1 and replace the stale `2020-01-01` with `2026-01-01`
- [ ] `docs/section-10.md`: add the missing H1
```

**The plan the user edited it into** — three deliberate divergences:

1. the open items were **reordered** (`section-09` and `section-08` first, `section-03` last, with a
   blank line before it);
2. `docs/section-08.md` was **ticked by hand** although its H1 is still missing;
3. the store still records iterations 1–2 as complete, and its checkpoint still names iteration 2 as
   the last one done.

```markdown
Status: in-progress

- [x] `release-notes.md`: ... (unchanged)
- [x] `release-notes.md`: ... (unchanged)
- [x] `docs/section-01.md`: add the missing H1
- [x] `docs/section-02.md`: add the missing H1
- [ ] `docs/section-09.md`: add the missing H1 and replace the stale `2020-01-01` with `2026-01-01`
- [x] `docs/section-08.md`: add the missing H1
- [ ] `docs/section-04.md`: add the missing H1
- [ ] `docs/section-05.md`: add the missing H1
- [ ] `docs/section-06.md`: add the missing H1
- [ ] `docs/section-07.md`: add the missing H1
- [ ] `docs/section-10.md`: add the missing H1
- [ ] `docs/section-03.md`: add the missing H1 and repoint the broken `./runner.md` link to `./section-01.md`
```

**The resume** — `./resume.sh`:

```console
$ RUN_DIR=.scratch/band-b-evidence/divergence KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
...
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 3
last stage: iteration-complete, plan digest bbef84d1f745f802
  recorded step: agent-turn — I added the H1 to `docs/section-01.md` (`# Section 01`) and `docs/section-02.md` (`# Section 02`). I re-read both files … (7 tool calls)
  recorded step: reconcile — no plan item changed; 8 item(s) still open
memos on record: 4
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/10 ===
  plan digest: d6b06db0ad2a02e5
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/3/agent-turn written
  reconcile: unticked item-10 (condition does not hold on disk); 6 item(s) still open
iteration 3 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 4/10 ===
  plan digest: 267b7acaf3720fac
  agent-turn: no memo step/4/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/4/agent-turn written
  reconcile: formatted 2 section heading(s); 4 item(s) still open
iteration 4 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 5/10 ===
  plan digest: c1c3ec45424910e5
  agent-turn: no memo step/5/agent-turn — running the turn
  ⚙ view
  ⚙ bash
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/5/agent-turn written
  reconcile: no plan item changed; 2 item(s) still open
iteration 5 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 6/10 ===
  plan digest: 7e94d99d117d1b6f
  agent-turn: no memo step/6/agent-turn — running the turn
  ⚙ view
  ⚙ view
  ⚙ view
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ grep
  ⚙ edit
  ⚙ edit
  ⚙ bash
  ⚙ edit
  agent-turn: done; memo step/6/agent-turn written
  reconcile: no plan item changed; plan has no open item
iteration 6 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
plan reports complete after 6 iteration(s).

verified: the target matches the plan
```

Three things are visible in that output, and each is the point of the constraint:

- **The position came from the store.** `resuming at iteration 3` and the two recorded step
  outcomes are the store's, not the file's. The loop did not restart because the markdown looked
  unfinished, and it did not re-derive its position from the checkboxes.
- **The new digest was picked up as input.** The plan's digest changed from `bbef84d1f745f802`
  (recorded by process 1) to `d6b06db0ad2a02e5` (the edited file), and iteration 3 worked the
  edited file's list in the edited file's order — `section-09` first, `section-03` last.
- **The hand-ticked lie was corrected.** `docs/section-08.md` was ticked but had no H1; the
  reconcile step unticked it (`unticked item-10 (condition does not hold on disk)`), so the run did
  the work and only then ticked it again. The final plan has every item `[x]` and every condition
  holds on disk — `verify.sh` passed.

The final `PLAN.md` kept the user's order, which is the correct outcome: the store knows what was
*done*, the file says what is *wanted*, and a resume reconciles the second against the first.

---

## 6. Custom durability code

The Band B matrix row asks how many lines of the Durable variant exist only because the layer has
to keep durable state.

**Counting rule.**

- Count code lines in `durable/ralph-loop-durable.cs` only — not the shell runners, not
  `lib/Durable.cs`.
- Blank lines and comment lines (`//`, and the `#:` file directives) are excluded. The regions below
  are disjoint and their union is every such line in the file: **22 regions, 658 code lines**.
  Re-running the region boundaries over the committed file reproduces both columns exactly.
- A code line counts as **durability** when its only reason to exist is durable state: opening or
  disposing the store, creating or finding the task, deciding first-run versus resume, reading the
  run's stored input, writing or reading a checkpoint, writing or reading a memo, detecting an
  in-flight step, reading the store's checkpoint documents back, a seam that exists to stop at a
  checkpoint boundary, or a type whose sole purpose is to be serialized into the store.
- A code line counts as **application** when it would exist without durability: argument and
  environment parsing, console output, prompt construction, the SDK turn call, the reconcile
  conditions, the plan and heading parsers, and the formatting normalizer.
- Classification is per line, not per region — most regions are a mix. A line that names a durable
  concept (`prior.Checkpoint`, `runtime.MemoAsync`, `store.Tasks`) is durability even when it also
  prints or branches; a line that only reads the turn's answer or the plan is application.

Applied to the file, region by region (line ranges in the committed source):

| Region | Lines | Code | Durability | Application |
| --- | --- | --- | --- | --- |
| File header: `#:` directives and `using` | 1–60 | 5 | 0 | 5 |
| Top-level statements | 61–62 | 2 | 0 | 2 |
| `DurableLoopAsync` — target, args, model, store path | 64–103 | 29 | 4 | 25 |
| `DurableLoopAsync` — store, harness, extension install | 104–124 | 12 | 12 | 0 |
| `DurableLoopAsync` — first-run branch | 125–149 | 21 | 17 | 4 |
| `DurableLoopAsync` — resume branch: stored input, terminal check, resume banner | 150–184 | 27 | 18 | 9 |
| `DurableLoopAsync` — `try`/`finally` around the store | 185–189 | 4 | 4 | 0 |
| `AwaitRunAsync` — await the runner, the stopped-at-seam path | 190–205 | 15 | 11 | 4 |
| `Report` — the outcome, the cap line, the exit statuses | 206–231 | 20 | 4 | 16 |
| `Definition` — the task definition and its one phase | 235–248 | 13 | 13 | 0 |
| `RunIterationAsync` — plan read, completion and cap from the stored input | 249–278 | 23 | 19 | 4 |
| `RunIterationAsync` — in-flight detection and the report it prints | 279–293 | 9 | 9 | 0 |
| `RunIterationAsync` — banner, checkpoint before the turn, memo read, no-turn branches | 294–331 | 29 | 29 | 0 |
| `RunIterationAsync` — the turn, checkpoint after it, the memo, the two seams | 332–384 | 36 | 36 | 0 |
| `RunIterationAsync` — reconcile, settle checkpoint, stop at the seam | 389–430 | 35 | 23 | 12 |
| `RunAgentTurnAsync` — the SDK turn and the seam that leaves it in flight | 437–495 | 42 | 0 | 42 |
| `Choices` | 496–504 | 6 | 0 | 6 |
| `BuildTurnPrompt` | 505–532 | 21 | 0 | 21 |
| `ReportInFlightTurn` — reading the store's checkpoint documents back | 542–606 | 49 | 49 | 0 |
| `Reconcile`, its conditions and parsers, `NormalizeSectionHeadings` | 608–864 | 207 | 0 | 207 |
| Helpers — `StepKey`, the three seams, `Digest`, `IsComplete`, `OpenItems`, … | 867–905 | 24 | 12 | 12 |
| `Job` constants and the `LoopInput`/`LoopCheckpoint`/`StepOutcome`/`LoopResult` records | 907–948 | 29 | 29 | 0 |
| **Total** | | **658** | **289** | **369** |

The durability column sums to **289** and the application column to **369**; together they are
**658**, the file's own code-line count. That is the check the previous revision's table failed: its
rows summed to 504/219 against a stated 570/201, because three regions (`Report`, the top-level
statements, and the `try`/`finally` around the store) were folded into other rows and two more were
undercounted.

**289 of 658 code lines — 44% — exist only for durability.** The baseline has **0**:
`baseline/ralph-loop.cs` contains no store, no task, no checkpoint, no memo, and no resume path; its
entire progress model is the `iteration` local variable plus the markdown file.

Two rows deserve a note, because they are where a reader is most likely to disagree:

- **`ReportInFlightTurn` (49 lines) is durability.** It exists only to read the layer's own
  checkpoint history back, so the 49 lines would not exist without a store. It is the largest
  single durability block, and it is the demonstration §3c rests on.
- **`Reconcile` (207 lines) is application.** It is the target's contract expressed in C#; the
  baseline keeps the same contract in `baseline/verify.sh` instead. Moving it between languages
  moves the number without changing what the loop does.

`NormalizeSectionHeadings` (719–754, 32 lines) sits inside the `Reconcile` row and is classified
**application**. Its own comment says pinning the blank line after the H1 "is what makes two
correct runs byte-identical", which reads like a durability concern; it is not. The property it
protects is the *comparison* in §2 — two baseline runs need the same normalizer to be comparable —
and it describes the product's required formatting rather than anything the store does. Move the
loop off the durable layer and the same 32 lines remain. Recording the judgement here is the point:
reclassifying it would move 32 lines and change nothing about the comparison the number is for.

---

## 7. Scratch provenance: nothing is emptied that the script did not create

`run.sh` and `resume.sh` both take `RUN_DIR` (and `TARGET`) from the environment and remove the
scratch directory on exit unless `KEEP=1`. That is a destructive default pointed at a caller's
path, so both scripts follow the convention #189 established for Band G: a directory the script
created is marked with an empty `.band-b-scratch` file, and only a marked directory is removed.
Both directions are captured below.

### A hand-written directory survives

A directory this loop never created, holding its own `notes.txt` and a hand-written target:

```console
$ mkdir -p .scratch/band-b-evidence/hand-written/target/.durable
$ printf 'keep me\n' > .scratch/band-b-evidence/hand-written/notes.txt
$ printf '# Plan\n\nStatus: in-progress\n' > .scratch/band-b-evidence/hand-written/target/PLAN.md
$ printf '{"v":1}\n' > .scratch/band-b-evidence/hand-written/target/.durable/store.jsonl
$ RUN_DIR=.scratch/band-b-evidence/hand-written \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
target:    .scratch/band-b-evidence/hand-written/target
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

target:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/hand-written/target
store:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/hand-written/target/.durable/store.jsonl
model:     gpt-5.1-codex-mini (pinned)
plan:      /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/hand-written/target/PLAN.md
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
max:       10 iterations
run:       new durable task t8 (cap 10, written to the store)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 1/10 ===
  plan digest: 8e08bf3d4ffafa68
  agent-turn: no memo step/1/agent-turn — running the turn
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
durable: run did not complete: failed Could not find file '/Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-190-b2/.scratch/band-b-evidence/hand-written/target/PROMPT.md'.

the durable variant exited with status 1
target: .scratch/band-b-evidence/hand-written/target
not removing non-scratch directory: .scratch/band-b-evidence/hand-written

$ echo "exit=$?"
exit=1
$ ls -A .scratch/band-b-evidence/hand-written
notes.txt
target
```

`resume.sh` refused to remove it, said so on stderr, and the caller's `notes.txt` is still there.
The failure it reports is the loop's own (this hand-written target has no `PROMPT.md`); it is not a
cleanup failure, and it is the reason the example is honest about the exit status.

And `run.sh` refuses rather than emptying it — including when it would have had to create `RUN_DIR`
to hold a pre-populated `TARGET`:

```console
$ mkdir -p .scratch/band-b-evidence/pre-populated-target/target
$ printf 'hand written\n' > .scratch/band-b-evidence/pre-populated-target/target/notes.txt
$ RUN_DIR=.scratch/band-b-evidence/pre-populated-target/runs/new \
    TARGET=.scratch/band-b-evidence/pre-populated-target/target \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 1
run.sh: refusing to use .scratch/band-b-evidence/pre-populated-target/target: TARGET already has content
        Point RUN_DIR (and TARGET) at a new or empty path, or remove it yourself.
        run.sh does not empty a directory it cannot prove it created.

$ echo "exit=$?"
exit=2
$ ls -A .scratch/band-b-evidence/pre-populated-target/runs
$ ls -A .scratch/band-b-evidence/pre-populated-target/target
notes.txt
```

Two things to read out of that: the `runs/` directory `run.sh` had already created for itself is
gone again (it was marked, so the script removed it when it refused), and the caller's `target/`
and its `notes.txt` are untouched.

### A directory the script created is still removed

```console
$ RUN_DIR=.scratch/band-b-evidence/created-by-run \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 1
...
iteration cap reached; the plan is still open. See the loop output above.
target: .scratch/band-b-evidence/created-by-run/target
$ echo "exit=$?"
exit=3
$ ls -d .scratch/band-b-evidence/created-by-run 2>&1
ls: .scratch/band-b-evidence/created-by-run: No such file or directory
```

And the same for `resume.sh`, which is the process that usually cleans up a kept run:

```console
$ RUN_DIR=.scratch/band-b-evidence/created-kept KEEP=1 STOP_AFTER_ITERATION=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
...
stopped at the seam: iteration 1 is committed, the run is not finished.
kept: .scratch/band-b-evidence/created-kept/target
$ ls -A .scratch/band-b-evidence/created-kept
.band-b-scratch
target
$ RUN_DIR=.scratch/band-b-evidence/created-kept \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
...
verified: the target matches the plan
kept: .scratch/band-b-evidence/created-kept/target
$ echo "exit=$?"
exit=0
$ ls -d .scratch/band-b-evidence/created-kept 2>&1
ls: .scratch/band-b-evidence/created-kept: No such file or directory
```

The marker is visible in the second listing — that is the file that tells `resume.sh` the directory
is its own to remove. `KEEP=1` still wins in both scripts, which is how the flows in §1–§5 kept
their directories long enough to be diffed.

---

## 8. What ran, and what did not

**Ran, on this machine, in this worktree:**

- The reference flow (`run.sh 10`, one process, completion in 6 iterations) — exit 0, verified.
- The kill-and-resume flow (`STOP_AFTER_ITERATION=3`, then `resume.sh`) — both exits 0, verified,
  and byte-identical to the reference.
- The mid-turn flow (`STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1`, then `resume.sh`) — memo
  reconstructed, turn not repeated, byte-identical to the reference.
- The memo-hit flow (`STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1`, then `resume.sh`) — memo read,
  step skipped, byte-identical to the reference.
- The cap flow (`run.sh 3` stopping at 2, then `resume.sh`) — resumed run stopped at 3, exit 3.
- The fresh-cap attempt (`resume.sh 9` against that run) — stored cap won.
- The divergence flow (`run.sh 10` stopping at 2, hand-edited `PLAN.md`, `resume.sh`) — exit 0,
  verified.
- The in-flight flow (`STOP_AFTER_ITERATION=2 STOP_MID_TURN=1`, then `resume.sh`) — process 1
  exits 1 with the task left at `turn-started`; process 2 fails the run with the message in §3c,
  after printing what the store holds.
- Both marker directions (§7), against hand-written and script-created directories.
- `dotnet build` of `durable/ralph-loop-durable.cs` — clean.

**Did not run:**

- **No SIGKILL.** Every seam is an environment variable and a clean unwind, as the issue asks. A
  SIGKILL would demonstrate the JSONL store's torn-tail recovery, which is the store's guarantee
  (covered elsewhere), not this loop's resume behaviour.
- **One model only.** `gpt-5.1-codex-mini`, the baseline's pinned model. The flows are
  model-independent in shape, but a different model would produce different item groupings per
  pass, so every tool-call count and outcome string in this file would differ.
- **No comparison run of the baseline in this evidence file.** The baseline's own run is captured in
  the band README and its B1 evidence; B3 (#151) is where the two are put side by side.

---

## 9. What would have to change in `lib/Durable.cs`

Nothing had to. The variant uses the existing surface as-is:

- `DurableStore.Create(new DurableStoreOptions { Path, Format = Jsonl })` — the persistent store.
- `DurableHarness.OpenAsync(store, registry)` + an `Extension` carrying a `DurableTaskDefinition`.
- `DurableTask.DefinePhases<TInput, TCheckpoint>` — one `iteration` phase, entered repeatedly.
- `runtime.CommitAsync(() => TaskCommit.Running(...))` — the checkpoint before and after the turn.
- `runtime.MemoAsync(key)` / `runtime.MemoAsync(key, value)` — the step idempotency guard.
- `harness.WaitForTaskAsync` — the run, and the resume of a `Running` task.
- `DurableStore.Documents` / `DocumentState.Versions` / `ConversationCheckpoint` — reading the
  layer's own checkpoint history back, which is what §3c's report is built from.
- The task's stored `Input` — the run's budget, which is what makes the cap survive the process.

Two things were learned about the layer while writing it, and neither is a gap:

1. **`MemoAsync` is not transactional with a checkpoint.** `MemoAsync(taskId, key, value)` commits
   on its own, outside any `Transaction`. So the order of operations *is* the crash-safety design:
   commit the checkpoint first, write the memo second, and treat the checkpoint as the authority
   when the memo is missing. The reverse order loses the effect; this order only loses the memo, and
   the memo can be rebuilt. The variant encodes that order explicitly.
2. **`MemoAsync` writes are keyed per task, not per conversation.** A memo key is only unique within
   one task, so `step/3/agent-turn` means iteration 3 of *that run*. Two runs over the same target
   with two tasks would not collide — which is right, and worth knowing before choosing a key that
   looks global.

One limitation is recorded here rather than patched into the layer, and it is narrower than "the
layer cannot tell what a dead turn did":

- The layer **does** record each completed tool call (`RecordToolCheckpoint`, `lib:4271`) on the
  conversation's `durable.checkpoint` document, and that document is `DocumentHistory.Rewindable`,
  so a finished turn's calls are readable afterwards.
- What it does **not** do is commit those records at the point of the call. They are queued per
  conversation and awaited by the completion barrier at the end of the turn
  (`CheckpointBarrierAsync`, `lib:4150`), so a process that dies mid-turn can take uncommitted tool
  records with it. §3c captures exactly that: the turn was issued, one tool call completed, and the
  store holds neither the call nor the turn.
- And it does **not** offer replay. The SDK will not continue a half-finished turn, so
  `Stage = "turn-started"` is a state the variant can describe but not recover from.

Closing the second point would need either per-tool-call replay in the SDK, or a tool record
written *before* each call and settled after it — a durable cursor rather than a history. That is a
real feature request, and it is written up in the PR for #150 rather than attempted in this tree.
