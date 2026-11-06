# Band B — the Ralph Loop baseline, and its Durable variant

A port of the vendored Ralph Loop recipe (`upstream/`) to C#, against the plain
GitHub Copilot SDK. The loop reads a plan file, sends a prompt to a fresh session, lets the
agent edit files in a scratch directory, re-reads the plan, and repeats until the plan
reports complete.

**What it is not:** durable. The loop's position is a local variable in a running process.
Nothing in `baseline/` imports, references, or asserts on the Durable library.
**If you kill the process mid-iteration, there is no record of which step was running.** The
plan file may have been half-updated; recovery is re-reading the markdown and hoping the last
edit was consistent. That is the property Band B exists to measure — issue #148.

Everything under `baseline/` is one file-based .NET app plus a shell runner. Nothing there
references `lib/Durable.cs`; there is no store, no checkpoint, no task, no admission, and no
conversation document.

`durable/` is the other half of the comparison: the same job, the same prompt, the same
`baseline/verify.sh`, with the loop position in a Durable task instead of a local variable. A
killed run resumes at the next iteration (issue #150). Run both with the commands below.

---

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

## One command

```bash
./real-world-scenarios/band-b-local-job-agent/run.sh
```

The runner creates the target directory fresh, runs the loop, verifies the result, and
deletes the target on exit. To keep it:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/run.sh
```

To shorten the run for a smoke test, pass the cap as the first argument. The default is the
constant in `baseline/ralph-loop.cs`.

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
| 0 | The plan reported complete and the files match |
| 1 | The target does not match the plan (from `run.sh`) |
| 2 | No `PLAN.md` in the working directory, or no `dotnet` on `PATH` (from `run.sh`) |
| 3 | Iteration cap reached with the plan still open (`run.sh` reports this as 3, not 1) |
| 4 | Another process owns this run's store, so nothing was done (the Durable variant; `run.sh` and `resume.sh`) |

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

## Running both variants

Both variants do the same job against the same template and are checked by the same verifier,
`baseline/verify.sh`. The baseline keeps its position in a local variable; the Durable variant
keeps it in a Durable store, so a second process can continue the first one's run.

```bash
# Baseline: one process, no resume. Killed mid-run it starts over.
./real-world-scenarios/band-b-local-job-agent/run.sh

# Durable: one process, resumable. run.sh and resume.sh remove the scratch directory
# on exit unless KEEP=1, so a two-process sequence keeps it across both. Neither script
# will empty or delete a directory it did not create: it marks its own with
# `.band-b-scratch` and refuses (exit 2) a RUN_DIR or TARGET that already has content.
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh   # only after a stop
```

### The Durable variant's runs

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

Cost, measured: `durable/ralph-loop-durable.cs` is 663 code lines, of which 283 exist only for
durability (43%). `baseline/ralph-loop.cs` has 0. The counting rule and a per-region table whose
rows sum to those totals are in `evidence/durable-kill-and-resume.md` §6.

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

## Fault evidence

Five faults, each run against both variants, plus the comparison this band exists for. Every number below
is produced by `faults/`; none is typed. `faults/README.md` describes each injection; this is the result.

```bash
./real-world-scenarios/band-b-local-job-agent/faults/run-all.sh
```

| Fault | baseline | durable |
| --- | --- | --- |
| `kill-mid-turn` | **lost its position** | **refused to resume the dead turn** |
| `restart-after-checkpoint` | **restarted from the plan** | **not exercised** |
| `duplicate-delivery` | **doubled** | **doubled** |
| `concurrent-clients` | **not corrupted** | **refused the second client** |
| `store-outage` | **not exercised** | **continued past the outage** |

Each row has a control that removes the injection: the held frame is released, the store is not moved, the
second loop starts after the first has finished, the delivery is not repeated. Every control's outcome
differed from its fault's, which is what makes the fault attributable. `faults/results/summary.txt` holds
the matrix and the control table the rows above come from, generated from the result files.

**Kill mid-turn.** The baseline loses the loop's position: a new process re-read the plan and started again
at its own iteration 1. The Durable variant does not resume either, and does not pretend to — the store
records `turn-started` and no outcome, so a second process reports what it holds and fails the run rather
than re-issuing a turn that may already have written files. That is the limit §"The honest limit" states,
now measured from outside the process.

**Restart after a committed iteration.** The baseline has no such seam: one pass finishes the whole plan on
this machine, so the harness stops it after a returned tool call instead, and the second process starts
over. For the Durable variant the row is **not exercised**, with the reason recorded: the only frame a
process outside the run can stop at between iterations is the creation of the next iteration's
conversation, and at that frame the store already says `turn-started` — so a stop there is `kill-mid-turn`,
not a boundary. A restart from a settled boundary exists only inside the process.

**Duplicate delivery.** One prompt request forwarded twice by the gate. Both variants removed a second set
of tool calls: 4 for the baseline against 0 after the re-delivery in its control, 7 for the Durable variant
against 0. Neither refused the second delivery, so on both, a duplicate is silent extra work over the same
files.

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

### A run that was interrupted and continued

The strongest claim this band can make is that an interrupted run leaves the same product as one that was
not. Three runs of the same variant over one staged target: uninterrupted, repeated, and stopped then
continued. Compared: **the same file set, with the same SHA-256 per file, at the same relative path** —
every regular file under the target except `.durable/`, which is the record and differs by construction.

| Variant | Repeat vs reference | Interrupted vs reference |
| --- | --- | --- |
| durable | identical, byte for byte | **identical, byte for byte** |
| baseline | identical, byte for byte | identical, byte for byte |

The Durable variant's interruption is its own committed-iteration stop (`STOP_AFTER_ITERATION=2`), and the
second process resumed at iteration 3 and finished: the product is the same 17 files with the same bytes as
one uninterrupted run. The baseline cannot continue a run, so its "continuation" is a second process
starting over after a SIGKILL — and its product matched as well.

The comparison is not vacuous: on every run the comparator is asked to say "differs" for one appended byte
and for one extra file, and "identical" for an unmodified tree, and the run stops if any of the three
misbehaves. The repeat run is the second guard — two clean runs being byte-identical is what makes a
matching interrupted product a statement about the work rather than about the model's mood.

What it does **not** say: the Durable variant's equality is over the *files*. The store's account of the
interruption differs from the uninterrupted run's, by construction, and is not compared.

## Upstream

`upstream/` is byte-identical to `github/awesome-copilot` at commit
`7e408815ddcb64889a3683799a85399331cfd4ef`, MIT. See `upstream/PROVENANCE.md`.
