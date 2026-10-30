# Ralph Instructions
<!-- User-owned: customize this file to override Ralph's autonomous-execution behavior.
     squad init creates this file on first install; squad upgrade never overwrites it. -->

<!--
  PURPOSE
  -------
  When `.squad/ralph-instructions.md` exists, `squad watch --execute` instructs the
  spawned Copilot session to read this file and follow ALL sections here instead of
  the built-in fallback prompt.  If the file is absent, the built-in prompt is used.

  CONTRACT (stable — safe to build on)
  --------------------------------------
  YOU CAN  customize via this file:
    • Extra instructions given to Ralph at session start (Teams/Slack notifications,
      calendar checks, post-task hooks, MCP-powered side effects, escalation paths)
    • Additional eligibility rules or priority ordering for issue selection
    • Agent persona, tone, or verbosity for session output

  YOU CANNOT override via this file:
    • Parallelism — Ralph always spawns agents for all actionable issues simultaneously
    • Core eligibility filter (squad/squad:* label required, not blocked, not assigned)
    • The underlying `gh` / Copilot CLI command used to spawn each session

  TRUST IMPLICATIONS
  ------------------
  This file is read by the spawned Copilot session with full agent permissions.
  Treat it like code — never paste untrusted content here.  Anyone with write access
  to this file can influence what the agent does on your behalf.

  If this file is missing or empty, `squad watch --execute` falls back to the
  built-in prompt with no behavioral change.

  PLACEHOLDERS
  ------------
  The following values are injected by execute.ts before the session reads this file:
    (none currently — Ralph builds the issue list dynamically at runtime)

  FORMAT
  ------
  Plain markdown.  Structure with ## sections.  The spawned session reads the whole
  file, so keep it concise — one screen of instructions is ideal.
-->

## Ralph, Go!

Read this file for your full instructions.  Follow ALL sections.
MAXIMIZE PARALLELISM — spawn agents for ALL actionable issues simultaneously.

### Issue Selection

Work on every open, unblocked, unassigned issue labeled `squad` or `squad:{member}`.
Skip issues that are assigned to a human, blocked, or marked `status:on-hold`.

### Post-Task Actions

<!-- Uncomment and customize to add post-task hooks, e.g. Teams notifications:

After completing work on each issue:
- Post a brief summary to the team channel via your Teams MCP tool.
- Update the issue with a progress comment if no PR has been opened yet.
-->

### Escalation

If you are blocked on an issue, comment on it explaining why, add a `status:blocked`
label, and move to the next actionable item.  Do not halt the loop.

## Session Handoff — read this before your first dispatch

State as of 2026-10-30. Everything below is verified, not assumed. Do not trust a
commit hash here — it goes stale the moment this file is committed. Confirm the
board with `gh issue list` and the worktrees with `git worktree list`.

### Worktree 40 is live — do not delete it

`/Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/40` on branch
`squad/40-facade-contract-tests` holds **PR #86**, which is open, mergeable, and
green. An earlier note here said this worktree held no work and should be removed;
that was true when written and is now wrong. **Check `gh pr list` before removing
any worktree.**

### The board

Open: #87, #41, #40, #19, #8, #7, #6, #5, #4, #3.

- **#40** — **delivered by PR #86** (open, mergeable, `build=SUCCESS`). It adds
  `tests/Durable.Tests/FacadeContractTests.cs` (14 tests, +862 lines) covering
  reopen, resume, checkpoint failure, duplicate delivery, interrupted fork, hook
  composition, ownership, and policy application. Merge it, then close #40.
- **#87** — the hook-mutation defect PR #86 documents but does not fix:
  `BuildSessionConfig` assigns the caller's `SessionHooks` by reference and then
  mutates it, so `AgentChoices.Hooks` is rewritten and a second build stacks the
  composition. `HookComposition_LeavesTheCallersOwnHooksUnchanged` is skipped
  against it. Unskip that test to reproduce.
- **#41** — blocked by #40.
- **#19** — blocked by #6 and #3.
- **#3** — **do not close it.** Only 2 of its 8 acceptance criteria pass; the
  evidence is in `.squad/decisions.md`. It needs a re-scope decision from a human
  before any agent works it.
- **#6, #8, #4, #5** — unstarted, no blockers.

### Known follow-ups, not yet filed

Six real gaps surfaced during PR #83 and PR #84 and were deliberately left out of
scope. They are listed in `.squad/decisions.md` under "Six agent-surfaced
follow-ups are known and unfiled". File them when the board is otherwise clear.

### Hard constraints

- `dev` is the integration branch; every PR targets `dev`. `main` is release-only.
- There is **no solution file** at the repo root — `dotnet build -c Release` fails
  with `MSB1003`. Build `lib/Copilot.Durable.csproj` by path.
- Baseline: `dotnet test tests/Durable.Tests` → **127 passed** on `dev`;
  **140 passed, 1 skipped** on `squad/40-facade-contract-tests` (the skip is #87).
- `scripts/validate.sh` bans `mock|fake|stub|simulat` in `examples/*.cs`. Use
  "double", "stand-in", or "synthesised".
- Any new internal document must be added to `InternalDocumentKinds` in
  `lib/Durable.cs`, or its anchor entries will leak into the transcript.
