# Squad Decisions

## Active Decisions

### 2026-10-23T13:32:09.577-04:00: User directive — dev branch as the integration branch

**Date:** 2026-10-23
**Author:** Copilot App (via Copilot)

**What:** Introduce a `dev` branch for this scope (issue #1 epic). All feature work branches from `dev`, PRs target `dev`, and merges land on `dev`. Release tags are picked from `dev` when `dev` is merged forward to `main` (master). This aligns with the repo's existing `.github/skills/git-workflow` three-branch model, where `main` is released/tagged-only and `dev` is the integration branch.

**Why:** User directive — captured for team memory. Feature work should not land directly on the release branch, and release tags must be cut from the integration branch after promotion to `main`.

**How it applies:** `.squad/templates/issue-lifecycle.md` branch/PR commands updated from `main` to `dev`. Issue branches keep the `squad/{issue-number}-{kebab-case-slug}` convention and are created from `origin/dev`.

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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
