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

State as of **2026-11-03, Ralph round 9**. Everything below was verified against the
live board, the open PRs, and `git worktree list` — not assumed. Do not trust a commit
hash here; it goes stale the moment this file is committed. Confirm with
`gh issue list`, `gh pr list`, and `git worktree list`.

### Where `dev` is

`dev` head is **`a953d32`** — "Port examples 22-35 with native parity evidence (#125)".
**Zero open PRs.** The polyglot epic #4 is **CLOSED** with all five criteria verified.

### Open issues — four, all epics

| Issue | Owner label | State |
|-------|-------------|-------|
| #8 | `squad:core-engineer` | 7 of 8 criteria verified; criterion 8 in progress |
| #19 | `squad:tester` | un-decomposed, zero sub-issues |
| #6 | `squad:tester` | un-decomposed, zero sub-issues |
| #5 | `squad:polyglot-engineer` | un-decomposed, zero sub-issues, `help wanted` |

All four board items are `Todo`. **Only #8 has sub-issues** (6, all CLOSED). #5, #6, and
#19 have **zero** sub-issues — they are un-decomposed epics and cannot be closed on
sub-issue completion alone. Decompose before working them.

### Closed this round — do not reopen

| PR | Issue | Why closed |
|----|-------|------------|
| #119 | #102 | Superseded by #126 — merge tree byte-identical to `dev` |
| #122 | #103 | Superseded by #126 — merge tree byte-identical to `dev` |

`git merge-tree --write-tree origin/dev origin/<branch>` returned tree
`7f0fc017ba188b4eff8104e63f4ae750e9f0eb44`, which **is** `origin/dev`'s tree. Merging
them would have changed nothing. That tree-hash comparison is the proof; use it before
closing any PR as superseded.

Issues closed this round: #98, #102, #103, #104, #105, and **epic #4**.

### Merged this round

| PR | Issue | Merge commit | What |
|----|-------|--------------|------|
| #123 | — | `d34d84d` | SDK-bound TypeScript and Python durable language support |
| #126 | #105 | `352c076` | Full 36×2 parity contract, runners, global manifest |
| #125 | #104 | `a953d32` | The `advanced-read-tools.test.mjs` guard, +56 lines |

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

### Epic #8 criterion 8 — the one open criterion, and its built-in contradiction

Criterion 8 is a three-clause sentence:

> The host's agent-definition schema accepts a sandbox policy, **a host-started agent
> reports the resolved backend and platform on startup**, and the host's documentation
> states the interaction between "approve all permissions" and "no sandbox".

Two clauses are already satisfied: the sandbox policy is in the schema
(`AgentDefinitionValidator.cs:111` → `ValidateSandboxPolicy` at 115, documented at
`docs/DESIGN.md:351` §3.7, with `docs/SANDBOX_MATRIX.md`), and the approve-all vs
no-sandbox interaction is documented (`README.md:492`, `docs/DESIGN.md:231`, `:369`).

**The "platform" half is FORBIDDEN by an existing invariant.**
`tests/Durable.Tests/RuntimeHostPortabilityTests.cs:80`
`CorePath_HasNoPlatformSpecificApis()` asserts that no `src/Durable.Runtime/*.cs`
except `HostShutdown.cs` matches
`\b(OperatingSystem|OSPlatform|RuntimeInformation|PosixSignal|PosixSignalRegistration|DllImport|LibraryImport|Registry|WindowsIdentity)\b|Environment\s*\.\s*(OSVersion|SpecialFolder)|[A-Za-z]:\\`.
That test **is** the enforcement of criterion 6. A literal platform startup line would
break criterion 6. **Do not add platform detection; do not weaken that test.** Record
the platform half as a deliberate, documented exclusion.

**The "resolved backend" half is satisfiable.** `DurableStore` keeps `_format` private
with no accessor (`lib/Durable.cs:1746`), but `internal IDurableStoreProvider Provider
=> _provider;` already exists at `:2336` and `lib/Copilot.Durable.csproj:42` grants
`InternalsVisibleTo("Durable.Runtime")`. Adding `internal DurableStoreFormat Format =>
_format;` is sufficient and does not change public API. `DurableStoreOptions.Format` is
public (line 734, default `Auto`) — the *requested* format is public, only the
*resolved* one is not.

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
  can coexist. Widening it is a real gap that belongs in its own issue.

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

**Startable now:** #8 criterion 8 (the resolved-store-backend startup report). #19 is the
next most tractable epic — its `lib/SandboxSupport.cs` surface already partly exists
(`SandboxRunMode` at 28, `Resolve` at 73, `AssertPolicy` at 111, `AssertDenied` at 174,
`AssertNoPermissionDenial` at 207).

**Blocked — do not start:**

- **#5** ← #4 (now closed, so #5 is unblocked but un-decomposed). **#6** ← #5, #4.
  **#19** ← #6.
- **#4, #8** — epics. Work them through their children, never directly.

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
