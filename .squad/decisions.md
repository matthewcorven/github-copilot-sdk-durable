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
