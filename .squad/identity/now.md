---
updated_at: 2026-10-29T05:58:00-04:00
focus_area: Ralph rounds 1-2 complete — five PRs open and green (#55-#59); all remaining open issues natively blocked on human merges
active_issues: [2, 3, 4, 5, 6, 7, 8, 17, 18, 19, 35, 36, 43, 44, 49, 50]
---

# What We're Focused On

**v0.4.0 — SHIPPED (2026-10-28).** Issue #30 (release strategy + changelog + starting ref)
implemented by Lead, PR #33 merged to `dev`, `dev` promoted to `main`, tag `v0.4.0` cut,
GitHub release published, **default branch switched `dev` → `main`**, issue closed, board Done.

**#16 — durable sandbox policy — SHIPPED (2026-10-29).** PR #34 squash-merged to `dev` as
`5147e25`, issue closed, board Done. Delivered `DurableSandboxPolicy` + nested
`Auth`/`Credentials`/`Filesystem`/`Network`/`Proxy` types, `AgentChoices.Sandbox`,
`BuildSandboxConfig` -> `GitHub.Copilot.Rpc.SandboxConfig`, `{workspace}` expansion,
`SandboxDecisions` / `WaitForSandboxDecisionAsync`, `DurableHarnessOptions.TolerateHostHookFailures`,
`lib/SandboxSupport.cs`, `examples/33-sandbox-sdk-filesystem.cs`, `docs/SANDBOX_MATRIX.md`,
`docs/DESIGN.md` §3.6, `CHANGELOG.md` Unreleased entry, version 0.4.0 -> 0.5.0.

Verified independently: build 0/0, suite **41/41**, `COMPILE_ONLY=1 ./scripts/validate.sh`
33/33 PASS, and example 33 runs end-to-end to **exit 0** with real seatbelt enforcement
(`run mode: Enforced (backend: seatbelt, platform: macos, outcome: engaged)`, 7 sandbox
decisions including `access_denied` / `class: filesystem_read` / `point: shell`).

**Epic #7 — DECOMPOSED (2026-10-29).** Lead created 7 native sub-issues, all on project #5
with Status = Todo, all 16 acceptance criteria covered:

| # | Slice | Owner | Blocked by |
|---|-------|-------|------------|
| 35 | 7.1 Finalize approved public signatures | Core Engineer | — |
| 36 | 7.2 Extract the shared durable runtime | Core Engineer | #35 |
| 37 | 7.3 Identity binding, admission, checkpointing, resume | Core Engineer | #36 |
| 38 | 7.4 Coordinated transcript and document forks | Core Engineer | #36 |
| 39 | 7.5 Propagate #16 sandbox policy + `AddDurableCopilotClient()` DI | Core Engineer | #37 |
| 40 | 7.6 Public-path contract tests | Tester | #38, #39 |
| 41 | 7.7 One-call example + builder/facade docs | Docs | #40 |

Example number reserved: `examples/36-durable-client-builder.cs` (33/34/35 taken by #16/#17/#18).

## Incident — runaway agent (2026-10-28/29)

The first Core Engineer dispatch for #16 ran **11.2 hours continuously** with **zero commits**:
1,353 bash calls, 99 `.scratch/probe*.cs` files, 73 context compactions, 2% of wall clock in
actual tool execution. It was reverse-engineering the SDK XML in a loop it had already solved.

- Work was salvaged and committed as `1046962` before the agent was stopped.
- The agent was stopped via `write_agent` (the harness exposes **no hard-kill** for background
  agents; subagents run inside the parent `copilot-runtime` process, so there is no PID to kill).
- Re-dispatched with a **pre-researched brief** containing the full SDK sandbox API surface, an
  explicit no-scratch-loop rule, and a commit-every-30-minutes rule. It finished in ~1 hour.

**Lesson (new, keep applying):** a long-running agent is not evidence of progress. Check for
commits, not elapsed time. `list_agents` duration is wall-clock, not work.

## Branch model

`main` is the **release branch and the repo default** (changed 2026-10-28). `dev` is the
integration branch. Feature work branches from `origin/dev` and PRs target `dev`. `dev` -> `main`
only at release. `origin` is SSH. **Squash-merge is the convention** (`gh pr merge <n> --squash
--delete-branch`); `dev` has linear history, no merge commits.

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
Done: #1, #9-#16, #30.
Todo: #2, #3, #4, #5, #6, #7, #8, #17, #18, #19, #35-#41.

**Native-unblocked and startable:** #2, #3, #17, #18, #35.
**Blocked (do not start):** #4, #5, #6, #8, #19, #36-#41.

## Next action

**Ralph rounds 1-2 complete (2026-10-29).** Six issues delivered, five PRs open, all green.

| PR | Issue | Branch | Size | Local verification |
|---|---|---|---|---|
| #55 | #17 | `squad/17-sandbox-network-credentials` | +604/-2, 4 files | build 0/0, suite 41/41, example 34 compile PASS |
| #56 | #35 | `squad/35-finalize-durable-client-signatures` | +1142/-0, 4 files | build 0/0, suite 41/41 |
| #57 | #49 | `squad/49-ef-core-dbcontext` | +927/-0, 13 files | build 0/0, suite 41/41, EfCore suite 8/8 |
| #58 | #43 | `squad/43-deterministic-replay-runtime` | +1187/-0, 10 files | build 0/0, suite 41/41 |
| #59 | #18 | `squad/18-sandbox-observability-bypass` | +619/-3, 5 files | build 0/0, suite 41/41, example 35 compile PASS |

All five are `MERGEABLE` and target `dev`. **Only a human merges.** Squash-merge per the branch
model: `gh pr merge <n> --squash --delete-branch`.

Each PR body carries its required artifact: #58 enumerates the implemented JSON-RPC methods,
#57 records the four provider-specific model decisions, #56 ships
`docs/implementation/ISSUE_7_API_SIGNATURES.md`.

**Board is now merge-gated.** Every remaining open issue is natively blocked:

| Issue | Blocked by |
|---|---|
| #36 | #35 (PR #56) |
| #44 | #43 (PR #58) |
| #50 | #49 (PR #57) |
| #4 | #3 |
| #5 | #3, #4 |
| #6 | #3, #4, #5 |
| #8 | #3, #7 |
| #19 | #3, #6 |
| #2, #3, #7 | open epics (children in flight or blocked) |

So the next startable work appears only after a human merges: merging #56 unblocks **#36**,
merging #58 unblocks **#44**, merging #57 unblocks **#50**. Merging #17 and #18 closes the
sandbox example pair with no dependents.

**Process lessons recorded** in `.squad/decisions.md` this round:

- Delivery-gap lesson — a dispatched agent can report done with only a local commit. The #18
  agent finished and re-ran its example successfully but never committed the final edit, pushed,
  or opened a PR. Audit artifacts (commit SHA, remote branch, PR, CI), never the completion report.
  Every implementation prompt now requires pushing the branch and opening the PR.
- PR evidence standard — CI `build=SUCCESS` is necessary but not sufficient. Verify locally with
  `dotnet build`, `dotnet test tests/Durable.Tests` (plus any new test project the PR added), and
  `COMPILE_ONLY=1 ./scripts/validate.sh <example>`.

**Open question — do not auto-release:** `lib/Copilot.Durable.csproj` is at `0.5.0` and
`CHANGELOG.md` has an `## [Unreleased]` entry. Cutting `v0.5.0` is a separate `dev` -> `main`
promotion per `docs/RELEASING.md` and needs user sign-off. `dev` will be well ahead of `main`
once the five PRs land.
