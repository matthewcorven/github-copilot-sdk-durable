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

### 2026-10-29T06:05:00-04:00: PR #56 (issue #35) approved with eight carry-forward findings

**Date:** 2026-10-29
**Author:** lead (design review of PR #56)

**What:** PR #56 (issue #35, "7.1 Finalize approved public signatures") was reviewed and the verdict is APPROVE WITH COMMENTS. The review was posted via `gh pr review 56 --comment`, not `--approve`, because a human owns merge approval. There is no blocking issue and no public signature must change before #36. The lead verified the actual declarations in `lib/Durable.cs` (3996-4680), `lib/Durable.DependencyInjection.cs`, and `lib/Copilot.Durable.csproj` against `docs/implementation/ISSUE_7_API_SIGNATURES.md` — not the document alone. Confirmed passing: `Build()` returns `DurableCopilotClient` and is a bare throw with no I/O; `DurableConversation` is reused everywhere; the one-runtime requirement is documented in §6 and in the type remarks; the escape hatch is complete in three places; DI lives in a package-owned namespace with both overloads; naming is consistent with `Durable.cs`. #36 may start the moment a human merges PR #56.

The eight non-blocking findings, each with its owning issue:

1. The `Registry` getter (`lib/Durable.cs:4341`) allocates a fresh `ExtensionRegistry` on every read when none was supplied. This breaks reference identity and hides extensions installed through one reference from another. It must cache a single instance. Owner: #36.
2. `Store` and `NativeClient` (`lib/Durable.cs:4333` and `:4357`) read `_options` directly. Resources the durable layer itself creates in `InitializeAsync` have no home in that model. The §8 entry for 7.2 must state whether these are written back into options or held as runtime state. Owner: #36.
3. §7 "SDK configuration" of `docs/implementation/ISSUE_7_API_SIGNATURES.md` (line ~379) has no way in: no builder or constructor overload accepts an SDK configuration object, and `BuildSessionConfig` merges nothing. This must be settled before #37. Owner: #37.
4. `ForkAsync(parent, entryId)` (`lib/Durable.cs:4439`) never states whether `entryId` is a durable entry id or an SDK event id. #38 requires mapping to `ToEventId`. Guessing wrong is a public API break. This must be settled before #38. Owner: #38.
5. `ResumeTasksAsync` awaits its outcomes, but `DurableHarness.ResumeAsync` (`lib/Durable.cs:3590`) is fire-and-forget. §6 forbids the public and harness paths diverging. #7.5 owns reconciling both. Owner: #39.
6. `GetSessionConfig` is exactly equivalent to `BuildSessionConfig`; the equivalence is unnamed and undocumented. Owner: #36.
7. The bare `AddDurableCopilotClient()` DI overload requires a container-registered `DurableStore`. The lead believes this is deliberate but it is stated nowhere; both overloads must document it. Owner: #36.
8. State explicitly that #37's binding/admission/checkpoint surface is internal, so #37 adds no public API. Owner: #37.

**References:** #35, #36, #37, #38, #39, #40, #41, PR #56, `lib/Durable.cs`, `lib/Durable.DependencyInjection.cs`, `lib/Copilot.Durable.csproj`, `docs/implementation/ISSUE_7_API_SIGNATURES.md`

**Why:** These are design-review findings against the document that gates #36-#41. If they are not recorded, the next agents will re-discover them or, worse, encode a guess into a phase-2 issue where changing it becomes a public break.

**How it applies:** Every spawn prompt for #36-#41 must carry the findings that name that issue. Findings 3 and 4 are documentation edits to `docs/implementation/ISSUE_7_API_SIGNATURES.md` and fold into #37 and #38 respectively.

### 2026-10-24T03-13-55: Project board + native blocked-by links are authoritative for issue status/dependencies; github-issues skill declared in agent context files
**By:** coordinator
**What:** Project board + native blocked-by links are authoritative for issue status/dependencies; github-issues skill declared in agent context files
**References:** lead, ralph, squad.agent.md, issue-lifecycle.md, routing.md, team.md
**Why:** Decision: GitHub Project board and native blocked-by links are the authoritative sources for issue status and dependencies.

1. **Status authority:** The GitHub Project board "github-copilot-sdk-durable Roadmap" (project #5, PVT_kwHOAajMkc4BobdZ) Status field (Todo / In Progress / Done) is the authoritative source for issue status. Labels, issue bodies, and inference are secondary.

2. **Dependency authority:** Native GitHub `blocked by` / `blocking` issue links (GraphQL `issue.blockedBy` / `issue.blocking`) are the authoritative source for dependencies. The `## Dependencies` / "Depends on" sections in issue bodies are informational only and may be stale; on conflict the native link wins and the body should be corrected.

3. **Skill declaration:** The `github-issues` skill (corven-agent-tools) must be invoked for any issue/board work — agents should not wait for the user to name it. If unavailable, fall back to `gh` CLI / GitHub MCP tools and note the fallback.

Work completed:
- Created 26 native `blocked by` links across issues #1-#19 (verified via GraphQL `issue.blockedBy`), replacing the informational body-only dependency model.
- Verified project #5 board statuses: #9-#12 Done, #13 In Progress, all others Todo.
- Declared the `github-issues` skill and the authoritative-source rules in: `.github/agents/squad.agent.md` (GitHub Issues Mode section), `.squad/templates/issue-lifecycle.md`, `.squad/routing.md` (Issue Routing), `.squad/team.md` (Issue Source), `.squad/agents/lead/charter.md`, `.squad/agents/ralph/charter.md`.

Note: the `github-issues` skill itself is not registered in this environment (not in the available skills list, not under `.github/skills/` or `.squad/skills/`); the declarations instruct agents to invoke it and to fall back to gh CLI/MCP when it is absent.

### 2026-10-29T09:22:51-04:00: #45 vocabulary doc edits are in scope and were accepted
**By:** coordinator (verification of PR #63)
**What:** During verification of PR #63 (#45), the branch carried three documentation edits fixing the last 4 pre-existing banned-vocabulary hits (in `docs/implementation/ISSUE_1_HANDOFF.md` around lines 219 and 242, replacing "mock runtime" prose). These had previously been flagged as belonging to #48. I judged them in-scope and kept them because they were correct, harmless, and shrank #48's remaining scope. Consequence: **the whole source tree (`examples/`, `lib/`, `docs/`, `README.md`, `tools/`, `scripts/`) now greps clean for `mock|fake|stub|simulat`.** #48's vocabulary obligation is therefore already satisfied and its scope should be re-read before it is dispatched.
**References:** #45, #48, PR #63, `docs/implementation/ISSUE_1_HANDOFF.md`, `scripts/validate.sh`
**Why:** A future agent reading #48 would otherwise re-do work already merged, or worse, "restore" the old prose.
**How it applies:** Any vocabulary check for #48 starts from a clean tree; only new violations matter.

### 2026-10-29T09:22:51-04:00: #50's provider seam was delivered additively, as the Lead required
**By:** coordinator (verification of PR #61)
**What:** The Lead flagged-not-decided (during #2 decomposition) that `DurableStore.Create` had no external-provider seam, and required #50 to add one **without touching the settled public surface**. Verified merged: `DurableStoreOptions.Provider` (`Func<DurableStoreOptions, IDurableStoreProvider>?`, `init`) taking precedence over `ProviderName` then `Format`; `DurableStoreOptions.ProviderName` (`string?`, `init`); and public statics `DurableStore.RegisterProvider` / `UnregisterProvider` / `IsProviderRegistered` over an `OrdinalIgnoreCase` `ConcurrentDictionary`. The settled `DurableStore(string?)` / `Create` / `CreateEphemeral` / `IsPersistent` surface was not changed. Registration is process-wide; the factory is called once per store; the provider is owned and disposed by that store.
**References:** #50, #51, #2, PR #61, `lib/Durable.cs`, `lib/Durable.EfCore/EfCoreStoreProvider.cs`
**Why:** #51, #52, #53 and #54 all build on this seam; if they don't know the exact precedence and ownership rules they will re-invent them or break them.
**How it applies:** Provider work selects through `Provider` > `ProviderName` > `Format`; never mutate the settled store-creation surface; the store owns the provider lifetime.

### 2026-10-29T09:22:51-04:00: Regression — the #36 refactor silently deleted #44's `ResolveRuntimePath` feature; a clean build did not catch it
**By:** coordinator (rebase of PR #62)
**What:** PR #62 (#36) was cut before #44 merged, so rebasing it onto `dev` conflicted in `lib/Durable.cs`. The refactor commit had **silently dropped #44's entire `ResolveRuntimePath` implementation** (`git show <refactor-sha>:lib/Durable.cs | grep ResolveRuntimePath` returned nothing). My first conflict resolution "kept HEAD's block" but stranded it inside `DurableRuntime` — a dead duplicate — while the real `DurableHarness.OpenAsync` still constructed `new CopilotClient()` with no `ResolveRuntimePath` call. That built with **0 errors** and shipped none of #44's feature. The only signal was 3 failing tests asserting "No exception was thrown": the missing-path `FileNotFoundException` and the two environment-precedence cases.

Fix: removed the dead duplicate block, ported #44's logic into the real `DurableHarness.OpenAsync`, restored `internal static string? DurableHarness.ResolveRuntimePath(DurableHarnessOptions)`, reverted the 7 test references. Result: build 0/0, **65/65 passed**.

**Lesson (binding on all future conflict resolutions):** after resolving a conflict by keeping a block, grep for the method name, verify the **enclosing class** is the one on the live call path, and confirm the live path actually invokes it. **A clean build does not prove a feature survived a rebase.** Run the test suite — it is the only signal.
**References:** #36, #44, PR #60, PR #62, `lib/Durable.cs`, `tests/Durable.Tests/RuntimeConnectionTests.cs`
**Why:** This cost a full diagnostic cycle and nearly shipped a silent feature regression on the exact feature #44 had just added.
**How it applies:** Every future rebase or conflict resolution in the #36–#41 chain (all of which touch `lib/Durable.cs`) must verify that no merged feature vanished. Treat a deleted-method grep as a mandatory rebase step, not an optional one.

### 2026-10-30T03:55:00-04:00: The internal-document set must be derived, never hardcoded — #82's allowlist broke #80
**By:** coordinator (fixing PR #80's conflict with #82)
**What:** #82 fixed the fork-transcript leak by filtering bookkeeping entries, but implemented `IsInternalDocument(kind)` as a **hardcoded two-item allowlist** (`ForkDocument.Kind`, `AgentDocument.Kind`). PR #80 then added three more internal documents — `SessionBindingDocument` (`durable.session-binding`), `AdmissionDocument` (`durable.admission`), `CheckpointDocument` (`durable.checkpoint`) — registered in the `DurableRuntime` constructor. Their anchor entries were not recognised as bookkeeping and leaked into the transcript, so `Fork_TranscriptHoldsTheInheritedPrefixAndItsOwnEntriesOnly` failed with `Expected ["e24","e8"] / Actual ["e24","e21","e8","e5"]` and `Fork_TranscriptIsUnchangedAfterReopen` failed with `Expected 2 / Actual 5`. Fixed by replacing the literal with a lazily-built `HashSet<string>` property `InternalDocumentKinds` derived from the runtime's five document definitions, plus a `_internalDocumentKinds` backing field.
**References:** #37, #81, PR #80, PR #82, `lib/Durable.cs` (`DurableRuntime` constructor, `BookkeepingEntries`, `IsInternalDocument`, `InternalDocumentKinds`)
**Why:** A hardcoded allowlist silently rots the moment a new internal document is registered, and the failure surfaces as a confusing transcript-count assertion in an unrelated PR.
**How it applies:** **Any new internal document must be added to `InternalDocumentKinds`.** Better: derive the set from the registration point so it cannot be forgotten. When a fork/transcript count assertion fails after adding a document, suspect a missing internal-document kind before suspecting the fork logic.

### 2026-10-30T03:55:00-04:00: Epic #3 is not satisfiable as written — 2 of 8 criteria pass; do not close it
**By:** coordinator (empirical re-evaluation after #78 and #39 merged)
**What:** Epic #3's eight acceptance criteria were checked individually against `dev` at `7e19f03`:
- **C1 FAIL** — "a documented command runs all 31 examples" — `./scripts/validate.sh --deterministic` runs **16 of 37**; the repo has 37 examples, not 31. The bar is stale.
- **C2 PASS** — `grep -rniE 'mock|fake|stub|simulat' examples/` returns nothing.
- **C3 FAIL** — "every example prints `effective model: <id>`" — no such prefix exists anywhere in `examples/`.
- **C4 FAIL** — depends on C3.
- **C5 FAIL** — no run-twice-and-diff-stdout determinism check in `scripts/validate.sh`.
- **C6 PASS** — `docs/EXAMPLES_AUDIT.md:52` now says "The Recorded Runtime Is a Separate Process, Not an In-Example Stand-In" and states 16 of 37 with the synthesised marker.
- **C7 FAIL** — `grep -rn "policy_resolved" --include=*.cs lib/ tools/ tests/` returns **nothing**.
- **C8 FAIL** — depends on C7.
**References:** #3, #78, #39, PR #83, PR #84, `scripts/validate.sh`, `docs/EXAMPLES_AUDIT.md`, `docs/DESIGN.md` §6.1
**Why:** Closing it would claim a determinism guarantee the repo does not provide; leaving it untouched means the next agent re-derives the same eight-way audit.
**How it applies:** Epic #3 needs a decision before any agent works it: either re-scope C1 to the real 16-of-37 bar, or split the genuinely unimplemented parts (effective-model reporting, `policy_resolved` decision event, byte-identical determinism check) into follow-up issues. **Do not close it as-is.**

### 2026-10-30T03:55:00-04:00: Six agent-surfaced follow-ups are known and unfiled
**By:** coordinator (accumulated from PR #83 and PR #84 verification)
**What:** Two PRs surfaced six real gaps that were deliberately left out of scope. None has an issue yet.
From **PR #83** (#78):
1. `sessions.fork` is missing from `ReplayHost.SupportedMethods` — adding it would unlock `02-forks` and `08-context` for the deterministic suite.
2. `07-harness-conversations` needs a fixture recording a model with a supported reasoning effort.
3. `30-reload-and-restart` takes 124s and times out.
4. The SDK captures the runtime's stderr and discards it on success, so a synthesised-session warning can be lost; `REPLAY_MARKER_PATH` exists as the workaround sink.
From **PR #84** (#39):
5. `BuildSandboxConfig` silently drops a `Network.Proxy` with no `Url`. `examples/34` asserts that drop, so changing it belongs with #40.
6. `DurableClientBuilder` itself is not container-registered (only `DurableCopilotClient` is).
**References:** #78, #39, #40, PR #83, PR #84, `tools/ReplayRuntime/ReplayHost.cs`, `lib/Durable.cs` (`BuildSandboxConfig`, `DurableClientBuilder`), `examples/34`
**Why:** These were found by agents doing adjacent work and will be lost when those sessions end; re-discovering them costs a full investigation each.
**How it applies:** File them as issues when the board is otherwise clear, or fold #5 into #40. Do not silently "fix" #5 without updating `examples/34`.

### 2026-11-01T00:43:27-04:00: Section numbers are pre-assigned by the coordinator before concurrent doc-touching PRs are dispatched
**By:** coordinator (Ralph round 6, after #110 and #111 both went CONFLICTING)
**What:** When more than one in-flight PR adds a section to the same document, the coordinator assigns each PR its section number **before** dispatch, and the assignment is recorded in the dispatch prompt. This round #110 (TypeScript port) and #111 (Python port) were both dispatched against `origin/dev` @ `01aeded` and both independently claimed `docs/DESIGN.md` §3.9 and `docs/COMPARATIVE_ANALYSIS.md` §7. PR #112 then landed the runtime-host section as `DESIGN.md` §3.9, and both port PRs immediately became CONFLICTING on the same file and the same heading. The rebases in flight renumber #110 to `DESIGN.md` §3.10 / `COMPARATIVE_ANALYSIS.md` §8 and #111 to `DESIGN.md` §3.11 / `COMPARATIVE_ANALYSIS.md` §7 (retained, with the §3.2 clarification kept). #95 and #97 were dispatched afterwards with §3.12 and §3.13 pre-assigned.
**References:** #100, #101, #94, #95, #97, PR #110, PR #111, PR #112, `docs/DESIGN.md`, `docs/COMPARATIVE_ANALYSIS.md`
**Why:** Two agents working from the same base cannot see each other's section numbers, so "append the next section" is a guaranteed conflict whenever the doc is touched concurrently. The conflict is cheap to prevent and expensive to resolve — it costs a rebase of a large port PR plus a renumber of every cross-reference in the body.
**How it applies:** Before dispatching two or more PRs that add sections to a shared doc, assign the numbers in the dispatch prompt and state the base commit. A later PR that lands first does not get to renumber the others; the coordinator's assignment is authoritative. When a conflict does appear, renumber the *later* PR, never the merged one.

### 2026-11-01T00:43:27-04:00: `RunResult` is the single serialized turn-result shape for the runtime host — transports must not define their own
**By:** coordinator (established by #94 / PR #112)
**What:** `src/Durable.Runtime/RunResult.cs` defines the one record every transport serializes for a single agent occurrence. The fields are `agent`, `conversationId`, `admissionId`, `checkpointId`, `effectiveModel` (`model` + `source`, or `null`), `modelReported`, `containment`, `containmentReason`, `outcome`, `error`. Nulls are **written rather than omitted** (`DefaultIgnoreCondition = JsonIgnoreCondition.Never`), so `effectiveModel: null` is distinguishable from an absent property — a consumer must be able to tell "the runtime reported no model" from "this build does not report models". `effectiveModel` is the runtime's own report, never the requested model and never `DurableHarness.DefaultModel`; when nothing was reported it is `null` and `modelReported` is `false`.
**References:** #94, #95, #96, #97, PR #112, `src/Durable.Runtime/RunResult.cs`, `src/Durable.Runtime/Program.cs`
**Why:** #95 (HTTP), #96 (JSON-lines RPC), and #97 (persistent scheduler) all serialize the same occurrence. If each defines its own shape, the contract forks four ways and every consumer has to special-case the transport. The null-vs-absent distinction is load-bearing for Hazard B: collapsing "not reported" into "absent" reintroduces the silent fallback to the requested model that #93 forbids.
**How it applies:** #95, #96, and #97 serialize `RunResult`; they do not define their own result record. A transport that needs extra fields adds them to `RunResult` (and to the tests) rather than wrapping it in a transport-specific envelope. Any new field must keep the write-nulls rule.

### 2026-11-01T00:43:27-04:00: `dev` has branch protection with zero required approvals — CI `build` is the only gate
**By:** coordinator (verified against the live repo this round)
**What:** `dev` is protected (`required_pull_request_reviews.required_approving_review_count = 0`, `enforce_admins = false`, force-pushes and deletions disabled, no rulesets). `gh pr merge <n> --squash` therefore works directly with no approval step. The only gate is the `build` check from the "Build and pack" workflow. All three PRs merged or opened this round passed it: #112 merged, #110 and #111 green.
**References:** #110, #111, #112, `.github/workflows/`, `dev`
**Why:** The previous handoff implied a review gate that does not exist, which made the coordinator wait for approvals that were never required. Knowing the real gate changes the merge loop from "request review, wait" to "wait for `build`, squash-merge".
**How it applies:** Merge with `gh pr merge <n> --squash --delete-branch` once `build` is green. Do not wait for an approving review. Do not force-push or delete `dev`. Squash-merge remains the convention so `dev` keeps linear history.

### 2026-11-01T00:43:27-04:00: The runtime host is the reference implementation for D1, D2, Hazard A, and Hazard B — ownership is claimed before `OpenAsync`
**By:** coordinator (established by #94 / PR #112)
**What:** `src/Durable.Runtime/Program.cs` is now the worked example every later #8.x child follows. The load-bearing detail is ordering: `Program.ClaimOwnership(store, options.StorePath)` runs at line 106 (and again at 205 for the second entry path) **before** `DurableHarness.OpenAsync` at line 119. This is deliberate and documented in the `ClaimOwnership` remarks: `OpenAsync` starts the SDK client before constructing the harness, so a host that validated ownership *after* `OpenAsync` would have already spawned a runtime process and would orphan it when the claim is refused. The host also carries the D2 validator (`AgentDefinitionValidator.cs`), the Hazard A classifier (`Containment.cs`), and the Hazard B reporting path (`RunResult.EffectiveModel` + `ModelReported`).
**References:** #93, #94, #95, #96, #97, #98, PR #112, `src/Durable.Runtime/Program.cs`, `src/Durable.Runtime/Containment.cs`, `src/Durable.Runtime/AgentDefinitionValidator.cs`, `lib/Durable.cs` (`DurableHarness.OpenAsync`)
**Why:** D1's refusal path is only correct if it runs before any side effect. Getting the order wrong does not fail a test — it leaks a process, which is invisible in CI and shows up as a stuck host in production. Recording the reference implementation means later transports copy a correct order instead of re-deriving it.
**How it applies:** #95, #96, #97, and #98 preserve the claim-before-open ordering. A new entry point claims ownership first, then opens the harness. Do not "simplify" by moving the claim inside `OpenAsync` — that reintroduces the orphan. Hazard A classification and Hazard B reporting are likewise taken from the host, not reimplemented per transport.

### 2026-11-02T21:10:00Z: Stale-branch conflicts resolve toward `dev`; the merged side never renumbers
**By:** coordinator (Ralph round 7, after #123 merged and four PRs went CONFLICTING)
**What:** After PR #123 squash-merged to `dev` as `d34d84d`, all four remaining polyglot PRs (#119/#102, #122/#103, #125/#104, #126/#105) became CONFLICTING. The conflicts are on `tests/ReplayRuntime.Tests/JsonRpcFramingTests.cs` (119, 122, 125), `tests/Durable.Tests/LateJoinExampleTests.cs` (122, 126), and `docs/COMPARATIVE_ANALYSIS.md` (119, 122). None is a design disagreement. `dev` moved ahead with #124 (ReplayRuntime frame serialization), #127 (C# transcript/permission fork boundaries), #128 (C# late join async SDK tool), and #123 (the SDK-bound ports); each branch carries a pre-merge copy of a file those commits rewrote. The ruling: **`dev` wins** for all three files, and a branch re-applies only its genuinely range-specific additions on top. This extends the round-6 section-number rule — the merged side is never renumbered, so `dev`'s `COMPARATIVE_ANALYSIS.md` §8.3 stays §8.3 and a PR needing a further subsection takes §8.4.
**References:** #102, #103, #104, #105, PR #119, PR #122, PR #123, PR #125, PR #126, `tests/ReplayRuntime.Tests/JsonRpcFramingTests.cs`, `tests/Durable.Tests/LateJoinExampleTests.cs`, `docs/COMPARATIVE_ANALYSIS.md`
**Why:** A large port PR branched weeks ago will always conflict with integration work that landed since. Treating every conflict as a judgment call invites an agent to "reconcile" two versions of a test file and silently drop the newer coverage — which is exactly what #124's `FlushOffsets` rewrite and #128's late-join rewrite added. Naming the winner up front makes the resolution mechanical and auditable.
**How it applies:** Before dispatching a rebase, run `git merge-tree --write-tree --name-only origin/dev origin/<branch>` to enumerate the real conflict surface, then state per file which side wins and why. Default to `dev` for test files and for any doc section a merged PR introduced. A branch may only add, never renumber or replace, what `dev` already holds.

### 2026-11-02T21:10:00Z: The deterministic gate is narrower than "five green" implies — it does not run the parity mutation suite
**By:** coordinator (Ralph round 7, verifying Supplement 002's claims)
**What:** `scripts/validate.sh --deterministic` runs 17 of the 38 examples, sets `DURABLE_RUNTIME_PATH` itself, and applies only the `ReplayToolDispatchTests` filter. It does **not** run `tests/parity/test_mutations.py`. A report of "all five deterministic gates green" is therefore compatible with a real failure in the parity mutation suite, and the two claims are not in tension — they measure different things. Separately, Supplement 002's specific claim that `test_mutations.py` holds a stale TS abort anchor (because `lib/ts/durable.ts` was refactored to `abortCascadeAsync`) does **not** reproduce at the PR126 head: all eight mutation anchors resolve to exactly one occurrence, `abortTaskAsync` is still defined at `lib/ts/durable.ts:2277`, and no `abortCascadeAsync` exists in the file.
**References:** `scripts/validate.sh`, `tests/parity/test_mutations.py`, `lib/ts/durable.ts`, PR #126, #105
**Why:** "Green" is only meaningful relative to what the gate actually executes. A gate that covers 17 of 38 examples and one test filter will report success while an entire suite is red, and a handoff that says "all gates green" will be read as "everything passes" unless the coverage boundary is stated. Equally, a handoff's specific defect claim must be re-verified against the tree before an agent is dispatched to fix it — dispatching a fix for a non-existent defect burns a full agent cycle and risks "fixing" working code.
**How it applies:** When reporting gate status, name the command, the example count, and the test filter — never just "green". When a handoff asserts a specific broken anchor or line, verify the anchor count in the actual tree first; if it does not reproduce, report that instead of dispatching a fix. Widening `validate.sh` to cover the parity suite is a real gap and belongs in its own issue, not a silent edit.

### 2026-11-03T04:15:00Z: Rebasing a branch cut before a squash-merge requires reset + selective cherry-pick, not `rebase`
**By:** Polyglot Engineer (Ralph round 7, rebasing #119/#122/#125/#126 onto `d34d84d`)
**What:** All four polyglot branches were cut from `915e13a` and carry the full shared SDK history that `d34d84d` squash-merged. A plain `git rebase origin/dev` replays all of it and conflicts on every `lib/` file (PR #119: 28 commits, 7 conflicting files). `git rebase --onto origin/dev 915e13a <head> -X ours` also fails, because `-X ours` only resolves *conflicting* hunks in `dev`'s favour — non-conflicting branch hunks still land, and PR #119 produced 4544 spurious insertions across `lib/`. The working procedure is `git reset --hard origin/dev` followed by selective `git cherry-pick` of only the commits whose file lists are disjoint from the merged payload, then verifying tree-hash equality against `origin/dev` for every top-level directory the branch should not touch (`lib/ts`, `lib/py`, `docs`, `tools`, `src`).
**References:** #102, #103, #104, #105, PR #119, PR #122, PR #125, PR #126, `d34d84d`, `915e13a`
**Why:** A squash-merge destroys the commit identity that `rebase` relies on to detect already-applied work. Every commit in the branch looks new to the rebase machinery, so it replays the entire shared history and the conflict count explodes far beyond the branch's real payload. `-X ours` looks like the fix but is worse: it silently keeps non-conflicting branch hunks, so the branch appears to rebase cleanly while carrying thousands of lines of reverted integration work.
**How it applies:** When a branch predates a squash-merge of shared history, do not rebase. Reset to `origin/dev` and cherry-pick only the branch's own payload commits. Inspect the full `git show --name-only` list for each candidate commit — a commit that touches both a no-op file and a genuine file must still be cherry-picked, so never filter on a single path. After the cherry-picks, prove correctness with `git diff --stat origin/dev origin/<branch> -- <dirs the branch must not touch>`; it must be empty. Note also that `git cherry-pick --continue` after resolving to identical content prints "nothing to commit" and leaves the sequencer in place — follow it with `--skip`.

### 2026-11-03T04:15:00Z: `read_file` handlers must use `resolve`, never `join` — `join` prepends the working directory to absolute paths
**By:** coordinator (Ralph round 7, caught by #125's guard test before #126 merged)
**What:** PR #126 changed `examples/ts/26-plan-mode.ts` and `examples/ts/27-reviewer.ts` from `resolve(base, path)` to `join(base, path)`. `join('/a', '/b/c')` returns `/a/b/c`, so an **absolute** path argument gets the working directory prepended and the file is not found; `resolve` returns `/b/c` correctly. #126 shipped this change with **no test of its own** asserting `join` — it was unguarded. PR #125's `tests/parity/advanced-read-tools.test.mjs` is the correct guard: it extracts the real handler via `stripTypeScriptTypes` + `runInNewContext` and asserts relative reads, absolute reads without duplicating the working directory, missing-file errors, and invalid-argument rejection, plus a deliberate `resolve`→`join` mutation that must fail. Fixed in `98af351` before #126 merged.
**References:** PR #125, PR #126, `98af351`, `examples/ts/26-plan-mode.ts`, `examples/ts/27-reviewer.ts`, `tests/parity/advanced-read-tools.test.mjs`
**Why:** The two functions differ only on absolute inputs, so every relative-path test passes either way. A refactor from `resolve` to `join` therefore looks safe, passes the existing suite, and breaks only the absolute-path case — which is exactly the case a tool handler receives when a model passes a full path. The Python ports were already correct because `Path(directory) / params.path` discards the left operand when the right is absolute; only the TS ports were exposed.
**How it applies:** Any handler that joins a caller-supplied path onto a base directory uses `resolve`, not `join`. When reviewing a port or refactor that touches path construction, check the absolute-path case explicitly — a green suite is not evidence here. Keep `tests/parity/advanced-read-tools.test.mjs` as the guard; it is the only test that fails when this regresses.

### 2026-11-03: A criterion that contradicts an enforced invariant is resolved by documenting the exclusion, never by weakening the invariant

**By:** coordinator (Ralph round 9)
**What:** Epic #8 criterion 8 asks for "a host-started agent reports the resolved backend and platform on startup". The platform half cannot be satisfied: `tests/Durable.Tests/RuntimeHostPortabilityTests.cs:80` `CorePath_HasNoPlatformSpecificApis()` asserts that no `src/Durable.Runtime/*.cs` except `HostShutdown.cs` matches `\b(OperatingSystem|OSPlatform|RuntimeInformation|PosixSignal|PosixSignalRegistration|DllImport|LibraryImport|Registry|WindowsIdentity)\b|Environment\s*\.\s*(OSVersion|SpecialFolder)|[A-Za-z]:\\`. That test *is* the enforcement of criterion 6 (cross-platform core path). Adding a platform startup line would break criterion 6 to satisfy criterion 8.
**Why:** When two acceptance criteria in the same epic are mutually exclusive, the resolution is to satisfy the one that is enforced by a test and record the other as a deliberate, documented exclusion with its rationale. Weakening a portability invariant to satisfy a reporting nicety inverts the priority: the invariant protects the product, the report is cosmetic. The host's real answer to "what bounds this agent's authority" is the containment report — `containment: {Sandboxed|PermissionGated|None} for agent '{name}': {reason}` (`Program.cs:1268-1272`) — which is a stronger statement than "what OS is this". The "resolved backend" half *is* satisfiable and is being implemented.

### 2026-11-03: Reporting the resolved store backend needs one internal accessor, not new public API

**By:** coordinator (Ralph round 9)
**What:** `DurableStore` holds `private readonly DurableStoreFormat _format` (`lib/Durable.cs:1746`) with no accessor, and `IDurableStoreProvider` exposes no format or name property. But `internal IDurableStoreProvider Provider => _provider;` already exists at `:2336`, and `lib/Copilot.Durable.csproj:42` grants `InternalsVisibleTo("Durable.Runtime")`. Adding `internal DurableStoreFormat Format => _format;` beside it is sufficient for the host to report the resolved format, and changes no public surface. `DurableStoreOptions.Format` is already public (line 734, default `Auto`) — the *requested* format is public, only the *resolved* one is not.
**Why:** The obvious-looking fix (make `Format` public, or expose the provider type) would expand the library's public contract for a host-only diagnostic. The `InternalsVisibleTo` grant already exists for exactly this kind of host/library coupling, so the internal accessor is the correct and minimal mechanism. `DetectFormat` (`:1924`) and the concrete providers (`LegacyJsonStoreProvider` at 1309, `JsonlStoreProvider` at 1384) are internal too — the host must not re-derive the format itself.

### 2026-11-03T06:20:00Z: A "resolved" accessor is only truthful if the value is resolved at open time, not stored as requested

**By:** Core Engineer (Ralph round 10, implementing epic #8 criterion 8)
**What:** The coordinator's brief assumed `DurableStore._format` already held the resolved format and that criterion 8 needed only an accessor. It did not: `Create` assigned `options.Format` **verbatim**, so an `--format auto` store carried `Auto` forever. Adding `internal DurableStoreFormat Format => _format;` alone would have produced a startup line reading `store: auto at <path>` — a report of the question, not the answer. The fix is `private static DurableStoreFormat ResolveFormat(IDurableStoreProvider provider, DurableStoreOptions options)`, which maps the **selected provider's type** to its format (`JsonlStoreProvider`→`Jsonl`, `LegacyJsonStoreProvider`→`LegacyJson`, `InMemoryStoreProvider`→`InMemory`) and falls back to `options.Format` for caller-supplied factories and registered names. `Create` now passes `ResolveFormat(provider, options)`.
**References:** `lib/Durable.cs`, `src/Durable.Runtime/Program.cs`, `tests/Durable.Tests/DurableStoreTests.cs`, PR #130, epic #8
**Why:** The provider is what the store actually writes through, so the provider's type is the only honest source of the resolved format. Deriving it from `options.Format` reports intent; deriving it from the provider reports behaviour. The distinction matters because `Auto` is the default, so the *common* case was the one that lied. A second-order consequence: a `LegacyJson` store writes nothing until its first commit, so a freshly created legacy path is still empty and `Auto` correctly sniffs it as `Jsonl` — the test must commit a conversation before reopening with `Auto` to exercise the reopen path rather than the empty-file path.
**How it applies:** When adding an accessor that reports a "resolved" or "effective" value, first verify the backing field is actually resolved. A field named `_format` assigned from `options.Format` is a request, not a resolution. Prefer deriving the answer from the object that does the work (the provider) over the object that asked for it (the options). And when a test asserts format detection, remember that lazy-writing stores are indistinguishable from empty ones until their first write.

### 2026-11-03T06:20:00Z: A tree hash equal to the base branch's tree is proof a PR is empty — close it, do not merge it

**By:** coordinator (Ralph round 8, closing #119 and #122)
**What:** After #126 merged, `git merge-tree --write-tree --name-only origin/dev <branch>` for both #119 and #122 returned tree `7f0fc017ba188b4eff8104e63f4ae750e9f0eb44` — **byte-identical to `origin/dev`'s own tree**. Both PRs were therefore empty: every file they touched was already present in `dev` with identical content, because #126's global regeneration subsumed them. Both were closed as superseded, each citing the tree hash, rather than merged.
**References:** PR #119, PR #122, PR #125, PR #126, #102, #103, #104, #105
**Why:** `gh pr view --json mergeable` reports `MERGEABLE` for an empty PR — it answers "can this merge without conflict", not "does this change anything". Merging an empty PR produces a merge commit that claims work it did not do, pollutes history, and makes the issue-closure trail misleading. The tree hash is the decisive diagnostic because it compares *content*, not commit ancestry: two branches with entirely different commit graphs can produce the same tree, and that is exactly what a squash-merge of shared history produces.
**How it applies:** Before merging any PR that overlaps another already-merged PR, run `git merge-tree --write-tree <base> <branch>` and compare the resulting tree hash to `git rev-parse <base>^{tree}`. If they match, the PR is empty — close it as superseded and cite the hash. Do not rely on `mergeable` status, commit count, or diff stat against the branch's own base, all of which are misleading after a squash-merge. Note that pairwise mergeability does not imply sequence mergeability: every one of these four PRs merged cleanly into `dev` alone, yet the issue-order sequence conflicted at #125.

### 2026-11-03T07:00:00Z: A gate that runs a subset of the suites is a gate that lies

**By:** coordinator (Ralph round 11)
**What:** `scripts/validate.sh --deterministic` runs 17 of 38 examples and only the
`ReplayToolDispatchTests` filter. It does **not** run the parity suite, `test_mutations.py`, or either
language unit suite. A run can therefore report five green gates while the parity suite is red — which
is exactly what happened during the #126 merge investigation. Widening it is issue #129.
**Why:** The value of a deterministic gate is that green means green. A gate that silently omits the
suites most likely to catch a regression is worse than no gate, because it manufactures false
confidence. `.github/workflows/build.yml` already invokes the script as its own step, so widening the
script widens CI with no workflow change.

### 2026-11-03T07:02:00Z: When a criterion contradicts an enforced invariant, document the exclusion — do not weaken the invariant

**By:** coordinator (Ralph round 11)
**What:** Epic #8 criterion 8 asked the host to report "the resolved backend and platform on startup".
The backend half shipped in PR #130. The platform half is impossible without breaking criterion 6,
because `RuntimeHostPortabilityTests.CorePath_HasNoPlatformSpecificApis()` forbids every platform API
in `src/Durable.Runtime/*.cs` except `HostShutdown.cs` — and that test *is* criterion 6's enforcement.
The exclusion is recorded in `docs/DESIGN.md` §3.9 rather than satisfied.
**Why:** Two criteria in the same epic cannot both be satisfied literally. The portability invariant is
load-bearing (it is what makes the host runnable on Linux and Windows from one code path); a startup log
line is not. When a spec contradicts itself, satisfy the invariant and write down why the other clause
was dropped — silently picking one side leaves the next reader to re-litigate it.

### 2026-11-03T09:35:00Z: A gate that cannot run in CI is not a gate — install the dependency, do not narrow the check

**By:** coordinator (Ralph round 12)
**What:** #129 widened `scripts/validate.sh --deterministic` to run the Python and TypeScript parity
suites and both language unit suites. The TypeScript parity suite runs the examples as subprocesses, so
it resolves `@github/copilot-sdk` from `examples/ts/node_modules` — and `.github/workflows/build.yml`
installed only `lib/ts`. The widened gate would therefore have failed CI on a missing directory. The fix
was one line, `npm ci --prefix examples/ts`, not a narrower `require_node_modules`.
**Why:** The tempting fix is to relax the check so it passes in the environment that cannot satisfy it.
That converts a real prerequisite into a silent skip, which is the exact failure mode #129 exists to
close. When a gate needs something CI does not provide, provide it in CI. The same missing directory had
already produced three spurious example failures (05, 06, 09) during earlier verification, so the
workflow gap was costing correctness outside the gate too.

### 2026-11-03T09:38:00Z: Verify the gate yourself before merging the PR that widens it

**By:** coordinator (Ralph round 12)
**What:** Tester reported `./scripts/validate.sh --deterministic` green with all four new suites PASS.
I re-ran it end to end on `dev` at `2fef50c` and confirmed independently: exit 0, `PASS` on
`parity (python)`, `parity (typescript)`, `lib/ts unit tests`, `lib/py unit tests`, **zero** `FAIL`
lines, final line `Deterministic run passed.`
**Why:** An agent's report is a claim, not evidence — and this particular claim is about the gate that
is supposed to catch unverified claims. A gate whose greenness was never observed by the person merging
it is exactly the false confidence the gate is meant to prevent. Also: do not pipe a long gate through
`tail`; it buffers everything and makes progress unobservable. Redirect to a log file and grep it.

### 2026-11-03T11:15:00Z: The slowest CI step is the one that proves the most — budget for it, do not trim it

**By:** coordinator (Ralph round 13)
**What:** Widening `scripts/validate.sh --deterministic` to run the Python and TypeScript parity suites plus both language unit suites took the `build` job from roughly 8 minutes to **14m12s**. That is a 75% increase in the wall-clock cost of the only gate that stands between `dev` and a parity regression. It was merged anyway, and the cost was stated explicitly in the PR body rather than discovered later by whoever next waits on CI.
**Why:** The alternative — leaving the gate at five green checks that never touch the parity contract — is a gate that can be green while a real regression ships. That is not a cheaper gate; it is a more expensive one, because the cost moves from CI minutes to a shipped bug. The rule: when a gate's coverage and its runtime trade off, widen the coverage and pay the minutes. State the new cost in the PR so the trade is visible and reviewable, and so nobody later "optimizes" the suites back out without knowing what they are removing.

### 2026-11-03T11:15:00Z: A gate that cannot run in CI is not a gate — install the dependency, do not narrow the check

**By:** coordinator (Ralph round 13)
**What:** Tester's #129 work added `npm run test:parity:ts` to the deterministic gate, but `.github/workflows/build.yml` installed only `lib/py` and `lib/ts` — never `examples/ts`. The parity suite imports `@github/copilot-sdk` from `examples/ts/tools.ts`, so the new step would have failed `ERR_MODULE_NOT_FOUND` on the first CI run. The fix was one line: `npm ci --prefix examples/ts`. The tempting alternative — dropping the parity suite from the gate because "it needs a dependency CI does not have" — would have silently reverted the entire point of the issue.
**Why:** A prerequisite gap is an argument for installing the prerequisite, never for removing the check. The failure mode to watch for is a gate that passes locally (where `node_modules` happens to exist from earlier work) and fails in CI, or worse, gets quietly narrowed until it passes in both. Verify a widened gate **in the environment that will run it**, not just on the developer's machine — and when the environment is missing something, fix the environment.

---

## 2026-11-03T13:45:00Z — Round 14: an agent with a broken environment will revert a correct fix

**By:** coordinator (Ralph round 14)

**What:** Backend Engineer applied the correct one-line fix for #132
(`"test": "tsx --test --test-force-exit *.test.ts"`). It then ran the suite itself,
saw 117 pass / 2 fail, concluded its change had broken something, and **reverted it**.
I had already verified the same change green (123/123, exit 0) — the two failures were
`Runtime executable does not exist`, i.e. a wrong `DURABLE_RUNTIME_PATH`, the exact
trap that had already caught me earlier in the session.

The agent re-applied the fix only after I sent it the literal path. Its own baseline
run with the *unmodified* `package.json` produced the identical 117/2/2-cancelled
signature, which is the proof the failures were never attributable to the flag.

**Why it matters:** a dispatch prompt that says "verify with the suite" without pinning
the environment variables that suite needs is a prompt that invites a false negative —
and a false negative here produced a *silent regression of a correct fix*. The tree was
clean after the agent reported, which read like completion but was actually a revert.

**Rule:**
- When a fix's verification depends on an environment value I already know, **put the
  literal value in the dispatch prompt.** Do not say "build the runtime and set
  `DURABLE_RUNTIME_PATH`" — write out the full path.
- After an agent reports, **re-check `git status`/`git diff` before believing it
  landed.** A clean tree can mean a revert, not a completion.
- When an agent reports failures, **reproduce them yourself before accepting the
  diagnosis.** Twice this session the "failure" was environmental.

The concrete value, for the next prompt:
`DURABLE_RUNTIME_PATH=/Users/core/git/matthewcorven/github-copilot-sdk-durable/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime`

---

## 2026-11-03T13:50:00Z — Round 14: a before/after control is the only real proof a fix works

**By:** coordinator (Ralph round 14)

**What:** For #133 I verified the fix by running the PortHarness test three ways:

1. With the fix stashed — **1 failed**.
2. With the fix restored, same ambient `PATH` — **1 passed**.
3. The whole `LateJoinExample` filter — **3/3 passed**.

Step 1 is the part that matters. Without it, "it passes now" is consistent with the
environment having changed rather than the code having been fixed — and this session
had already produced two environmental false alarms (#132 above, and Tester's TS
"regression" that turned out to pre-date all four open PRs).

**Why it matters:** a fix verification that only runs the post-state cannot distinguish
"fixed" from "was never broken in this environment" or "the environment shifted".
The before-state costs one `git stash push <file>` and one test run.

**Rule:** for every bug fix, run the failing test **without** the fix first, then with
it, on the same environment. Record both results in the PR body. If the pre-fix run
does not fail, the bug report is wrong and the fix is unverified — stop and find out
which.

---

## 2026-11-03T13:55:00Z — Round 14: decompose an epic by verifying its criteria, not by trusting its suggested children

**By:** coordinator (Ralph round 14)

**What:** Epic #19 lists six suggested sub-issues. Before filing anything I verified
all nine acceptance criteria against the current tree, and found **five already
satisfied**:

| Criterion | Status before decomposition | Evidence |
|---|---|---|
| 1 — run modes | satisfied | `SandboxRunMode` `Replayed`/`Enforced`, `lib/SandboxSupport.cs:28` |
| 3 — assertion split | satisfied | `AssertPolicy` 111, `AssertDenied` 174, `AssertNoPermissionDenial` 207 |
| 4 — `policy_resolved` | satisfied | `examples/py/34-…py:130`, `_sandbox_policy_22_35.py:82`, `35-…py:104,140` |
| 5 (first half) — live examples | satisfied | `33 34 35` already in `LIVE_EXAMPLES`, `scripts/validate.sh:31` |
| 9 (part) — vocabulary | satisfied | `grep -rniE 'mock\|fake\|stub\|simulat' examples/*.cs examples/*.py` → no matches |

Four genuine gaps remained, and they became **#134** (`SKIP_SANDBOX=1`), **#135**
(`policy`-mode fixtures), **#136** (advisory CI job + `docs/SANDBOX_CI.md`), **#137**
(check-first: does the completeness gate name language *and* id?).

Note that the filed set is **not** the suggested set. The issue's six suggested children
did not map cleanly onto the four real gaps, and one suggested child (#137's subject)
may already be satisfied — it was filed as `type:spike`, check-first, precisely so that
the work starts by disproving the need.

**Why it matters:** an epic's "suggested sub-issues" are a guess written when the epic
was filed. Decomposing by transcribing them would have generated work for five
already-built criteria and missed the shape of the four real gaps.

**Rule:** before filing an epic's sub-issues, verify every acceptance criterion against
the live tree and record the evidence in a decomposition note on the epic. File only
the gaps. When a gap is uncertain, file it as a check-first spike rather than as
implementation work.

### 2026-11-03T14:20:00Z: A green `build` on a bug-fix PR is not evidence the bug is fixed

**By:** coordinator (Ralph round 15)
**What:** #138 and #139 both went green — `build` 14m45s and 13m16s, both `Host smoke` jobs
pass — and both were merged. But CI never exercised the *defect*: `build` runs
`./scripts/validate.sh --deterministic`, which invokes the lib/ts suite as
`npm test -- --test-force-exit` (the flag supplied on the command line), so it would have
passed with or without the `package.json` change. Likewise the PortHarness test passes in CI
because CI's `PATH` happens to contain a working `python3`; the fix only matters on a machine
where it does not.
**Why:** The real proof for both was the local before/after control, not the CI result. For
#133 that was explicit: stash the fix → 1 failed, pop the fix → 1 passed. For #132 it was
`npm test` exiting 0 with 123/123 where it previously hung forever.
**Consequence:** When a fix targets an environment-dependent defect, CI green is necessary but
not sufficient. State the control that actually proves the fix, and do not let a green check
substitute for it. If CI cannot reproduce the defect, say so in the PR body rather than
implying CI validated the fix.

### 2026-11-03T15:10:00Z: A broad gate failure is usually a toolchain problem, not a diff

**By:** coordinator (Ralph round 16)
**What:** `./scripts/validate.sh --deterministic` reported 22 failed checks — `ReplayRuntime.Tests`,
`ReplayToolDispatchTests`, `lib/py unit tests` (`errors=1`), and 17 C# examples at `exit 150`. The
cause was not the four-file change under test. `dotnet` resolved to `/usr/local/share/dotnet/dotnet`,
a .NET 11 RC whose only runtime is `Microsoft.NETCore.App 11.0.0-rc.2`, so every net10.0 test host
aborted with `You must install or update .NET to run this application. Framework:
'Microsoft.NETCore.App', version '10.0.0'`. The Homebrew SDK 10.0.402 was still installed but
**unlinked** — `/opt/homebrew/bin/dotnet` did not exist. `brew link --overwrite dotnet` restored 72
symlinks and the gate returned `EXIT=0` with zero failures, with the change still in place.
**Why:** I nearly attributed a 22-check failure to a four-file manifest-message change, which is
categorically impossible. The signature was unmistakable in hindsight: `exit 150` on *every* C#
example is a runtime-resolution failure, never a code regression. **Diagnostic order for any broad
gate failure: read the log's actual error text FIRST, then `which -a dotnet` / `dotnet --list-sdks`
/ `dotnet --list-runtimes`.** A five-second toolchain probe would have saved a ten-minute gate run
and a wrong hypothesis.

### 2026-11-03T15:10:00Z: An agent record can be cleared while its work survives on disk

**By:** coordinator (Ralph round 16)
**What:** `read_agent` on Tester's #137 agent returned `No agent found with agent_id: … The agent
may have been cleared or never existed.` The work was fully present: four modified files in the
working tree, +75/−11, with four new tests. I recovered it, verified it (17/17 with the venv
interpreter; 9 failures with the source fix stashed), and landed it as `bbb781b`.
**Why:** A missing agent record is not evidence of missing work. **Always check
`git status --porcelain` before concluding an agent produced nothing** — and before re-dispatching
the same task, which would duplicate effort and risk a conflicting second implementation.

### 2026-11-03T21:55:00Z: Real-world scenario program — seven bands, measured baselines, no pre-filed releases

**By:** coordinator (program planning)
**What:** Filed #142 (program epic) + 7 band epics (#143, #148, #153, #158, #164, #170, #175) + 29 work
issues + matrix (#180) and TL;DR (#181). 70 native `blocked by` edges. All labeled `model:high` or `model:low`
with a rationale line so the coordinator routes without re-deriving difficulty.

Rules that shaped it:
- **Baselines are references, not adversaries.** Every band vendors a first-party Microsoft/Azure/GitHub
  implementation (MIT required for vendoring; `NO-VENDOR` otherwise), normalizes it to C#, and keeps a pinned
  unmodified copy under `upstream/` for fidelity checks.
- **Fault evidence is mandatory and identical across bands** — kill -9 mid-turn, restart after checkpoint,
  duplicate delivery, concurrent clients, store outage. Band E adds endpoint death; Band C adds the
  cooldown-desync race.
- **Two bands are known to be honest ties, and the issues say so.** Band G's baseline already resumes across
  processes (`magentic_checkpoint.py`), and `agent-framework-durable-extension` is an official event-sourced
  durable agent. Band F's baseline already checkpoints via `FileCheckpointStorage`. Their Durable-variant
  issues require writing down what, if anything, is actually added.
- **Release issues are not pre-filed.** A durable-variant issue that discovers a real gap files the release
  issue itself and blocks on it. Pre-filing seven releases would manufacture work for changes that may not be
  needed.

**Versioning contract** (revised twice, final): start pinned at `v0.4.0`; iterate on `dev`; land a scenario
pinned to a release cut in a *preceding* PR. A scenario PR does not carry an unpublished layer shape.

**Why:** The repo's 38 examples prove capabilities in isolation but never against a third-party baseline and
never on purpose-failing runs. Without a baseline the comparison is assertion; without faults it flatters the
library. The program exists to make both measurable, and to publish the result even where it is a tie.

### 2026-11-03T22:20:00Z: Native `blocked by` links alone do not make a queue — the helper reads issue bodies

**By:** coordinator (program planning)

**What:** Filed the program's first dependency graph entirely with native `blocked by` links (70 edges, all
verified via GraphQL). The `issue-work-queue` helper then reported **every one of the 40 issues as available** —
its 25-item blocked list was empty. The helper parses `## Depends On` / `## Blocked By` sections from issue
bodies; native links are invisible to it.

Fixes applied, in order:

1. Installed the plugin: `copilot plugin install github-issues@corven-agent-tools`. The marketplace
   (`corven-agent-tools`) was already registered and `change-learn` was installed from it — only the plugin was
   missing. Note the repo is **private**, so `raw.githubusercontent.com` returns 404; read it through the
   authenticated API.
2. Added `## Depends On` to all 40 issues.
3. Added `.github/issue-queue.json` declaring project 5, so no agent needs to know the project number.
4. Fixed a second, worse bug: band epics depended on #142 and each first work issue depended on its epic, so
   **nothing was startable** — epics stay open all program long. Work issues now depend only on work issues.
   Result: 12 available (5 pre-existing + the 7 band entry points), 25 blocked.
5. Added the precedence rule to `.github/copilot-instructions.md`, as the skill instructs consumers to do.
6. The drift diagnostic caught 4 issues never added to the board (#178–#181); they are now on it.

**Why:** Two independent traps. The first is a contract mismatch — "dependencies" meant different things to
me (GitHub's native feature) and to the tool (issue-body prose). The second is a modelling error that only
surfaced once the tool could read the graph: modelling an epic as a prerequisite makes the whole tree
unstartable, because an epic is a container, not a task. Verification against the real consumer is what caught
both; GraphQL showed 70/70 edges and proved nothing about whether work could start.

**Rule:** After filing a dependency graph, run the consumer — not a query that confirms what was written.
`issue_queue.py --mode status` is the acceptance test, and "all N issues available" is the failure signal.

### 2026-11-05T05:30:00Z: The Durable layer commits a turn's tool records only when the turn returns — a dead turn leaves no record of its work
**By:** core-engineer (Band B, PR #190 review follow-up)
**What:** PR #190's evidence and header attributed a gap to `lib/Durable.cs` that the layer partly
covers. `RecordToolCheckpoint` (`lib/Durable.cs:4271`) writes a per-call record on every completed
tool call into `durable.checkpoint`, which is `DocumentHistory.Rewindable` (`lib:4590`), so a
*finished* turn's calls stay readable through `DurableStore.Documents` / `DocumentState.Versions`
(`lib:2072`, `lib:110`). The claim "no evidence in the store distinguishes 'the turn did nothing
yet' from 'the turn had written four of five files'" was wrong for a run that finished, and so was
"would need a durable 'tool intent' record the layer writes before each tool execution". The first
correction then over-swung: it said a dead turn's work is "observable" because those records exist.
They do not exist for a dead turn — nothing is committed at the call — so the record now states
both halves.
**References:** #150, #148, PR #190, real-world-scenarios/band-b-local-job-agent/evidence/durable-kill-and-resume.md §3c, durable/ralph-loop-durable.cs
**Why:**
#### Position

The real gap is narrower and has two parts, both now demonstrated rather than asserted:

1. **Tool records are committed only when the turn returns.** `RecordToolCheckpoint` queues one
   per completed call (`CheckpointAsync` → `QueueCheckpointAsync`, `lib:4074`) and returns; the only
   drain is `CheckpointBarrierAsync` (`lib:4150`), which `SubmitAsync` reaches at `lib:4246` *after*
   `if (result is not null)`. Neither the `catch` (`lib:4251`) nor the `finally` (`lib:4259`) drains
   it. A cancelled or failed turn therefore commits **no** tool record — not a partial list — and
   the queue is discarded with the conversation. `STOP_MID_TURN=1` reproduces it end to end: the
   dead conversation (`c37`) has one `durable.admission` at `admitted` and **no**
   `durable.checkpoint` document at all, while every conversation whose turn returned ends its
   history at `turn`.
2. **There is no replay.** The SDK will not continue a half-finished turn, and the layer offers no
   per-tool-call resume.

The honest one-liner is: **what a dead turn leaves is an admission and the loop's own
`turn-started` checkpoint — not a record of its work.** The work is therefore neither observable
after the fact nor replayable: the store can say the turn was issued, and nothing about what it did.
Closing that needs two things, and the first is the one a durable host notices: a record written
*before* each call and settled after it, so an interrupted call can be told from one that never
started (a cursor, not a history drained at success); and per-tool-call replay in the SDK. Both are
feature requests, not a scenario fix.

#### Why this matters beyond this PR

`.github/copilot-instructions.md` requires a *reproduced* gap before a scenario may claim one. The
previous text claimed a gap that source inspection contradicts, which is exactly the error class
that makes the program's conclusions wrong: a reader would have filed a layer issue for something
the layer already does, and missed the thing it actually does not do.
**Rule:** Before attributing a limitation to `lib/`, read the write path, not just the public
surface. A public getter existing does not mean the data is committed at the moment you need it —
and a record existing does not mean it can be replayed.

### 2026-11-05T17:00:00Z: A durable store's rollback must restore the provider's baseline, not just the record

**By:** coordinator (round 18, found while judging #155)

**What:** `Transaction.Memo` (#201's fix) exposed a pre-existing defect in the rollback path.
`InMemoryStoreProvider.UpdateTask` captures the current state into `_taskStates` *after* queuing the undo
action, and `UndoLog.Restore` only performed `snapshot.ApplyTo(task)`. The record was restored; the
baseline the *next* transaction arms itself with was not. So a memo written by a transaction the library
reported as rolled back came back on the **next** failed transaction — and with a JSONL store it reached
disk after a reopen. TypeScript was already correct (`restore(snapshot, task, states)` also does
`states.set(task.id, snapshot)`); C# and Python were not.

**Why:** The failure mode is the one this program keeps finding: silence. The memo is the record that an
external effect happened, so a resurrected memo makes a host skip work it never did — the "silently
missing comment" the Band C variant's own README calls unrecoverable. Nothing throws, nothing logs, and
the single-failure test passes either way; only a **second** failing transaction exposes it.

**Rule:** When a layer keeps two copies of a piece of state — the record and a snapshot baseline — a
rollback must put back both, and the test must fail the transaction **twice**. A one-shot rollback test
proves the record and says nothing about the bookkeeping. Check every port: a divergence here is invisible
until someone's store corrupts.

