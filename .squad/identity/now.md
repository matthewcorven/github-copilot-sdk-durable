---
updated_at: 2026-10-23T13:32:09.577-04:00
focus_area: Issue #1 — pluggable durable-store provider abstraction + JSONL backend
active_issues: [1, 9, 10, 11, 12, 13, 14, 15]
---

# What We're Focused On

Planning GitHub issue #1 (pluggable durable-store provider abstraction with a path-configurable
JSONL backend). The epic is decomposed into 7 dependency-ordered sub-issues (#9–#15) with one
accountable owner each. The implementation spine is serial: #9 → #10 → #11 → #12 → #13, with
#14 (example + README) and #15 (xUnit facts) consuming it.

Plan: `docs/implementation/ISSUE_1_PLAN.md`
Board: "github-copilot-sdk-durable Roadmap" (project 5, owner matthewcorven) — all 15 issues `Todo`.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from `origin/dev`
and PRs target `dev`. `main` is release-only; release tags are cut after `dev` is promoted
forward to `main`. Both branches have PR-required protection with no force-push and no deletion.

## Next action

Implementation starts at #9 (Core Engineer) — branch `squad/9-durable-store-provider-interface`
from `origin/dev`, PR base `dev`.
