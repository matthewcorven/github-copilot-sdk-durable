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

State as of `dev` @ `43f65a9` (2026-10-30). Everything below is verified, not assumed.

### A stale session may still own a worktree

A prior Ralph session (`ceff3860`) is still running and holds
`/Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/40` on branch
`squad/40-facade-contract-tests`. It has produced **0 commits and 0 edits** across
1801 turns and 90 context compactions, and is looping on repeated reads. It will
not deliver #40.

**Before dispatching #40:** check `git worktree list`. If that worktree still
exists, remove it (`git worktree remove --force <path>`) and delete the branch
(`git branch -D squad/40-facade-contract-tests`) — it holds no work. Do not
dispatch a second agent onto that branch while the old session is alive.

### The board

Open: #40, #41, #19, #8, #7, #6, #5, #4, #3.

- **#40** — unblocked, the right next target. Read
  `docs/implementation/ISSUE_40_NOTES.md` first: six of its eight contract areas
  are already covered by existing facts. The two real gaps are **checkpoint
  failure** (inject a provider whose `Commit()` throws via
  `DurableStoreOptions.Provider`) and **hook composition** (the wrapper at
  `lib/Durable.cs:5459-5483`). `docs/implementation/ISSUE_40_HOOK_API.md` has the
  hook type shapes.
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
- Baseline: `dotnet test tests/Durable.Tests` → **127 passed**.
- `scripts/validate.sh` bans `mock|fake|stub|simulat` in `examples/*.cs`. Use
  "double", "stand-in", or "synthesised".
- Any new internal document must be added to `InternalDocumentKinds` in
  `lib/Durable.cs`, or its anchor entries will leak into the transcript.
