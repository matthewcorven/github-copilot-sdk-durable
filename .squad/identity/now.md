---
updated_at: 2026-10-29T03:20:00-04:00
focus_area: v0.4.0 released; #16 (durable sandbox policy) in flight on squad/16-durable-sandbox-policy
active_issues: [2, 3, 7, 16]
---

# What We're Focused On

**v0.4.0 — SHIPPED (2026-10-28).** Issue #30 (release strategy + changelog + starting ref)
implemented by Lead, PR #33 merged to `dev`, `dev` promoted to `main`, tag `v0.4.0` cut,
GitHub release published, **default branch switched `dev` → `main`**, issue closed, board Done.

Independently verified: tag `v0.4.0` present; release listed as Latest; default branch `main`;
`origin/main` and `origin/dev` in sync; `CHANGELOG.md` and `docs/RELEASING.md` on `main`;
README states "Current version: 0.4.0".

**#16 — durable sandbox policy — IN FLIGHT.** Branch `squad/16-durable-sandbox-policy`,
1 commit ahead of `origin/dev`.

- The library layer is **done and verified**: `DurableSandboxPolicy` + nested
  `Auth`/`Credentials`/`Filesystem`/`Network`/`Proxy` types, `AgentChoices.Sandbox`
  (null = no sandbox), `BuildSandboxConfig` -> `GitHub.Copilot.Rpc.SandboxConfig`,
  `{workspace}` expansion, `SandboxDecisions` / `WaitForSandboxDecisionAsync`,
  `DurableHarnessOptions`. Builds 0/0; suite 32/32.
- Remaining: `examples/33-sandbox-sdk-filesystem.cs`, `docs/SANDBOX_MATRIX.md`,
  `docs/DESIGN.md` section 3, `CHANGELOG.md` Unreleased entry, version bump 0.4.0 -> 0.5.0,
  `33` added to `LIVE_EXAMPLES`, end-to-end roundtrip, PR -> `dev`.

## Incident — runaway agent (2026-10-28/29)

The first Core Engineer dispatch for #16 ran **11.2 hours continuously** with **zero commits**:
1,353 bash calls, 99 `.scratch/probe*.cs` files, 73 context compactions, 2% of wall clock in
actual tool execution. It was reverse-engineering the SDK XML in a loop it had already solved.

- Work was salvaged and committed as `1046962` before the agent was stopped.
- The agent was stopped via `write_agent` (the harness exposes **no hard-kill** for background
  agents; subagents run inside the parent `copilot-runtime` process, so there is no PID to kill).
- Re-dispatched with a **pre-researched brief** containing the full SDK sandbox API surface, an
  explicit no-scratch-loop rule, and a commit-every-30-minutes rule.

**Lesson (new, keep applying):** a long-running agent is not evidence of progress. Check for
commits, not elapsed time. `list_agents` duration is wall-clock, not work.

## Branch model

`main` is the **release branch and the repo default** (changed 2026-10-28). `dev` is the
integration branch. Feature work branches from `origin/dev` and PRs target `dev`. `dev` -> `main`
only at release. `origin` is SSH.

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
#1, #9-#15, #30 Done. #2, #3, #4, #5, #6, #7, #8, #16, #17, #18, #19 Todo.

**Native-unblocked and startable:** #2, #3, #7, #16.
**Blocked (do not start):** #4, #5, #6, #8, #17, #18, #19.

## Next action

1. Land #16 (in flight) — PR -> `dev`.
2. Then #7 (unified extension library). **Note the dependency inversion:** #7's acceptance
   criteria require #16's sandbox policy, but #16 is **not** a native `blocked by` link on #7 —
   only the issue body mentions it. Per charter, body prose is not authoritative, so the board
   reads #7 as unblocked while it is functionally dependent on #16. Add the native link.
3. Then #2 (shared-database tier, EF Core) and #3.
