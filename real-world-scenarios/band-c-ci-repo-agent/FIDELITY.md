# Fidelity boundary

What `baseline/` reproduces from `upstream/pr-triage-agent.md`, what it replaces, and what that means
for a comparison with a Durable variant. This file is the one to read before quoting any number this
band produces.

## The two axes

The vendored workflow is an **agent job on an Actions runner**. Two things about it are separately
durable, and they fail differently:

1. **Phase order and the state payload.** Eight named phases that run in sequence, one JSON file
   written at the end. This is what a reproducer can carry over faithfully.
2. **The platform that runs it.** A fresh runner, a fresh container, `/tmp` discarded, the model
   call and every API call out of process. This is what a reproducer has to substitute.

A comparison that is honest about one and not the other is measuring the substitute. The tables below
separate them.

## Faithful

| Upstream behaviour | Where it is in `baseline/` |
| --- | --- |
| Eight phases in order: load state → collect PRs → classify → score → recommend → comment → report → save state | `PrTriageBaseline.cs`, `RunPhasesAsync`, one block per phase, each printing its own `phase n/8` header |
| State is a JSON file on branch `memory/pr-triage` | `Repo.WriteState` — `hash-object` the blob, `mktree`, `commit-tree`, then `update-ref refs/heads/memory/pr-triage` |
| State file name `pr-triage-latest.json` | `Repo.StatePath` |
| Cold start per run: nothing is carried in the process | Each run is a separate process; state enters only through `Repo.TryReadState` in Phase 1 |
| `repo-memory` is a **write to a branch**, not a transaction | Phase 8 is the last thing that happens, after the comments and the report. A run that dies in Phase 6 has already written comments and leaves the previous run's state untouched |
| Cooldown: "skip any PR that was opened or last updated ... less than 1 hour before the current run time" | `CooldownMinutes = 60`; the youngest of `openedMinutesAgo` / `updatedMinutesAgo` is compared, strictly `< 60`, and a skipped PR is not labelled, not commented on and not in the report's triaged figures |
| Author filter `app/github-copilot`, including forks | Phase 1: `Author == "app/github-copilot"` is the only test; PR #111 is a fork and is triaged, PR #110 is not and is not |
| Only open PRs | Phase 1: `state != "open"` is skipped before classification |
| Classify into `docs`, `test`, `formatting`, `chore`, `refactor`, `bug`, `feature` | `Classify`, in that vocabulary, from file paths, diff shape and title prefix |
| Risk into `low`, `medium`, `high` | `AssessRisk` |
| `total = impact (0-50) + urgency (0-30) + quality (0-20)` | `ScoreBreakdown`; the total is printed as the sum of the three, never typed in |
| Actions: `auto_merge`, `fast_track`, `batch_review`, `defer`, `close` | `Recommend`, same five names |
| Batch IDs for groups of 3+ | `Batch` — a cluster needs ≥3 members, and cluster ids are assigned in a fixed category order so two runs agree |
| Label families `pr-type:*`, `pr-risk:*`, `pr-priority:*`, `pr-action:*`, `pr-agent:*`, `pr-batch:*`; remove conflicting old triage labels, preserve non-triage labels | `TriageLabels`, `IsTriageLabel`. PR #106 carries a stale `pr-type:docs` in the fixture, which Phase 5 removes; PR #106's `bug` and `needs-review` survive |
| Report uses `###`/`####` headings only, with long lists inside `<details>` | `Report` |
| A compact per-PR comment with category, risk, total score, breakdown, action and optional batch | `Comment` |

## Reduced, and how

| Upstream | Here | What it costs |
| --- | --- | --- |
| `pull_request_read` against the live API | `baseline/fixture/pull-requests.json`, 17 fixed PRs | The PR set is fixed. Nothing here measures how the workflow behaves on a repository whose PRs change mid-run, because nothing here changes them. |
| A model call that decides category, risk and score | `Classify`, `AssessRisk`, `Score` — pure functions of the fixture | Scoring is reproducible and therefore comparable, and it is **not the workflow's judgement**. A model could rank the same PRs differently. Any claim that depends on the *quality* of the triage decisions is out of scope for this band. |
| `add-comment`, `add-labels`, `create-issue` safe outputs | `comments/pr-NNN.md`, `labels.json`, `triage-report.md`, `triage-data.json` in the run directory | Effects are files. Whether the API rejects, rate-limits or partially accepts them is not modelled. |
| Actions runner, `timeout-minutes: 30`, `max-ai-credits: 1500`, `max-tool-denials: 3` | none | No budget, no timeout, no tool-denial counter. A run cannot be killed for spending too much. |
| The schedule (`every 6h`) | `run.sh` runs twice back to back, or `--after <minutes>` moves the clock | The gap between runs is a choice, not a schedule. The cooldown decides what happens in that gap, which is the part being tested. |
| `imports:` — `shared/reporting.md`, `shared/pr-review-base.md`, `shared/otlp.md`, `{{#runtime-import .github/triage.md}}` | not vendored, not applied | Extra reporting rules and the integrity gate on importing candidate PRs are absent. A PR that the integrity gate would have excluded is triaged here. |
| `evals:` | none | No eval harness; the run's own stdout is the only observation. |

### The one-hour cooldown, exactly

The workflow states one rule and one consequence:

> Apply a **1-hour cooldown**: skip any PR that was opened or last updated (new commits, edits) less
> than 1 hour before the current run time.
>
> Respect the 1-hour cooldown: never label, comment on, or report an action for a PR opened or
> updated within the last hour.

Both halves are implemented as written: the filter is `min(opened, updated) < 60`, and a skipped PR
appears in the report only under "in cooldown", never with a label, a comment or an action. The two
PRs the fixture puts inside the window (#108 at 30m, #116 at 10m) flow through that path on every run
and are named in the output so a reader can see them rather than trust a count.

### The run gate

`baseline/` adds one thing the vendored workflow does not have, and it is a consequence of the
substitution rather than a change to the rule:

> If the state loaded in Phase 1 records a run started less than 60 minutes before this run, skip
> phase 2 through 8 and write nothing.

A scheduled workflow does not need this: six hours pass between runs by construction. Here a "run" is
one command, so two runs in a row are possible, and the question a second run has to answer is
precisely what the cooldown says — nothing has changed and nothing is eligible yet. The gate reads the
state and returns; it does not modify it. `--after <minutes>` in the driver moves the clock instead,
which is what a later scheduled run would have done, and proves the gate is not simply "refuse every
second run".

This is the point where a Durable variant has somewhere to stand: the choice between "read the state
and do nothing" and "carry on with the run that was interrupted" is the difference the band exists to
measure. Keep the distinction in mind when reading a comparison: a durable run that resumes is not
violating the cooldown, it is finishing the run the cooldown was measured from.

## What "git branch as a KV store" costs, observed

These are behaviours of the shape the workflow uses, recorded here because the band compares against
them. None is fixed in `baseline/` — fixing them is the thing under test.

- **The write is the last step.** Comments, labels and the report are written before Phase 8. A run
  killed between Phase 6 and Phase 8 leaves the effects without the record that they happened, and
  the next run re-triages from the previous run's view. Nothing detects the redo.
- **No lock.** `update-ref <ref> <new> <old>` is a compare-and-swap; a concurrent writer that moved
  the branch between the read and the write makes the write fail, and this code lets that failure
  surface. Two runs reading the same state and writing different results is not prevented — the
  second to commit fails, the first to commit wins, and neither knows the other existed.
- **One file, whole-file replacement.** Every field is rewritten per run. There is no partial update
  and no history beyond the branch's commit log, which records a message per run and nothing a
  reader can query without `git show`.
- **State is only as good as the branch.** A force-push, a rewrite of `memory/pr-triage`, or a
  branch deleted by a cleanup job is a silent reset to cold start. The code cannot tell "no branch"
  from "branch lost".
- **A malformed file fails the run.** Phase 1 parses `pr-triage-latest.json`; if a previous writer
  left invalid JSON there, the next run reports `state on refs/heads/memory/pr-triage is not valid
  JSON` plus the parse position, and exits 1. It does not fall back to a previous commit, and it
  does not treat the branch as absent: a branch that exists but cannot be read is a failure, not a
  cold start.

`--dry-run` on the C# driver writes the state objects without moving the branch. That exists to make
the crash-in-Phase-8 seam reproducible; it is a diagnostic, not a behaviour of the workflow.

## What this means for the matrix

- A durability comparison against this baseline is fair on **phase order, cooldown semantics, payload
  shape and cold start**, because those are reproduced as written.
- It is **not** a comparison of triage quality. The scorer is deterministic and the model is absent.
- The one axis the baseline cannot represent is a run that **ends before it is finished** and is then
  continued. The workflow has nowhere to put "I was here": its only record is a whole-file snapshot
  written at the end. A variant that resumes mid-run is therefore not improving this baseline's
  numbers, it is adding a state the baseline does not have — and that is exactly what the band
  should say, in those words, rather than as a win.

## What was executed

Everything printed in `README.md` and `evidence/two-runs.md` was produced by `./run.sh` on
2026-11-05 with `dotnet` 10.0.402 at `/opt/homebrew/bin/dotnet`. The output is pasted from the shell
capture unedited. No model was called and no network access was needed: the run reads a local fixture
and writes to a local bare repository.
