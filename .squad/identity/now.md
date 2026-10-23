---
updated_at: 2026-10-23T19:08:33.866-04:00
focus_area: Issue #1 — #9 implemented and pushed; handoff written, PR decision pending
active_issues: [1, 9, 10, 11, 12, 13, 14, 15]
---

# What We're Focused On

**Read `docs/implementation/ISSUE_1_HANDOFF.md` first.** It is the authoritative operational
handoff for the next Lead session: resolved decisions, verified state, what #9 delivered, what
remains, board ids, gotchas, and next actions.

Issue #1 is an epic split into seven serial sub-issues (#9 → #10 → #11 → #12 → #13) plus two
consumers (#14, #15). All eight open design questions are **resolved and settled** — see
`docs/implementation/ISSUE_1_PLAN.md` §5. Do not re-open them.

**In flight:** #9 — provider interface. Implemented and pushed on
`squad/9-durable-store-provider-interface` (commits `05a9562` + `4e5ca88`). Builds clean, 4/4
xUnit facts pass, 31/31 examples compile. **No PR opened, nothing merged to `dev`** — that
decision is deliberately left to the next session.

**Spine (serial):** #9 → #10 → #11 → #12 → #13. **Consumers:** #14 (example + docs),
#15 (xUnit facts). Note: #9 pre-empted much of #10 — re-scope #10 before routing it.

**Parallel line (separate initiative, not part of #1):** #16–#19, the sandbox epic.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from `origin/dev`
and PRs target `dev`. `main` is release-only; release tags are cut after `dev` is promoted
forward to `main`. Both branches have PR-required protection with no force-push and no deletion.
`origin` is SSH — the HTTPS token lacks the `workflow` scope.

## Board

"github-copilot-sdk-durable Roadmap" (project 5, owner matthewcorven). #1 and #9 are
`In Progress`; everything else is `Todo`. Nothing is `Done` yet.

## Next action

Decide whether #9 is PR-ready (the handoff judges it is), open the PR against `dev`, then
re-scope and route #10 to Core Engineer.
