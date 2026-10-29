# Squad Decisions

## Active Decisions

### 2026-10-24T00-35-44: Keep the in-memory backend as an explicitly-named ephemeral tier; make the persistent tier the default of the options/factory API; migrate examples to explicit names. Decision #7 stands on its capability conclusion but its rationale is superseded.
**By:** lead
**What:** Keep the in-memory backend as an explicitly-named ephemeral tier; make the persistent tier the default of the options/factory API; migrate examples to explicit names. Decision #7 stands on its capability conclusion but its rationale is superseded.
**References:** #1, #2, #4, #5, #7, #12, #13, #14, #15, docs/implementation/ISSUE_1_PLAN.md, docs/implementation/ISSUE_1_HANDOFF.md, lib/Durable.cs
**Why:**
#### Position

**Keep the in-memory backend. Change what it is called and where the default lives.**

The capability is defensible and structurally load-bearing. The *silent default* is not. The fix is not "remove in-memory" and not "flip `new DurableStore()` to disk" — it is to make the durable tier the default of the **options/factory API** and make the ephemeral tier **explicitly named** everywhere.

#### Evidence

- `lib/Durable.cs:640` `InMemoryStoreProvider` is the **base class** of `LegacyJsonStoreProvider` (:899) and `JsonlStoreProvider` (:946). The undo log (`_undo`, :654), the sequence counter, and the entry/document/task collections all live in the base. The transactional contract is implemented *in* the in-memory provider and inherited by the persistent ones. `Persist()` (:829) is an empty `protected virtual` in the base.
- Examples: **28 of 31** use `new DurableStore()` (in-memory); **3** use a path — `12-recovery.cs:79,105,114`, `22-subagent-background.cs:148,170`, `30-reload-and-restart.cs:62,85`. 35 total call sites.
- Tests: **3 of 4** facts use `new DurableStore()` (`DurableStoreTests.cs:12,43,173`); only `Recovery_...` (:141,150) uses a path.
- The footgun is real and is in the repo's own teaching material: `examples/00-conversation.cs:40`, `examples/11-tasks.cs:79,93`, `examples/13-chat.cs:60` all assert "durable" while the store is in RAM.
- `docs/DESIGN.md:79` — "A single JSON-file-backed store per example (or in-memory)"; `README.md:147` — "or stays in memory".
- Parity: Pi Durable ships `MemoryStorage` as a first-class SPI backend (`docs/COMPARATIVE_ANALYSIS.md` §1).
- Issues #4/#5 require the in-memory backend in the polyglot ports ("the ports ship with the in-memory and JSONL backends only"). Issue #2 uses the in-process store as the **parity reference** for EF Core.
- The library is not packaged: 0 releases, no `.csproj` for `lib/`, packaging is issue #6's scope.

#### Decision #7 re-examination

Decision #7 ("in-memory stays the default") **stands as a capability conclusion but its rationale is superseded.** Its stated rationale was backwards-compatibility of an existing public API (`new DurableStore()`), not a judgement about long-term capability. The API shape has since changed: `DurableStore.Create(DurableStoreOptions)` exists (`lib/Durable.cs:1253`), and the #12 branch (`1173c75`) already resolves the per-user default path for any non-`InMemory` format. The durable tier is now reachable without a path, so the old constructor's default is a legacy-compat artifact, not a design position.

#### Recommended change set (scoped, ordered)

1. **`DurableStore.Create(new DurableStoreOptions())` → persistent at the per-user default path.** Already true after #12; requires #13's sniffing to be correct. **This is the durable default.**
2. **Add `DurableStore.CreateEphemeral()`** — a named factory for the in-memory tier. Explicit, greppable, self-documenting.
3. **Add `public bool DurableStore.IsPersistent { get; }`** — makes the tier observable. This is the "loud" mechanism; a runtime warning is not (28 examples would spam stderr).
4. **Migrate all 31 examples** off `new DurableStore()`: 28 → `DurableStore.CreateEphemeral()`, 3 → `DurableStore.Create(new DurableStoreOptions { Path = storePath })`. One line per file. Makes the suite's split explicit: 3 persistence demos, 28 semantics demos.
5. **Fix the overclaiming comments** in `00-conversation.cs:40`, `11-tasks.cs:79,93`, `13-chat.cs:60` — say "committed" / "in the store", not "durable", when the store is ephemeral.
6. **Amend #13**: `Auto` on a **non-existent** file must write **JSONL** (the new canonical format), not legacy. `Auto` reads legacy only when the file is legacy. Without this, `Create(new DurableStoreOptions())` writes the legacy format to the default path.
7. **Docs (fold into #14)**: README:147, `docs/DESIGN.md` §3.1, `docs/COMPARATIVE_ANALYSIS.md` §1 — describe the **tier ladder** (ephemeral / local-file / shared-database), not a binary.
8. **Tests (fold into #15)**: add `EphemeralStore_IsNotPersistent`, `DefaultOptions_ProducePersistentStore`, `EphemeralStore_StateIsLostOnReopen`. Target 9 → 12.

#### Blast radius on the spine

- **#12 (in flight, `1173c75`)**: no rework. It already lands `DefaultStorePath` and the `Path is null && Format is not InMemory` resolution. This is the enabling change.
- **#13**: needs amendment #6 above. Otherwise unchanged.
- **#14**: gains the tier model + the example migration + the comment fixes.
- **#15**: gains 3 facts. The 5 planned facts are unaffected.
- **#2 (EF Core)**: unaffected; the tier model extends (EF Core = shared tier).
- **#4/#5 (polyglot)**: unaffected; in-memory is required there.
- **#7 (unified extension library)**: the builder must expose the tier explicitly and default to durable — otherwise the same footgun reappears one level up.

#### Explicitly ruled out

- **Flipping `new DurableStore()` to persistent-at-default-path.** All 28 semantics examples would share `~/.local/state/durable/durable-store.jsonl`; `Check.Equal(2, harness.Store.Conversations.Count)` (`00-conversation.cs:47`) fails on the second run; the suite becomes order-dependent and non-hermetic; the constructor gains a filesystem side effect in the user's home directory. Worse than the current footgun.
- **Removing in-memory.** It is the base class of the provider hierarchy, 3/4 tests use it, 28/31 examples use it, #4/#5 require it in the ports, and Pi ships it for parity. That is a rewrite of the storage layer, not a deletion.
- **A runtime warning on the ephemeral path.** 28 examples would spam stderr. Loudness comes from the API shape, not from `Console.Error`.
- **Renaming the `InMemory` enum member.** Public surface, already shipped in the enum; buys little, breaks source compat. Add `CreateEphemeral()` instead.
- **Making `Format = Jsonl` the options default.** Breaks the legacy-file case (#13) and can destroy data on open. `Auto` is correct.
- **Re-opening decisions #1–#6, #8.** Only #7 was in scope.

#### Over-the-horizon

At 3 backends, 6 languages, and a unified extension library, the right abstraction is a **capability tier ladder**, not a binary: ephemeral (in-memory) → local-file (JSONL) → shared-database (EF Core). The library's value is that the *semantics* — transactions, fork isolation, memo idempotency, checkpoints — are identical across tiers, and the tier is a deployment choice. In-memory is the bottom rung: the lowest-common-denominator substrate the polyglot matrix and CI can rely on, and the reference implementation the other tiers are tested against. It is **not** a transitional artifact — but it must stop being the *default* of the durable API.

#### Compatibility promise

No breaking change to `DurableStore(string? path = null)` behaviour. No change to `DurableStoreFormat` members. No change to `IDurableStoreProvider`. New public surface: `CreateEphemeral()`, `IsPersistent` (2 members). `new DurableStore()` is documented as ephemeral now; `[Obsolete]` is deferred to after the first package release (issue #6 owns packaging) — obsoleting a public constructor before the library has a single external consumer buys nothing.

### 2026-10-23T23-27-58: Issue #1 handoff written; #9 implemented and pushed, PR decision deferred to next session

**Date:** 2026-10-23
**Author:** lead

**What:** Issue #1 handoff written; #9 implemented and pushed, PR decision deferred to next session

**References:** docs/implementation/ISSUE_1_HANDOFF.md, docs/implementation/ISSUE_1_PLAN.md, #1, #9, #10, #11, #12, #13, #14, #15

**Why:**

#### 2026-10-23T19:08:33.866-04:00: Issue #1 handoff written; #9 implemented, pushed, PR decision deferred

**By:** Lead (via Copilot)

**What:**

1. **Handoff document.** `docs/implementation/ISSUE_1_HANDOFF.md` is the authoritative operational handoff for the next Lead session. It carries the eight resolved design decisions (short form, pointing at `docs/implementation/ISSUE_1_PLAN.md` §5 for the full rationale), the three findings that changed the provider contract, the verified build/test/validate state, what #9 delivered with `lib/Durable.cs` line references, an honest gap list, the remaining spine, the parallel sandbox line, the board and issue state with pinned ids, ten gotchas, and numbered next actions with exact commands.

2. **#9 is implemented and pushed.** Branch `squad/9-durable-store-provider-interface` now exists on `origin` with commits `05a9562` (a `wip:` checkpoint that does not compile on its own) and `4e5ca88` (completes the refactor and restores compilation), plus `557bfb7` (the handoff) and `cddca31` (the merged squad decisions). The branch builds clean, `dotnet test tests/Durable.Tests` reports 4 passed / 0 failed, and `COMPILE_ONLY=1 ./scripts/validate.sh` reports all 31 examples compile. `examples/` and `tests/` are byte-identical to `origin/dev`.

3. **The PR decision is deliberately deferred.** No PR was opened and nothing was merged to `dev`. The handoff judges #9 PR-ready and states the three caveats the PR body must carry: the branch has two commits and the first does not compile (squash or explain); the branch already performs much of #10's facade refactor, so #10 must be re-scoped; and `DurableStoreFormat.Jsonl` throwing plus `Auto` not sniffing are deliberate and land in #11/#13.

4. **A plan-doc regression was found and repaired.** The working-tree copy of `docs/implementation/ISSUE_1_PLAN.md` had lost all four "Resolved:" markers and the entire §5 resolutions table that `HEAD` still carried. It was restored with `git checkout HEAD -- docs/implementation/ISSUE_1_PLAN.md`. The handoff records this as a gotcha so the next session repairs it the same way if it recurs.

**Why:** The user asked for the remaining open questions to be settled on judgement and for a handoff document the next Lead session could carry forward. The questions were already resolved in commit `9e9581f`; the work here was to preserve the at-risk uncommitted implementation, verify it, and capture the state operationally.

**How it applies:** `.squad/identity/now.md` now points at the handoff so session init lands on it. The board is unchanged — #1 and #9 are `In Progress`, nothing is `Done`. The next session decides the #9 PR, then re-scopes and routes #10 to Core Engineer.

### 2026-10-23T21:15:00-04:00: Core Engineer — #12 loud ephemeral tier API implemented (PR #24, draft)

**Date:** 2026-10-23
**Author:** core-engineer

**What:** Implemented items 1–3 of the Lead's "loud ephemeral tier" change set in `lib/Durable.cs`: `DurableStore.CreateEphemeral()`, `DurableStore.IsPersistent` (backed by a new `IDurableStoreProvider.IsPersistent` member), and `Create(new DurableStoreOptions())` resolving to the persistent tier at the per-user default path. The `Auto` arm was also amended so a non-existent file writes JSONL rather than the legacy snapshot.

**References:** #12, #13, #14, #15, PR #24, branch `squad/12-ephemeral-tier-api`, commits `a87ccdb` + `5601081`, `lib/Durable.cs`

**Why:**

1. **The `Auto`→JSONL change was implemented, not deferred.** The Lead's recommendation #6 was small and safe to land here, and it was a prerequisite for item 3: without it, `Create(new DurableStoreOptions())` would have written the legacy single-file format to the default path, which is the opposite of the intent. The discriminator is the JSONL envelope's top-level `type` property, which the legacy snapshot never carries — the same rule already settled in `docs/implementation/ISSUE_1_PLAN.md` §2.1. A file that does not exist, or exists but is empty, is written as JSONL.

2. **A bare "file exists ⇒ legacy" rule was rejected on evidence.** A scratch diagnostic showed the legacy provider reading a JSONL file opens it as an *empty* store rather than failing — silent data loss. So the `Auto` arm must never route a JSONL file to the legacy provider, which is why the discriminator is required rather than optional.

3. **The remaining legacy-reader gap is left to #13 and is stated precisely.** A file that is neither format — valid JSON but not a `StoreSnapshot` — currently opens as an empty legacy store instead of raising an actionable error. That distinction is #13's scope and is recorded in the PR body.

4. **Scope was respected.** `examples/` (#14) and `tests/` (#15) are untouched. `new DurableStore()` behaviour is unchanged and carries no `[Obsolete]` attribute. `DurableStoreFormat` members are unchanged.

**How it applies:** PR #24 is open as a draft against `dev`, based on `origin/squad/12-default-path` (`1173c75`) because #12's PR #23 is still open and unmerged. Gates green: build 0 warnings / 0 errors, tests 4 passed / 0 failed, all 31 examples compile. Verified end-to-end in a scratch console project against the real `lib/Durable.cs` with a hermetic `XDG_STATE_HOME` — the default path's first line is `{"v":1,"seq":1,"type":"conversation","payload":{"id":"c1"}}`, a JSONL envelope, not a legacy snapshot.

### 2026-10-23T20-42-49: Resolve all 8 open design questions for issue #1; provider contract, envelope, path rules, doc ownership

**Date:** 2026-10-23
**Author:** lead

**What:** Resolve all 8 open design questions for issue #1; provider contract, envelope, path rules, doc ownership

**References:** #1, #9, #10, #11, #12, #13, #14, #15, docs/implementation/ISSUE_1_PLAN.md

**Why:**

#### 2026-10-23T16:10:25.743-04:00: Resolve all eight open design questions for issue #1

**By:** Copilot App (via Copilot) — "use your best judgement on the open questions and move ahead"

**What:** All eight open questions in `docs/implementation/ISSUE_1_PLAN.md` §5 are resolved. The resolutions are the contract issues #9–#15 build against.

| # | Question | Resolution |
|---|---|---|
| 1 | Record envelope shape | `{v, seq, type, payload}`, `v: 1` |
| 2 | Legacy conversion timing | On open, into memory only; never rewritten unless `Format = Jsonl` is forced |
| 3 | `DurableStoreOptions` vs the existing constructor | Keep `DurableStore(string? path = null)` verbatim; add static factory `DurableStore.Create(DurableStoreOptions)` |
| 4 | Legacy write-back | No implicit write-back; read-only by default |
| 5 | Default path scope | Per-user for this epic |
| 6 | Per-line checksum | None in v1 |
| 7 | JSONL as the default backend | No — in-memory stays the default |
| 8 | Doc prose ownership | Issue #14 (Docs) owns `DESIGN.md` §3.1 and `COMPARATIVE_ANALYSIS.md` §1; #12 amended to drop the prose |

**Key design findings from reading the code:**

- **`UpdateTask` must be on the provider interface.** `DurableTaskRecord` is mutated in place after creation (`lib/Durable.cs:632-651`, `lib/Durable.cs:703`), and `MemoAsync` persists a memo change with no transaction. A provider that only saw `AddTask` would never learn about those changes and would lose task progress across a reopen.
- **The `UndoLog` moves into the provider.** The provider owns the collections the log protects. `UndoLog.Undo(DurableStore store)` never reads its `store` argument — it only replays recorded inverse actions (`lib/Durable.cs:2130-2138`) — so changing the parameter type to `IDurableStoreProvider` is behaviour-preserving. The `SemaphoreSlim` commit gate stays in the facade.
- **A constructor overload would be ambiguous.** `new DurableStore(null)` would match both `DurableStore(string?)` and `DurableStore(DurableStoreOptions)`. A static factory has no overload-resolution interaction, so the existing constructor stays byte-for-byte source-compatible and `examples/12`, `examples/22`, `examples/30` need no edits.
- **Three providers ship:** `InMemoryStoreProvider`, `JsonlStoreProvider`, `LegacyJsonStoreProvider`, selected by `DurableStoreOptions.Format`. `Auto` sniffs the file at open — a first line with a string `type` property means JSONL, a whole-file JSON object means a legacy `StoreSnapshot`, anything else raises `InvalidDataException`.
- **JSONL `task` records are last-wins per task id**, which is what makes `UpdateTask` survive a reopen.
- **The torn tail is truncated at open**, not at the next commit, so the file is immediately clean and a crash between open and first commit cannot leave the fragment to be misread twice.
- **The default-path directory is created on first write, not on open** — opening a store must not create directories as a side effect.

**Why:** The user delegated the remaining judgement calls and asked to move ahead. Every resolution is grounded in the actual `lib/Durable.cs` implementation rather than in the plan's proposals, and the plan document was updated to record the reasoning.

**How it applies:** The plan's §2, §2.1, §3, §4, §5, §5.1 and §5.2 now carry the resolved contract. Issues #9, #11, #12, #13, #14 and #15 have a "Resolved design decisions" section appended; #12's doc-prose acceptance criterion was amended to point at #14.

### 2026-10-23T17-49-38: Adopt dev integration branch; issue #1 split into 7 dependency-ordered sub-issues #9-#15

**Date:** 2026-10-23
**Author:** lead

**What:** Adopt dev integration branch; issue #1 split into 7 dependency-ordered sub-issues #9-#15

**References:** #1, #9, #10, #11, #12, #13, #14, #15, docs/implementation/ISSUE_1_PLAN.md

**Why:**

#### 2026-10-23T13:32:09.577-04:00: Adopt a `dev` integration branch; issue #1 split into 7 dependency-ordered child issues

**By:** Copilot App (via Copilot)

**What:**

1. **Branch model.** A `dev` branch is introduced as the integration branch. All feature work branches from `origin/dev` and its PRs target `dev`. `main` becomes release-only and receives release tags only after `dev` is promoted forward. `dev` is now the repository default branch. Both `dev` and `main` have branch protection: PR required, no force-push, no deletion.

2. **Issue #1 decomposition.** The epic is split into 7 real GitHub sub-issues linked via the sub-issues API, each with a single accountable owner:

| Issue | Title | Owner |
|-------|-------|-------|
| #9 | #1.1 Define the durable store provider interface | Core Engineer |
| #10 | #1.2 Refactor `DurableStore` into a facade over a backend | Core Engineer |
| #11 | #1.3 Implement the JSONL append-only backend | Core Engineer |
| #12 | #1.4 Add `DurableStoreOptions` and per-user default path resolution | Core Engineer |
| #13 | #1.5 Add a backward-compatible reader for the existing single-file JSON store | Core Engineer |
| #14 | #1.6 Add `examples/31-jsonl-store.cs` and update the README example table | Docs |
| #15 | #1.7 Add xUnit facts for the JSONL backend, path options, and legacy reader | Tester |

The implementation spine is strictly serial: #9 → #10 → #11 → #12 → #13. #14 and #15 consume the spine and start once their dependencies land.

3. **Plan document.** `docs/implementation/ISSUE_1_PLAN.md` holds the ordered task table, the provider interface contract sketch, the JSONL envelope and torn-tail recovery semantics, the per-platform default path rule, the branch workflow, and 8 open questions.

4. **Board.** All 15 issues (#1–#15) are on the "github-copilot-sdk-durable Roadmap" board (project 5, owner `matthewcorven`, project id `PVT_kwHOAajMkc4BobdZ`) with Status `Todo`. Board field/option ids are pinned in `.squad/config/github-board.json`.

**Why:** The user requested planning and status tracking for issue #1, then directed that feature work branch from and merge into `dev` with release tags picked from `dev` for `main` merges. The repo already carried a `git-workflow` skill documenting the three-branch model, so this aligns the actual repository with its own documented convention.

**How it applies:** `.squad/templates/issue-lifecycle.md` and `.squad/templates/worktree-reference.md` now branch from `dev` and target PRs at `dev`. `.squad/team.md` records the branch model and the GitHub issue source. The coding-agent assignment workflow bases work on `dev` when the branch exists, falling back to the repo default branch.

### 2026-10-23T17-42-12: Issue #1 split into 7 dependency-ordered child issues (#9–#15) with a JSONL provider abstraction plan; Core Engineer owns the lib spine, Tester owns the new xUnit facts, Docs owns example 31 + README/docs.

**Date:** 2026-10-23
**Author:** lead

**What:** Issue #1 split into 7 dependency-ordered child issues (#9–#15) with a JSONL provider abstraction plan; Core Engineer owns the lib spine, Tester owns the new xUnit facts, Docs owns example 31 + README/docs.

**References:** #1, #9, #10, #11, #12, #13, #14, #15, docs/implementation/ISSUE_1_PLAN.md

**Why:**

#### Decision

Accept the implementation plan for issue #1 (pluggable durable-store provider abstraction with a path-configurable JSONL backend) and split the epic into seven child issues, linked to the parent as GitHub sub-issues.

#### Context

`DurableStore` (`lib/Durable.cs:490`) is hard-wired to in-process dictionaries plus one atomically-replaced JSON file. `docs/COMPARATIVE_ANALYSIS.md` §1 records that Pi Durable already exposes a pluggable storage SPI including `JsonlStorage`, which is exactly the "jsonl in a specific path" capability this epic adds. The epic body proposed six child issues; triage added a seventh so the new xUnit facts have a single accountable owner and explicit test method names.

#### Accepted plan

Dependency-ordered spine (single owner, Core Engineer): #9 provider interface → #10 facade over a backend → #11 JSONL append-only backend → #12 `DurableStoreOptions` + per-user default path → #13 backward-compatible legacy single-file JSON reader. Two consumer workstreams: #15 xUnit facts (Tester) depends on #11/#12/#13; #14 `examples/31-jsonl-store.cs` + README table + `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose (Docs) depends on #10/#11/#12.

Key constraints accepted:
- The entire existing public surface of `DurableStore` stays source-compatible. `examples/12-recovery.cs:20`, `examples/22-subagent-background.cs:22`, and `examples/30-reload-and-restart.cs:32` compile without source edits. `DurableStore(string? path = null)` is kept verbatim; `DurableStoreOptions` arrives as an additive overload.
- `UndoLog` rollback and the `SemaphoreSlim` commit gate stay in the facade, not in the provider. Durable semantics (fork policies, memo idempotency, rollback) are byte-for-byte equivalent.
- Default path rule confirmed against issue #1 and `docs/DESIGN.md` §3.1: `Environment.SpecialFolder.LocalApplicationData` on Windows; `$XDG_STATE_HOME` when set and non-empty, else `~/.local/state` elsewhere. An explicit `Path` always wins.
- JSONL record format: one JSON object per line, UTF-8, newline-terminated, appended in write-sequence order, envelope `{"v":1,"seq":N,"type":"<kind>","payload":{...}}`. Torn-tail recovery: the final line is discarded when it is not newline-terminated or does not parse as a JSON object; all earlier records survive; the file is truncated back to the last complete record boundary.
- In-memory remains the default backend. JSONL is opt-in.
- Verification gates: `dotnet test tests/Durable.Tests` 4/0 before, 9/0 after #15; `COMPILE_ONLY=1 ./scripts/validate.sh` 31 examples before, 32 after #14. The validator resolves `examples/<arg>.cs`, so it is invoked as `./scripts/validate.sh 00-conversation`, never with a bare number.

#### Rejected / deferred

- A seventh suggested child issue for the new xUnit facts was not folded into the implementation issues; it became #15 with its own owner so the tests are written against the contract rather than retrofitted.
- Per-line checksums for the JSONL format are deferred; a torn tail is the realistic failure mode.
- Per-project or per-workspace path scoping is deferred; the default stays per-user for this epic.

#### Open questions for a human decision

1. Exact record envelope shape (`{v,seq,type,payload}` vs a flatter shape) — permanent once shipped.
2. Legacy `StoreSnapshot` conversion timing: on open (in-memory only, recommended) vs on first commit.
3. Whether the legacy reader ever writes back a converted file (recommended: no, read-only by default).
4. Whether the default path should become per-project/per-workspace in a follow-up.
5. Whether the JSONL backend should ever become the default.
6. Which owner lands the `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose — currently referenced from #12 but proposed for #14.

#### Where the work lives

Plan file: `docs/implementation/ISSUE_1_PLAN.md` (repository root, `docs/implementation/`).
Parent issue: #1. Child issues: #9, #10, #11, #12, #13, #14, #15.
Roadmap board: project 5 (project id `PVT_kwHOAajMkc4BobdZ`), Status field `PVTSSF_lAHOAajMkc4BobdZzhm7oGY`, Todo option `f75ad846`. Issue #1 stays `Todo`; all seven children are `Todo`.

### 2026-10-23T13:32:09.577-04:00: User directive — dev branch as the integration branch

**Date:** 2026-10-23
**Author:** Copilot App (via Copilot)

**What:** Introduce a `dev` branch for this scope (issue #1 epic). All feature work branches from `dev`, PRs target `dev`, and merges land on `dev`. Release tags are picked from `dev` when `dev` is merged forward to `main` (master). This aligns with the repo's existing `.github/skills/git-workflow` three-branch model, where `main` is released/tagged-only and `dev` is the integration branch.

**Why:** User directive — captured for team memory. Feature work should not land directly on the release branch, and release tags must be cut from the integration branch after promotion to `main`.

**How it applies:** `.squad/templates/issue-lifecycle.md` branch/PR commands updated from `main` to `dev`. Issue branches keep the `squad/{issue-number}-{kebab-case-slug}` convention and are created from `origin/dev`.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- Keep history focused on work, decisions focused on direction
### 2026-10-23T21:15:00-04:00: Core Engineer — #12 loud ephemeral tier API implemented (PR #24, draft)

**Date:** 2026-10-23
**Author:** core-engineer

**What:** Implemented items 1–3 of the Lead's "loud ephemeral tier" change set in `lib/Durable.cs`: `DurableStore.CreateEphemeral()`, `DurableStore.IsPersistent` (backed by a new `IDurableStoreProvider.IsPersistent` member), and `Create(new DurableStoreOptions())` resolving to the persistent tier at the per-user default path. The `Auto` arm was also amended so a non-existent file writes JSONL rather than the legacy snapshot.

**References:** #12, #13, #14, #15, PR #24, branch `squad/12-ephemeral-tier-api`, commits `a87ccdb` + `5601081`, `lib/Durable.cs`

**Why:**

1. **The `Auto`→JSONL change was implemented, not deferred.** The Lead's recommendation #6 was small and safe to land here, and it was a prerequisite for item 3: without it, `Create(new DurableStoreOptions())` would have written the legacy single-file format to the default path, which is the opposite of the intent. The discriminator is the JSONL envelope's top-level `type` property, which the legacy snapshot never carries — the same rule already settled in `docs/implementation/ISSUE_1_PLAN.md` §2.1. A file that does not exist, or exists but is empty, is written as JSONL.

2. **A bare "file exists ⇒ legacy" rule was rejected on evidence.** A scratch diagnostic showed the legacy provider reading a JSONL file opens it as an *empty* store rather than failing — silent data loss. So the `Auto` arm must never route a JSONL file to the legacy provider, which is why the discriminator is required rather than optional.

3. **The remaining legacy-reader gap is left to #13 and is stated precisely.** A file that is neither format — valid JSON but not a `StoreSnapshot` — currently opens as an empty legacy store instead of raising an actionable error. That distinction is #13's scope and is recorded in the PR body.

4. **Scope was respected.** `examples/` (#14) and `tests/` (#15) are untouched. `new DurableStore()` behaviour is unchanged and carries no `[Obsolete]` attribute. `DurableStoreFormat` members are unchanged.

**How it applies:** PR #24 is open as a draft against `dev`, based on `origin/squad/12-default-path` (`1173c75`) because #12's PR #23 is still open and unmerged. Gates green: build 0 warnings / 0 errors, tests 4 passed / 0 failed, all 31 examples compile. Verified end-to-end in a scratch console project against the real `lib/Durable.cs` with a hermetic `XDG_STATE_HOME` — the default path's first line is `{"v":1,"seq":1,"type":"conversation","payload":{"id":"c1"}}`, a JSONL envelope, not a legacy snapshot.

### 2026-10-29T05:52:00-04:00: Epic #3 (deterministic runtime) decomposed into 43→44→45→{46→48, 47}

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** Epic #3 is decomposed into a single startable chain: #43 → #44 → #45 → {#46 → #48, #47}. #43 merges the "runtime process" and "response script" slices because neither ships alone. #48 owns the docs plus the determinism check. #47 (polyglot attach verification) is a separate child of #45 so non-.NET toolchains do not gate the .NET chain.

**References:** #3, #43, #44, #45, #46, #47, #48

**Why:** Language-neutrality must not block the primary chain. Folding the polyglot attach verification into the .NET sequence would make every .NET milestone wait on toolchains that are not part of the deliverable, so it hangs off #45 as a sibling of #46 instead.

**How it applies:** Exactly one startable child at a time; the ordering is enforced with native GitHub `blocked by` links, not prose.

### 2026-10-29T05:52:00-04:00: Epic #2 (EF Core providers) decomposed into 49→50→51→{52, 53, 54}

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** Epic #2 is decomposed into #49 → #50 → #51 → {#52, #53, #54}.

**References:** #2, #49, #50, #51, #52, #53, #54

**Why:** Exactly one startable child per epic at any time keeps the board unambiguous and prevents parallel agents from colliding on the same provider surface.

**How it applies:** The chain is enforced with native GitHub `blocked by` links and verified by GraphQL read-back, not by prose in the issue bodies.

### 2026-10-29T05:52:00-04:00: EF Core example renumbered from 32 to 37

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** The EF Core example is numbered 37, not 32. `examples/32-jsonl-store.cs` already exists; 34 and 35 are reserved by #17 and #18, and 36 by #41.

**References:** #2, #17, #18, #41, `examples/32-jsonl-store.cs`

**Why:** Example numbers are a shared namespace across epics; assigning 32 would have collided with an existing file.

**How it applies:** Future example numbering must check `examples/` before assigning a number.

### 2026-10-29T05:52:00-04:00: `DurableStore.Create` has no external-provider seam; #50 adds one additively

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** `DurableStore.Create` currently has no seam for an external provider. #50 must add one additively. The settled surface — `DurableStore(string?)`, `Create`, `CreateEphemeral`, `IsPersistent` — is explicitly out of scope for change.

**References:** #2, #50, `lib/Durable.cs`

**Why:** The EF Core provider cannot be injected without a seam, but the existing factory surface is already settled and shipped; changing it would be a breaking change for no gain.

**How it applies:** #50 adds new surface only. Any change to the four settled members is out of scope and must be raised as a separate decision.

### 2026-10-29T05:52:00-04:00: Delivery-gap lesson — audit artifacts, do not trust completion reports

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** A dispatched agent can report "done" while having produced only a local commit: no pushed branch, no PR. In round 1 the #18 agent completed and re-ran its example successfully but stopped before committing the final edit, pushing, or opening a PR.

**References:** #18, Ralph round 1

**Why:** The completion report described the work the agent had done, not the artifacts it had delivered. The gap was only visible by inspecting the remote.

**How it applies:** The coordinator must audit artifacts — commit SHA, remote branch, PR number, CI status — rather than trust an agent's completion report. Every implementation spawn prompt must explicitly instruct the agent to push the branch and open the PR.

### 2026-10-29T05:52:00-04:00: PR review evidence standard — CI green is necessary but not sufficient

**Date:** 2026-10-29
**Author:** lead (Ralph rounds 1–2)

**What:** GitHub CI `build=SUCCESS` is necessary but not sufficient. Before declaring a PR ready for a human to merge, the coordinator verifies locally in the PR's worktree: `dotnet build` of the changed projects, `dotnet test tests/Durable.Tests` (and any new test project the PR added), and `COMPILE_ONLY=1 ./scripts/validate.sh <example>` for examples the PR touched.

**References:** Ralph rounds 1–2, `tests/Durable.Tests`, `scripts/validate.sh`

**Why:** CI proves the build compiles; it does not prove the changed projects build in isolation, that the test suite passes, or that touched examples still compile.

**How it applies:** Applies to every PR before it is declared ready for a human to merge.
