# Band C Durable variant — captured evidence

Every block below is the stdout of `./durable/evidence.sh`, unedited apart from the same two path
substitutions the script itself makes while it prints (`<scratch>` for the scratch directory and
`<worktree>` for the worktree root, so the capture does not carry a machine path). Each `$ line` is
the command, printed by the script before it ran, and each `[exit N]` is its status.

Executed on 2026-11-05, macOS arm64, `dotnet` 10.0.402 at `/opt/homebrew/bin/dotnet`, from a clean
scratch directory. The script exited 0 and every `✓` line below is one of its assertions; there is a
`✗` line in this file only in the paragraphs that record a rejected attempt.

Two runs of `./durable/evidence.sh` produce identical assertion output, which is what makes the
numbers here reproducible rather than a snapshot of one lucky run.

Contents:

1. Stage 1 — both variants, same fixture, same clock, identical output documents
2. Stage 2 — re-run unchanged posts no comment, and the control that posts thirteen
3. Stage 3 — interrupted after commenting, resumed, no duplicate
4. Stage 4 — interrupted *inside* the comment phase, resumed, seven memos honoured
5. Stage 5 — two simultaneous runs, one refused, five rounds, and the control
6. Stage 6 — the store read back, so no claim rests on the program's own output
7. Stage 7 — the line count for the matrix
8. Stage 8 — the layer gap this variant found, reproduced

## 1. Both variants, same fixture, same clock

The baseline and the durable variant read the same `baseline/fixture/pull-requests.json` at the same
clock. `compare.py --outputs` diffs the four output documents; `compare.py --code` re-reads both
programs and compares the shared triage functions line for line, so "the triage is the baseline's,
moved" is checked rather than claimed.

```
$ dotnet run baseline/PrTriageBaseline.cs -- --run-dir <scratch>/baseline --repo <scratch>/baseline/repo.git --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C baseline — local reproduction of github/gh-aw pr-triage-agent
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
repo:       <scratch>/baseline/repo.git
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/baseline

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
[exit 0]
  ✓ the baseline exited: 0

$ dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-1 --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/same/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/same/run-1

phase 1/8  load state and collect pull requests
  state:      no unfinished run record for this repository — a new generation
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
  wrote:      comments/pr-101.md (448 bytes)
  wrote:      comments/pr-102.md (444 bytes)
  wrote:      comments/pr-103.md (476 bytes)
  wrote:      comments/pr-104.md (483 bytes)
  wrote:      comments/pr-105.md (463 bytes)
  wrote:      comments/pr-106.md (454 bytes)
  wrote:      comments/pr-107.md (477 bytes)
  wrote:      comments/pr-109.md (415 bytes)
  wrote:      comments/pr-111.md (442 bytes)
  wrote:      comments/pr-112.md (486 bytes)
  wrote:      comments/pr-113.md (487 bytes)
  wrote:      comments/pr-114.md (481 bytes)
  wrote:      comments/pr-115.md (484 bytes)

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 13 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.
run:        new durable task t2 (journal c1)
────────────────────────────────────────────────────────────────────────

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the durable variant exited: 0

$ python3 evidence/compare.py --outputs <scratch>/baseline <scratch>/same/run-1
identical  comments/ (13 files)
identical  labels.json (3098 bytes)
identical  triage-report.md (3024 bytes)
identical  triage-data.json (1456 bytes)
  ✓ every output document is byte-identical: 0

$ python3 evidence/compare.py --code
identical  classification, risk, scoring, recommendation 163 significant lines
identical  batching and labels                            56 significant lines
identical  comment and report                            167 significant lines
  ✓ the shared triage code is identical line for line: 0
```

Note what the two `phase 1` blocks say. The baseline's is `cold start — no memory/pr-triage branch,
nothing loaded`; the durable variant's is `no unfinished run record for this repository — a new
generation`. They are the same absence, named differently, and the durable one distinguishes "there
is no record" from "the record says the last run finished" — which is the distinction stage 2 needs.

## 2. Requirement 2 — re-running unchanged does not re-comment

Run 2 is a new process over the same store, with the same clock: the state has not changed, so
nothing is eligible to be commented on again.

```
$ dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-2 --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/same/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/same/run-2

run:        task t2 is finished (generation 1); starting a new one
phase 1/8  load state and collect pull requests
  state:      no unfinished run record for this repository — a new generation
  prior run:  2026-11-05T12:00:00Z triaged 1 generation(s) of state
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

phase 2/8  classify and assess risk
  #101  docs       risk=low    2 file(s), +6/-6  docs: fix quickstart typos and the sample output   [from the store]
  #102  test       risk=low    3 file(s), +190/-4  test: add parity tests for the TypeScript port   [from the store]
  #103  formatting risk=low    1 file(s), +62/-62  formatting: reflow the long lines in durable.ts   [from the store]
  #104  chore      risk=medium 2 file(s), +18/-18  chore: bump the Copilot CLI pin to 1.0.105   [from the store]
  #105  refactor   risk=medium 12 file(s), +260/-240  refactor: extract the JSON store options out of Durable.cs   [from the store]
  #106  bug        risk=high   4 file(s), +180/-40  fix: replay drops the tool result when the runtime retries   [from the store]
  #107  feature    risk=high   41 file(s), +2400/-300  feat: apply backpressure when the admission queue is full   [from the store]
  #109  chore      risk=high   2 file(s), +0/-140  chore: remove the dead replay shim   [from the store]
  #111  docs       risk=low    2 file(s), +48/-3  docs: add the Python port notes to the README   [from the store]
  #112  formatting risk=low    1 file(s), +310/-310  formatting: normalise tabs to spaces in the Python port   [from the store]
  #113  formatting risk=low    2 file(s), +900/-900  formatting: normalise line endings in the example scripts   [from the store]
  #114  chore      risk=medium 2 file(s), +8/-8  chore: refresh the sandbox base image pins   [from the store]
  #115  chore      risk=medium 1 file(s), +44/-44  chore: bump the uv lock for the Python port   [from the store]

  reused:     13 assessment(s) at an unchanged digest, 0 newly classified

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
  skipped:    comments/pr-101.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-102.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-103.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-104.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-105.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-106.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-107.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-109.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-111.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-112.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-113.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-114.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable
  skipped:    comments/pr-115.md — ledger records it posted at 2026-11-05T12:00:00Z by run fixture/durable

  idempotent: 0 comment(s) posted, 0 skipped by memo, 13 skipped by the ledger

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 13 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.
run:        new durable task t223 (journal c222)
────────────────────────────────────────────────────────────────────────

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the second run exited: 0
  ✓ comments posted by run 2: 0
  ✓ comments run 2 adopted rather than re-posted: 13

$ python3 evidence/_store.py ledger <scratch>/same/store.jsonl
ledger entries: 13
  #101 digest=2b202a7aede878ba postedAt=2026-11-05T12:00:00Z run=fixture/durable bytes=448

$ diff -r <scratch>/same/run-1/comments <scratch>/same/run-2/comments
  ✓ run 2's comment files are byte-identical to run 1's: 0
```

### 2b. The control

The same command with `BAND_C_NO_IDEMPOTENCY_GUARD=1`, which removes the memo and ledger reads. Thirteen
comments are written again — including the thirteen that already exist. This is the before half of
the claim.

```
$ BAND_C_NO_IDEMPOTENCY_GUARD=1 dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-3 --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/same/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/same/run-3
seam:       BAND_C_NO_IDEMPOTENCY_GUARD=1 (exit 42)

run:        task t2 is finished (generation 1); starting a new one
phase 1/8  load state and collect pull requests
  state:      no unfinished run record for this repository — a new generation
  prior run:  2026-11-05T12:00:00Z triaged 1 generation(s) of state
  collected:  17 pull requests (pull_request_read get / get_files / get_reviews / get_check_runs)
  not ours:   #110 authored by octocat — not app/github-copilot
  not open:   #120 state=closed
  cooldown:   #108 updated 30m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  cooldown:   #116 updated 10m ago (< 60m) — skip any PR that was opened or last updated (new commits, edits) less than 1 hour before the current run time
  eligible:   13 pull request(s)

phase 2/8  classify and assess risk
  #101  docs       risk=low    2 file(s), +6/-6  docs: fix quickstart typos and the sample output   [from the store]
  #102  test       risk=low    3 file(s), +190/-4  test: add parity tests for the TypeScript port   [from the store]
  #103  formatting risk=low    1 file(s), +62/-62  formatting: reflow the long lines in durable.ts   [from the store]
  #104  chore      risk=medium 2 file(s), +18/-18  chore: bump the Copilot CLI pin to 1.0.105   [from the store]
  #105  refactor   risk=medium 12 file(s), +260/-240  refactor: extract the JSON store options out of Durable.cs   [from the store]
  #106  bug        risk=high   4 file(s), +180/-40  fix: replay drops the tool result when the runtime retries   [from the store]
  #107  feature    risk=high   41 file(s), +2400/-300  feat: apply backpressure when the admission queue is full   [from the store]
  #109  chore      risk=high   2 file(s), +0/-140  chore: remove the dead replay shim   [from the store]
  #111  docs       risk=low    2 file(s), +48/-3  docs: add the Python port notes to the README   [from the store]
  #112  formatting risk=low    1 file(s), +310/-310  formatting: normalise tabs to spaces in the Python port   [from the store]
  #113  formatting risk=low    2 file(s), +900/-900  formatting: normalise line endings in the example scripts   [from the store]
  #114  chore      risk=medium 2 file(s), +8/-8  chore: refresh the sandbox base image pins   [from the store]
  #115  chore      risk=medium 1 file(s), +44/-44  chore: bump the uv lock for the Python port   [from the store]

  reused:     13 assessment(s) at an unchanged digest, 0 newly classified

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
  wrote:      comments/pr-101.md (448 bytes)
  wrote:      comments/pr-102.md (444 bytes)
  wrote:      comments/pr-103.md (476 bytes)
  wrote:      comments/pr-104.md (483 bytes)
  wrote:      comments/pr-105.md (463 bytes)
  wrote:      comments/pr-106.md (454 bytes)
  wrote:      comments/pr-107.md (477 bytes)
  wrote:      comments/pr-109.md (415 bytes)
  wrote:      comments/pr-111.md (442 bytes)
  wrote:      comments/pr-112.md (486 bytes)
  wrote:      comments/pr-113.md (487 bytes)
  wrote:      comments/pr-114.md (481 bytes)
  wrote:      comments/pr-115.md (484 bytes)

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 26 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.
run:        new durable task t366 (journal c365)
────────────────────────────────────────────────────────────────────────

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the guard-disabled run exited: 0
  ✓ comments re-posted with the guard off: 13
  ✓ the control added thirteen entries to the ledger (13 + 13): ledger entries: 26
```

## 3. Requirement 3 — interrupted after commenting, then resumed

`BAND_C_EXIT_AFTER_PHASE=comment` leaves the process the moment the comment phase's checkpoint is
committed: the comments exist, the run record says the run is at the report phase, and the task record
says `running`. The resume is a new process, and it enters at `report` — the phase pointer alone is
enough here, because the comment step never re-runs.

```
$ BAND_C_EXIT_AFTER_PHASE=comment dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/interrupted/store.jsonl --run-dir <scratch>/interrupted/run --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/interrupted/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/interrupted/run
seam:       BAND_C_EXIT_AFTER_PHASE=comment (exit 42)

phase 1/8  load state and collect pull requests
  state:      no unfinished run record for this repository — a new generation
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
  wrote:      comments/pr-101.md (448 bytes)
  wrote:      comments/pr-102.md (444 bytes)
  wrote:      comments/pr-103.md (476 bytes)
  wrote:      comments/pr-104.md (483 bytes)
  wrote:      comments/pr-105.md (463 bytes)
  wrote:      comments/pr-106.md (454 bytes)
  wrote:      comments/pr-107.md (477 bytes)
  wrote:      comments/pr-109.md (415 bytes)
  wrote:      comments/pr-111.md (442 bytes)
  wrote:      comments/pr-112.md (486 bytes)
  wrote:      comments/pr-113.md (487 bytes)
  wrote:      comments/pr-114.md (481 bytes)
  wrote:      comments/pr-115.md (484 bytes)

  seam:       stopped after phase 'comment' (BAND_C_EXIT_AFTER_PHASE), exit 42
[exit 42]
  ✓ the seam exited 42 after the comment phase committed: 42
  ✓ comments on disk at the seam: 13

$ dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/interrupted/store.jsonl --run-dir <scratch>/interrupted/run --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/interrupted/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/interrupted/run

run:        resuming durable task t2 at phase 'report' (attempt 2, restarts so far 0)
resuming:   13 comment(s) recorded in this run's record
memos:      13 step memo(s) on record
────────────────────────────────────────────────────────────────────────
phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 13 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the resume exited: 0
  ✓ comments on disk after the resume: 13
  ✓ comments the resume wrote: 0

$ diff -r <scratch>/interrupted/before <scratch>/interrupted/run/comments
  ✓ every comment file is unchanged by the resume: 0

$ python3 evidence/_store.py task <scratch>/interrupted/store.jsonl
22 status=4 phase=save attempts=1 generation=1 commented=13 memos=13 outcome=completed
```

## 4. Requirement 3, the harder seam — interrupted inside the comment phase

This is the case the issue names, and the phase pointer cannot help: the stop happens *during* phase
6, after nine comments and before the phase commits. The resume re-enters phase 6 with the run record
still saying `comment`, and the only thing that stops the seven posted comments from being posted
again is the memo.

```
$ BAND_C_EXIT_AFTER_COMMENTS=7 dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/midway/store.jsonl --run-dir <scratch>/midway/run --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/midway/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/midway/run
seam:       BAND_C_EXIT_AFTER_COMMENTS=7 (exit 42)

phase 1/8  load state and collect pull requests
  state:      no unfinished run record for this repository — a new generation
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
  wrote:      comments/pr-101.md (448 bytes)
  wrote:      comments/pr-102.md (444 bytes)
  wrote:      comments/pr-103.md (476 bytes)
  wrote:      comments/pr-104.md (483 bytes)
  wrote:      comments/pr-105.md (463 bytes)
  wrote:      comments/pr-106.md (454 bytes)
  wrote:      comments/pr-107.md (477 bytes)
  seam:       stopped inside phase 'comment' after 7 comment(s) (BAND_C_EXIT_AFTER_COMMENTS), exit 42
[exit 42]
  ✓ the seam exited 42 after the seventh comment: 42

$ python3 evidence/_store.py memos <scratch>/midway/store.jsonl
comment/101/2b202a7aede878ba
comment/102/3a7981d07fa4302f
comment/103/717441717950cc1b
comment/104/df075f5169867822
comment/105/72bd32d303128ad5
comment/106/0df7f986069399fe
comment/107/b4010998c6bbd365
  ✓ step memos committed by the interrupted process: 7

$ dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/midway/store.jsonl --run-dir <scratch>/midway/run --fixture pull-requests.json --now 2026-11-05T12:00:00Z
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/midway/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/midway/run

run:        resuming durable task t2 at phase 'comment' (attempt 2, restarts so far 0)
resuming:   0 comment(s) recorded in this run's record
memos:      7 step memo(s) on record
────────────────────────────────────────────────────────────────────────
phase 6/8  comment each triaged PR
  skipped:    comments/pr-101.md — memo comment/101/2b202a7aede878ba present
  skipped:    comments/pr-102.md — memo comment/102/3a7981d07fa4302f present
  skipped:    comments/pr-103.md — memo comment/103/717441717950cc1b present
  skipped:    comments/pr-104.md — memo comment/104/df075f5169867822 present
  skipped:    comments/pr-105.md — memo comment/105/72bd32d303128ad5 present
  skipped:    comments/pr-106.md — memo comment/106/0df7f986069399fe present
  skipped:    comments/pr-107.md — memo comment/107/b4010998c6bbd365 present
  wrote:      comments/pr-109.md (415 bytes)
  wrote:      comments/pr-111.md (442 bytes)
  wrote:      comments/pr-112.md (486 bytes)
  wrote:      comments/pr-113.md (487 bytes)
  wrote:      comments/pr-114.md (481 bytes)
  wrote:      comments/pr-115.md (484 bytes)

  idempotent: 6 comment(s) posted, 7 skipped by memo, 0 skipped by the ledger

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 13 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the resume exited: 0
  ✓ comments the resume skipped by memo: 7
  ✓ comments the resume posted: 6
  ✓ comments on disk afterwards: 13
```

The store's own account of that stop, and of the resume that followed, is in stage 6.

### 4b. The control

The same seam and the same resume with the guard removed: all thirteen comments are written again,
including the seven that already existed.

```
$ BAND_C_NO_IDEMPOTENCY_GUARD=1 dotnet <worktree>/real-world-scenarios/band-c-ci-repo-agent/durable/bin/Debug/net10.0/BandCDurable.dll --store <scratch>/midway-off/store.jsonl --run-dir <scratch>/midway-off/run ...
band C durable — the pr-triage agent with its state in a Durable store
run time:   2026-11-05T12:00:00Z
repository: fixture/durable
store:      <scratch>/midway-off/store.jsonl
fixture:    <worktree>/real-world-scenarios/band-c-ci-repo-agent/baseline/fixture/pull-requests.json (17 pull requests)
output:     <scratch>/midway-off/run
seam:       BAND_C_NO_IDEMPOTENCY_GUARD=1 (exit 42)

run:        resuming durable task t2 at phase 'comment' (attempt 2, restarts so far 0)
resuming:   0 comment(s) recorded in this run's record
memos:      7 step memo(s) on record
────────────────────────────────────────────────────────────────────────
phase 6/8  comment each triaged PR
  wrote:      comments/pr-101.md (448 bytes)
  wrote:      comments/pr-102.md (444 bytes)
  wrote:      comments/pr-103.md (476 bytes)
  wrote:      comments/pr-104.md (483 bytes)
  wrote:      comments/pr-105.md (463 bytes)
  wrote:      comments/pr-106.md (454 bytes)
  wrote:      comments/pr-107.md (477 bytes)
  wrote:      comments/pr-109.md (415 bytes)
  wrote:      comments/pr-111.md (442 bytes)
  wrote:      comments/pr-112.md (486 bytes)
  wrote:      comments/pr-113.md (487 bytes)
  wrote:      comments/pr-114.md (481 bytes)
  wrote:      comments/pr-115.md (484 bytes)

phase 7/8  create the triage report issue
  wrote:      triage-report.md (3024 bytes)
  wrote:      triage-data.json (data safe output)

phase 8/8  save run state
  wrote:      band-c.run@fixture/durable — generation 1, phase save
  wrote:      band-c.pr@pr:<number> for 13 pull request(s), stage batch
  wrote:      band-c.comments@agent:pr-triage — 20 comment(s) on record
  outcome:    the task's terminal record carries the counts below

triaged 13 PR(s): #101 auto_merge, #102 auto_merge, #103 auto_merge, #104 batch_review, #105 fast_track, #106 defer, #107 batch_review, #109 close, #111 auto_merge, #112 auto_merge, #113 defer, #114 batch_review, #115 batch_review
2 skipped by the 1-hour cooldown, 1 not authored by app/github-copilot, 1 not open.

run:        completed — 13 triaged, 13 commented, 2 in cooldown, 1 not ours, 1 not open
[exit 0]
  ✓ the guard-disabled resume exited: 0
  ✓ comments re-posted by the control resume: 13
  ✓ comments the control resume skipped: 0
```

## 5. Requirement 4 — two simultaneous runs, one refused

Five rounds of two processes started together against one store. Exactly one proceeds each time; the
other exits 3 with the store's own refusal message, which names the lock file and the holding
process.

```
── round 1
  run-1 exit=3   run-2 exit=0
  ✓ runs that proceeded: 1
  ✓ runs refused at the store open: 1
  refusal: durable: cannot open the store: The durable store file '<scratch>/race-1/store.jsonl' is already open in another process. Its lock file '<scratch>/race-1/store.jsonl.lock' is held by process 30299 (since 2026-11-05T14:39:53.0806880+00:00). A store file supports a single writer at a time; close the other store, or point this one at a different path.

── round 2
  run-1 exit=0   run-2 exit=3
  ✓ runs that proceeded: 1
  ✓ runs refused at the store open: 1
  refusal: durable: cannot open the store: The durable store file '<scratch>/race-2/store.jsonl' is already open in another process. Its lock file '<scratch>/race-2/store.jsonl.lock' is held. A store file supports a single writer at a time; close the other store, or point this one at a different path.

── round 3
  run-1 exit=0   run-2 exit=3
  ✓ runs that proceeded: 1
  ✓ runs refused at the store open: 1
  refusal: durable: cannot open the store: The durable store file '<scratch>/race-3/store.jsonl' is already open in another process. Its lock file '<scratch>/race-3/store.jsonl.lock' is held by process 30465 (since 2026-11-05T14:39:55.7868850+00:00). A store file supports a single writer at a time; close the other store, or point this one at a different path.

── round 4
  run-1 exit=3   run-2 exit=0
  ✓ runs that proceeded: 1
  ✓ runs refused at the store open: 1
  refusal: durable: cannot open the store: The durable store file '<scratch>/race-4/store.jsonl' is already open in another process. Its lock file '<scratch>/race-4/store.jsonl.lock' is held by process 30580 (since 2026-11-05T14:39:57.0725380+00:00). A store file supports a single writer at a time; close the other store, or point this one at a different path.

── round 5
  run-1 exit=0   run-2 exit=3
  ✓ runs that proceeded: 1
  ✓ runs refused at the store open: 1
  refusal: durable: cannot open the store: The durable store file '<scratch>/race-5/store.jsonl' is already open in another process. Its lock file '<scratch>/race-5/store.jsonl.lock' is held by process 30673 (since 2026-11-05T14:39:59.2746910+00:00). A store file supports a single writer at a time; close the other store, or point this one at a different path.

  ✓ refusals across 5 rounds: 5
```

### 5b. The control

The same two processes against two different stores: both proceed. The refusal is the store's, not a
flag this program sets.

```
  control-a exit=0
  control-b exit=0
  ✓ process A against its own store: 0
  ✓ process B against its own store: 0
```

## 6. The store, read back

Read by `evidence/_store.py`, which parses the JSONL directly and knows nothing about this program's
output. The task history is the sequence of committed task records; the ledger is the last committed
value of `band-c.comments`.

```
$ python3 evidence/_store.py task <scratch>/midway/store.jsonl
 0 status=0 phase=collect attempts=0 generation=0 commented=0 memos=0 outcome=None
 1 status=1 phase=classify attempts=1 generation=1 commented=0 memos=0 outcome=None
 2 status=1 phase=score attempts=1 generation=1 commented=0 memos=0 outcome=None
 3 status=1 phase=recommend attempts=1 generation=1 commented=0 memos=0 outcome=None
 4 status=1 phase=batch attempts=1 generation=1 commented=0 memos=0 outcome=None
 5 status=1 phase=comment attempts=1 generation=1 commented=0 memos=0 outcome=None
 6 status=1 phase=comment attempts=1 generation=1 commented=0 memos=1 outcome=None
 7 status=1 phase=comment attempts=1 generation=1 commented=0 memos=2 outcome=None
 8 status=1 phase=comment attempts=1 generation=1 commented=0 memos=3 outcome=None
 9 status=1 phase=comment attempts=1 generation=1 commented=0 memos=4 outcome=None
10 status=1 phase=comment attempts=1 generation=1 commented=0 memos=5 outcome=None
11 status=1 phase=comment attempts=1 generation=1 commented=0 memos=6 outcome=None
12 status=1 phase=comment attempts=1 generation=1 commented=0 memos=7 outcome=None
13 status=1 phase=comment attempts=1 generation=1 commented=0 memos=7 outcome=None
14 status=1 phase=comment attempts=1 generation=1 commented=0 memos=8 outcome=None
15 status=1 phase=comment attempts=1 generation=1 commented=0 memos=9 outcome=None
16 status=1 phase=comment attempts=1 generation=1 commented=0 memos=10 outcome=None
17 status=1 phase=comment attempts=1 generation=1 commented=0 memos=11 outcome=None
18 status=1 phase=comment attempts=1 generation=1 commented=0 memos=12 outcome=None
19 status=1 phase=comment attempts=1 generation=1 commented=0 memos=13 outcome=None
20 status=1 phase=report attempts=1 generation=1 commented=13 memos=13 outcome=None
21 status=1 phase=save attempts=1 generation=1 commented=13 memos=13 outcome=None
22 status=4 phase=save attempts=1 generation=1 commented=13 memos=13 outcome=completed

$ python3 evidence/_store.py ledger <scratch>/midway/store.jsonl
ledger entries: 13
  #101 digest=2b202a7aede878ba postedAt=2026-11-05T12:00:00Z run=fixture/durable bytes=448
  #102 digest=3a7981d07fa4302f postedAt=2026-11-05T12:00:00Z run=fixture/durable bytes=444

$ python3 evidence/_store.py prs <scratch>/midway/store.jsonl
#101 stage=batch digest=2b202a7aede878ba docs/low auto_merge gen=1
#102 stage=batch digest=3a7981d07fa4302f test/low auto_merge gen=1
#103 stage=batch digest=717441717950cc1b formatting/low auto_merge gen=1

$ python3 evidence/_store.py versions <scratch>/midway/store.jsonl
band-c.run: 8 committed version(s)
band-c.pr: 52 committed version(s)
band-c.comments: 13 committed version(s)
```

## 7. Custom durability code, counted

`evidence/count.py` classifies every declaration in `durable/*.cs` and fails if a declaration has no
classification or the split does not add up to the file. The table in `README.md` is this output.

```
$ python3 evidence/count.py
File                Code  Durability  Application
-------------------------------------------------
Contracts.cs         103         103            0
ExitSeam.cs           66          66            0
Fixture.cs            69           0           69
Phases.cs            393         132          261
Program.cs             2           2            0
Runner.cs            223         154           69
Triage.cs            499          47          452
TriageStore.cs        65          65            0
-------------------------------------------------
Total               1420         569          851

durability: 569 of 1420 code lines (40%)
  ✓ every declaration is classified and the split adds up: 0
```

## 8. The layer gap, reproduced

`evidence/blocked-input.cs` is the pre-fix shape: a memo read from inside a commit callback. It exits
7, the watchdog's code, meaning the `await` did not return. Filed as #201 and fixed on this branch by
adding `Transaction.Memo`.

```
$ dotnet run evidence/blocked-input.cs
inside the transaction; calling runtime.MemoAsync
RESULT: MemoAsync did not return within 4s — the commit gate is not reentrant
[exit 7]
  ✓ the pre-fix shape is confirmed unreachable (7 = the watchdog fired): 7
```
