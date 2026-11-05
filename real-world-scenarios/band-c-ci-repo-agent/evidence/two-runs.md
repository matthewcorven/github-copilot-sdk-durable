# Band C baseline — captured evidence, two runs

Both runs were executed by `./run.sh` on 2026-11-05 with `dotnet` 10.0.402 at
`/opt/homebrew/bin/dotnet`, from a clean scratch directory. The block below is the shell capture
unedited: `./run.sh` stdout and stderr together, nothing added or removed. The script exited 0.

No model call was made and no network was used. The run reads `baseline/fixture/pull-requests.json`
and writes to a bare git repository created under `.scratch/band-c-triage/`, which `run.sh` removed
on exit — that is why the paths below no longer exist.

The second run is the one that matters: it is a new process, its only input from the first is the
`memory/pr-triage` branch in the shared bare repository, and it has to read that branch to decide it
is inside the cooldown.

## The command

```sh
./real-world-scenarios/band-c-ci-repo-agent/run.sh
```

## Output

```
scratch:   /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)
fixture:   /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json

════════════════════════════════════════════════════════════════════════
 run 1 — cold start: no memory/pr-triage branch, all eight phases
════════════════════════════════════════════════════════════════════════
band C baseline — local reproduction of github/gh-aw pr-triage-agent
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
repo:       /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/repo.git
fixture:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-1

phase 1/8  load state and collect pull requests
  state:      cold start — no memory/pr-triage branch, nothing loaded
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

phase 2/8  classify and assess risk
  #101  docs       risk=low    2 file(s), +6/-6  docs: fix quickstart typos and the sample output
  #102  test       risk=low    3 file(s), +190/-4  test: add parity tests for the TypeScript port
  #103  formatting risk=low    1 file(s), +62/-62  formatting: reflow the long lines in durable.ts
  #104  chore      risk=medium 2 file(s), +18/-18  chore: bump the Copilot CLI pin to 1.0.105
  #105  refactor   risk=medium 12 file(s), +260/-240  refactor: extract the JSON store options out of Durable.cs
  #106  bug        risk=high   4 file(s), +180/-40  fix: replay drops the tool result when the runtime retries
  #107  feature    risk=high   41 file(s), +2400/-300  feat: apply backpressure when the admission queue is full
  #109  chore      risk=high   2 file(s), +0/-140  chore: remove the dead replay shim
  #111  docs       risk=low    2 file(s), +48/-3  docs: add the Python port notes to the README
  #112  formatting risk=low    1 file(s), +310/-310  formatting: normalise tabs to spaces in the Python port
  #113  formatting risk=low    2 file(s), +900/-900  formatting: normalise line endings in the example scripts
  #114  chore      risk=medium 2 file(s), +8/-8  chore: refresh the sandbox base image pins
  #115  chore      risk=medium 1 file(s), +44/-44  chore: bump the uv lock for the Python port

phase 3/8  score priority (impact 0-50 + urgency 0-30 + quality 0-20)
  #101   44 = impact 24 + urgency  0 + quality 20
  #102   49 = impact 29 + urgency  0 + quality 20
  #103   46 = impact 30 + urgency  0 + quality 16
  #104   50 = impact 34 + urgency  0 + quality 16
  #105   61 = impact 41 + urgency  0 + quality 20
  #106   76 = impact 50 + urgency 24 + quality  2
  #107   61 = impact 45 + urgency  6 + quality 10
  #109   58 = impact 32 + urgency 14 + quality 12
  #111   42 = impact 22 + urgency  0 + quality 20
  #112   44 = impact 28 + urgency  0 + quality 16
  #113   26 = impact 14 + urgency  0 + quality 12
  #114   52 = impact 36 + urgency  0 + quality 16
  #115   50 = impact 34 + urgency  0 + quality 16

phase 4/8  recommend actions
  #101  auto_merge    low risk, quality 20/20, CI passing
  #102  auto_merge    low risk, quality 20/20, CI passing
  #103  auto_merge    low risk, quality 16/20, CI passing
  #104  batch_review  similar low/medium-risk work, reviewed together
  #105  fast_track    quality 20/20, impact 41/50
  #106  defer         high risk and CI failing
  #107  batch_review  similar low/medium-risk work, reviewed together
  #109  close         superseded by #107
  #111  auto_merge    low risk, quality 20/20, CI passing
  #112  auto_merge    low risk, quality 16/20, CI passing
  #113  defer         impact 14/50 below the triage threshold
  #114  batch_review  similar low/medium-risk work, reviewed together
  #115  batch_review  similar low/medium-risk work, reviewed together

phase 5/8  batch and label
  batch-001  formatting x3: #103, #112, #113
  batch-002  chore x3: #104, #114, #115
  #101  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [documentation]
  #102  add [pr-type:test, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [tests]
  #103  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #104  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #105  add [pr-type:refactor, pr-risk:medium, pr-priority:p1, pr-action:fast_track, pr-agent:same-repo] keep [refactor]
  #106  add [pr-type:bug, pr-risk:high, pr-priority:p0, pr-action:defer, pr-agent:same-repo] remove [pr-type:docs] keep [bug, needs-review]
  #107  add [pr-type:feature, pr-risk:high, pr-priority:p1, pr-action:batch_review, pr-agent:same-repo] remove [pr-risk:low] keep [enhancement]
  #109  add [pr-type:chore, pr-risk:high, pr-priority:p1, pr-action:close, pr-agent:same-repo] keep [chore, stale]
  #111  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:fork] keep [documentation]
  #112  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #113  add [pr-type:formatting, pr-risk:low, pr-priority:p3, pr-action:defer, pr-agent:same-repo, pr-batch:batch-001] keep []
  #114  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #115  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  wrote:      labels.json (add-labels safe output)

phase 6/8  comment each triaged PR
  wrote:      comments/pr-101.md
  wrote:      comments/pr-102.md
  wrote:      comments/pr-103.md
  wrote:      comments/pr-104.md
  wrote:      comments/pr-105.md
  wrote:      comments/pr-106.md
  wrote:      comments/pr-107.md
  wrote:      comments/pr-109.md
  wrote:      comments/pr-111.md
  wrote:      comments/pr-112.md
  wrote:      comments/pr-113.md
  wrote:      comments/pr-114.md
  wrote:      comments/pr-115.md

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      memory/pr-triage:pr-triage-latest.json (7346 bytes)
  commit:     3a5a8276 on refs/heads/memory/pr-triage (parent none)

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

════════════════════════════════════════════════════════════════════════
 run 2 — reads run 1's state back and applies the cooldown rule
════════════════════════════════════════════════════════════════════════
band C baseline — local reproduction of github/gh-aw pr-triage-agent
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
repo:       /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/repo.git
fixture:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-2

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
              #102 test: add parity tests for the TypeScript port
              #103 formatting: reflow the long lines in durable.ts
              #104 chore: bump the Copilot CLI pin to 1.0.105
              #105 refactor: extract the JSON store options out of Durable.cs
              #106 fix: replay drops the tool result when the runtime retries
              #107 feat: apply backpressure when the admission queue is full
              #109 chore: remove the dead replay shim
              #111 docs: add the Python port notes to the README
              #112 formatting: normalise tabs to spaces in the Python port
              #113 formatting: normalise line endings in the example scripts
              #114 chore: refresh the sandbox base image pins
              #115 chore: bump the uv lock for the Python port

run skipped by the one-hour cooldown; no state was written.

cooldown honoured: run 2 read the state, did not triage, and did not write state.

state branch:
3a5a827 pr-triage: run 2026-11-05T12:00:00Z

run 1 outputs: /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-1/{comments,triage-report.md,triage-data.json,labels.json}
run 2 outputs: /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-2/
```

## What to look at

- Run 1 phase 1: `cold start — no memory/pr-triage branch`. Run 1 phase 8: one commit on
  `refs/heads/memory/pr-triage`, `parent none`.
- Run 2 phase 1: `loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit 3a5a8276)` and
  `prior run: ... triaged 13 PR(s)` — the state was read back, from the branch, by a different
  process.
- Run 2's `run gate` line and `run skipped by the one-hour cooldown; no state was written.`: the
  cooldown is honoured, and the branch is still at one commit afterwards (`state branch:` at the end).
- Both runs skip PRs #108 and #116 as individual PRs, on the per-PR half of the cooldown, before the
  run gate is reached. They stay skipped in run 2 as well, because the fixture's clocks do not move.

## The other direction: run 2 past the cooldown

`RUN2_AFTER_MINUTES=61 ./run.sh` puts run 2's clock an hour past run 1's. It captures the fact
that the gate is a cooldown and not a refusal. Run on the same machine on 2026-11-05; the full
stdout is below, unedited.

```sh
$ RUN2_AFTER_MINUTES=61 ./real-world-scenarios/band-c-ci-repo-agent/run.sh
```

```
scratch:   /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage
dotnet:    /opt/homebrew/bin/dotnet (10.0.402)
fixture:   /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json

════════════════════════════════════════════════════════════════════════
 run 1 — cold start: no memory/pr-triage branch, all eight phases
════════════════════════════════════════════════════════════════════════
band C baseline — local reproduction of github/gh-aw pr-triage-agent
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
repo:       /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/repo.git
fixture:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-1

phase 1/8  load state and collect pull requests
  state:      cold start — no memory/pr-triage branch, nothing loaded
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

phase 2/8  classify and assess risk
  #101  docs       risk=low    2 file(s), +6/-6  docs: fix quickstart typos and the sample output
  #102  test       risk=low    3 file(s), +190/-4  test: add parity tests for the TypeScript port
  #103  formatting risk=low    1 file(s), +62/-62  formatting: reflow the long lines in durable.ts
  #104  chore      risk=medium 2 file(s), +18/-18  chore: bump the Copilot CLI pin to 1.0.105
  #105  refactor   risk=medium 12 file(s), +260/-240  refactor: extract the JSON store options out of Durable.cs
  #106  bug        risk=high   4 file(s), +180/-40  fix: replay drops the tool result when the runtime retries
  #107  feature    risk=high   41 file(s), +2400/-300  feat: apply backpressure when the admission queue is full
  #109  chore      risk=high   2 file(s), +0/-140  chore: remove the dead replay shim
  #111  docs       risk=low    2 file(s), +48/-3  docs: add the Python port notes to the README
  #112  formatting risk=low    1 file(s), +310/-310  formatting: normalise tabs to spaces in the Python port
  #113  formatting risk=low    2 file(s), +900/-900  formatting: normalise line endings in the example scripts
  #114  chore      risk=medium 2 file(s), +8/-8  chore: refresh the sandbox base image pins
  #115  chore      risk=medium 1 file(s), +44/-44  chore: bump the uv lock for the Python port

phase 3/8  score priority (impact 0-50 + urgency 0-30 + quality 0-20)
  #101   44 = impact 24 + urgency  0 + quality 20
  #102   49 = impact 29 + urgency  0 + quality 20
  #103   46 = impact 30 + urgency  0 + quality 16
  #104   50 = impact 34 + urgency  0 + quality 16
  #105   61 = impact 41 + urgency  0 + quality 20
  #106   76 = impact 50 + urgency 24 + quality  2
  #107   61 = impact 45 + urgency  6 + quality 10
  #109   58 = impact 32 + urgency 14 + quality 12
  #111   42 = impact 22 + urgency  0 + quality 20
  #112   44 = impact 28 + urgency  0 + quality 16
  #113   26 = impact 14 + urgency  0 + quality 12
  #114   52 = impact 36 + urgency  0 + quality 16
  #115   50 = impact 34 + urgency  0 + quality 16

phase 4/8  recommend actions
  #101  auto_merge    low risk, quality 20/20, CI passing
  #102  auto_merge    low risk, quality 20/20, CI passing
  #103  auto_merge    low risk, quality 16/20, CI passing
  #104  batch_review  similar low/medium-risk work, reviewed together
  #105  fast_track    quality 20/20, impact 41/50
  #106  defer         high risk and CI failing
  #107  batch_review  similar low/medium-risk work, reviewed together
  #109  close         superseded by #107
  #111  auto_merge    low risk, quality 20/20, CI passing
  #112  auto_merge    low risk, quality 16/20, CI passing
  #113  defer         impact 14/50 below the triage threshold
  #114  batch_review  similar low/medium-risk work, reviewed together
  #115  batch_review  similar low/medium-risk work, reviewed together

phase 5/8  batch and label
  batch-001  formatting x3: #103, #112, #113
  batch-002  chore x3: #104, #114, #115
  #101  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [documentation]
  #102  add [pr-type:test, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [tests]
  #103  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #104  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #105  add [pr-type:refactor, pr-risk:medium, pr-priority:p1, pr-action:fast_track, pr-agent:same-repo] keep [refactor]
  #106  add [pr-type:bug, pr-risk:high, pr-priority:p0, pr-action:defer, pr-agent:same-repo] remove [pr-type:docs] keep [bug, needs-review]
  #107  add [pr-type:feature, pr-risk:high, pr-priority:p1, pr-action:batch_review, pr-agent:same-repo] remove [pr-risk:low] keep [enhancement]
  #109  add [pr-type:chore, pr-risk:high, pr-priority:p1, pr-action:close, pr-agent:same-repo] keep [chore, stale]
  #111  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:fork] keep [documentation]
  #112  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #113  add [pr-type:formatting, pr-risk:low, pr-priority:p3, pr-action:defer, pr-agent:same-repo, pr-batch:batch-001] keep []
  #114  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #115  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  wrote:      labels.json (add-labels safe output)

phase 6/8  comment each triaged PR
  wrote:      comments/pr-101.md
  wrote:      comments/pr-102.md
  wrote:      comments/pr-103.md
  wrote:      comments/pr-104.md
  wrote:      comments/pr-105.md
  wrote:      comments/pr-106.md
  wrote:      comments/pr-107.md
  wrote:      comments/pr-109.md
  wrote:      comments/pr-111.md
  wrote:      comments/pr-112.md
  wrote:      comments/pr-113.md
  wrote:      comments/pr-114.md
  wrote:      comments/pr-115.md

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      memory/pr-triage:pr-triage-latest.json (7346 bytes)
  commit:     3a5a8276 on refs/heads/memory/pr-triage (parent none)

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

════════════════════════════════════════════════════════════════════════
 run 2 — reads run 1's state back and applies the cooldown rule
════════════════════════════════════════════════════════════════════════
band C baseline — local reproduction of github/gh-aw pr-triage-agent
run time:   2026-11-05T13:01:00Z
repository: fixture/durable
repo:       /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/repo.git
fixture:    /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-2

phase 1/8  load state and collect pull requests
  state:      loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit 3a5a8276)
  prior run:  2026-11-05T12:00:00Z triaged 13 PR(s)
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

phase 2/8  classify and assess risk
  #101  docs       risk=low    2 file(s), +6/-6  docs: fix quickstart typos and the sample output
  #102  test       risk=low    3 file(s), +190/-4  test: add parity tests for the TypeScript port
  #103  formatting risk=low    1 file(s), +62/-62  formatting: reflow the long lines in durable.ts
  #104  chore      risk=medium 2 file(s), +18/-18  chore: bump the Copilot CLI pin to 1.0.105
  #105  refactor   risk=medium 12 file(s), +260/-240  refactor: extract the JSON store options out of Durable.cs
  #106  bug        risk=high   4 file(s), +180/-40  fix: replay drops the tool result when the runtime retries
  #107  feature    risk=high   41 file(s), +2400/-300  feat: apply backpressure when the admission queue is full
  #109  chore      risk=high   2 file(s), +0/-140  chore: remove the dead replay shim
  #111  docs       risk=low    2 file(s), +48/-3  docs: add the Python port notes to the README
  #112  formatting risk=low    1 file(s), +310/-310  formatting: normalise tabs to spaces in the Python port
  #113  formatting risk=low    2 file(s), +900/-900  formatting: normalise line endings in the example scripts
  #114  chore      risk=medium 2 file(s), +8/-8  chore: refresh the sandbox base image pins
  #115  chore      risk=medium 1 file(s), +44/-44  chore: bump the uv lock for the Python port

phase 3/8  score priority (impact 0-50 + urgency 0-30 + quality 0-20)
  #101   44 = impact 24 + urgency  0 + quality 20
  #102   49 = impact 29 + urgency  0 + quality 20
  #103   46 = impact 30 + urgency  0 + quality 16
  #104   50 = impact 34 + urgency  0 + quality 16
  #105   61 = impact 41 + urgency  0 + quality 20
  #106   76 = impact 50 + urgency 24 + quality  2
  #107   61 = impact 45 + urgency  6 + quality 10
  #109   58 = impact 32 + urgency 14 + quality 12
  #111   42 = impact 22 + urgency  0 + quality 20
  #112   44 = impact 28 + urgency  0 + quality 16
  #113   26 = impact 14 + urgency  0 + quality 12
  #114   52 = impact 36 + urgency  0 + quality 16
  #115   50 = impact 34 + urgency  0 + quality 16

phase 4/8  recommend actions
  #101  auto_merge    low risk, quality 20/20, CI passing
  #102  auto_merge    low risk, quality 20/20, CI passing
  #103  auto_merge    low risk, quality 16/20, CI passing
  #104  batch_review  similar low/medium-risk work, reviewed together
  #105  fast_track    quality 20/20, impact 41/50
  #106  defer         high risk and CI failing
  #107  batch_review  similar low/medium-risk work, reviewed together
  #109  close         superseded by #107
  #111  auto_merge    low risk, quality 20/20, CI passing
  #112  auto_merge    low risk, quality 16/20, CI passing
  #113  defer         impact 14/50 below the triage threshold
  #114  batch_review  similar low/medium-risk work, reviewed together
  #115  batch_review  similar low/medium-risk work, reviewed together

phase 5/8  batch and label
  batch-001  formatting x3: #103, #112, #113
  batch-002  chore x3: #104, #114, #115
  #101  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [documentation]
  #102  add [pr-type:test, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo] keep [tests]
  #103  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #104  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #105  add [pr-type:refactor, pr-risk:medium, pr-priority:p1, pr-action:fast_track, pr-agent:same-repo] keep [refactor]
  #106  add [pr-type:bug, pr-risk:high, pr-priority:p0, pr-action:defer, pr-agent:same-repo] remove [pr-type:docs] keep [bug, needs-review]
  #107  add [pr-type:feature, pr-risk:high, pr-priority:p1, pr-action:batch_review, pr-agent:same-repo] remove [pr-risk:low] keep [enhancement]
  #109  add [pr-type:chore, pr-risk:high, pr-priority:p1, pr-action:close, pr-agent:same-repo] keep [chore, stale]
  #111  add [pr-type:docs, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:fork] keep [documentation]
  #112  add [pr-type:formatting, pr-risk:low, pr-priority:p2, pr-action:auto_merge, pr-agent:same-repo, pr-batch:batch-001] keep []
  #113  add [pr-type:formatting, pr-risk:low, pr-priority:p3, pr-action:defer, pr-agent:same-repo, pr-batch:batch-001] keep []
  #114  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  #115  add [pr-type:chore, pr-risk:medium, pr-priority:p2, pr-action:batch_review, pr-agent:same-repo, pr-batch:batch-002] keep [dependencies]
  wrote:      labels.json (add-labels safe output)

phase 6/8  comment each triaged PR
  wrote:      comments/pr-101.md
  wrote:      comments/pr-102.md
  wrote:      comments/pr-103.md
  wrote:      comments/pr-104.md
  wrote:      comments/pr-105.md
  wrote:      comments/pr-106.md
  wrote:      comments/pr-107.md
  wrote:      comments/pr-109.md
  wrote:      comments/pr-111.md
  wrote:      comments/pr-112.md
  wrote:      comments/pr-113.md
  wrote:      comments/pr-114.md
  wrote:      comments/pr-115.md

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      memory/pr-triage:pr-triage-latest.json (7364 bytes)
  commit:     35a5d342 on refs/heads/memory/pr-triage (parent 3a5a8276)

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

cooldown expired: run 2 triaged again and wrote a second commit on memory/pr-triage.

state branch:
35a5d34 pr-triage: run 2026-11-05T13:01:00Z
3a5a827 pr-triage: run 2026-11-05T12:00:00Z

run 1 outputs: /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-1/{comments,triage-report.md,triage-data.json,labels.json}
run 2 outputs: /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-154-c1-triage/.scratch/band-c-triage/run-2/
```

The 18-byte growth between the two state files (7346 B to 7364 B) is the `previousRun` field on
the second run's payload.
