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

State as of **2026-11-03, Ralph round 14**. Everything below was verified against the
live board, the open PRs, and `git worktree list` — not assumed. Do not trust a commit
hash here; it goes stale the moment this file is committed. Confirm with
`gh issue list`, `gh pr list`, and `git worktree list`.

### Where `dev` is

`dev` head is **`feafd0a`** — "ci: widen the deterministic gate to the parity and
language suites (#131)". **Epics #4 and #8 are both CLOSED** with every criterion
verified. **Two open PRs**, both from this round: **#138** (#132) and **#139** (#133).
Both fixes are independently verified; both are waiting on the `build` job.

### Open issues — three epics, two bugs in review, four sub-issues

| Issue | Owner label | State |
|-------|-------------|-------|
| #19 | `squad:tester` | **decomposed** into #134-#137; blocked by all four |
| #6 | `squad:tester` | un-decomposed epic, zero sub-issues |
| #5 | `squad:polyglot-engineer` | un-decomposed epic, zero sub-issues, `help wanted` |
| #132 | `squad:tester` | `type:bug` — fix in review as **#138** |
| #133 | `squad:tester` | `type:bug` — fix in review as **#139** |
| #134 | `squad:tester` | `SKIP_SANDBOX=1` escape in `scripts/validate.sh` |
| #135 | `squad:tester` | `policy`-mode expected-output fixtures for 33/34/35 |
| #136 | `squad:docs` | advisory host-enforcement CI job + `docs/SANDBOX_CI.md` |
| #137 | `squad:tester` | check-first: does the completeness gate name language *and* id? |

#5 and #6 have **zero** sub-issues — they are un-decomposed epics and cannot be closed
on sub-issue completion alone. Decompose before working them.

**#19 is decomposed and startable.** Its four sub-issues are filed, board-added as
`Todo`, and linked the right way round (`#19` is blocked by each of them). Work them
individually; do not work the epic directly. **Five of #19's nine criteria were already
satisfied** before decomposition — read the decomposition note on #19 (comment
`6602722428`) before writing any code, or you will re-implement what already exists.

### Closed this round — do not reopen

| PR | Issue | Why closed |
|----|-------|------------|
| #119 | #102 | Superseded by #126 — merge tree byte-identical to `dev` |
| #122 | #103 | Superseded by #126 — merge tree byte-identical to `dev` |

`git merge-tree --write-tree origin/dev origin/<branch>` returned tree
`7f0fc017ba188b4eff8104e63f4ae750e9f0eb44`, which **is** `origin/dev`'s tree. Merging
them would have changed nothing. That tree-hash comparison is the proof; use it before
closing any PR as superseded.

Issues closed this round: #98, #102, #103, #104, #105, **#129**, **epic #4**, and
**epic #8**.

### Merged this round

| PR | Issue | Merge commit | What |
|----|-------|--------------|------|
| #123 | — | `d34d84d` | SDK-bound TypeScript and Python durable language support |
| #126 | #105 | `352c076` | Full 36×2 parity contract, runners, global manifest |
| #125 | #104 | `a953d32` | The `advanced-read-tools.test.mjs` guard, +56 lines |
| #130 | #8 | `7a5e373` | Resolved store backend reported on host startup |
| #131 | #129 | `feafd0a` | Widened `--deterministic` to the parity and language suites |
| #138 | #132 | pending | `--test-force-exit` in `lib/ts`'s `test` script |
| #139 | #133 | pending | `PortHarness` resolves its own Python interpreter |

### #126 subsumed #102/#103/#104

#126 ports all 36 examples (00–35) in both languages and owns the global
`examples/parity.json` manifest (`"scope": [0, 35]`) — exactly what #105 was scoped to
do. Before assuming a range PR still has content, diff its changed-file list against
`dev` and check the merge tree. Two of the three had nothing left.

### The regression #126 shipped, and the guard that caught it

#126 changed `resolve(base, path)` → `join(base, path)` in the `read_file` handlers of
`examples/ts/26-plan-mode.ts` and `examples/ts/27-reviewer.ts`. `join('/a', '/b/c')`
returns `/a/b/c`, so an **absolute** path argument gets the working directory prepended
and the file is not found. #126 had no test asserting `join` — the change was unguarded.
#125's `tests/parity/advanced-read-tools.test.mjs` is the guard; it extracts the real
handler via `stripTypeScriptTypes` + `runInNewContext` and asserts relative reads,
absolute reads, missing-file errors, and invalid-argument rejection, plus a deliberate
`resolve`→`join` mutation that must fail. Fixed in `98af351` before #126 merged.

The Python ports were already correct: `Path(directory) / params.path` discards the left
operand when the right is absolute.

**Rule:** any handler joining a caller-supplied path onto a base directory uses
`resolve`, never `join`. A green suite is not evidence here — the two functions differ
only on absolute inputs, so every relative-path test passes either way.

### The rebase trap — reset + cherry-pick, never `rebase`

Every polyglot branch was cut from `915e13a` and carried the shared SDK history that
`d34d84d` squash-merged. `git rebase origin/dev` replays all of it and conflicts on every
`lib/` file (PR #119: 28 commits, 7 conflicting files). `git rebase --onto origin/dev
915e13a <head> -X ours` is **worse** — `-X ours` only resolves *conflicting* hunks in
`dev`'s favour, so non-conflicting branch hunks still land; PR #119 produced 4544
spurious insertions across `lib/`.

Correct procedure: `git reset --hard origin/dev`, then selectively `git cherry-pick`
only the commits whose file lists are disjoint from the merged payload, then prove it
with `git diff --stat origin/dev origin/<branch> -- lib/ts lib/py docs tools src` — it
must be empty. Inspect the full `git show --name-only` list per commit: a commit that
touches both a no-op file and a genuine file must still be cherry-picked. And
`git cherry-pick --continue` after resolving to identical content prints "nothing to
commit" and leaves the sequencer in place — follow it with `--skip`.

### Epic #8 criterion 8 — closed, with one deliberate exclusion

Criterion 8 was a three-clause sentence:

> The host's agent-definition schema accepts a sandbox policy, **a host-started agent
> reports the resolved backend and platform on startup**, and the host's documentation
> states the interaction between "approve all permissions" and "no sandbox".

All three clauses are now satisfied except the word "platform", which is a **deliberate,
documented exclusion**:

- **Sandbox policy in the schema** — `AgentDefinitionValidator.cs:111` →
  `ValidateSandboxPolicy` at 115, documented at `docs/DESIGN.md:351` §3.7, with
  `docs/SANDBOX_MATRIX.md`.
- **Approve-all vs no-sandbox interaction** — `README.md:492`, `docs/DESIGN.md:231`,
  `:369`.
- **Resolved backend on startup** — PR #130. `DurableStore` gained
  `internal DurableStoreFormat Format => _format;` beside the existing `Provider`
  accessor, and `Program.ReportStore` writes `store: {format} at {path}` (or
  `store: {format} (ephemeral)`) to stderr immediately after `ClaimOwnership` and
  **before** `ReportContainment`, on all four store-opening paths.

**The "platform" half is FORBIDDEN by an existing invariant.**
`tests/Durable.Tests/RuntimeHostPortabilityTests.cs:80`
`CorePath_HasNoPlatformSpecificApis()` asserts that no `src/Durable.Runtime/*.cs`
except `HostShutdown.cs` matches
`\b(OperatingSystem|OSPlatform|RuntimeInformation|PosixSignal|PosixSignalRegistration|DllImport|LibraryImport|Registry|WindowsIdentity)\b|Environment\s*\.\s*(OSVersion|SpecialFolder)|[A-Za-z]:\\`.
That test **is** the enforcement of criterion 6. A literal platform startup line would
break criterion 6. **Do not add platform detection; do not weaken that test.** The
exclusion is recorded in `docs/DESIGN.md` §3.9 under "The startup report".

**The trap that made this non-trivial:** `_format` was *not* already the resolved value.
`DurableStore.Create` stored `options.Format` verbatim, so an `Auto` store reported
`Auto` forever. The fix is `ResolveFormat(provider, options)`, which maps the **selected
provider's type** to its format and falls back to `options.Format` for caller-supplied
factories. A "resolved" accessor is only truthful if the value is resolved at open time.

**Second trap:** `LegacyJson` stores write nothing until their first commit, so a freshly
created legacy path is still empty and `Auto` correctly sniffs it as `Jsonl`. Any test
that reopens a legacy store with `Auto` must commit a conversation first.

The host's real answer to "what bounds this agent's authority" is the containment
report: `containment: {Sandboxed|PermissionGated|None} for agent '{name}': {reason}`
(`Program.cs:1268-1272`, called from all four store-opening paths at 134, 177, 496, 873).

### Corrections to prior handoffs — do not re-litigate

- **Supplement 002's "stale abort anchor" claim is false.** All 8 mutation anchors in
  `tests/parity/test_mutations.py` resolve to exactly one occurrence. `abortTaskAsync`
  is defined at `lib/ts/durable.ts:2277`; no `abortCascadeAsync` exists. Verified twice
  (coordinator + Tester); `test_mutations.py` passes 2/2. Do not dispatch a fix.
- **Tester's "2 failing TS tests" was an environment artifact.** With
  `DURABLE_RUNTIME_PATH` set, `npx tsx --test --test-force-exit durable.test.ts` on
  `dev` gives **119/119 pass**. The guard at `lib/ts/durable.ts:3290-3301` and the test
  were both introduced by `d34d84d`, and no PR touches those files.
- **`scripts/validate.sh --deterministic` does not run the parity suite**,
  `test_mutations.py`, or either language unit suite. It runs 17 of 38 examples with
  only the `ReplayToolDispatchTests` filter. "Five green gates" and "parity suite red"
  can coexist. Widening it is tracked by **#129**. `.github/workflows/build.yml` already
  runs `./scripts/validate.sh --deterministic` as its own step, so widening the script
  widens CI automatically — **no workflow change is needed**.

### Environment prerequisites that silently break suites

- **`lib/py/.venv` is required and was deleted once by disk cleanup.**
  `python examples/run_all.py --deterministic` fails all 15 selected examples with
  `ModuleNotFoundError: No module named 'copilot'` from `lib/py/durable.py:38`. Recreate
  with `python -m venv .venv` in `lib/py`, then
  `pip install "github-copilot-sdk==1.0.28"`. **`pytest` is NOT installed there** — run
  the unit suite as `python lib/py/test_sdk.py` (unittest).
- **`examples/ts` needs its own `npm ci`.** `npm run examples:ts` fails with
  `ERR_MODULE_NOT_FOUND: Cannot find package '@github/copilot-sdk' imported from
  examples/ts/tools.ts` unless `examples/ts/node_modules` exists. `lib/ts/node_modules`
  alone is not enough. This produced 3 spurious failures (05, 06, 09).
- `tsx --test` never exits without `--test-force-exit`.
- `node --test tests/parity/` fails with MODULE_NOT_FOUND; use `npm run test:parity:ts`.
  A single file works with `node --experimental-transform-types --test <file>`.
- `PortHarness_PhysicalLineEndingsPreserveGeneratedClients` needs `lib/py/.venv/bin` on
  `PATH`. `lib/ts` `npm test` needs `DURABLE_RUNTIME_PATH` set.
- Disk pressure is real: `/System/Volumes/Data` hit 100% twice, causing `index.lock` and
  `MSB3923` failures. `uv cache clean` freed 11.6 GiB; `~/.nuget/packages` (13G) is the
  next lever. **Cleanup deleted `lib/py/.venv` once — check it before blaming the code.**
- `gh pr view --json mergeable` returns `UNKNOWN` for ~20–25s after a push.
- Draft PRs cannot be merged — `gh pr ready <n>` first.
- GitHub project automation moves a board item to `Done` when its issue closes; manual
  `item-edit` is usually unnecessary. Item IDs are `PVTI_lAHOAajMkc4BobdZzhE1Z…` — note
  the `h`; better to read `.id` from
  `gh project item-list 5 --owner matthewcorven --format json`.

### Startable vs blocked

**Startable now:** **#134**, **#135**, **#136**, or **#137** — epic #19's four
sub-issues, all filed, board-added as `Todo`, and linked so that #19 is blocked by each
of them. Work them individually; do not work the epic directly.

#137 is check-first and cheapest. #134 is a small `scripts/validate.sh` change. #135
and #136 are larger (fixtures and a CI job respectively).

**Before touching any of them, read the decomposition note on #19** (comment
`6602722428`). Five of #19's nine criteria were already satisfied before decomposition
— `lib/SandboxSupport.cs` already has `SandboxRunMode` (28), `Resolve` (73),
`AssertPolicy` (111), `AssertDenied` (174), `AssertNoPermissionDenial` (207); `33 34 35`
are already in `LIVE_EXAMPLES` (`scripts/validate.sh:31`); `policy_resolved` is already
emitted and asserted. Re-implementing those is the main risk in this epic.

**Blocked — do not start:**

- **#5** ← #4 (now closed, so #5 is unblocked but un-decomposed). **#6** ← #5, #4.
- **#19** ← #134, #135, #136, #137. It is startable only through its sub-issues.
- **#4, #8** — CLOSED epics. Do not reopen; work any follow-up as a new issue.

These are native `blockedBy` links and they are authoritative. Before 2026-10-31 the
sequencing lived only in issue-body prose, so a naive scan saw eight blocked issues as
startable. **Trust the native links, not the issue bodies.**

### Do not close #3

Issue #3 is **closed** but only 2 of its 8 acceptance criteria pass. The ruling in
`.squad/decisions.md` stands: it needs human re-scope. Do not reopen it, do not
re-close it, do not "fix" it by editing the criteria.

### The runtime host is the reference for #95–#98

`src/Durable.Runtime/` landed in #112 and is the worked example every remaining #8.x
child follows. Read it before touching #95, #96, #97, or #98.

- **`RunResult` is the single serialized turn-result shape.** Fields: `agent`,
  `conversationId`, `admissionId`, `checkpointId`, `effectiveModel` (`model` +
  `source`, or null), `modelReported`, `containment`, `containmentReason`, `outcome`,
  `error`. Nulls are **written, not omitted**, so `effectiveModel: null` is
  distinguishable from an absent property. #95, #96, and #97 serialize this record;
  they do not define their own.
- **Claim ownership before opening the harness.** `Program.ClaimOwnership` runs
  *before* `DurableHarness.OpenAsync` because `OpenAsync` starts the SDK client first —
  a refused host would otherwise orphan a runtime process. Preserve that ordering.
- **D2 validation** lives in `AgentDefinitionValidator.cs`; **Hazard A** classification
  in `Containment.cs`; **Hazard B** reporting in `RunResult.EffectiveModel` +
  `ModelReported`. Take them from the host, do not reimplement per transport.

### The #93 design record is still the spec

Issue #93 is closed and holds the approved design decision record. The load-bearing
points, unchanged:

- **D1 store ownership** — one host owns its store for its lifetime; a second host is
  refused at startup. `ClaimTaskScheduler` (`lib/Durable.cs:2275`) in-process,
  `StoreFileLock` (`:1191`) cross-process. **Ordering trap:** `DurableHarness.OpenAsync`
  (`:5966`) starts the SDK client at `:5982` *before* constructing the harness at
  `:5983`, so ownership must be validated *before* `OpenAsync`.
- **D2 agent definition** — document kind `durable.agent-definition`, owner id
  `agent:{name}`, `DocumentHistory.Rewindable`, `ForkPolicy.Initial`, added to
  `InternalDocumentKinds` (`:4836`). Validate at import, at host startup, and on every
  invocation, because `ExtensionRegistry.Uninstall` (`:2638`) exists. An unknown
  extension must be refused naming the agent and the missing extension — do **not**
  route through `ResolveExtensions` (`:5259`), which silently drops unresolved names.
  No permission-handler field (delegates cannot be persisted); carry `Permissions`
  grants instead.
- **D3 conversation identity** — one conversation per agent; each occurrence is an
  `InputAdmission` + `ConversationCheckpoint` inside it. `--new-conversation` is the
  opt-out.
- **Hazard A** — approve-all handler plus null sandbox means zero containment. Classify
  `Sandboxed` / `PermissionGated` / `None`. `run` proceeds with a warning; `serve` and
  `schedule` refuse to start with `Containment.None` unless `--allow-uncontained`.
- **Hazard B** — effective model comes from `WaitForEffectiveModelAsync` (`:3574`). On
  `NotReported` (`:3164`) report null plus `modelReported: false` — never the requested
  model, never `DefaultModel` (`:5880`). `--require-effective-model` uses
  `DurableModelReport.RequiredModel` (`:3190`).

**The #93 AC1 gap is now closed.** `docs/DESIGN.md` §3.9 ("The runtime host —
`src/Durable.Runtime`") landed with #112. Sections now run §3.1–§3.9.

### Section numbers are pre-assigned

When two or more PRs add sections to the same doc concurrently, the coordinator assigns
the numbers **before** dispatch and states them in the dispatch prompt. #95 got §3.12
and #97 got §3.13 that way. A later PR that lands first does not get to renumber the
others; when a conflict does appear, renumber the *later* PR, never the merged one.

### No TypeScript or Python toolchain at the repo root

`lib/` holds only C# projects. There is no root `tsconfig.json` and no root
`package.json`. The polyglot work lives under `lib/ts/` and `lib/py/` and deliberately
does **not** add root-level packaging that would disturb the C# build. If you need to
run those suites, look under `lib/ts/` and `lib/py/`, not the repo root.

### Known follow-ups, not yet filed

Six real gaps surfaced during PR #83 and PR #84 and were deliberately left out of
scope. They are listed in `.squad/decisions.md` under "Six agent-surfaced follow-ups
are known and unfiled". File them when the board is otherwise clear.

Three more surfaced during #129. Two are now fixed and in review; one remains unfiled:

- **#132 — fixed, PR #138.** `lib/ts/package.json`'s `test` script lacked
  `--test-force-exit`, so a bare `npm test` hung forever. The gate worked around it;
  the script itself is now fixed. Verified: 123/123 pass, exits on its own.
- **#133 — fixed, PR #139.** `PortHarness_PhysicalLineEndingsPreserveGeneratedClients`
  (`tests/Durable.Tests/LateJoinExampleTests.cs`) shelled out to `python3 -m unittest`
  via `PATH` and needed `lib/py/.venv/bin` on `PATH`. It now resolves its own
  interpreter. Verified with a before/after control: fails without the fix, passes
  with it.
- **Still unfiled** — `README.md`'s example counts in the same paragraph are worth
  re-checking against `examples/parity.json`.

### Hard constraints

- `dev` is the integration branch; every PR targets `dev`. `main` is release-only.
- `dev` is protected with **zero required approvals**. `gh pr merge <n> --squash
  --delete-branch` works directly; CI `build` is the only gate. Do not force-push or
  delete `dev`.
- There is **no solution file** at the repo root — `dotnet build -c Release` fails with
  `MSB1003`. Build `lib/Copilot.Durable.csproj` by path.
- Baseline: `dotnet test tests/Durable.Tests` → **162 passed** on `dev` @ `3a0b17b`.
- `scripts/validate.sh` bans `mock|fake|stub|simulat` in `examples/*.cs`. Use
  "double", "stand-in", or "synthesised".
- Any new internal document must be added to `InternalDocumentKinds` in
  `lib/Durable.cs`, or its anchor entries will leak into the transcript.
- GitHub only auto-closes an issue on `Closes #N` when the PR targets the **default**
  branch (`main`), not `dev`. Close issues manually after merging.
