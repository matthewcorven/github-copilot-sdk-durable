---
updated_at: 2026-10-29T09:22:51-04:00
focus_area: Ralph round 5 — nine PRs merged (#55-#63), nine issues closed, wave 5 dispatched (#37, #38, #46, #47, #51)
active_issues: [37, 38, 46, 47, 51]
---

# What We're Focused On

**Ralph round 5 — NINE PRs MERGED, NINE ISSUES CLOSED (2026-10-29).** User granted merge approval
for the session. All nine green PRs squash-merged to `dev` in dependency order:

| PR | Issue | Squash commit | Unblocked |
|----|-------|---------------|-----------|
| #56 | #35 | `e53bddf` | #36 |
| #58 | #43 | `65ef760` | #44 |
| #57 | #49 | `50936a7` | #50 |
| #55 | #17 | `7afb560` | — |
| #59 | #18 | `86ea980` | — |
| #60 | #44 | `7ebca4d` | #45 |
| #61 | #50 | `6f90060` | #51 |
| #63 | #45 | `121954e` | #46, #47 |
| #62 | #36 | `8d13a83` | #37, #38 |

**All nine issues closed manually.** GitHub only auto-closes on `Closes #N` when the PR targets the
*default* branch (`main`), not `dev` — so PRs that did carry the keyword left their issues open.
Closed #17, #18, #35, #43, #49, #44, #50, #45, #36 with `--reason completed`.

**Board flipped:** #36, #44, #45, #50 → Done. #37, #38, #46, #47, #51 → In Progress.

**Wave 5 dispatched — five issues, all in background from `origin/dev` @ `8d13a83`:**

| # | Slice | Owner | Worktree | Branch |
|---|-------|-------|----------|--------|
| 37 | 7.3 Identity binding, admission, checkpointing, resume | Core Engineer | `-37` | `squad/37-identity-binding-admission` |
| 38 | 7.4 Coordinated transcript and document forks | Core Engineer | `-38` | `squad/38-coordinated-forks` |
| 46 | 3.4 Deterministic replay docs + determinism check | Core Engineer | `-46` | `squad/46-replay-docs-determinism` |
| 51 | 2.3 Provider-neutral store conformance suite | Core Engineer | `-51` | `squad/51-provider-conformance` |
| 47 | 3.5 Polyglot attach verification | Polyglot Engineer | `-47` | `squad/47-polyglot-attach` |

#37 and #38 carry the PR #56 review findings that name them (SDK configuration entry point;
`ForkAsync` entry-id semantics). #51 builds on #50's provider seam — `Provider` > `ProviderName` >
`Format`, process-wide registration, store-owned provider lifetime. #46 starts from a tree that
already greps clean for banned vocabulary, so only new violations matter.

**Still blocked:** #4←3, #5←4,3, #6←5,4,3, #8←7,3, #19←6,3, #39←37, #40←39,38, #41←40, #48←46,
#52←51, #53←51, #54←51. Epics #2, #3, #7 remain open by design — their children are in flight.

**Not granted:** release / `dev`→`main` promotion. Merge approval covers PRs only, this session.

## Round-5 durable decisions

- **#45 vocabulary doc edits are in scope and were accepted.** Three doc edits in
  `docs/implementation/ISSUE_1_HANDOFF.md` fixed the last 4 pre-existing banned-vocabulary hits.
  The whole source tree now greps clean for `mock|fake|stub|simulat`; #48's vocabulary obligation
  is already satisfied and its scope should be re-read before dispatch.
- **#50's provider seam was delivered additively, as the Lead required.** `DurableStoreOptions.Provider`
  > `ProviderName` > `Format`; `RegisterProvider` / `UnregisterProvider` / `IsProviderRegistered`
  over an `OrdinalIgnoreCase` dictionary; the settled store-creation surface was not changed.
- **Regression: the #36 refactor silently deleted #44's `ResolveRuntimePath` feature.** A clean
  build (0 errors) did not catch it; only 3 failing tests did. Binding lesson: after a conflict
  resolution, grep the method name, verify the enclosing class is on the live call path, and run
  the suite. A clean build does not prove a feature survived a rebase.

## Branch model

`main` is the **release branch and the repo default** (changed 2026-10-28). `dev` is the
integration branch. Feature work branches from `origin/dev` and PRs target `dev`. `dev` -> `main`
only at release. `origin` is SSH. **Squash-merge is the convention** (`gh pr merge <n> --squash
--delete-branch`); `dev` has linear history, no merge commits.

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
Done: #1, #9-#18, #30, #35, #36, #43, #44, #45, #49, #50.
In Progress: #37, #38, #46, #47, #51.
Todo: #2, #3, #4, #5, #6, #7, #8, #19, #39, #40, #41, #48, #52, #53, #54.

**Native-unblocked:** #2, #3, #7 — all three are epics whose children are already in flight or
natively blocked. No leaf issue is startable beyond wave 5.
**Blocked (do not start):** #4, #5, #6, #8, #19, #39, #40, #41, #48, #52, #53, #54.

## Next action

**Ralph round 5 in flight (2026-10-29).** Five issues dispatched in background from `origin/dev` @
`8d13a83`. Next startable work appears only after a human merges: merging #37 unblocks **#39**,
merging #38 unblocks **#40** (with #39), merging #46 unblocks **#48**, merging #51 unblocks
**#52**, **#53** and **#54**. #47 has no dependents.

**Process lessons recorded** in `.squad/decisions.md` this round:

- Delivery-gap lesson — a dispatched agent can report done with only a local commit. Audit
  artifacts (commit SHA, remote branch, PR, CI), never the completion report.
- PR evidence standard — CI `build=SUCCESS` is necessary but not sufficient. Verify locally with
  `dotnet build`, `dotnet test tests/Durable.Tests` (plus any new test project the PR added), and
  `COMPILE_ONLY=1 ./scripts/validate.sh <example>`.
- Rebase-survival lesson — a clean build does not prove a merged feature survived a conflict
  resolution. Grep the deleted method, check the enclosing class, run the suite.

**Open question — do not auto-release:** `lib/Copilot.Durable.csproj` is at `0.5.0` and
`CHANGELOG.md` has an `## [Unreleased]` entry. Cutting `v0.5.0` is a separate `dev` -> `main`
promotion per `docs/RELEASING.md` and needs user sign-off. `dev` is now well ahead of `main`.
