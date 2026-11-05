# Band C — the gh-aw PR triage agent, reproduced locally

A reproduction of `github/gh-aw`'s `.github/workflows/pr-triage-agent.md`: a scheduled agent job that
triages open `app/github-copilot` pull requests in eight phases and keeps its only memory between
runs in a **git branch**. The vendored workflow is in `upstream/`; the reproduction is built **twice**:

| | `baseline/` | `durable/` |
| --- | --- | --- |
| What it is | the workflow's phase order, cooldown rule and payload, reproduced with the state shape the workflow uses | the same eight phases and the same triage code, with the state moved into a `DurableStore` |
| Where state lives | `pr-triage-latest.json`, one blob on branch `memory/pr-triage` in a bare git repository | three document kinds in a JSONL Durable store: `band-c.run`, `band-c.pr`, `band-c.comments` |
| Written when | once, as the last step of Phase 8 | at every phase transition, and at four stages of every PR's assessment |
| A run that dies | leaves its comments and the previous run's view; the next run redoes the work | leaves a record naming the phase it reached; the next run continues it |
| Run | `./run.sh` | `(cd durable && ./run.sh)` |
| Faults | `./faults/run-all.sh` — six faults × both variants, with controls | |

**The state is the thing.** Phase 8 writes `pr-triage-latest.json` to branch `memory/pr-triage` via a
custom `repo-memory` tool. That file, and nothing else, survives a run — the runner, the workspace, the
process and `/tmp` are created and discarded per run. `baseline/` keeps that shape exactly, including
what it costs: the write is the last step, there is no lock, and a run that dies between the comments
and the state write leaves the comments and the *previous* run's view. None of that is improved in
`baseline/`; it is the property this band exists to measure.

## Read this before any number: it is a local reproduction, not the hosted Action

Every measurement in this band was taken by **this repository's code on the author's machine**. The
hosted Action was not run, for either variant. Four things about the real workflow are substituted, and
each one bounds what a number here can mean:

| The Action has | This band has | What the substitution costs |
| --- | --- | --- |
| a fresh Actions runner per run, plus `timeout-minutes: 30`, `max-ai-credits: 1500` and `max-tool-denials: 3` from the workflow front matter | two local .NET processes and a bash driver | no budget, timeout or denial counter; nothing here can kill a run for spending too much |
| live `pull_request_read` calls | `baseline/fixture/pull-requests.json`, 17 fixed PRs | the PR set is fixed; nothing measures PRs changing mid-run, or an API call rejected or rate-limited |
| `add-comment`, `add-labels`, `create-issue` safe outputs | files under the run directory | "posted" means "the file exists"; partial success and API rejection are absent |
| a model call that decides category, risk and score | `Classify`, `AssessRisk`, `Score` — deterministic functions of the fixture | **this is not a measure of triage quality.** A model could rank the same PRs differently |

So what this band proves is narrow enough to say in one sentence:

> **Given the same fixture and the same clock, moving this workflow's state from a whole-file branch
> snapshot into the Durable store changes what survives an interruption, and leaves every triage
> decision byte-identical.**

It does **not** prove anything about the hosted Action's runner lifecycle, its API behaviour, its
model's judgement, or its cost. `FIDELITY.md` is the full boundary document — faithful, reduced, and
what each reduction costs — and it is the file to read before quoting a number.

Three consequences worth stating on this screen rather than two documents down:

- **`resumed` is not "what the Action would do".** The Action has no resume path: the runner that held
  the run is gone, and nothing in the state says a run was interrupted. The Durable variant adds a state
  the platform does not have. That is the finding, stated as an addition rather than a repair.
- **The comment is a file, not an API call.** Idempotency is measured against the file write. A real
  `add-comment` would have the same window between the effect and its record — the Durable variant
  names that window rather than closing it (`durable/README.md` §*The window a memo cannot close*).
- **The concurrency result is per store file, not per repository.** Two runs that agree on the store
  path cannot overlap; two that disagree both proceed and nothing notices. `durable/evidence.sh` stage
  5b is that control, run deliberately against the expected answer.

## Pinned source

| Field | Value |
| --- | --- |
| Repository | `github/gh-aw` |
| Commit | `380ae7bb0436b9b72a0be3ed026d3998914d7ee8` |
| Commit date | 2026-11-05T10:47:19Z (`main` at time of vendoring) |
| License | MIT |
| Workflow | `.github/workflows/pr-triage-agent.md` (161 lines) |
| Licence file | `LICENSE` (repository root) |

The vendored files are byte-identical to that commit: `git hash-object` returns the blob ids the
GitHub API reports for the same paths, and a re-fetch reproduces the same SHA-256 sums. The
`imports:` the workflow declares are listed in `upstream/PROVENANCE.md` and are deliberately not
copied — the reporting rules and the integrity gate on importing candidate PRs are absent, so a PR the
integrity gate would have excluded is triaged here.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0 or later | File-based apps (`dotnet run file.cs`) and the `durable/` project. On this machine `/opt/homebrew/bin/dotnet` is 10.0.402 |
| `git` | any recent | The baseline's state is a branch of a local bare repository, written with git plumbing |

No model, no API, no network, no Actions runner, no GitHub token. **That is not a convenience of this
band; it is the boundary above.** A run that needed none of those cannot say what one of them does.

Both drivers prefer `/opt/homebrew/bin/dotnet`, then whatever `dotnet` is first on `PATH`. Set
`DOTNET` to override.

## Commands

```bash
cd real-world-scenarios/band-c-ci-repo-agent

./run.sh                                   # baseline: full triage, then a second run inside the cooldown
(cd durable && ./run.sh)                   # Durable variant: one run, eight phases, a store
(cd durable && ./evidence.sh)              # every claim in durable/evidence/, reproduced (66 assertions)
./faults/run-all.sh                        # six faults, both variants, controls included (~50 s)
```

### Baseline: two runs, and what they prove

`./run.sh` runs two processes and exits 0 when both behaved:

| Run | What it does |
| --- | --- |
| 1 | Cold start: no `memory/pr-triage` branch, so Phase 1 has nothing to load. All eight phases run, 13 PRs are triaged, and Phase 8 writes the first commit on the branch. |
| 2 | A new process. Phase 1 reads run 1's branch back — `loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit 3a5a8276)` — and the run gate stops at the cooldown: no comments, no labels, no report, no state write. The branch is still at one commit. |

`run.sh` checks both outcomes and fails if run 2 either ignores the cooldown or is skipped when it
should not have been. The unedited capture of both runs is `evidence/two-runs.md`.

```bash
KEEP=1 ./run.sh                    # keep the scratch directory for inspection
RUN2_AFTER_MINUTES=61 ./run.sh     # put run 2 an hour past run 1: it triages again, second commit
NOW=2026-11-05T14:00:00Z ./run.sh  # move the base clock both runs start from
```

The fixture's dates are `openedMinutesAgo` / `updatedMinutesAgo`, so the run reproduces on any date.
`NOW` defaults to the fixture's own `capturedAt`, which makes two separate runs produce byte-identical
stdout.

The run gate is the one thing `baseline/` adds to the vendored workflow, and it is a consequence of the
substitution rather than a change to the cooldown rule: a scheduled job has six hours between runs, a
command can be run twice in a row. `FIDELITY.md` §"The run gate" argues it, and
`RUN2_AFTER_MINUTES=61` exists to prove the gate is not simply "refuse every second run".

### Durable variant: the store, the seams, and the controls

```bash
cd durable
./run.sh                                        # one run; eight phases and the store's account of itself
KEEP=1 ./run.sh && REUSE=1 ./run.sh             # the second run posts nothing (ledger)
./run.sh --now 2027-01-01T00:00:00Z             # move the clock
BAND_C_EXIT_AFTER_PHASE=comment ./run.sh        # stop after phase 6 commits
BAND_C_EXIT_AFTER_COMMENTS=7 ./run.sh           # stop inside phase 6, after seven comments
BAND_C_EXIT_AFTER_COMMENT_EFFECT=1 ./run.sh     # stop between a comment and its ledger entry
BAND_C_NO_IDEMPOTENCY_GUARD=1 ./run.sh          # remove memo and ledger reads: every entry re-posts
DURABLE_STORE=/somewhere/store.jsonl ./run.sh   # the lock is per path, so this is a different mutex
```

`BandCDurable.csproj` references `../../../lib/Copilot.Durable.csproj` — the shared layer, not a copy
of it. Whatever the layer cannot express is recorded in `durable/README.md` as a limitation and a
proposed change to `lib/`, never worked around inside the scenario.

## Teardown

| What | How |
| --- | --- |
| Either variant | it exits on its own; nothing stays running |
| The baseline's scratch tree (`.scratch/band-c-triage/`) | removed on exit unless `KEEP=1`; with `KEEP=1`, delete the path the run printed |
| The Durable variant's scratch tree (`.scratch/band-c-durable/`, store plus `.lock` and `.lock.owner` sidecars) | removed on exit unless `KEEP=1`. `REUSE=1` keeps the store *across* runs but does not keep the directory after one — set `KEEP=1` too if you want to inspect it after the second run |
| A fault-harness run | the script reaps every PID it started, by PID and by run directory, then removes `.scratch/band-c-faults/<variant>/<fault>/`. `KEEP=1` leaves it |
| Anything else | nothing — no docker, no cloud resource, no background service to shut down |

Both drivers only remove a scratch directory they marked themselves (`.band-c-scratch`,
`.band-c-durable-scratch`) and refuse with exit 2, touching nothing, one that already holds content
they did not write. Nothing is ever written inside this folder.

## The six faults, transcribed

Run against both variants, each with a `--fault-not-injected` control. Every cell below is the
`outcome` field of `faults/results/<variant>/<fault>.json`; `faults/results/summary.txt` is rendered
from those files by `faults/lib/summarise.py`, which fails if a variant is missing a fault's result or
its control's. Harness detail and the controls are in `faults/README.md`.

| Fault | baseline | durable | Result files |
| --- | --- | --- | --- |
| `kill -9` mid-turn | **lost, and re-commented** | **resumed** | [`baseline`](faults/results/baseline/kill-mid-turn.json) · [`durable`](faults/results/durable/kill-mid-turn.json) |
| restart after a committed checkpoint | **lost** | **resumed** | [`baseline`](faults/results/baseline/restart-after-checkpoint.json) · [`durable`](faults/results/durable/restart-after-checkpoint.json) |
| duplicate delivery | **doubled** | **refused, not doubled** | [`baseline`](faults/results/baseline/duplicate-delivery.json) · [`durable`](faults/results/durable/duplicate-delivery.json) |
| concurrent clients | **doubled, no conflict detected** | **refused, not doubled** | [`baseline`](faults/results/baseline/concurrent-clients.json) · [`durable`](faults/results/durable/concurrent-clients.json) |
| store unavailable mid-run | **failed loudly, effects kept, state lost** | **failed safely, with a split store** | [`baseline`](faults/results/baseline/store-outage.json) · [`durable`](faults/results/durable/store-outage.json) |
| cooldown desync | **doubled — 13 PRs hold two comments** | **not doubled — 0 posted, 13 adopted** | [`baseline`](faults/results/baseline/cooldown-desync.json) · [`durable`](faults/results/durable/cooldown-desync.json) |

All 24 runs — 6 faults × 2 variants plus 12 controls — exited 0 (`summary.txt` §*Harness status*); a
non-zero exit is a harness error, never a scenario losing work. Nothing in this table is `not
exercised`: both variants have a store on disk, both can be killed, and both can be started twice.

What each cell says, in the result files' own words:

- **`kill-mid-turn`, baseline — lost, and re-commented.** The `kill -9` landed with all 13 comment
  files on disk and no state written, so Phase 8 had not run. The next process was a cold start: *"it
  re-classified every PR and posted all thirteen comments again, of which 13 were byte-for-byte what
  was already there."*
- **`kill-mid-turn`, durable — resumed.** *"The committed run record named phase 'comment' …, so the
  new process resumed at 'comment' — the phase the kill cut short — and finished with the run at
  'save'. It wrote 4 comment(s) of which 0 were already on disk."*
- **`restart-after-checkpoint`, baseline — lost.** SIGTERM after Phase 5 had written `labels.json` and
  13 comments. *"Nothing was resumed, because a whole-file snapshot written at the end of Phase 8 is
  the baseline's only state and this run never reached it. The restart is not exact — there was nothing
  to be exact about."*
- **`restart-after-checkpoint`, durable — resumed.** *"The restart resumed that task at 'comment'
  rather than starting over … the phase it re-entered is the one the stop left, so the restart is
  exact: the committed phases did not run again."*
- **`duplicate-delivery`, baseline — doubled.** Two live processes each read a cold start and each
  posted thirteen comments; both exited 0. *"The second write used the parent it read at Phase 8, not
  at Phase 1, so the compare-and-swap never saw a conflict and neither run reports an error. The
  duplicate is entirely silent."*
- **`duplicate-delivery`, durable — refused, not doubled.** *"The first process took the store's
  exclusive lock; the second was refused at the open (exit 3 …) before it read the fixture."* The cost
  is in the same file: a refused run *"does nothing and does not queue"*, and *"the refusal is per
  store file rather than per repository."*
- **`concurrent-clients`, baseline — doubled, no conflict detected.** The first process was held with
  SIGSTOP after Phase 5 while the second ran to completion, then released. Both exited 0, the branch
  ended at one commit, and one run's state replaced the other's with no error.
- **`concurrent-clients`, durable — refused, not doubled.** The first held the lock while paused at
  phase 2; the second was refused at the open and *"did nothing at all — no read of the fixture, no
  comments."*
- **`store-outage`, baseline — failed loudly, effects kept, state lost.** `repo.git` was moved aside
  while Phase 8 was about to commit; the run exited 1 with 13 comments, `labels.json` and the report on
  disk and no state anywhere. *"That is failing loudly and it is also the reason the baseline is in
  this band: the outage is discovered, but only after the comments were posted."*
- **`store-outage`, durable — failed safely, with a split store.** The store file was moved aside
  under a live handle: exit 0, 13 comments, path recreated. *"Nothing that was committed was lost …
  but the state is now in two files … it does not fail loudly, and it does not tell anyone the file it
  was writing had gone."* A limitation, not a pass.
- **`cooldown-desync`, baseline — doubled.** Run 2 posted 13 comments and died before its state write;
  run 3, 61 minutes later, read run 1's record, found it past the cooldown and posted the same
  thirteen again, byte for byte.
- **`cooldown-desync`, durable — not doubled.** Run 3 resumed the unfinished task at `comment`,
  adopted 13, posted 0.

## The headline: `cooldown-desync` in numbers

Transcribed from the `observations` objects in `faults/results/*/cooldown-desync.json`, not from the
prose above.

| Variant | run 2 comments | run 3 posted | run 3 adopted | run 3 duplicated (byte-level) | run 3 resumed | guard-disabled arm: posted / duplicated |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| `baseline` | 13 | 13 | 0 | **13** | no | not run (no guard to disable) |
| `durable` | 13 | **0** | **13** | **0** | yes | **13 / 13** |

Baseline PRs holding two comments: `#101 #102 #103 #104 #105 #106 #107 #109 #111 #112 #113 #114 #115`
— every eligible PR, all thirteen. Durable: none (`prs_doubled: []`).

"Duplicated" is measured, not inferred: for each comment a run says it wrote, the harness compares the
bytes with a snapshot taken before the run. A count of files cannot see a duplicate, and neither can
"did it post anything".

The mechanism, plainly. **Baseline:** its only memory is a whole-file snapshot written as the last step
of Phase 8. A run that dies between the comments and that write leaves the comments and the *previous*
run's view, and it has no record of a comment — no digest, no timestamp, no id — so nothing exists that
could notice the re-post. **Durable:** each comment is memoised as it is made and written to a
`band-c.comments` ledger in the same transaction; run 3 finds an unfinished task at `comment`, resumes
there and adopts all thirteen. Run 4, a new generation of a finished task (what a later scheduled run
is), still posts nothing — so *"the guard outlived the task, not just the process."* The guard-disabled
arm is what makes the zero attributable: replayed from the same post-run-2 store with
`BAND_C_NO_IDEMPOTENCY_GUARD=1`, the same three runs post 13 with 13 duplicates.

Two caveats, stated here because this is the band's headline:

- **The two injections stop at different program counters.** The Durable variant has no end-of-run
  state write to fail, so its stop is *inside* the comment phase. The window is the same window in the
  sense that matters — comments posted, run not committed — but it is not the same instruction. The
  residue a memo cannot close is one duplicate, measured in `durable/evidence.sh` stage 4c and not
  re-derived by the harness.
- **The baseline's control for this fault does not flip** (`control: doubled`): committing run 2's
  state still leaves run 3 re-posting all thirteen, because the baseline has no comment identity. What
  the fault row adds is the stale view, not the duplicate. `faults/README.md` §"Where the control's
  outcome does not flip" records that rather than hiding it.

## Where the Durable variant is worse

Stated here rather than left to the other document, because a comparison that only lists wins is not
evidence:

- **73 store versions against one blob.** One clean run of 13 PRs leaves 8 `band-c.run`, 52 `band-c.pr`
  and 13 `band-c.comments` versions (`durable/evidence/_store.py versions`). The store grows with PRs
  times phases, not with runs. That is the price of resuming at any phase.
- **`store-outage` is not a durable win.** The baseline fails loudly and loses the state after the
  effects; this variant survives but splits its store in two and says nothing. The row records that.
- **A refused run does nothing.** No queue and no retry: an operator who starts two runs gets exit 3 to
  interpret, and the work the second run would have done is not done. The baseline produces a confusing
  failure instead — but it does produce work.
- **The refusal is per store file, not per repository.** `durable/run.sh` uses one default store path,
  so runs started through that script cannot overlap — a convention in a script, not a guarantee in the
  layer. Point two runs at two paths (`DURABLE_STORE=`) and both proceed.
- **The digest is a new failure mode.** Adding a field to `Triage.InputDigest` invalidates every stored
  assessment at once; forgetting to add one silently keeps an assessment the change should have
  replaced. The baseline's cold-start-every-run has no such trap.
- **A runtime dependency for a fixture with no model in it.** `baseline/` is one file and `git`; the
  Durable variant drags in the `Copilot.Durable` assembly and, through it, the Copilot SDK.

## Where the baseline wins on purpose

- **One write per run**, and the whole state is one `git show` away. Here, "what did the last run decide
  about #107" is a document lookup that needs `evidence/_store.py` or the program.
- **No lock, no sidecars, no format.** Nothing to install, nothing to migrate, no `.lock.owner` to
  explain to an operator.
- **It is the workflow's own shape.** The defects are upstream properties, reproduced rather than
  invented — which is why a difference against it is worth something.

## Lines of custom durability code

Measured with `wc -l` on 2026-11-05, in this directory:

```bash
wc -l baseline/PrTriageBaseline.cs
wc -l durable/Contracts.cs durable/ExitSeam.cs durable/Fixture.cs durable/Phases.cs \
      durable/Program.cs durable/Runner.cs durable/Triage.cs durable/TriageStore.cs
(cd durable && python3 evidence/count.py)    # the non-blank, non-comment split
```

| Variant | Files counted | `wc -l` total | Durability-specific | Split from |
| --- | --- | ---: | ---: | --- |
| `baseline/` | `baseline/PrTriageBaseline.cs` (1243) | **1243** | **106** — its whole `Repo` class, which is the read, the write and the compare-and-swap | `PrTriageBaseline.cs:931-1062`, counted non-blank, non-comment here |
| `durable/` | 8 files: `Contracts.cs` (253), `ExitSeam.cs` (143), `Fixture.cs` (86), `Phases.cs` (585), `Program.cs` (34), `Runner.cs` (275), `Triage.cs` (656), `TriageStore.cs` (184) | **2216** | **592** of **1443** code lines (41%) | `durable/README.md` §*Custom durability code, counted*; `python3 evidence/count.py` |

Two numbers per variant, and the difference matters:

- **`wc -l`** is every line of the files listed, blank lines and comments included. This is the literal
  measurement; the file list and the command are above so it can be repeated.
- **Durability-specific** is the per-declaration split from `evidence/count.py`, which classifies
  **non-blank, non-comment** lines in `durable/*.cs` and fails if a declaration is unclassified or the
  split does not add up to the file. A reader who disagrees with a call changes the split, not the
  `wc -l` total. The baseline's 106 is the same counting rule applied by hand to the one class whose
  lines would disappear if the state went elsewhere; `count.py` reads only `durable/*.cs`, so there is
  no automated baseline figure and this one is human-counted — say so when quoting it.
- **The two variants are not comparable at the same granularity.** `durable/Triage.cs` (499 of the
  1443 code lines) is the baseline's triage logic moved: `evidence/compare.py --code` re-reads both and
  checks it, 386 significant lines identical. The honest reading of the row is *"592 durability lines
  added to a 1243-line baseline, 106 of which the baseline already had."*
- **Not counted:** `run.sh`, `durable/run.sh`, `durable/evidence.sh`, `durable/evidence/*.py`,
  `faults/**`. They present and check the variants; they are not part of the agent.

## Layout

```
real-world-scenarios/band-c-ci-repo-agent/
├── README.md                       # this file
├── FIDELITY.md                     # faithful / reduced, and what it means for the matrix
├── run.sh                          # baseline: two runs, the second inside the cooldown
├── baseline/
│   ├── PrTriageBaseline.cs         # the eight phases and the git-branch store
│   └── fixture/pull-requests.json  # 17 fixed PRs, the Phase 1 snapshot
├── evidence/two-runs.md            # captured stdout of both baseline runs
├── durable/                        # the same agent over lib/Durable.cs
│   ├── README.md                   # checkpoint design, evidence, line-count table
│   ├── run.sh · evidence.sh        # one run; every claim reproduced
│   └── evidence/                   # captured output, plus the readers that check it
├── faults/                         # six faults against both variants, controls included
│   ├── README.md                   # the harness, the injection points, the vocabulary
│   ├── run-all.sh                  # sequence them, then render the summary
│   └── results/                    # one JSON per run, plus summary.txt rendered from them
└── upstream/
    ├── LICENSE                     # MIT, github/gh-aw
    ├── PROVENANCE.md               # repo, commit, paths, hashes
    └── pr-triage-agent.md          # the workflow, verbatim
```

## What was executed

On 2026-11-05, macOS arm64, .NET SDK 10.0.402 at `/opt/homebrew/bin/dotnet`:

- `./run.sh` twice, exit 0 both times, byte-identical stdout. The capture in `evidence/two-runs.md` is
  the first.
- `dotnet build` of `BandCDurable.csproj` and of `baseline/PrTriageBaseline.cs`, both clean.
- `./durable/evidence.sh` twice, exit 0 both times with identical assertion output. The capture in
  `durable/evidence/durable-triage-and-idempotency.md` is the first; 66 assertions, all passing.
- `./faults/run-all.sh` — 6 faults × 2 variants plus 12 controls, **exit 0 for all 24 runs**, results
  recorded between 2026-11-05T18:04:06Z and 18:05:02Z. Results in `faults/results/`, rendered into
  `faults/results/summary.txt`.

Not executed, and therefore not claimed:

- **The hosted Action, in either direction.** No Actions runner, no `gh aw` invocation, no live
  `add-comment`/`add-labels`/`create-issue` call, at any point.
- **Any model call.** The phases are deterministic functions of the fixture; the run needs no sign-in
  and produces the same output on any date. This is why the band cannot speak to triage quality.
- **A PR set that changes mid-run**, an API rejection, a rate limit, a partial success.
- **A store-write failure that is not a file move.** Neither variant injects one.
- **A restored or merged store after `store-outage`.** Neither variant merges; the Durable one is
  recreated, not repaired.

## Licence

MIT. See `upstream/LICENSE` and `upstream/PROVENANCE.md`.
