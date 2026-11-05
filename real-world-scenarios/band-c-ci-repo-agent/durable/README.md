# Band C — Durable variant

The same pr-triage agent as `../baseline/`, with its state in a Durable store instead of a JSON blob
on a git branch. The eight phases, the cooldown rule, the classifier, the scorer, the batch and label
rules, the comment and the report are the baseline's: `Triage.cs` is that code moved, not rewritten,
and `evidence/compare.py --code` re-reads both files and checks it line for line (386 significant
lines identical). What changes is **where state lives**, and therefore what survives a process.

```bash
./run.sh                          # one run: cold start, eight phases, a store
KEEP=1 ./run.sh                   # keep the store and the run directory
KEEP=1 ./run.sh && REUSE=1 ./run.sh         # the second run posts nothing
./evidence.sh                     # every claim in evidence/, reproduced (66 assertions)
BAND_C_EXIT_AFTER_PHASE=comment ./run.sh     # stop after phase 6 commits
BAND_C_EXIT_AFTER_COMMENTS=7 ./run.sh        # stop inside phase 6, after seven comments
BAND_C_EXIT_AFTER_COMMENTS=7 ./run.sh && REUSE=1 ./run.sh   # the seam, then the resume
```

| Path | What it is |
| --- | --- |
| `Contracts.cs` | The three documents, the store keys, and the phase vocabulary. |
| `Triage.cs` | The baseline's triage logic, moved, plus the input digest and the fixture partitions. |
| `Fixture.cs` | The baseline's fixture model, unchanged. |
| `TriageStore.cs` | Every read and write of durable state, in one place. |
| `Phases.cs` | The eight phases as one durable task: one handler per phase, one commit per decision. |
| `ExitSeam.cs` | The deterministic stop points, and the switch that removes the idempotency guard. |
| `Runner.cs` | The process: open the store (and be refused if another has it), resume or create, drive. |
| `run.sh` | One run, with the scratch-directory provenance rules. |
| `evidence.sh` | Nine stages: the comparison, the idempotency proofs, the controls, the race, the count. |
| `evidence/` | The captured output, and the readers that check claims against the store. |

## The baseline, and what is different

| Baseline | Here |
| --- | --- |
| `pr-triage-latest.json` on branch `memory/pr-triage` | a `band-c.run` document per repository, a `band-c.pr` document per pull request, and a `band-c.comments` ledger |
| cold start every run: the file is read and the whole triage redone | the run record says whether the last attempt finished; a finished one starts a generation that reuses every assessment at an unchanged digest |
| cooldown read from timestamps in one JSON blob | the cooldown is the same rule, but the run's own clock and generations are committed facts |
| Phase 8 is the only write; a failure before it loses nothing and repeats everything | every phase commits its decision before the next one starts |
| a run that dies after commenting leaves the previous run's view, and the next run re-triages and re-comments | the comment step is memoised and the ledger is a document, so the re-entry does not post again |
| overlapping runs race the same blob; `update-ref` fails for the loser, silently for the winner | the store is opened exclusively, so the second process cannot start at all |
| no way to say "this run was interrupted" | a run record at `phase: comment` with 7 memos says exactly that |

**The baseline is not modified.** Its defects are the comparison, and the places where it is *better*
are stated in "Where the baseline wins" below rather than smoothed away.

## The checkpoint, and how a run continues

**A checkpoint is one committed phase transition.** Each transition commits two things: the run
record at its new phase, and — in phases 2 to 5 — that pull request's assessment at the stage it
reached. A process that stops between two transitions restarts at the phase the record names, and
finds every document the phases before it wrote.

The record is the resume point, and this is the difference that matters most against the baseline:
the baseline's state file cannot say "a run was interrupted", so its second run inside the cooldown
has to refuse (its run gate). Here the record says it, so a second run either **continues** the
unfinished run or **starts a new generation**, and the two are different behaviours:

| The record says | This process does |
| --- | --- |
| no record, or a finished task | creates a new task, generation +1, and because every PR's digest is unchanged, reuses every assessment and posts no comment |
| `running` at phase N | enters at phase N; the phase pointer, and for phase 6 the memos, stop it repeating work |

**Which task is "the current one" is not a detail, and getting it wrong made the resume unreachable.**
`TriageStore.CurrentTask` first selected `Tasks.FirstOrDefault(name == …)` — the first task in store
order, which after one completed generation is always that finished task. Every later process then
declared it finished and created a *new* one, leaving an interrupted run stranded with nobody to
continue it: exactly the case requirement 3 exists for, silently dead. The selection now prefers a
non-terminal task and tie-breaks on creation sequence, and stage 5c is the evidence: generation 1
completes, generation 2 is stopped at `collect`, and the third process **adopts the interrupted task**
(`resuming durable task t223 at phase 'classify'`), never prints `is finished`, and finishes the work.

The *generation* is not a cooldown replacement. The baseline's gate exists because its state cannot
express "unfinished"; the durable variant's state can, so the question "has anything changed" is
answered per PR by the digest instead of per run by a clock. That is a behaviour difference and the
comparison table below records it as one.

### The per-phase write, stated plainly

Phase 2 commits `stage: classify` with the category and risk; phase 3 adds the score; phase 4 the
action and its reason; phase 5 the batch and labels. Four commits per pull request across the run. A
process that stops after phase 4 leaves a document whose stage is `recommend`, and the phase that
resumes reads it instead of recomputing it. The cost is visible in the store: 52 document versions for
13 pull requests in one run (`evidence/_store.py versions`), against one blob in the baseline.

## Requirement by requirement

The five requirements are the issue's, and each one is demonstrated by `./evidence.sh`, whose captured
output is `evidence/durable-triage-and-idempotency.md`.

1. **All eight phases remain visible.** `Phases.Order` is the baseline's eight headings and
   `Phases.Header` prints the same `phase n/8` lines, so the two stdouts read side by side. The
   comparison in stage 1 diffs the four output documents — comments, labels, report, data — and they
   are byte-identical.
2. **Re-running without changes must not re-comment.** Stage 2: a second process over the same store
   writes zero comments and adopts thirteen by ledger. The control in stage 2b removes the guard and
   the same command writes all thirteen again.
3. **A run interrupted after commenting must not duplicate on resume.** Two seams, because they are
   two questions:
   - Stage 3 stops *between* phase 6 and phase 7 (`BAND_C_EXIT_AFTER_PHASE=comment`). The resume
     enters at `report` and writes nothing; thirteen comments stay thirteen.
   - Stage 4 stops *inside* phase 6, after seven of thirteen (`BAND_C_EXIT_AFTER_COMMENTS=7`). The
     resume re-enters phase 6, skips seven and posts six. **At that seam the memo and the ledger both
     hold the seven**, so this stage on its own does not show which one prevented the re-post — a
     resume with the memos erased produces the identical "7 skipped" through the ledger. Stage 4a is
     the control that separates them: `BAND_C_HIDE_LEDGER=1` withholds the ledger entry and leaves
     the memo, the ledger is empty (0 entries) and the seven are still skipped — **by the memo**. The
     control 4b removes every guard and the same resume posts all thirteen.
4. **Concurrent runs must not both proceed.** Stage 5: five rounds of two processes started together
   against one store; each round has exactly one completion and one refusal, and the refusal is the
   store's own message with the lock file and the holding process in it. The control (5b) runs the
   same two processes against different stores and both proceed, so the refusal is the store's and not
   a flag.

   **This meets the requirement as written and only as written, and the reason is worth stating
   plainly rather than leaving to a footnote.** The guarantee is not "one run per repository"; it is
   "one writer per store file". Two runs of this program that agree on the store path cannot overlap —
   that is a property of the layer and it holds. Two runs that disagree on the path both proceed, and
   nothing in the layer or in this program notices: §"Concurrency" below measures that case and the
   stage-5b control is it. So the requirement is satisfied *because the host chooses one path per
   repository*, not because the store knows what a repository is. If that convention breaks — a second
   checkout, a different worktree, `DURABLE_STORE` pointing elsewhere — the refusal does not happen.
   Making it a property of the data rather than of the filename would need an ownership record inside
   the store, which the layer does not have.
5. **Use `lib/Durable.cs`. No private store.** `BandCDurable.csproj` references
   `../../../lib/Copilot.Durable.csproj`. Nothing from the store is copied: `Contracts.cs` defines
   document shapes and `TriageStore.cs` calls the layer. The one layer change this variant needed is
   recorded below and is a change to `lib/`, not a workaround inside the scenario.

## Concurrency: what the refusal is, and what it costs

A persistent `DurableStore` takes an exclusive lock on its file for the store's lifetime
(`StoreFileLock`, a sidecar `{path}.lock` opened `FileShare.None`). The second process does not queue
and does not retry: `DurableStore.Create` throws and this program exits 3 with

```
durable: cannot open the store: The durable store file '<scratch>/race-1/store.jsonl' is already open
in another process. Its lock file '<scratch>/race-1/store.jsonl.lock' is held by process 30299 ...
A store file supports a single writer at a time; close the other store, or point this one at a
different path.
```

**This is a refusal, not a queue.** The issue allows either ("refused or serialized") and the layer
offers the first. Two consequences, stated because the alternative is a claim the evidence does not
support:

- A refused run **does nothing at all** — it exits before the fixture is read. A caller that wants the
  second run to wait and then perform its own triage has to retry; nothing here does that.
- **The refusal is per store file, not per repository — this is the honest limit of requirement 4.**
  Two runs pointed at two different store paths for the same repository both proceed, and nothing in
  the layer notices. Stage 5b is exactly that case, and it is a control *because* the expected
  behaviour is the opposite. The lock is only a mutex if every writer agrees on the path, so what this
  variant demonstrates is *"two processes over one store cannot both proceed"*, not *"two triage runs
  of one repository cannot both proceed"*. `run.sh` derives the path from the fixture's repository name
  (`.scratch/band-c-durable/store.jsonl`, whose documents are keyed by repository), which makes the
  second true in practice for runs started through that script — a convention in this script, not a
  guarantee in the layer, and not something this variant can enforce.

  Closing it properly needs an owner record *inside* the store — "repository X is being triaged by
  process P since T" — written in a transaction before any work, which is a layer feature this variant
  does not have and does not invent: a private lock file next to the store would be exactly the "no
  private store" line requirement 5 draws. It is stated here as a gap rather than dressed up as a
  property.

The baseline has neither property: its two runs race the same branch, the loser's `update-ref` fails
and the winner's write lands, and neither run knows the other existed.

## Idempotency across an external effect

The comment is the external effect. Two records have to disagree before it is written:

| Record | Lives in | Key | Stops |
| --- | --- | --- | --- |
| the step memo | the run's task record | `comment/{number}/{digest}` | re-posting within one run's re-entry |
| the ledger entry | `band-c.comments`, owned by `agent:pr-triage` | the same pair | re-posting across runs, which have different tasks and no memos |

Both keys are the **digest**, not the run and not the clock, and that is the whole mechanism: an
unchanged pull request produces the same key in every future run. It is the digest rather than the
number alone so a pull request whose input changed gets a new key and a new comment — a correction,
not a duplicate. `Triage.InputDigest` covers exactly the fields the decision reads (its doc comment
lists them and the two it deliberately leaves out).

The ledger lives in a document rather than only in memos because memos belong to a task, and a run is
a new task: a memo alone would forget everything the moment the previous run finished. The ledger is
written in the same transaction as the memo (`TriageStore.RecordCommentAsync`), so a resume sees both
or neither — which is only possible because of the layer change below.

### The window a memo cannot close

The comment file is written first, then the ledger entry and the memo commit. A process that dies
between those two leaves a comment that no record mentions. Measured, not argued — stage 4c
(`BAND_C_EXIT_AFTER_COMMENT_EFFECT=1`) stops exactly there and asserts the shape: **1 comment on disk,
0 ledger entries, 0 memos**, and the resume posts that one comment again — a duplicate of one, and no
more than one, however many times the run is repeated.

That is the honest shape of an external effect with no transaction on the other side. The alternative
would be to commit the ledger first and write the comment second, which turns a lost comment into a
false record: the store would claim a comment the repository does not have. A duplicate is
recoverable; a silently missing comment is not. The ordering is a decision, and it is written here
because the code alone does not say why.

## Custom durability code, counted

Counting rule, the same one Band A states so the numbers are comparable: **non-blank, non-comment
lines** in `durable/*.cs`, where a comment is `//` (including `///` doc comments) or a `/* … */` block.
The split is per declaration and is recorded in `evidence/count.py`; the script fails if a declaration
is unclassified or the split does not add up to the file.

| File | Code lines | Durability | Application |
| --- | ---: | ---: | ---: |
| `Contracts.cs` | 103 | 103 | 0 |
| `TriageStore.cs` | 71 | 71 | 0 |
| `ExitSeam.cs` | 70 | 70 | 0 |
| `Program.cs` | 2 | 2 | 0 |
| `Runner.cs` | 227 | 158 | 69 |
| `Phases.cs` | 402 | 141 | 261 |
| `Triage.cs` | 499 | 47 | 452 |
| `Fixture.cs` | 69 | 0 | 69 |
| **Total** | **1443** | **592** | **851** |

**592 durability-specific lines against 851 application lines** — 41% of the variant is the
durability this row of the matrix counts. These are the numbers `./evidence.sh` stage 7 prints; the
script fails rather than reporting a table that disagrees with the code.

Three judgement calls a reader may disagree with, named so the disagreement is easy to make:

- **`Triage.cs`'s 47 durability lines** are the input digest (22), the four fixture partitions plus
  the clock (20), and the digest helper (5). The partitions are application logic in the baseline —
  eight lines inside Phase 1 — and are here because a phase cannot carry a local variable across a
  process. Counting them as durability is the stricter reading; the alternative is 47 lines treated as
  "the same code in a different place".
- **`Phases.cs`'s split is uneven on purpose.** `CommentAsync` (64) and `SaveAsync` (33) are
  durability: one is the guarded step, the other exists only because the phase writes a record instead
  of a file. The other six are application — the same decisions, the same prints. `Build`, `StepAsync`
  and the seam mapping (29) are durability.
- **`Runner.cs`'s `Options` (69) is application.** It is the command line; `--store` replaces the
  baseline's `--repo` and nothing about it is state.

Not counted: `run.sh`, `evidence.sh`, and `evidence/*.py` present the variant and check it; they are
not part of the agent. `baseline/PrTriageBaseline.cs` is the baseline's, not this variant's.

For comparison, the baseline's whole durability story is `Repo` — 106 code lines (`PrTriageBaseline.cs:931-1062`),
of which the write, the read and the compare-and-swap are the ones that matter — plus the
`Options.DryRun` flag that exists to make its crash seam reproducible.

## What `lib/Durable.cs` could not express, and the one change made

One genuine gap surfaced, and it blocked the shape requirement 3 needs: **a memo could not be written
inside a commit.** `TaskRuntime.MemoAsync` takes the store's commit gate, and a phase handler called
from `CommitAsync` is inside that gate already, so the inner wait never returns. `SemaphoreSlim` is not
reentrant, so the failure is a hang, not an exception.

Reproduced in two places, both runnable:

- `evidence/blocked-input.cs` — a standalone file-based app that calls the pre-fix API from inside a
  commit with a four-second watchdog. It exits 7, i.e. the await did not return. `evidence.sh` stage 8
  runs it and asserts the exit code, so the claim is checked on every evidence run.
- `tests/Durable.Tests/TaskResumeTests.cs` — `MemoAsyncInsideCommit_DoesNotReturn_BecauseTheGateIsNotReentrant`
  keeps the trap as a test, and the two tests beside it assert the replacement's behaviour.

Filed as [#201](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/201) with a minimal
reproduction, and fixed on this branch (commit "fix(durable): write a memo inside a transaction instead
of hanging"): `Transaction.Memo(taskId, key, value)` in all three ports, C#, TypeScript and Python, with
tests in each that fail without it. C# is the variant's; the two ports are kept in step because the memo
is part of the shared contract and a port that lacked it would make the shape unexpressible for two
thirds of the library's consumers. The write lands in the task's memo map before the provider is told
and before the commit, so it commits with the checkpoint and rolls back through the existing
`TaskStateSnapshot` path.

`Transaction.Memo` is used by `TriageStore.RecordCommentAsync`; the ledger entry and the memo are then
one transaction, which is what makes "a resume sees both or neither" true.

A second defect came out of the review of this variant, and it is the more dangerous of the two
because it is silent: **a rollback discarded a write and the next failed transaction put it back.**
`InMemoryStoreProvider.UpdateTask` keeps a baseline of each task's last known state so the next
transaction can arm its own undo with it, and it moved that baseline forward to the state the failing
transaction wrote. Restoring only the record left the baseline ahead of it, so the following failure
restored the memo — or checkpoint — this one had discarded. The TypeScript port already reset the
baseline; C# and Python did not, which is what made it a port divergence and not a design choice.
Fixed in both (`UndoLog.Restore` now takes the baseline map and puts it back), with two tests that
fail without the fix: one for a memo, one for a checkpoint, both staging a second, unrelated failing
transaction because that is what makes the stale baseline observable.

Two smaller things the layer made awkward, recorded because the next consumer will meet them:

- **A phase handler gets no store.** `TaskRuntime` has the task, the definition, the cancellation token
  and the commit methods — but nothing to read another document. A handler that writes more than its
  own checkpoint has to close over the store, which is why `TriagePhases.Build` is a factory and takes
  one. Workable, and arguably right (the store is a per-process resource), but the layer documents a
  task as "the checkpoint is the whole state", and this variant's phases are not.
- **A run with no model session still needs a conversation.** Every document version points at an
  entry, and an entry belongs to a conversation, so a scenario with no prompt still has to create one
  to hang its journal on. `TriageStore.CreateJournalAsync` does that; it is a real record in the store
  rather than an invented id, which is the right answer, but it is the sort of thing "no session" does
  not prepare you for.

## The comparison table, filled in

The issue's table, with what each row actually measured. Every cell names the artefact; a row that is
a tie is called one.

| Dimension | Baseline | Durable variant | Where it is measured |
| --- | --- | --- | --- |
| **Where state lives** | `pr-triage-latest.json` on branch `memory/pr-triage` in a bare git repository, written with `hash-object`/`mktree`/`commit-tree`/`update-ref` | three document kinds in a JSONL Durable store: `band-c.run` per repository, `band-c.pr` per pull request, `band-c.comments` once | `TriageStore.cs`, `Contracts.cs`; the store's own records in `evidence` stage 6 |
| **Cold start every run** | yes: Phase 1 reads the blob and every eligible PR is re-classified, re-scored, re-commented | no: a finished record starts a generation that reuses every assessment at an unchanged digest, and the ledger stops the comments | stage 2: `reused: 13 assessment(s) at an unchanged digest, 0 newly classified`, `0 comment(s) posted, 13 skipped by the ledger` |
| **Cooldown** | the per-PR rule, plus a run gate that refuses any second run inside the hour because the state cannot say "unfinished" | the per-PR rule unchanged; the run gate is gone, replaced by "resume if the record says running, new generation if it says finished" | stage 1 vs `../evidence/two-runs.md`: the baseline's run 2 says `run skipped by the one-hour cooldown`; this variant's run 2 triages the same 13 and posts nothing |
| **Failed save-state** | Phase 8 is the last step; a run that dies before it leaves the effects and the previous view, and the next run redoes the effects | every phase committed as it went; a run that dies leaves a record at the phase it reached and no work is silently redone | stage 3 and stage 4: both seams leave a coherent record, and both resumes duplicate nothing |
| **Overlapping runs** | no lock: both read, both triage, the second `update-ref` fails, the winner's write lands and neither run knows | the store is opened exclusively; a second process over the *same store file* is refused before it reads anything. Two different store files do not refuse (§"Concurrency"): the guarantee is one writer per store path, not one run per repository, and satisfying requirement 4 depends on the host deriving one path per repository | stage 5: 5/5 rounds have exactly one refusal; stage 5b: two stores, both proceed |
| **Duplicate delivery of the comment** | not prevented: a re-run re-comments every eligible PR | prevented by a memo within a run and a ledger across runs, keyed `(pr, digest)` | stage 2/2b, stage 3, stage 4/4b |
| **Resume from a mid-run stop** | not expressible: its only state is a whole-file snapshot written at the end | expressible: the run record names the phase, each document names its stage, memos name the completed steps | stage 3 (between phases) and stage 4 (inside a phase) |
| **Store outage / write failure** | `repo.git` moved aside mid-run: the run fails loudly (exit 1) with the comments on disk and the state gone | the store file moved aside mid-run: the process survives, recreates the path and finishes, and the state is split across the moved file and the new one — nothing committed is lost, and nothing tells the operator | `../faults/store-outage.sh`, both variants (`../faults/results/`). Not a win: the baseline's failure is loud and loses the state, this variant's is quiet and splits it |
| **Fault evidence, all six** | lost / lost / doubled / doubled, no conflict detected / failed loudly / **doubled: 13 PRs get two comments** | resumed / resumed / refused / refused / failed safely, split store / **not doubled: 0 re-posted, 13 adopted** | `../faults/` — the matrix is rendered by `../faults/lib/summarise.py` from checked-in results |
| **Lines of custom durability code** | `Repo`: 106 code lines, of which the read, the write and the compare-and-swap are the ones that matter | 592 of 1443 code lines (41%) | `evidence/count.py`, stage 7 |
| **Operational burden** | a branch and a bare repository; nothing to install | a store file per repository, its `.lock` and `.lock.owner` sidecars, and a resume-or-create decision in the host | `Runner.cs`; `run.sh` |

### Where the baseline wins

Not a footnote, and not empty:

- **One write per run against 73.** The baseline commits the whole triage once. This variant commits the
  run record at every phase transition and every pull request's assessment at four stages, so one clean
  run of 13 pull requests leaves **8 `band-c.run` versions, 52 `band-c.pr` versions and 13
  `band-c.comments` versions — 73 in all** (`evidence/_store.py versions`). That is the price of being
  able to resume at any phase, and it is a real one: the store grows with the number of pull requests
  times the number of phases, not with the number of runs.
- **No runtime dependency.** The baseline needs `git` and the .NET SDK. This variant needs the .NET SDK
  and a store file, and it drags in the whole `Copilot.Durable` assembly — which references the Copilot
  SDK — to run a deterministic fixture with no model in it. The baseline is one file.
- **A single artifact to inspect.** `git show memory/pr-triage:pr-triage-latest.json` is the whole state
  in one readable document. Here the answer to "what did the last run decide about #107" is a
  document lookup, `git` cannot show it, and a reader needs `evidence/_store.py` or the program.
- **Cold start is sometimes right.** The baseline re-triages everything every run, so a change to the
  classifier takes effect on the next run with no thought about digests. This variant's digest is a
  contract: adding a field to `Triage.InputDigest` invalidates every stored assessment at once, and
  forgetting to add one silently keeps an assessment the change should have replaced. That is a new
  failure mode the baseline does not have.
- **No lock is not always worse.** A refused run is a run that did nothing; an operator who starts two
  triage runs by accident gets an exit 3 to interpret, and there is no queue behind it. The baseline
  produces a confusing failure instead, but it does produce *work*.

## Two things that are not improved, stated as ties

- **The comment is still a file.** The baseline writes `comments/pr-NNN.md`; so does this variant. The
  external effect is a file write, so "posted" means "the file exists". The memo and the ledger make
  the *decision* idempotent, and the window in §"The window a memo cannot close" is where the file
  write and the record can disagree. A real `add-comment` API call would have the same window and the
  same ordering problem; this variant does not make it smaller, it names it.
- **The triage decisions are unchanged.** Same classifier, same scorer, same actions, same labels —
  verified by `compare.py --code` and by the byte-identical outputs. This variant is not better at
  triage and does not claim to be.

## Running

```bash
./run.sh                    # one run; prints the eight phases and the store's account of itself
KEEP=1 ./run.sh             # keep .scratch/band-c-durable/ for inspection
KEEP=1 ./run.sh && REUSE=1 ./run.sh    # a second generation over the first run's store
./run.sh --now 2027-01-01T00:00:00Z    # move the clock the fixture's ages are relative to
RUN_DIR=/somewhere KEEP=1 ./run.sh     # put the scratch area somewhere else
DURABLE_STORE=/somewhere/store.jsonl ./run.sh   # a store somewhere else (the lock is per path)
```

`REUSE=1` is required to keep a previous run's store: without it the scratch directory is recreated,
because a run that starts from the previous run's state is a different experiment and should have to
say so.

`run.sh` writes under `.scratch/band-c-durable/` in the worktree root — never inside this tree — and
carries an empty `.band-c-durable-scratch` marker. It removes a directory it created and refuses
(exit 2, touching nothing) one that already holds content it did not write.

### Seams and controls

| Variable | What it does |
| --- | --- |
| `BAND_C_EXIT_AFTER_PHASE=<phase>` | exits 42 after that phase's checkpoint commits |
| `BAND_C_EXIT_AFTER_COMMENTS=<n>` | exits 42 inside the comment phase, after n comments |
| `BAND_C_EXIT_AFTER_COMMENT_EFFECT=1` | exits 42 between the comment and its ledger entry |
| `BAND_C_NO_IDEMPOTENCY_GUARD=1` | removes the memo and ledger reads: every entry into phase 6 re-posts |
| `--fixture <file>` | use another fixture (a CLI flag, not an environment variable) |

The exit is `Environment.Exit`, not a thrown exception: no `finally`, no disposal, no flush beyond what
the store already did. That is what a killed process leaves, which is what the resume has to survive.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The run completed. |
| 1 | The task failed; the store holds the checkpoint and the next run resumes from it. |
| 2 | Bad usage, or `BAND_C_EXIT_AFTER_PHASE` named a phase that does not exist. |
| 3 | **Another process holds this store.** Nothing was read and nothing was written. |
| 42 | A seam fired. The store was committed before the exit. |

## What was executed, and what was not

Executed on 2026-11-05, macOS arm64, .NET SDK 10.0.402 at `/opt/homebrew/bin/dotnet`:

- `dotnet build` of `BandCDurable.csproj` and of `baseline/PrTriageBaseline.cs`, both clean.
- `./evidence.sh` twice, both times with exit 0 and identical assertion output. The capture in
  `evidence/durable-triage-and-idempotency.md` is from the first; 66 assertions, all passing.
- `./run.sh` for the ordinary single-run path, and each seam by hand before it was wired into the
  evidence script.
- `dotnet test tests/Durable.Tests` for the layer change: 221 tests, 220 passing. The one failure is
  `LateJoinExampleTests.ActualExample_BarrierReadinessFailuresCleanupAndSourceMutations`, which is
  **pre-existing and flaky**: it fails on `dev` without this branch's change too (2 of 3 runs), with a
  30-second timeout waiting for the recorded runtime.
- `lib/ts` (`npm test`) and `lib/py` (`test_sdk.py`) for the ported `memo`: 122 and 75 tests. TS has 2
  failures and Python has 1, all pre-existing on `dev` and unrelated to the memo (a `session.resume` vs
  `session.create` expectation drift in the recorded runtime's RPC log).

Not executed:

- Any live GitHub API call. The `add-comment` effect is a file, as in the baseline.
- Any model call. The phases are deterministic functions of the fixture; there is no session, which is
  why the run needs no sign-in and produces the same output on any date.
- A store-write failure. Neither variant injects one; the matrix row says `not exercised`.
- A retry queue for a refused run. The refusal is terminal by design, and the README says so rather
  than implying a queue that does not exist.
