---
updated_at: 2026-10-23T23:52:00-04:00
focus_area: Issue #1 — #9 and #10 Done; #11 in flight (PR #22 draft, verification-hardened)
active_issues: [1, 11, 12, 13, 14, 15, 16, 17, 18, 19]
---

# What We're Focused On

**Progress this session (2026-10-23, continuing from ISSUE_1_HANDOFF.md):**

- **#9 + #10 — DONE.** PR #21 (squash of `squad/9-durable-store-provider-interface`
  to a single clean commit, 877…) merged into `dev` as `644ae88`. All gates green
  (build 0/0, tests 4/0, examples 31/31). #10 re-scoped and verified against all its
  acceptance criteria by Core Engineer; PR body amended to `Closes #9, closes #10`,
  issue #10 got a re-scope comment. Both issues closed, board items flipped to Done.
  ⚠ Gotcha: GitHub did not auto-close #9/#10 even though `closingIssuesReferences`
  resolved [9,10]; the close keywords were inside backticks in the PR body. Issues
  were closed manually with `gh issue close --reason completed`.
- **#11 — IN PROGRESS.** `squad/11-jsonl-backend` → draft PR #22 (base `dev`,
  mergeable CLEAN). Two real bugs were caught by independent verification beyond the
  compile/test gates and fixed by Core Engineer (amended commit, now `e21abb9`):
  1. Null-deref in `TakePendingRecords` — `_committedEntries.GetValueOrDefault(...).Count`
     on first commit; fixed with `?.Count ?? 0` plus `SeedCommittedFromReplay()` after
     `Replay` so the first post-reopen commit doesn't re-emit existing records.
  2. `Replay`'s torn-tail guard was dead code — `JsonSerializer.Deserialize` throws
     `JsonException` instead of returning null; now caught and classified (last line →
     discarded + truncated; any earlier bad line → `InvalidDataException`).
  Six-point independent scratch verification now fully green (roundtrip, 2nd commit,
  both torn-tail shapes, mid-file garbage, first-line garbage).

**Spine (serial):** #9 ✅ → #10 ✅ → #11 (PR #22, needs review → ready-for-review →
merge) → #12 → #13. **Consumers:** #14 (example + README/docs prose), #15 (xUnit facts
— 5 new named facts, target 9/0 tests).

**Lesson learned (repeat this pattern):** compile/gates do NOT cover the JSONL provider
until #15's facts land. Every spine item that ships a new provider needs an independent
end-to-end scratch verification (real API roundtrip + reopen) before "green" is believed.

**Parallel line (separate initiative, not part of #1):** #16–#19, the sandbox epic.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from
`origin/dev` and PRs target `dev`. `main` is release-only. `origin` is SSH.

## Board

"github-copilot-sdk-durable Roadmap" (project 5, owner matthewcorven).
#1 In Progress; #9, #10 Done; #11 In Progress; #12–#19 Todo.

## Next action

Review PR #22 (mark ready-for-review and merge when satisfied), then route #12
(`DurableStoreOptions` per-user default path) to Core Engineer on a fresh
`squad/12-{slug}` branch from `origin/dev`.