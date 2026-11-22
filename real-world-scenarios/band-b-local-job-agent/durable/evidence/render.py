"""Assembles evidence/durable-kill-and-resume.md from the recordings run-all.sh wrote.

Every console block in the output is a verbatim lift of a recording: the command lines, their
output, and their exit status, in the order the recording has them. A block that the recording
truncated is marked `...` and the prose says which part is missing.

    ./durable/evidence/run-all.sh && ./durable/evidence/render.py

The prose is written here; the output never is.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# durable/evidence -> band-b-local-job-agent -> real-world-scenarios -> worktree
BAND = os.path.abspath(os.path.join(HERE, "..", ".."))
ROOT = os.path.abspath(os.path.join(BAND, "..", ".."))
LOG = os.path.join(ROOT, ".scratch", "logs")
OUT = os.path.join(BAND, "evidence", "durable-kill-and-resume.md")


# Every recording a rendered console block depends on. A block whose recording is absent or empty
# is a block that would have to be typed, which is the thing this file exists to prevent.
EXPECTED_RECORDINGS = (
    "reference.txt", "kill-resume-1.txt", "kill-resume-2.txt",
    "mid-turn-1.txt", "mid-turn-2.txt", "memo-hit-1.txt", "memo-hit-2.txt",
    "cap-1.txt", "cap-2.txt", "cap-3.txt", "divergence-1.txt", "divergence-2.txt",
    "in-flight-1.txt", "in-flight-2.txt",
    "diff-kill-resume.txt", "diff-mid-turn.txt", "diff-memo-hit.txt",
    "kill-resume-store.txt", "in-flight-store.txt", "cap-restarts.txt", "edit-plan.txt",
    "control-hand-written.txt", "control-pre-populated.txt",
    "control-removed.txt", "control-resumed.txt",
)


def log(name):
    with open(os.path.join(LOG, name)) as handle:
        return handle.read()


def from_line(text, marker):
    return text[text.index(marker):]


def cut(text, *stops):
    for stop in stops:
        if stop in text:
            text = text[:text.index(stop)]
    return text.rstrip("\n")


def flow(name):
    """A whole flow transcript: the command, its output, and its exit status."""
    return log(name + ".txt").rstrip("\n")


def recorded(name):
    return log(name).rstrip("\n")


BANNER = "\u2501" * 60


def main():
    reference = flow("reference")
    diff_kill, diff_mid, diff_memo = (recorded(p) for p in
                                      ("diff-kill-resume.txt", "diff-mid-turn.txt", "diff-memo-hit.txt"))
    edit_plan = recorded("edit-plan.txt")
    k1, k2 = flow("kill-resume-1"), flow("kill-resume-2")
    m1, m2 = flow("mid-turn-1"), flow("mid-turn-2")
    h1, h2 = flow("memo-hit-1"), flow("memo-hit-2")
    c1, c2, c3 = flow("cap-1"), flow("cap-2"), flow("cap-3")
    d2 = flow("divergence-2")
    i1, i2 = flow("in-flight-1"), flow("in-flight-2")

    plan_at_seam = cut(k1.split("\n=== Iteration 4")[0], "\nstopped at") if "=== Iteration 4" in k1 else ""
    edited = open(os.path.join(ROOT, ".scratch", "band-b-evidence", "divergence", "target",
                               "PLAN.md")).read().strip("\n")

    body = f"""# Band B Durable variant — kill and resume

Captured output of the Durable variant of the Band B job agent (issue #150), run on this machine
against a live Copilot CLI.

**Provenance.** Every console block below is a verbatim lift from a recording that
`durable/evidence/run-all.sh` wrote, pasted by `durable/evidence/render.py` — the script that wrote
this file. A recording holds the command, its output, and its exit status, in order, and a block is
one whole recording: not a command retyped beside output, and not two invocations stitched
together. Nothing in a console block is hand-written.

That invariant is enforced rather than promised: `render.py` verifies every console block ends with
an `exit=` line and that every recording it needs is present and non-empty, and exits non-zero
without writing this file if either fails. Re-run `run-all.sh` to regenerate the recordings.

| Field | Value |
| --- | --- |
| Worktree | `{ROOT}` |
| Branch | `squad/150-b2-durable` |
| `dotnet` | `/opt/homebrew/bin/dotnet` 10.0.402 |
| Copilot CLI | 1.0.105 |
| Model | `gpt-5.1-codex-mini` (the baseline's pinned model) |
| Loop source | `durable/ralph-loop-durable.cs` |
| Runner | `durable/run.sh` (process 1), `durable/resume.sh` (every later process) |
| Scratch root | `.scratch/band-b-evidence/` in the worktree root; kept with `KEEP=1` |
| Verifier | `baseline/verify.sh` — the baseline's, reused, not re-written |
| Recorder | `durable/evidence/run-all.sh`; readers in `durable/evidence/*.py` |
| Assembler | `durable/evidence/render.py` |

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

---

## 1. The headline: kill after iteration N, resume at N+1

### Process 1 — `STOP_AFTER_ITERATION=3 ./run.sh 10`

```console
{k1}
```

The process is a real process and it exits on its own. `STOP_AFTER_ITERATION` is not a signal
handler: the phase commits iteration 3, prints the line above, and throws on its own cancellation
token, so the runner unwinds and the process exits 0 with the task left `Running` at the
checkpoint. Nothing is shared with the next process but the store file.

### Process 2 — `./resume.sh`

```console
{k2}
```

`resuming at iteration 4` is read from the store's checkpoint. The resume process is given no
iteration number, no cap, and no plan digest on its command line.

`restarts before this process: 0` is the count on the record as this process reads it. The runner
increments `Restarts` when it re-enters a `Running` task (`lib/Durable.cs:5226`), which happens
after this banner prints, so the store holds `1` immediately afterwards — §4 shows that number read
back.

---

## 2. Idempotency: the resumed run's file mutations equal an uninterrupted run's

### The reference run

```console
{reference}
```

The recording is whole: all six iterations, from the same run.

### The diff

```console
{diff_kill}
```

**Empty.** The kill-and-resume flow produced a target byte-identical to the uninterrupted flow's:
`PLAN.md`, `release-notes.md`, and all ten `docs/section-*.md` files. The `.durable/` store is
excluded because it is the record, not the product — the two stores differ by construction (one
had two processes open it, the other had one).

### What the store holds

The task record and the per-conversation document history of the kill-and-resume store, read by the
two readers in `durable/evidence/`:

```console
{recorded("kill-resume-store.txt")}
```

Twelve memo keys for six iterations — one `agent-turn` and one `reconcile` per iteration, numbered
from 1. They span two processes: iterations 1–3 were run by process 1, 4–6 by process 2, and the
key for iteration *n* is the same string whichever process wrote it. Every conversation that sent a
prompt ends its checkpoint history at `turn` and its admission is `completed` — that is a run in
which every turn returned, and it is the contrast §3c needs.

---

## 3. The memo boundary: present, hit, and the honest limit

### 3a. A stop between the turn's commit and its memo

The interesting crash is not "between iterations"; it is "after the turn mutated files, before its
step memo was written". `STOP_BEFORE_MEMO=1` puts the seam exactly there.

```console
{m1}
```

The recording is whole: both iterations, from the same run.

```console
{m2}
```

The turn is **not** re-run. The checkpoint written immediately after the turn carries the turn's
outcome, so the resume reconstructs `step/2/agent-turn` from the checkpoint instead of re-issuing
it. That is why the checkpoint-after-the-turn exists at all: it is what makes the memo recoverable
when the process dies between the commit and the memo write.

And its files are identical to the reference run's:

```console
{diff_mid}
```

### 3b. A memo hit: the guard working, not merely present

3a shows the memo being *rebuilt*. This flow shows it being *read*: the seam stops after the
turn's memo is written and before the iteration settles, so the resume re-enters iteration 2 with
the memo already in the store and skips the step.

```console
{h1}
```

```console
{h2}
```

The line `agent-turn: memo step/2/agent-turn is present — not run again` is the branch at
`ralph-loop-durable.cs:321`: the memo was read, the turn was not issued, and the recorded outcome
was carried into the settle checkpoint. The iteration's files are still identical to the
reference run's:

```console
{diff_memo}
```

Two things are worth separating here, because they are different mechanisms:

- **3a is recovery.** No memo exists; the checkpoint proves the turn happened; the memo is rebuilt
  from it. The guard is what stops that rebuild from becoming a second turn.
- **3b is the guard itself.** The memo exists; the step is skipped on the strength of it alone.

### 3c. The limit: a turn that was issued and never finished

The layer writes a `tool` checkpoint into the conversation's `durable.checkpoint` document for every
completed tool call (`RecordToolCheckpoint`, `lib/Durable.cs:4271`), and the document is
`DocumentHistory.Rewindable` (`lib:4590`), so a run that finishes keeps its per-turn history — §2
shows that history for the kill-and-resume store.

That is not the same as keeping the calls of a turn that **never returned**. `RecordToolCheckpoint`
hands each record to `CheckpointAsync` (`lib:4126` → `QueueCheckpointAsync`, `lib:4074`), which
queues it on the conversation's serial chain and returns; the only drain is `CheckpointBarrierAsync`
(`lib:4150`), which `SubmitAsync` reaches at `lib:4246`, *after* `if (result is not null)`. A turn
that is cancelled or throws never gets there, and neither the `catch` at `lib:4251` nor the
`finally` at `lib:4259` drains the chain. The queued records are discarded with the conversation.

So the store's account of a dead turn is:

- the loop's own `turn-started` checkpoint, written by the phase before it issued the turn;
- the turn's `durable.admission`, at `admitted`, because `AdmitAsync` commits before the prompt is
  sent (`lib:4019`);
- **no** `durable.checkpoint` document for that conversation at all — not a partial list of tool
  calls, none — and no `turn` record.

`STOP_MID_TURN=1` reproduces the state end to end: the process leaves while iteration 2's turn is
running. Here is the store as the resume will find it:

```console
{recorded("in-flight-store.txt")}
```

`c9` is iteration 1's conversation, which finished: four tool calls and a turn. `c37` is the dead
turn: one admission, `admitted`, and no `durable.checkpoint` line at all — which is the whole of
what "no tool record was committed" means here.

```console
{i1}
```

Process 1 leaves the task at `turn-started` and exits 1 — the runner reports `failed task did not
finish`, which is the run being torn down mid-phase rather than an outcome the loop chose. The
store is whole and closed normally, so the next process opens it like any other run.

```console
{i2}
```

The report is accurate about all three facts:

- **The turn is partly represented.** The admission and the `turn-started` checkpoint exist, so the
  store can say *which* iteration was in flight and what the plan looked like when it started.
- **No tool call is committed.** Not "some of them landed" — the queue never drains for a turn that
  does not return, so there is no partial record to read. The line `the store holds no tool record
  for it` is that fact, and the store dump above is the evidence for it.
- **Not replayable.** The SDK will not continue the turn, so re-issuing it would repeat whatever it
  did. The run fails instead, naming the iteration, and asks for the target to be recreated.

Re-entering a *finished* turn is the different case, and the store answers it directly: the turn's
checkpoint carries its outcome, so the resume reports what that turn did. That is 3a, and it is the
demonstrable half. 3c is the half that cannot be recovered, and it is the reason the variant fails
rather than retries.

What would close it is a primitive the layer does not have, and the gap is worth stating precisely:

- the **replay** half needs per-tool-call continuation in the SDK;
- the **record** half needs a write that is durable at the call rather than at the turn's end — a
  record written *before* each call and settled after it, so a resume could tell an interrupted
  call from one that never started. The layer's current record is a history drained at success, not
  a cursor.

Both are real feature requests, written up in the PR for #150 rather than attempted in this tree.

---

## 4. Bounded: a resumed run does not get a fresh cap

### Process 1 — cap 3, stop after iteration 2

```console
{c1}
```

### Process 2 — resume

```console
{c2}
```

It stops at 3, not at 5 (2 + 3). The cap lives in the task's stored input, which the first process
wrote once. This is also the flow the banner's restart wording is about: the resume prints `0`
because it reads the record before re-entering the task, and the record says:

```console
{recorded("cap-restarts.txt")}
```

A resumed process that tries to supply a different cap is told so and the stored value wins:

```console
{c3}
```

So the run is bounded by its own budget, and a finished run stays finished: re-entering a terminal
task reports the recorded outcome instead of starting new work.

---

## 5. The plan file is input, not the record

Between process 1 and process 2 of the divergence flow, `PLAN.md` was edited by hand by
`durable/evidence/edit-plan.py`. The store was not touched.

```console
{edit_plan}
```

**The plan the user edited it into** — three deliberate divergences: the open items were
**reordered** (`section-09` and `section-08` first, `section-03` last), `docs/section-08.md` was
**ticked by hand** although its H1 is still missing, and the store still records iterations 1–2 as
complete:

```markdown
{edited}
```

**The resume** — `./resume.sh`:

```console
{d2}
```

Three things are visible in that output, and each is the point of the constraint:

- **The position came from the store.** `resuming at iteration 3` and the two recorded step
  outcomes are the store's, not the file's. The loop did not restart because the markdown looked
  unfinished, and it did not re-derive its position from the checkboxes.
- **The new digest was picked up as input.** The plan's digest changed from `bbef84d1f745f802`
  (recorded by process 1) to `d6b06db0ad2a02e5` (the edited file), and iteration 3 worked the
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
- Blank lines and comment lines (`//`, and the `#:` file directives) are excluded. The regions below
  are disjoint and their union is every such line in the file: **TABLE_REGIONS regions,
  TABLE_TOTAL code lines**. Re-running the region boundaries over the committed file reproduces both columns
  exactly.
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

TABLE

The durability column sums to **TABLE_DUR** and the application column to **TABLE_APP**; together
they are **TABLE_TOTAL**, the file's own code-line count.

**TABLE_DUR of TABLE_TOTAL code lines — TABLE_PCT% — exist only for durability.** The baseline has
**0**: `baseline/ralph-loop.cs` contains no store, no task, no checkpoint, no memo, and no resume
path; its entire progress model is the `iteration` local variable plus the markdown file.

Two rows deserve a note, because they are where a reader is most likely to disagree:

- **`ReportInFlightTurn` is durability.** It exists only to read the layer's own checkpoint history
  back, so those lines would not exist without a store. It is the largest single durability block,
  and it is the report §3c rests on.
- **`Reconcile` is application.** It is the target's contract expressed in C#; the baseline keeps
  the same contract in `baseline/verify.sh` instead. Moving it between languages moves the number
  without changing what the loop does.

`NormalizeSectionHeadings` sits inside the `Reconcile` row and is classified **application**. Its
own comment says pinning the blank line after the H1 "is what makes two correct runs
byte-identical", which reads like a durability concern; it is not. The property it protects is the
*comparison* in §2 — two baseline runs need the same normalizer to be comparable — and it describes
the product's required formatting rather than anything the store does. Move the loop off the
durable layer and the same lines remain. Recording the judgement here is the point: reclassifying
it would move those lines and change nothing about the comparison the number is for.

---

## 7. Scratch provenance: nothing is emptied or removed that the script did not create

`run.sh` and `resume.sh` both take `RUN_DIR` (and `TARGET`) from the environment and remove the
scratch directory on exit unless `KEEP=1`. That is a destructive default pointed at a caller's
path, so both scripts follow the convention #189 established for Band G: a directory the script
created carries an empty `.band-b-scratch` marker, only a marked directory is removed, and a
`RUN_DIR` or `TARGET` that already holds content is refused with exit 2.

Each control below is **one** recording: every command it takes to establish and observe it — the
run, and the `ls` that proves the outcome — with its output and exit status, in order, inside one
fenced block. Rerun `durable/evidence/run-all.sh` to reproduce them.

### A directory the scripts did not create

```console
{recorded("control-hand-written.txt")}
```

`resume.sh` refused to remove the caller's directory, said why on stderr, and `notes.txt` is still
there. The exit status is 1 because the loop itself failed — `PROMPT.md` is missing. That is worth
being explicit about: this control proves the *cleanup* path, and the directory it proves it on is
one the loop rejects before writing anything, because a first-run `TARGET` with only `PLAN.md` dies
on the missing prompt. It answers "does a hand-written directory survive a failing `resume.sh`",
not "does a real user's target survive". Those are different questions, and only the first is
measured here.

`run.sh` refuses such a directory outright, including when it has to create `RUN_DIR` to hold a
pre-populated `TARGET`:

```console
{recorded("control-pre-populated.txt")}
```

Two things to read out of that: the `runs/` directory `run.sh` had already created for itself is
gone again — it was marked, so the script removed it when it refused — and the caller's `target/`
and its `notes.txt` are untouched.

### A directory the script created

`run.sh`, when it created the directory and the run finished:

```console
{recorded("control-removed.txt")}
```

`resume.sh`, on a run kept across the two processes:

```console
{recorded("control-resumed.txt")}
```

The `.band-b-scratch` marker is visible in the `ls` between the two processes — that is the file
that tells `resume.sh` the directory is its own to remove, and its absence from the hand-written
directory above is why that one survived. `KEEP=1` wins in both scripts, which is how the flows in
§1–§5 kept their directories long enough to be diffed.

---

## 8. What ran, and what did not

**Ran, on this machine, in this worktree**, through `durable/evidence/run-all.sh`:

- The reference flow (`run.sh 10`, one process, completion in 6 iterations) — exit 0, verified.
- The kill-and-resume flow (`STOP_AFTER_ITERATION=3`, then `resume.sh`) — both exits 0, verified,
  and byte-identical to the reference.
- The mid-turn flow (`STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1`, then `resume.sh`) — memo
  reconstructed, turn not repeated, byte-identical to the reference.
- The memo-hit flow (`STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1`, then `resume.sh`) — memo read,
  step skipped, byte-identical to the reference.
- The cap flow (`run.sh 3` stopping at 2, then `resume.sh`) — resumed run stopped at 3, exit 3.
- The fresh-cap attempt (`resume.sh 9` against that run) — stored cap won.
- The divergence flow (`run.sh 10` stopping at 2, `edit-plan.py`, `resume.sh`) — exit 0, verified.
- The in-flight flow (`STOP_AFTER_ITERATION=2 STOP_MID_TURN=1`, then `resume.sh`) — process 1
  exits 1 with the task left at `turn-started`; process 2 reports the store's account (§3c) and
  fails the run.
- The store readers in §2, §3c and §4, over the stores those flows left.
- The four scratch-directory controls (§7) — each one recording, so the `ls` that proves the
  outcome sits in the same fenced block, after the command that caused it, rather than in a block
  of its own.
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

- `DurableStore.Create(new DurableStoreOptions {{ Path, Format = Jsonl }})` — the persistent store.
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
  so a run that finishes keeps its per-turn history.
- The layer does **not** commit those records while the turn is running. Each one is queued on the
  conversation's serial chain (`QueueCheckpointAsync`, `lib:4074`) and the queue is drained by
  `CheckpointBarrierAsync` (`lib:4150`), which `SubmitAsync` reaches at `lib:4246` only when the
  turn returned. A turn that is cancelled or throws never drains it, so it commits no tool record
  at all. §3c's store dump is the measured consequence: the dead turn's conversation has an
  `admitted` admission and no `durable.checkpoint` document, while every finished turn ends at
  `turn`.
- The layer does **not** offer replay. The SDK will not continue a half-finished turn, so
  `Stage = "turn-started"` is a state the variant can describe but not recover from.

Closing that needs two things, and the first is the one a durable host would notice: a tool record
written *before* the call and settled after it, so the store can distinguish an interrupted call
from one that never started; and per-call replay in the SDK, so the turn can continue. A history
drained at success gives neither. That is a real feature request, and it is written up in the PR
for #150 rather than attempted in this tree.
"""

    table, totals = load_table()
    for token, value in totals.items():
        body = body.replace(token, value)
    body = body.replace("TABLE\n", table + "\n")

    verify(body)
    with open(OUT, "w") as handle:
        handle.write(body)
    print("wrote", OUT, len(body), "chars")


def verify(body):
    """Refuse to write a file that breaks the invariant its own header states.

    Every console block must end with an `exit=` line: that is what makes a block a whole
    recording rather than a command retyped beside output. Checked here because the header asserts
    it, and an assertion in prose is enforced by nothing.
    """
    blocks = re.findall(r"```console\n(.*?)```", body, re.S)
    if not blocks:
        raise SystemExit("no console blocks rendered")

    problems = []
    for index, block in enumerate(blocks, 1):
        lines = [line for line in block.rstrip("\n").split("\n") if line.strip()]
        first = lines[0] if lines else "(empty)"
        if not first.startswith("$"):
            problems.append(f"block {index}: does not start with a command: {first[:60]!r}")
        if not lines or not lines[-1].startswith("exit="):
            problems.append(f"block {index}: does not end with an exit status: {first[:60]!r}")

    missing = [name for name in EXPECTED_RECORDINGS
               if not os.path.exists(os.path.join(LOG, name))
               or os.path.getsize(os.path.join(LOG, name)) == 0]
    for name in missing:
        problems.append(f"recording is missing or empty: {name}")

    if problems:
        for problem in problems:
            print("render.py:", problem, file=sys.stderr)
        raise SystemExit(f"{len(problems)} problem(s); not writing {OUT}")

    print(f"verified {len(blocks)} console blocks, {len(EXPECTED_RECORDINGS)} recordings")


def load_table():
    """The §6 table, rendered from the region list so the ranges and sums cannot drift."""
    import sys
    sys.path.insert(0, os.path.dirname(__file__))
    from table import render
    return render()


if __name__ == "__main__":
    main()
