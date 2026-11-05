# Band C — the gh-aw PR triage agent, reproduced locally

A reproduction of `github/gh-aw`'s `.github/workflows/pr-triage-agent.md`: a scheduled agent job that
triages open `app/github-copilot` pull requests in eight phases and keeps its only memory between
runs in a **git branch**. The vendored workflow is in `upstream/`; the reproduction is in
`baseline/`.

**The state is the thing.** Phase 8 writes `pr-triage-latest.json` to branch `memory/pr-triage`
via a custom `repo-memory` tool. That file, and nothing else, survives a run — the runner, the
workspace, the process and `/tmp` are created and discarded per run. `baseline/` keeps that shape
exactly, including what it costs: the write is the last step, there is no lock, and a run that dies
between the comments and the state write leaves the comments and the previous run's view. None of
that is improved here; it is the property Band C exists to measure.

The Durable variant of this scenario is a separate work item (#155). Nothing in `baseline/` imports,
references, or asserts on the Durable library: there is no store, no checkpoint, no task and no
conversation document in this tree.

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The workflow and the repository's `LICENSE`, byte-identical to a pinned commit. Hashes in `upstream/PROVENANCE.md`. |
| `baseline/PrTriageBaseline.cs` | The reproduction. Single file-based .NET app, eight phases, state on a git branch. |
| `baseline/fixture/pull-requests.json` | 17 fixed pull requests, the `pull_request_read` calls of Phase 1 reduced to a snapshot. |
| `run.sh` | Two runs: one full triage, then a second that reads the state back and is inside the cooldown. |
| `FIDELITY.md` | What is faithful, what is reduced, and what that means for the matrix. Read it before quoting a number. |
| `evidence/two-runs.md` | The captured stdout of both runs, with the command that produced them. |
| `faults/` | Six faults — `kill-mid-turn`, `restart-after-checkpoint`, `duplicate-delivery`, `concurrent-clients`, `store-outage`, `cooldown-desync` — each run against both variants with a `--fault-not-injected` control. `./faults/run-all.sh` renders the matrix from the checked-in results. |

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
copied.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0 or later | File-based apps (`dotnet run file.cs`). On this machine `/opt/homebrew/bin/dotnet` is 10.0.402 |
| `git` | any recent | The state is a branch of a local bare repository, written with git plumbing |

No model, no API, no network, no Actions runner, no GitHub token.

`run.sh` prefers `/opt/homebrew/bin/dotnet`, then whatever `dotnet` is first on `PATH`. Set
`DOTNET` to override.

## One command

```bash
./real-world-scenarios/band-c-ci-repo-agent/run.sh
```

It runs two processes and exits 0 when both behaved:

| Run | What it does |
| --- | --- |
| 1 | Cold start: no `memory/pr-triage` branch, so Phase 1 has nothing to load. All eight phases run, 13 PRs are triaged, and Phase 8 writes the first commit on the branch. |
| 2 | A new process. Phase 1 reads run 1's branch back — `loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit 3a5a8276)`, `prior run: … triaged 13 PR(s)` — and the run gate stops at the cooldown: no comments, no labels, no report, no state write. The branch is still at one commit afterwards. |

`run.sh` checks both outcomes and fails if run 2 either ignores the cooldown or is skipped when it
should not have been.

Variants:

```bash
KEEP=1 ./run.sh                    # keep the scratch directory for inspection
RUN2_AFTER_MINUTES=61 ./run.sh     # put run 2 an hour past run 1: it triages again and writes a second commit
NOW=2026-11-05T14:00:00Z ./run.sh  # move the base clock both runs start from
```

The fixture's dates are expressed as `openedMinutesAgo` / `updatedMinutesAgo`, so the run reproduces
on any date. `NOW` defaults to the fixture's own `capturedAt`, which makes two separate runs of
`run.sh` produce byte-identical stdout. `NOW` and `RUN2_AFTER_MINUTES` combine: run 1 starts at
`NOW`, run 2 at `NOW + RUN2_AFTER_MINUTES`.

### What a run writes

Everything lands in `.scratch/band-c-triage/` in the worktree root — never inside this tree, so a run
cannot damage the repo. The directory carries an empty `.band-c-scratch` marker; `run.sh` removes a
directory it created and refuses (exit 2, leaving the path alone) one that already holds content it
did not write.

| Path | Contents |
| --- | --- |
| `repo.git/` | The bare repository standing in for the GitHub repository. Branch `main` holds an empty root commit; `memory/pr-triage` is the state. |
| `run-1/comments/pr-NNN.md` | The Phase 6 comment for each triaged PR — the `add-comment` safe output, as files |
| `run-1/labels.json` | Phase 5's label additions, removals and preserved non-triage labels |
| `run-1/triage-report.md` | Phase 7's report issue, `###`/`####` headings, long lists in `<details>` |
| `run-1/triage-data.json` | Phase 7's `data` safe output: the four fields per PR the workflow declares |
| `run-2/` | Empty except for whatever run 2 wrote, which is nothing when it is skipped |
| `run-2.log` | Run 2's stdout, captured while `run.sh` checks the cooldown, then deleted. `KEEP=1` keeps the scratch tree but does not keep this file — re-run without `KEEP` and tee the output if you need it |

The state file itself is not a file on disk: it is a blob on the branch. Read it with

```bash
git --git-dir .scratch/band-c-triage/repo.git show memory/pr-triage:pr-triage-latest.json
git --git-dir .scratch/band-c-triage/repo.git log --oneline memory/pr-triage
```

## The eight phases, as implemented

Printed by the run in this order, one `phase n/8` header each:

1. **Load state and collect PRs.** `Repo.TryReadState` reads `memory/pr-triage`; the fixture is read;
   PRs not authored by `app/github-copilot`, PRs not open, and PRs inside the one-hour cooldown are
   filtered and named.
2. **Classify and assess risk.** `docs`, `test`, `formatting`, `chore`, `refactor`, `bug`, `feature`;
   `low`, `medium`, `high`.
3. **Score priority.** `impact (0-50) + urgency (0-30) + quality (0-20)`, printed as the sum of its
   parts.
4. **Recommend actions.** `auto_merge`, `fast_track`, `batch_review`, `defer`, `close`, each with the
   reason that produced it.
5. **Batch and label.** Clusters of 3+, then the five label families plus optional `pr-batch:*`;
   conflicting old triage labels are removed, everything else is preserved.
6. **Comment each triaged PR.** One file per PR under `comments/`.
7. **Create the triage report.** Totals, distribution, top priority, the action groups, batches,
   the cooldown list, and the trends.
8. **Save run state.** One commit on `memory/pr-triage`.

### The one-hour cooldown

The workflow's rule, implemented as written:

> skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the
> current run time

`CooldownMinutes = 60`; the younger of `openedMinutesAgo` and `updatedMinutesAgo` decides; a skipped
PR gets no label, no comment and no action. `#108` (updated 30m ago) and `#116` (updated 10m ago) are
in the fixture for exactly this path, and the run prints both by number so the skip is visible rather
than a count to trust.

`baseline/` adds a second, run-level use of the same rule that the vendored workflow does not need:
if the loaded state records a run started less than 60 minutes before this one, the run stops after
Phase 1 and writes nothing. A scheduled job has six hours between runs and never faces the question;
a command can be run twice in a row, and the answer the cooldown's own wording gives is "nothing is
eligible yet". `FIDELITY.md` §"The run gate" explains why this is a consequence of the substitution
rather than a change to the rule, and why `RUN2_AFTER_MINUTES=61` exists to prove it.

## The captured run

`./run.sh` on 2026-11-05, `dotnet` 10.0.402, exited 0. Run 1's Phase 1 and Phase 8, then run 2's
Phase 1 and its stop:

```
phase 1/8  load state and collect pull requests
  state:      cold start — no memory/pr-triage branch, nothing loaded
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)
...
phase 8/8  save run state
  wrote:      memory/pr-triage:pr-triage-latest.json (7346 bytes)
  commit:     3a5a8276 on refs/heads/memory/pr-triage (parent none)

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.
```

Run 2, a new process:

```
phase 1/8  load state and collect pull requests
  state:      loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit 3a5a8276)
  prior run:  2026-11-05T12:00:00Z triaged 13 PR(s)
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

  run gate:   the previous run finished 0m ago (< 60m); this run is inside the cooldown and takes no action.
  run gate:   would have triaged 13 PR(s); no comments, no labels, no report, no state write.
              #101 docs: fix quickstart typos and the sample output
              ... (13 lines, one per eligible PR)
              #115 chore: bump the uv lock for the Python port

run skipped by the one-hour cooldown; no state was written.

cooldown honoured: run 2 read the state, did not triage, and did not write state.

state branch:
3a5a827 pr-triage: run 2026-11-05T12:00:00Z
```

The full unedited capture of both runs is `evidence/two-runs.md`. The `#101 … #115` list is elided
there too in this file but not in the evidence; `run.sh` prints all thirteen.

## Exit codes

| Code | `run.sh` |
| --- | --- |
| 0 | Both runs behaved: run 1 wrote one commit, run 2 read it back and either honoured the cooldown (`RUN2_AFTER_MINUTES` < 60) or did not apply it (≥ 60). |
| 1 | The baseline build failed, or a run exited non-zero, or run 2 did the wrong thing with the cooldown. The scratch directory is still removed unless `KEEP=1`. |
| 2 | No `dotnet`, bad usage, or a `.scratch/band-c-triage` that already holds content this script did not write. The path is not touched. |

## Layout

```
real-world-scenarios/band-c-ci-repo-agent/
├── README.md                       # this file
├── FIDELITY.md                     # faithful / reduced, and what it means for the matrix
├── run.sh                          # two runs: full triage, then the cooldown
├── baseline/
│   ├── PrTriageBaseline.cs         # the eight phases and the git-branch store
│   └── fixture/
│       └── pull-requests.json      # 17 fixed PRs, the Phase 1 snapshot
├── evidence/
│   └── two-runs.md                 # captured stdout of both runs
├── faults/                         # six faults against both variants, controls included
└── upstream/
    ├── LICENSE                     # MIT, github/gh-aw
    ├── PROVENANCE.md               # repo, commit, paths, hashes
    └── pr-triage-agent.md          # the workflow, verbatim
```

## What the baseline does not do

Stated here so a comparison does not have to discover it:

- It does not model a model. Classification, risk and score are deterministic functions of the
  fixture, so the run is reproducible and is **not** a measure of triage quality. `FIDELITY.md`
  says which claims that rules out.
- It does not model the API. Comments, labels and the report are files. A rejected request, a
  partial success and a rate limit are all absent.
- It does not resume. There is no state for "which phase was running". A run either finishes and
  writes state, or leaves the previous state in place with its effects already written.
- It does not lock. `update-ref … <old>` fails if a concurrent writer moved the branch, and that
  failure surfaces as exit 1 rather than being retried or merged.

Each of those is a gap a Durable variant may close; the evidence file for that variant should say
which, rather than a difference in the numbers being read as an improvement.
