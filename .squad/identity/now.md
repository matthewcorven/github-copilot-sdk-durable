---
updated_at: 2026-10-24T00:30:00-04:00
focus_area: Issue #1 — #9/#10/#11 Done; #12 in flight (PR #23 draft, independently verified)
active_issues: [1, 12, 13, 14, 15, 16, 17, 18, 19]
---

# What We're Focused On

**Progress this session (2026-10-23/24, continuing from ISSUE_1_HANDOFF.md):**

- **#9 + #10 — DONE.** PR #21 (squashed to a single clean commit) merged into `dev`
  as `644ae88`. Gates green (build 0/0, tests 4/0, examples 31/31). #10 re-scoped and
  verified against all its acceptance criteria; PR body amended to `Closes #9, closes #10`;
  both issues closed, board items Done. Gotcha: backticked close keywords in a PR body
  are NOT parsed by GitHub (issues had to be closed manually); unadorned close keywords
  on their own line DO auto-close (confirmed with #11).
- **#11 — DONE.** PR #22 (`JsonlStoreProvider`, commit `e21abb9` after two fixes)
  merged as a squash merge `e21abb9` into `dev` by the user (matthewcorven). Two real
  bugs were caught by independent scratch verification beyond the compile/test gates and
  fixed before merge: (1) NRE on every first commit with an entry (`_committedEntries
  .GetValueOrDefault(...)` → null) plus missing baseline seeding after open/Replay;
  (2) torn-tail guard was dead code — `JsonSerializer.Deserialize` throws rather than
  returning null, so torn tails crashed `Open`; now caught, classified (last line →
  discarded + truncated; earlier bad line → `InvalidDataException`). Six-point scratch
  verification fully green.
- **#12 — IN PROGRESS.** `squad/12-default-path` → draft PR #23 (base `dev`, commit
  `1173c75`). `DurableStore.DefaultStorePath(fileName)` public and deterministic;
  explicit `Path` wins; directory created on first write not on open; InMemory never
  touches filesystem.Independently verified (hermetic XDG_STATE_HOME scratch run):
  resolved path under `durable/`, no dir after open, file after commit, explicit path
  wins, InMemory creates nothing. Gates green.
- **Lesson (keep applying):** compile-only gates do NOT cover new providers; every
  spine item needs an end-to-end scratch roundtrip before "green" is believed.

**Spine (serial):** #9 ✅ #10 ✅ #11 ✅ → #12 (PR #23 awaiting review/merge) → #13.
**Consumers:** #14 (example + README + docs prose), #15 (xUnit facts, target 9/0).

**Parallel line (separate initiative, not part of #1):** #16–#19, the sandbox epic.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from
`origin/dev` and PRs target `dev`. `main` is release-only. `origin` is SSH.
Direct pushes to `dev` are rule-bypassed only for squad-state commits (observed, not
a pattern to rely on).

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
#1 In Progress; #9/#10/#11 Done; #12 In Progress; #13–#19 Todo.

## Next action

Review PR #23 → ready → merge, then route #13 (legacy single-file JSON reader) to
Core Engineer on `squad/13-{slug}`. Remember: #13 must keep StoreSnapshot types in
place, read legacy format on open into memory, no implicit write-back.