# Band B Durable variant — kill and resume

Captured output of the Durable variant of the Band B job agent (issue #150), run on this machine
against a live Copilot CLI. Nothing below is retyped: each block is the command shown, with the
output it produced.

| Field | Value |
| --- | --- |
| Worktree | `/Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150` |
| Branch | `squad/150-b2-durable` (from `origin/dev` @ `5c6d167`) |
| `dotnet` | `/opt/homebrew/bin/dotnet` 10.0.402 |
| Copilot CLI | 1.0.105 |
| Model | `gpt-5.1-codex-mini` (the baseline's pinned model) |
| Loop source | `durable/ralph-loop-durable.cs` |
| Runner | `durable/run.sh` (process 1), `durable/resume.sh` (every later process) |
| Scratch root | `.scratch/band-b-evidence/` in the worktree root; kept here with `KEEP=1` |
| Verifier | `baseline/verify.sh` — the baseline's, reused, not re-written |

Four flows were run, each in its own scratch directory, each with a store of its own:

| Directory | Flow |
| --- | --- |
| `reference/target` | Uninterrupted. One process, cap 10, runs to completion. |
| `kill-resume/target` | Process 1 stops after iteration 3 commits; process 2 resumes and finishes. |
| `mid-turn/target` | Process 1 stops between iteration 2's turn commit and its step memo; process 2 resumes. |
| `cap/target` | Cap 3, process 1 stops after iteration 2, process 2 resumes and stops at the run's cap. |
| `divergence/target` | `PLAN.md` edited by hand between the two processes. |

---

## 1. The headline: kill after iteration N, resume at N+1

### Process 1 — `STOP_AFTER_ITERATION=3 ./run.sh 10`

```console
$ RUN_DIR=.scratch/band-b-evidence/kill-resume KEEP=1 STOP_AFTER_ITERATION=3 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10
target:    .scratch/band-b-evidence/kill-resume/target
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)

target:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target
store:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target/.durable/store.jsonl
model:     gpt-5.1-codex-mini (pinned)
plan:      /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target/PLAN.md
stopping: after iteration 3 commits (STOP_AFTER_ITERATION)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
max:       10 iterations
run:       new durable task t8 (cap 10, written to the store)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 1/10 ===
  plan digest: 3e60cf2491efcc1c
  agent-turn: no memo step/1/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/1/agent-turn written
  reconcile: no plan item changed; 10 item(s) still open
iteration 1 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 2/10 ===
  plan digest: af36a70803b9c0e2
  agent-turn: no memo step/2/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
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
  reconcile: no plan item changed; 6 item(s) still open
iteration 3 complete.
stopping: iteration 3 is committed (STOP_AFTER_ITERATION=3).

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.

stopped at the seam: iteration 3 is committed, the run is not finished.
resume with: KEEP=1 RUN_DIR=.scratch/band-b-evidence/kill-resume ./resume.sh
target: .scratch/band-b-evidence/kill-resume/target
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

target:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target
store:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target/.durable/store.jsonl
model:     gpt-5.1-codex-mini (pinned)
plan:      /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-150/.scratch/band-b-evidence/kill-resume/target/PLAN.md
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 4
last stage: iteration-complete, plan digest 603155e8dfaf31ae
  recorded step: agent-turn — I finished both items for this pass, re-read both files to confirm the changes are on disk, and ticked them in `PLAN.md`… (3 tool calls)
  recorded step: reconcile — no plan item changed; 6 item(s) still open
memos on record: 6
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 4/10 ===
  plan digest: 603155e8dfaf31ae
  agent-turn: no memo step/4/agent-turn — running the turn
  ⚙ bash
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
  ⚙ bash
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
  plan digest: 936e4e5d11031cb7
  agent-turn: no memo step/6/agent-turn — running the turn
  ⚙ bash
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
      "outcome": "Both items are finished, and I re-read the files to confirm the changes are on disk. (5 tool calls)"
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

## 3. The memo boundary, and the honest limit

### 3a. A stop between the step's effect and its memo

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
  ⚙ bash
  ⚙ bash
  ⚙ edit
stopping: iteration 2's turn is complete, the step memo is not written.

run:       stopped at a committed iteration boundary.
           the store holds the checkpoint; the next process resumes from it.

stopped at the seam: iteration 2 is committed, the run is not finished.
```

```console
$ RUN_DIR=.scratch/band-b-evidence/mid-turn KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
...
max:       10 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 2
last stage: turn-complete, plan digest af36a70803b9c0e2
  recorded step: agent-turn — I added the H1 to `docs/section-01.md` (`# Section 01`) and `docs/section-02.md` (`# Section 02`) and ticked both items … (6 tool calls)
memos on record: 2
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 2/10 ===
  plan digest: bbef84d1f745f802
  agent-turn: memo step/2/agent-turn is missing but the checkpoint records the turn
  agent-turn: restoring the memo from the checkpoint — not run again
  reconcile: no plan item changed; 8 item(s) still open
iteration 2 complete.
...
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

### 3b. The limit: a turn that was *issued* and never finished

The layer cannot recover a turn that was in flight, and this variant does not pretend to. If the
process dies after the `turn-started` checkpoint and before the `turn-complete` one, the store
knows a turn was issued but not what it did. The resume from that state:

```console
iteration N was in flight: the turn was issued (plan digest <digest>) and its outcome was never
recorded, so it may have mutated files. Re-running it would duplicate those mutations. Recreate
the target from baseline/target/ and re-run.
```

The run fails with that message rather than re-running the turn. That is deliberate: re-running is
precisely the silent duplication the issue warns about, and no evidence in the store distinguishes
"the turn did nothing yet" from "the turn had written four of five files". The memo key protects
*turns*; it cannot protect a *tool call inside a half-finished turn*, because the durable layer
records a turn as a conversation checkpoint only after it completes. Making that boundary finer
would need per-tool-call replay, which the SDK does not offer — see
"What would have to change in `lib/Durable.cs`" below.

This is a real property of the layer, and it belongs in the comparison: the baseline has no
recovery at all here (it re-reads the markdown and starts a fresh iteration), and the Durable
variant has a *known* boundary rather than no boundary. Neither can replay a half-finished turn.

---

## 4. Bounded: a resumed run does not get a fresh cap

### Process 1 — cap 3, stop after iteration 2

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 STOP_AFTER_ITERATION=2 \
    ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 3
...
max:       3 iterations
run:       new durable task t8 (cap 3, written to the store)
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
```

### Process 2 — resume

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh
...
max:       3 iterations (from the run's stored input)
run:       resuming durable task t8 (restarts before this one: 0)
resuming at iteration 3
last stage: iteration-complete, plan digest bbef84d1f745f802
...
=== Iteration 3/3 ===
  plan digest: bbef84d1f745f802
  agent-turn: no memo step/3/agent-turn — running the turn
  ⚙ bash
  ⚙ bash
  ⚙ bash
  ⚙ bash
  agent-turn: done; memo step/3/agent-turn written
  reconcile: no plan item changed; 6 item(s) still open
iteration 3 complete.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
iteration cap reached: 3 iterations and the plan is not complete.
first open item: - [ ] `docs/section-05.md`: add the missing H1
```

It stops at 3, not at 5 (2 + 3). The cap lives in the task's stored input, which the first process
wrote once.

A resumed process that tries to supply a different cap is told so and the stored value wins:

```console
$ RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 \
    ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh 9
...
max:       3 iterations (from the run's stored input)
ignored:   -- 9 is not this run's cap; the store says 3.
run:       task t8 is already finished; nothing to resume.

iteration cap reached: 3 iterations and the plan is not complete.
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

1. the open items were **reordered** (`section-09` first, `section-03` last);
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
  recorded step: agent-turn — I added the H1 to `docs/section-01.md` and `docs/section-02.md`, and ticked both items in `PLAN.md`. I re-read both file… (6 tool calls)
  recorded step: reconcile — no plan item changed; 8 item(s) still open
memos on record: 4
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
=== Iteration 3/10 ===
  plan digest: 2038fb57aed54f66
  agent-turn: no memo step/3/agent-turn — running the turn
...
plan reports complete after 6 iteration(s).

verified: the target matches the plan
```

Three things are visible in that output, and each is the point of constraint 3:

- **The position came from the store.** `resuming at iteration 3` and the two recorded step
  outcomes are the store's, not the file's. The loop did not restart because the markdown looked
  unfinished, and it did not re-derive its position from the checkboxes.
- **The new digest was picked up as input.** The plan's digest changed from `bbef84d1f745f802`
  (recorded by process 1) to `2038fb57aed54f66` (the edited file), and iteration 3 worked the
  edited file's list in the edited file's order — `section-09` first, `section-03` last.
- **The hand-ticked lie was corrected.** `docs/section-08.md` was ticked but had no H1; the
  reconcile step unticked it, so the run did the work and only then ticked it again. The final plan
  has every item `[x]` and every condition holds on disk — `verify.sh` passed.

The final `PLAN.md` kept the user's order, which is the correct outcome: the store knows what was
*done*, the file says what is *wanted*, and a resume reconciles the second against the first.

---

## 6. Custom durability code

The Band B matrix row asks how many lines of the Durable variant exist only because the layer has
to keep durable state.

**Counting rule.**

- Count code lines in `durable/ralph-loop-durable.cs` only — not the shell runners, not
  `lib/Durable.cs`.
- Blank lines and comment lines (`//`, and the `#:` file directives) are excluded from both buckets.
- A code line counts as **durability** when its only reason to exist is durable state: opening or
  disposing the store, creating or finding the task, deciding first-run versus resume, reading the
  run's stored input, writing or reading a checkpoint, writing or reading a memo, detecting an
  in-flight step, or a type whose sole purpose is to be serialized into the store.
- A code line counts as **application** when it would exist without durability: argument and
  environment parsing, the resume banner's console output, prompt construction, the SDK turn call,
  the reconcile conditions, the plan and heading parsers, and the formatting normalizer.

Applied to the file, region by region (line ranges in the committed source):

| Region | Code lines | Durability |
| --- | --- | --- |
| File header: `#:` directives and `using` | 5 | 0 |
| `DurableLoopAsync` — env/args, store creation, harness open, first-run branch, task creation | 32 | 32 |
| `DurableLoopAsync` — resume branch: stored input, terminal check, checkpoint read, resume banner | 25 | 25 |
| `DurableLoopAsync` — `try`/`finally` around the store | 4 | 4 |
| `AwaitRunAsync` — await the runner, the stopped-at-seam path | 20 | 10 (half is output) |
| `Definition` — the task definition and its one phase | 13 | 13 |
| `RunIterationAsync` — completion and cap from the stored input | 15 | 12 |
| `RunIterationAsync` — in-flight detection, checkpoint before the turn | 24 | 24 |
| `RunIterationAsync` — memo read/branch/write, checkpoint after the turn, seams | 44 | 44 |
| `RunIterationAsync` — reconcile call, settle checkpoint, stop-at-seam | 19 | 19 |
| `RunAgentTurnAsync`, `Choices`, `BuildTurnPrompt` | 48 | 0 |
| `Reconcile` + conditions + `NormalizeSectionHeadings` | 207 | 0 |
| Helpers (`StepKey`, `Digest`, `IsComplete`, `OpenItems`, …) | 20 | 8 |
| `Job` constants and the `LoopInput`/`LoopCheckpoint`/`StepOutcome`/`LoopResult` records | 28 | 28 |
| **Total** | **570** | **201** |

**201 of 570 code lines — 35% — exist only for durability.** The baseline has **0**:
`baseline/ralph-loop.cs` contains no store, no task, no checkpoint, no memo, and no resume path; its
entire progress model is the `iteration` local variable plus the markdown file.

The excluded majority is not free, and the table says where it went: the largest block is the
reconcile verifier (207 lines), which in the baseline is `verify.sh` instead of C#. Counting it
against durability would inflate the row; counting it as zero would hide that moving verification
into the loop is where most of this file's size comes from. It is application logic by the rule
above, and it is named here so the number can be re-derived.

---

## 7. What ran, and what did not

**Ran, on this machine, in this worktree:**

- The reference flow (`run.sh 10`, one process, completion in 6 iterations) — exit 0, verified.
- The kill-and-resume flow (`STOP_AFTER_ITERATION=3`, then `resume.sh`) — both exits 0, verified,
  and byte-identical to the reference.
- The mid-turn flow (`STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1`, then `resume.sh`) — memo
  reconstructed, turn not repeated, byte-identical to the reference.
- The cap flow (`run.sh 3` stopping at 2, then `resume.sh`) — resumed run stopped at 3, exit 3.
- The fresh-cap attempt (`resume.sh 9` against that run) — stored cap won.
- The divergence flow (`run.sh 10` stopping at 2, hand-edited `PLAN.md`, `resume.sh`) — exit 0,
  verified.
- `dotnet build` of `durable/ralph-loop-durable.cs` — clean.

**Did not run:**

- **No SIGKILL.** The seam is an environment variable and a clean unwind, as the issue asks. A
  SIGKILL during a commit would demonstrate the JSONL store's torn-tail recovery, which is the
  store's guarantee (covered elsewhere), not this loop's resume behaviour.
- **The in-flight-turn failure path was not captured end to end.** Producing it needs a stop after
  the `turn-started` commit and before the turn completes, and every seam this loop exposes stops at
  a boundary the loop chooses. The failure message in §3b is the string the phase commits; it was
  read from the source, not from a run. §3a is the captured case, and it is the one a memo can
  recover from.
- **One model only.** `gpt-5.1-codex-mini`, the baseline's pinned model. The flows are model-
  independent in shape, but a different model would produce different item groupings per pass.
- **No comparison run of the baseline in this evidence file.** The baseline's own run is captured in
  the band README and its B1 evidence; B3 (#151) is where the two are put side by side.

---

## 8. What would have to change in `lib/Durable.cs`

Nothing had to. The variant uses the existing surface as-is:

- `DurableStore.Create(new DurableStoreOptions { Path, Format = Jsonl })` — the persistent store.
- `DurableHarness.OpenAsync(store, registry)` + an `Extension` carrying a `DurableTaskDefinition`.
- `DurableTask.DefinePhases<TInput, TCheckpoint>` — one `iteration` phase, entered repeatedly.
- `runtime.CommitAsync(() => TaskCommit.Running(...))` — the checkpoint before and after the turn.
- `runtime.MemoAsync(key)` / `runtime.MemoAsync(key, value)` — the step idempotency guard.
- `harness.WaitForTaskAsync` / `harness.ResumeAsync` — the run, and the resume of a `Running` task.
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

One thing the layer cannot express — recorded here rather than patched into the layer: **there is
no idempotency primitive at a granularity finer than the caller's own step.** `MemoAsync` gives a
keyed check-then-act, but nothing that lets a host say "this tool call, inside this turn, already
ran". The layer records a turn as a conversation checkpoint after the turn completes
(`DurableConversation.CheckpointAsync` fires on `SessionIdleEvent`), so a turn that dies halfway
leaves neither a memo nor a checkpoint. This variant does not work around that: it detects the
state and fails the run rather than replaying the turn (§3b). Closing it would need either
per-tool-call replay in the SDK, or a durable "tool intent" record the layer writes before each
tool execution and settles after it — a real feature request, not a scenario fix. It is written up
in the PR for #150 and not attempted in this tree.
