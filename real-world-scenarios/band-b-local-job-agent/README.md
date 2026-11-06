# Band B — the Ralph Loop job agent, and its Durable variant

## The scenario

A long-running unattended agent job on a developer's machine. A plan file lists work; the loop
sends a prompt to a **fresh** session, lets the agent edit files in a scratch directory, re-reads
the plan, and repeats until the plan reports complete. It runs for minutes, makes live model calls,
and holds no server. The question Band B measures is what happens to that job when the process
that owns it goes away in the middle — issue #148.

Both halves are ports of the vendored Ralph Loop recipe (`upstream/`, byte-identical to
`github/awesome-copilot` at `7e40881`, MIT) to C# against the plain GitHub Copilot SDK. **The baseline
is the recipe's own shape, and it is not durable:** the loop's position is a local variable in a running
process, and nothing in `baseline/` imports, references, or asserts on the Durable library. **If you
kill the process mid-iteration, there is no record of which step was running.** The plan file may have
been half-updated; recovery is re-reading the markdown and hoping the last edit was consistent.

## The two variants

Both variants do the same job against the same template and are checked by the same verifier,
`baseline/verify.sh`. Only the progress model differs.

| | `baseline/` | `durable/` |
| --- | --- | --- |
| The loop | `ralph-loop.cs`, one file-based .NET app | `ralph-loop-durable.cs`, the same loop over `lib/Durable.cs` |
| Where the position lives | the `iteration` local variable, plus the markdown file | a Durable task's checkpoint, in a JSONL store at `<target>/.durable/store.jsonl` |
| Resume | none — a new process re-reads the plan and starts at its own iteration 1 | `resume.sh` reopens the store and continues at the iteration the checkpoint names |
| Scratch area | `.scratch/band-b-target/` | `.scratch/band-b-durable/target/` (with the store inside it) |
| Runner | `run.sh` (72 `wc -l`) | `run.sh` (171) + `resume.sh` (106) |

Everything under `baseline/` is one file-based .NET app plus a shell runner. Nothing there
references `lib/Durable.cs`; there is no store, no checkpoint, no task, no admission, and no
conversation document.

`durable/` is the other half of the comparison: the same job, the same prompt, the same
`baseline/verify.sh`, with the loop position in a Durable task instead of a local variable. A
killed run resumes at the next iteration (issue #150). Run both with the commands below.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0 or later | File-based apps (`dotnet run file.cs`). On this machine `/opt/homebrew/bin/dotnet` is 10.0.402 |
| GitHub Copilot CLI | 1.0.103 or later | The SDK drives the CLI; the SDK ships its own copy if none is on `PATH` |
| GitHub Copilot access | a signed-in CLI | The loop makes live model calls |

`run.sh` prefers `/opt/homebrew/bin/dotnet`, then whatever `dotnet` is first on `PATH`. Set
`DOTNET` to override.

The loop pins **`gpt-5.1-codex-mini`** — the model `upstream/ralph-loop.ts` pins. Set
`RALPH_MODEL` to run it against another model:

```bash
RALPH_MODEL=gpt-6-luna ./real-world-scenarios/band-b-local-job-agent/run.sh
```

The run header prints the model it will use and whether the pinned default or `RALPH_MODEL`
supplied it.

## Commands

### Baseline — one process, no resume

```bash
./real-world-scenarios/band-b-local-job-agent/run.sh
```

The runner creates the target directory fresh, runs the loop, verifies the result, and
deletes the target on exit. To keep it:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/run.sh
```

To shorten the run for a smoke test, pass the cap as the first argument. The default is the
constant in `baseline/ralph-loop.cs`. Killed mid-run the baseline starts over: that is the
property this band measures, not a defect to work around.

### Durable — `run.sh`, then `resume.sh`

Two processes, and the second one is the point:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh   # only after a stop
```

`run.sh` and `resume.sh` remove the scratch directory on exit unless `KEEP=1`, so a two-process
sequence keeps it across both. Neither script will empty or delete a directory it did not create: it
marks its own with `.band-b-scratch` and refuses (exit 2) a `RUN_DIR` or `TARGET` that already has
content.

Both variants together, from a clean checkout, are under ten minutes on this machine — most of it is
the model's tool calls, not the build. The measurement behind that: the fault harness ran **21 harness
runs** across both variants — five faults plus nine controls, each a complete job, and two run-equivalence
tests — in the 19 minutes between 03:33:43Z and 03:52:17Z. A baseline job is one model pass on the
template. The Durable variant's kill-and-resume seam is an environment variable rather than a
wait-for-it, so it runs the same way every time; see the `STOP_*` table below.

## Teardown

| What | How |
| --- | --- |
| Either loop | Ctrl-C. The Durable variant leaves the task `Running` at its last checkpoint, so a later `resume.sh` continues it |
| The scratch target | on exit, unless `KEEP=1`. With `KEEP=1`, delete the path the run printed (`.scratch/band-b-target/` or `.scratch/band-b-durable/`) |
| The Durable store | lives inside the scratch target at `<target>/.durable/store.jsonl`, plus the `<store>.lock` and `<store>.lock.owner` sidecars the layer keeps while a process holds it; both go with the directory |
| A fault-harness run | the harness reaps the application, the gate and the CLI by PID from its `servers` file, and removes its own directory under `.scratch/band-b-faults/` unless `KEEP=1`. To check: `pgrep -f 'ralph-loop'` should print nothing |
| The Copilot sign-in | none — the CLI's own session is reused, and nothing is written by these scripts |

No `docker`, no cloud resource, and no background service to shut down. The only durable artifact is the
scratch directory, and both runners remove the one they created.

## What the target directory contains

`.scratch/band-b-target/` in the worktree root — never inside this tree, so a run cannot
damage the repo:

| Path | Contents |
| --- | --- |
| `PROMPT.md` | The instruction sent on every iteration |
| `PLAN.md` | The checklist, with `Status: in-progress` |
| `release-notes.md` | Notes whose headings are one level too shallow and whose `New` bullets lack a marker |
| `docs/section-01.md` … `section-10.md` | Ten sections, each missing its H1; two also carry a broken link or a stale date |

Twelve checkable defects, described in `PLAN.md`, one item per line. The
agent edits real files and ticks items as it goes, and only after re-reading the file.

## How the loop proves it did real work

Printing is not enough. After the loop exits, `baseline/verify.sh` checks the target on disk:

- every heading in `release-notes.md` moved exactly one level (each expected heading is
  asserted, not a sample), and the fenced block is untouched;
- every `New` bullet ends with `<!-- NEW -->` exactly once, preceded by one space;
- every `docs/section-*.md` opens with an H1;
- the link and the date in sections 03 and 09 were repointed and replaced;
- `PLAN.md` says `Status: complete` and has no open item.

`run.sh` exits non-zero if any check fails, or if the plan is complete but the files are not.

## Observing progress

A run prints the target path, then one block per iteration with the tool calls the agent made
and the first line of its answer. In a second terminal:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/run.sh &
tail -f .scratch/band-b-target/PLAN.md     # watch items get ticked
ls -R .scratch/band-b-target/docs          # watch the sections change
git -C .scratch/band-b-target diff 2>/dev/null || true
```

## The iteration cap

`const int MaxIterations = 10;` at the top of `baseline/ralph-loop.cs`. When the cap is
reached without the plan reporting complete, the loop prints the cap, prints the first open
item, and exits with status 3. A broken loop cannot run forever.

Exit statuses:

| Status | Meaning |
| --- | --- |
| 0 | The plan reported complete and the files match — **or**, for the Durable variant, the run stopped at its own `STOP_AFTER_ITERATION` seam, which is reported as 0 because the run never claimed to be finished and is not verified |
| 1 | The target does not match the plan, or (Durable) the loop failed — the dead-turn refusal exits 1 with the plan still open |
| 2 | No `PLAN.md` in the working directory, or no `dotnet` on `PATH`. `resume.sh` adds: no target, or no store at `<target>/.durable/store.jsonl` to resume. Both Durable runners also return it for a `RUN_DIR`/`TARGET` that already holds content and is not theirs to empty |
| 3 | Iteration cap reached with the plan still open (`run.sh` reports this as 3, not 1) |
| 4 | Another process owns this run's store, so nothing was done — the Durable variant only (`durable/run.sh:140`, `durable/resume.sh:84`); the loop did not read the plan, send a prompt or write a file |

`RALPH_MODEL` is passed straight to the session. The loop does not check it against the
account's model list, so a model the CLI rejects fails that iteration; this CLI (1.0.105)
accepted an unknown name and ran on its default, without saying so.

## The loop shape

The vendored recipe runs a **fixed iteration count**: it reads the prompt once before the
loop (`upstream/ralph-loop.ts:37`), creates a session and sends that prompt per iteration,
and stops only when it reaches `maxIterations` — there is no completion check in the file.
Issue #149 asks for a loop that also re-reads the plan and stops when it reports complete, so
items 1 and 2 below are **added over the vendored file**, not preserved from it:

1. read the plan file once per iteration; *(added — the recipe reads the prompt once)*
2. if the plan reports complete, stop; *(added — the recipe has no completion condition)*
3. create a **fresh** session (no resume, no context carried over) in the target directory,
   with permission requests approved for unattended operation; *(preserved)*
4. send the prompt and wait for the turn to finish; *(preserved)*
5. dispose the session and loop. *(preserved)*

Also not ported: the recipe's `plan`/`build` modes, and its numeric argument anywhere on the
command line — this port takes the cap only as the first argument.

The SDK bindings are one method, `RunOneIterationAsync`. The control flow above it —
`RalphLoopAsync` — is plain C#, so a port to another language swaps that one method and
leaves the loop visible.

## Layout

```
real-world-scenarios/band-b-local-job-agent/
├── README.md              # this file
├── run.sh                 # one baseline run against a fresh scratch target
├── baseline/
│   ├── ralph-loop.cs      # the port
│   ├── verify.sh          # checks the target after a run — used by both variants
│   └── target/            # the template copied into either scratch target
├── durable/
│   ├── ralph-loop-durable.cs   # the resumable variant, over lib/Durable.cs
│   ├── run.sh             # process 1: create the run and iterate
│   └── resume.sh          # later processes: reopen the store and continue
├── evidence/
│   └── durable-kill-and-resume.md  # captured kill/resume, idempotency diff, cap, divergence
├── faults/                # five faults, controls, and the diff test — see faults/README.md
│   ├── run-all.sh
│   └── results/           # one JSON per fault per variant, checked in
└── upstream/
    ├── LICENSE            # MIT, github/awesome-copilot
    ├── PROVENANCE.md      # repo, commit, paths, hashes
    ├── ralph-loop.md
    └── ralph-loop.ts
```

## The Durable variant's runs and seams

Every stop below is an environment variable the loop reads, not a signal handler: the phase
commits, prints the line, and throws on its own cancellation token, so the runner unwinds and the
process exits 0 with the task left `Running` at the checkpoint. Nothing is shared with the next
process but the store file.

| Command | What it does |
| --- | --- |
| `durable/run.sh` | Fresh scratch target, iterate to the cap or completion, verify |
| `durable/run.sh 3` | Same, with 3 as this run's cap |
| `KEEP=1 durable/run.sh` | Keep the scratch directory |
| `STOP_AFTER_ITERATION=2 durable/run.sh 10` | Commit iteration 2, then stop — the killed-run seam |
| `STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1 durable/run.sh 10` | Stop between a turn's commit and its step memo |
| `STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1 durable/run.sh 10` | Stop after the step memo, before the iteration settles — resume hits the memo |
| `STOP_AFTER_ITERATION=2 STOP_MID_TURN=1 durable/run.sh 10` | Stop inside the turn — the unrecoverable state |
| `KEEP=1 durable/resume.sh` | Reopen the store and continue where the last process stopped |
| `RUN_DIR=/path durable/run.sh` | Put the scratch area somewhere other than `.scratch/band-b-durable/` |

`resume.sh` takes no cap and no iteration number. Both come from the run's stored input and
checkpoint, which is what makes a resumed run continue rather than restart. `KEEP=1` on the first
process is required — the default removes the scratch directory on exit.

The scratch area is `.scratch/band-b-durable/` in the worktree root (the baseline uses
`.scratch/band-b-target/`), so the two variants never share a directory or a store. `RUN_DIR`
moves the whole area; `TARGET` moves the target directory inside it. Both scripts follow the
convention #189 established for Band G: a directory they created carries an empty
`.band-b-scratch` marker, only a marked directory is removed on exit, and a `RUN_DIR` or `TARGET`
that already holds content is refused with exit 2 instead of being emptied.

### What the Durable variant adds, and its cost

- **One iteration = one checkpoint.** The task's checkpoint records the completed iteration index,
  the plan digest, and the outcomes of the iteration's steps.
- **A memo per step.** `step/{iteration}/agent-turn` and `step/{iteration}/reconcile` guard the
  effects; a resume that finds them does not repeat the step.
- **The cap is part of the run.** It is written to the task's input once, so a resumed process
  cannot grant itself more iterations.
- **The plan file stays the input.** A resume reads its position from the store and the work list
  from the file, and corrects a hand-ticked item whose change is not on disk.

Cost, measured: `durable/ralph-loop-durable.cs` is **674 code lines**, of which **294 exist only for
durability (44%)**. `baseline/ralph-loop.cs` has **0** of 104. Counted with `python3
durable/evidence/table.py`, which aborts rather than renders if its regions stop covering the file;
the per-region table whose rows sum to those totals is in `evidence/durable-kill-and-resume.md` §6.
`wc -l` on the same two files is 989 and 163 — every line, blank and comment included. Both numbers,
and which is which, are in §*Lines of custom durability code* below.

The honest limit, in two parts. The memo boundary is the *turn*, not the tool call inside it, and
the layer is asymmetric about that boundary:

- **It records a turn that returns.** Every completed tool call becomes a `tool` checkpoint on the
  conversation's `durable.checkpoint` document, which keeps full history. A finished turn's calls
  can be read back from the store, and a resume reports that turn's outcome from the record.
- **It records nothing from a turn that does not return.** Each tool record is queued on the
  conversation's serial chain and the queue is drained at the turn's completion barrier, so a
  cancelled or failed turn never drains it. The store is left with the turn's admission and the
  loop's own `turn-started` checkpoint, and no tool record at all — not a partial list.
- **It does not replay.** The SDK will not continue a half-finished turn, so the variant reports
  what the store holds and fails the run, rather than re-issuing a turn that may have already
  written files.

So a dead turn's work is *observable* only to the extent of "which iteration was in flight, and
what the plan looked like when it started", and never *replayable*. §3 of the evidence captures all
three cases: memo rebuilt from a checkpoint (3a), memo hit and the step skipped (3b), and the
unrecoverable in-flight turn, with the store dumped before the resume reads it (3c).

Full transcript with diffs, the cap demonstration, the plan-divergence demonstration, and the
scratch-directory controls: `evidence/durable-kill-and-resume.md`.

## The faults, transcribed

Five faults, each run against both variants, plus the comparison this band exists for. Every number below
is transcribed from `faults/results/`; none is typed. [`faults/README.md`](faults/README.md) describes each
injection; this is the result.

```bash
./real-world-scenarios/band-b-local-job-agent/faults/run-all.sh               # both variants
./real-world-scenarios/band-b-local-job-agent/faults/run-all.sh --no-controls # faults only
```

| Fault | `baseline` | `durable` | Result files |
| --- | --- | --- | --- |
| `kill-mid-turn` | **lost its position** | **refused to resume the dead turn** | [`baseline`](faults/results/baseline/kill-mid-turn.json) · [`durable`](faults/results/durable/kill-mid-turn.json) |
| `restart-after-checkpoint` | **restarted from the plan** | **not exercised** | [`baseline`](faults/results/baseline/restart-after-checkpoint.json) · [`durable`](faults/results/durable/restart-after-checkpoint.json) |
| `duplicate-delivery` | **doubled** | **doubled** | [`baseline`](faults/results/baseline/duplicate-delivery.json) · [`durable`](faults/results/durable/duplicate-delivery.json) |
| `concurrent-clients` | **not corrupted** | **refused the second client** | [`baseline`](faults/results/baseline/concurrent-clients.json) · [`durable`](faults/results/durable/concurrent-clients.json) |
| `store-outage` | **not exercised** | **continued past the outage** | [`baseline`](faults/results/baseline/store-outage.json) · [`durable`](faults/results/durable/store-outage.json) |

All 21 harness runs — 5 faults × 2 variants, **9 controls** (the baseline has no `store-outage` control to run) and the 2 run-equivalence tests — exited 0
([`run-all.log`](faults/results/run-all.log)), recorded 2026-11-06T03:33:43Z–03:52:17Z at
`harness.commit 32770e70b581`.

Each row has a control that removes the injection: the held frame is released, the store is not moved, the
second loop starts after the first has finished, the delivery is not repeated. Every control's outcome
differed from its fault's, which is what makes the fault attributable.
[`faults/results/summary.txt`](faults/results/summary.txt) holds the matrix and the control table the rows
above come from, rendered from the result files by `faults/lib/summarise.py`.

**Only one of these five faults is a clean win for the Durable variant, and it is about exclusion, not
recovery.** The table is read wrong if the diffing cells are counted as more than they are:

| Cell | Why it is not upgraded |
| --- | --- |
| `duplicate-delivery` | a **tie** — both variants made a second set of tool calls and neither reported it |
| `restart-after-checkpoint` / `durable` | **`not exercised`**, and the reason is a finding: no frame outside the process lands between two iterations |
| `store-outage` / `baseline` | **`not exercised`** — there is no store to take away |
| `kill-mid-turn` / `durable` | the variant **refused**, so the run did not finish: a stated limit, measured from outside |

What is left is `concurrent-clients`, where the layer's exclusive lock stops the second client before it
reads the plan — an exclusion win, about concurrency rather than recovery. And `store-outage` / `durable`
is `continued past the outage`, which is survival *without detection*, not a pass. The durable side
finished neither of its two interruption rows.

**Kill mid-turn.** The baseline loses the loop's position: a new process re-read the plan and started again
at its own iteration 1. The Durable variant does not resume either, and does not pretend to — the store
records `turn-started` and no outcome, so a second process reports what it holds and fails the run rather
than re-issuing a turn that may already have written files. That is the limit §*What the Durable variant
adds, and its cost* states, now measured from outside the process.

**Restart after a committed iteration.** The baseline has no such seam: one pass finishes the whole plan on
this machine, so the harness stops it after a returned tool call instead, and the second process starts
over. For the Durable variant the row is **not exercised**, with the reason recorded: the only frame a
process outside the run can stop at between iterations is the creation of the next iteration's
conversation, and at that frame the store already says `turn-started` — so a stop there is `kill-mid-turn`,
not a boundary. A restart from a settled boundary exists only inside the process.

**Duplicate delivery — a tie.** One prompt request forwarded twice by the gate. Both variants did a second
set of tool calls: **4 of the baseline's 4** `tool.execution_start` frames crossed the gate after the
re-delivery, and **8 of the Durable variant's 8**. Neither refused the second delivery, so on both, a
duplicate is silent extra work over the same files. The controls are the counts to read those against — the
baseline's one-delivery control logs 24 `tool.execution_start` frames over the whole (larger) run, the
Durable variant's 4 — and in neither control does anything cross after a re-delivery, because there is not
one. Read the ratio, not the raw totals: the two variants make different numbers of calls per pass, so
"4 against 24" and "8 against 8" are the same claim — a second delivery produced its own calls — and not
evidence of different behaviour. Neither variant names a delivery, so neither can deduplicate one; the
Durable variant's `run.sh` exit 3 here is its cap, not a refusal.

**Concurrent clients.** Two loops over one folder, the first blocked at a tool call when the second starts.
The baseline did not corrupt: both finished, the plan was still a plan and the verifier passed. The Durable
variant refused the second client with exit 4 before it read the plan, sent a prompt or wrote a file —
`lib/Durable.cs` already took an exclusive lock on the store file; the variant now reports that refusal as
itself instead of dying of an unhandled exception (the change the fault required).

**Store outage.** The baseline writes no state outside the job's own files, so it is **not exercised**, with
the reason checked on every run. For the Durable variant the store was moved aside while the process held
it open; the writes made afterwards went into the renamed file — a `FileStream` follows the inode — so the
run neither refused nor stopped. That is the row's word: `continued past the outage`. It is not
`failed safely`, and the result says so rather than choosing the flattering vocabulary.

### The run-equivalence result, with its numbers

The strongest claim this band can make is that an interrupted run leaves the same product as one that was
not. Per variant, three runs over three copies of one staged target: **reference** (uninterrupted),
**repeat** (a second clean run, which is what makes the comparison mean anything), and **interrupted**
(killed, then a second process against the same target). Both variants' `outcome` field is `identical` —
and the paragraphs below keep apart what that word means on each, because it does not mean the same thing.

**What was compared, exactly.** The product, not the directory: every regular file under the target
**except `.durable/`** (the store is the record, and it differs by construction — one process opened it
versus two) and except the harness's build directory. For each file, its SHA-256 and its path relative to
the target, as one text; two runs are identical when that text is equal. So "identical" means *the same
file set with the same bytes in each file* — not the same names, and not the same contents in a different
arrangement. A renamed file fails the comparison. **17 files** in every case: `PLAN.md`, `PROMPT.md`,
`release-notes.md`, `docs/section-01.md` … `docs/section-14.md`.

| Variant | Interruption | Repeat vs reference | Interrupted vs reference |
| --- | --- | --- | --- |
| `durable` | `STOP_AFTER_ITERATION=2`, then `resume.sh` (resumed at iteration 3, finished at 8) | **identical** — 17 files, same sha256 per file and path | **identical** — 17 files, same sha256 per file and path |
| `baseline` | `SIGKILL` at `out tool.execution_start 2`, then the same command again | **differs** byte-for-byte in 14 of 17 files; identical after dropping blank lines | **differs** byte-for-byte in 14 of 17 files; identical after dropping blank lines |

Both variants' verdicts are taken from the **normalised** comparison (runs of blank lines collapsed,
trailing whitespace stripped), and the run reports `not comparable: two clean runs differed` and claims
nothing about the interruption if the repeat run does not agree. On the Durable variant the normaliser does
no work: reference, repeat and interrupted are byte-identical, 17 files each. On the baseline it is doing
real work — two *clean* runs differ byte-for-byte in 14 of 17 files, so the baseline's agreement is "the
same work, differently spaced", not "the same bytes".

**The comparator is made able to fail, on every run.** Before any comparison is used, the script proves it
can say no: an appended byte must report a difference (it does — `./PLAN.md`, 17 vs 17 files), an extra
file must report a difference (it does — `./extra.txt`, 17 vs 18 files), and an unmodified tree must report
identity (it does). An appended *line* is checked too, after normalising, and reports `differs: 1 of 17
file(s) differ even after dropping blank lines`. If any of those misbehaves the run stops and records
`comparator failed its self-check` rather than a comparison it could not make.

**What the baseline's agreement does and does not mean.** The baseline cannot continue a run: its
"interrupted" product was written by two processes where the reference's was written by one, and the second
process **restarted from the plan** and did the work again. So its `identical` says *the job can be finished
by starting over and the end state is the same*. It does **not** say the interruption was recovered — no
position was restored, nothing was skipped, and the cost of the kill (every iteration after the kill re-done)
does not appear in the product at all. It also rests on the normaliser, because the baseline's second pass
does not reproduce the first pass's spacing. Reading it as "the baseline is durable too" would be the
mistake this row exists to prevent: a product comparison cannot distinguish *resumed* from *redone*, and only
the Durable variant's run has a resume in it.

**What the Durable variant's agreement does not say.** The equality is over the *files*. The store's account
of the interruption differs from the uninterrupted run's by construction, and is not compared. And the
variant's interruption is its own committed-iteration stop, not a SIGKILL: the one stop a process outside
the run can put between two iterations is inside the next turn (see `restart-after-checkpoint` above), so
the seam that proves a resume is the seam the variant itself provides.

Result files: [`faults/results/durable/run-equivalence.json`](faults/results/durable/run-equivalence.json) ·
[`faults/results/baseline/run-equivalence.json`](faults/results/baseline/run-equivalence.json). The
comparison is implemented and documented in [`faults/run-equivalence.sh`](faults/run-equivalence.sh) and
[`faults/README.md`](faults/README.md) §*The extra evidence*.

## Where the baseline is simpler — deliberately

The comparison is one-sided only if this is left out. Every item below is the baseline doing less, and
the Durable variant paying for it.

| | `baseline/` | `durable/` |
| --- | --- | --- |
| Code | **104** code lines, one file, one runner | **674** code lines, two runners (`run.sh` 171 `wc -l`, `resume.sh` 106) |
| State on disk | none — the target's own files and nothing else | `<target>/.durable/store.jsonl`, plus `.lock` / `.lock.owner` sidecars while open |
| Things that can go wrong at startup | no `dotnet`, no `PLAN.md` | those, plus a store that will not open, a store from another run, a second writer (exit 4), a target with no store to resume (exit 2) |
| Running it twice in a row | safe by construction: nothing is shared | refused — exit 4, because the store is exclusively locked — which is a feature you must know about before you hit it |
| Migration | none; delete the directory | the store is a record the next process must read, so its shape is a contract: a rename or a re-typed field in `LoopCheckpoint` has to keep reading stores the previous build wrote — `DurableStoreFormat.Auto` sniffs an existing file rather than assuming its own |
| A dead turn | re-run the job; the result is the same finished product, measured | report and stop; the operator decides |

Two of those are real advantages and not just absence of machinery. **The baseline cannot be locked out
of its own work**, so a re-run always proceeds. And **its recovery is a command the operator already
knows** (`run.sh` again), where the Durable variant's needs the operator to understand the difference
between a committed iteration and a dead turn. On this job, a re-run costs about a minute; that is the
price the 294 durability lines buy down, not to zero, but to a resume at the last committed iteration.

## Lines of custom durability code

Two measurements, and the difference matters. In this directory, on 2026-11-06:

```bash
wc -l baseline/ralph-loop.cs durable/ralph-loop-durable.cs
#   163 baseline/ralph-loop.cs
#   989 durable/ralph-loop-durable.cs

python3 durable/evidence/table.py    # the per-region classification, §6 of the evidence
```

| Variant | `wc -l` | Code lines | Durability-specific | Baseline for the same |
| --- | ---: | ---: | ---: | ---: |
| `baseline/` | **163** | **104** | **0** — no store, no task, no checkpoint, no memo, no resume path | — |
| `durable/` | **989** | **674** | **294** of **674** (**44%**) | 104 code lines / 0 durability |

- **`wc -l` = 989 and 163** is every line of each file, blank lines and comments included. That is the
  literal measurement the issue asks for.
- **Code lines = 674 and 104** excludes blank lines, `//` comments and the `#:` file directives. That is the
  denominator the split is over.
- **294 of 674** is the durability-specific count, from the 22-region table in
  [`evidence/durable-kill-and-resume.md`](evidence/durable-kill-and-resume.md) §6, which classifies
  **line by line** with its rule stated there. `durable/evidence/table.py` recomputes it from the
  committed file and exits non-zero rather than rendering if the regions stop covering the file, so a
  stale table fails loudly instead of publishing a number nobody re-derived.

Neither number includes `lib/Durable.cs` (the layer both halves are measured against, not the scenario's
code), the shell runners (`run.sh` 72, `durable/run.sh` 171, `durable/resume.sh` 106), `baseline/verify.sh`
(87), or `faults/` (the measurement, not the application).

**276 of the difference is not durability.** 674 − 104 = 570 code lines more than the baseline, and 294
of those are durability; the other **276** are the variant doing more in code the baseline does in the prompt
or in `verify.sh` — a bounded two-item pass, the reconcile conditions, the heading normalizer, the in-flight
report's formatting. The honest reading is "294 durability lines added to a 104-line baseline", and a reader
who disagrees with one classification call changes the 294, not the 674.

## Upstream

`upstream/` is byte-identical to `github/awesome-copilot` at commit
`7e408815ddcb64889a3683799a85399331cfd4ef`, MIT. See `upstream/PROVENANCE.md`.
