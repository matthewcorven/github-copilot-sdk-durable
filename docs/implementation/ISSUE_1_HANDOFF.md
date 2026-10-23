# Handoff — Issue #1 (pluggable durable-store provider abstraction)

**Date:** 2026-10-23
**Repo:** `matthewcorven/github-copilot-sdk-durable`
**Written by:** Lead (handoff for the next Lead session)
**Current branch:** `squad/9-durable-store-provider-interface` (pushed to `origin`)

## Branch model (read this first)

| Branch | Purpose | Work lands here? |
|---|---|---|
| `dev` | Integration branch — **repo default branch** | Yes. Every `squad/{issue}-{slug}` PR targets `dev`. |
| `main` | Release-only — released/tagged code | Only via promotion from `dev`. |

Both `dev` and `main` have branch protection: **PR required, no force-push, no deletion**.
Feature branches are cut from `origin/dev` and follow `squad/{issue-number}-{kebab-case-slug}`.
Release tags are cut after `dev` is promoted forward to `main` — never from a feature branch.

`origin` is **SSH**: `git@github.com:matthewcorven/github-copilot-sdk-durable.git`.

## Where things stand

Issue #1 is an epic split into seven serial sub-issues (#9 → #10 → #11 → #12 → #13) plus two
consumers (#14, #15). All eight open design questions were resolved on 2026-10-23 and recorded in
`docs/implementation/ISSUE_1_PLAN.md` §5 — **they are settled; do not re-open them.** The first
spine item, **#9 (provider interface)**, is implemented and committed on
`squad/9-durable-store-provider-interface` (commits `05a9562` + `4e5ca88`), which is now pushed
to `origin`. The branch builds clean, all 4 xUnit facts pass, and all 31 examples compile. **No PR
has been opened and nothing has been merged to `dev`** — that decision is deliberately left to the
next session (see *What #9 still needs*). The next spine item, **#10 (facade over a backend)**, is
substantially pre-empted by the #9 work: `DurableStore` is already a pure facade over
`IDurableStoreProvider`. The next session should read this document, decide whether #9 is PR-ready,
and then route #10.

---

## 1. The 8 resolved design decisions

**Authoritative source: `docs/implementation/ISSUE_1_PLAN.md` §5 "Resolved design decisions".**
That file carries the full rationale; the table below is the short form. The resolutions were also
appended to issues #9 and #11–#15 as "Resolved design decisions" sections.

| # | Question | Resolution | One-line rationale |
|---|---|---|---|
| 1 | Exact record envelope shape | **`{v, seq, type, payload}`**, `v: 1` | `type` and `v` at the top level make format sniffing and future migration possible without parsing payloads; `payload` deserializes straight into the existing public record types. |
| 2 | When does legacy `StoreSnapshot` conversion happen? | **On open, into memory only.** The legacy file is never rewritten unless the caller explicitly forces `Format = Jsonl`. | Converting on open makes the store immediately usable in the new format; keeping the file untouched means "open" never mutates a user's data. |
| 3 | How does `DurableStoreOptions` coexist with `DurableStore(string? path = null)`? | **Keep `DurableStore(string? path = null)` verbatim; add a static factory `DurableStore.Create(DurableStoreOptions)`.** No deprecation. | A constructor overload would be ambiguous for `new DurableStore(null)`; a static factory has no overload-resolution interaction, so the existing constructor stays source-compatible. |
| 4 | Does the legacy reader write back a converted file? | **No implicit write-back.** Read-only by default; conversion only when the caller forces `Format = Jsonl`. | "Open" must not mutate a user's file. The conversion path is explicit and documented. |
| 5 | Is the default path per-user, per-project, or per-workspace? | **Per-user** for this epic. | Matches the issue's wording; the default is opt-in and an explicit path is one property away. |
| 6 | Is a per-line checksum needed? | **No checksum in v1.** | The realistic failure mode is a truncated final write, which the newline-termination rule already catches. |
| 7 | Should the JSONL backend be the default when no path is given? | **No — in-memory stays the default.** JSONL is opt-in via `DurableStoreOptions`. | Making JSONL the default would write to disk unexpectedly and change the behaviour of every existing caller. |
| 8 | Where do the `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose changes land? | **Issue #14 (Docs).** #12's acceptance criteria were amended to drop the prose. | One owner per document keeps the prose consistent with the README table it sits next to. |

---

## 2. The three findings that changed the contract

These are the non-obvious ones. They are why the interface looks the way it does, and they are the
first thing to re-read before changing the contract.

### 2.1 `UpdateTask` is required on the provider interface

`DurableTaskRecord` is **mutated in place** after creation — `task.Status`, `task.Checkpoint`,
`task.WaitingOn`, `task.Memos` are all assigned directly. `MemoAsync` (`lib/Durable.cs:1150`)
mutates `task.Memos` and then calls `_provider.UpdateTask(task); _provider.Commit();`
**outside any transaction**.

A provider that only ever saw `AddTask` would never learn about those changes, so the JSONL backend
would **lose task progress across a reopen**. `UpdateTask` emits the task's full current state; the
JSONL reader applies `task` records **last-wins per task id**, which is what makes the reopen
correct.

### 2.2 The `UndoLog` moved into the provider

The provider owns the collections the undo log protects, so the log lives with the provider
(`InMemoryStoreProvider._undo`, `lib/Durable.cs:654`). `UndoLog.Undo(IDurableStoreProvider provider)`
(`lib/Durable.cs:2421`) **never reads its argument** — it only replays recorded inverse actions in
reverse — so the parameter type change from `DurableStore` to `IDurableStoreProvider` is
**behaviour-preserving**. The `SemaphoreSlim` commit gate stays in the facade.

`UndoLog.Restore` now takes a `TaskStateSnapshot` (`lib/Durable.cs:2435`) instead of capturing the
task's fields inline, so the provider can hold the snapshot it must restore on rollback.

### 2.3 `DurableStoreOptions` is entered via a static factory, not a constructor overload

`new DurableStore(null)` would match **both** `DurableStore(string?)` and a
`DurableStore(DurableStoreOptions)` overload, and the compiler could not pick. A static factory has
no overload-resolution interaction at all, so `DurableStore(string? path = null)` stays
byte-for-byte source-compatible and `examples/12-recovery.cs`, `examples/22-subagent-background.cs`
and `examples/30-reload-and-restart.cs` need no edits.

---

## 3. Verified state

All three gates were re-run on this branch on 2026-10-23 and are green.

| Gate | Command | Result |
|---|---|---|
| Build | `dotnet build tests/Durable.Tests` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| Tests | `dotnet test tests/Durable.Tests` | **Passed! Failed: 0, Passed: 4, Skipped: 0, Total: 4** |
| Examples | `COMPILE_ONLY=1 ./scripts/validate.sh` | **All examples passed** — 31/31 examples compile |

The 4 existing facts (must keep passing unchanged): `FailedTransaction_RollsBackAllWrites`,
`Fork_InheritsOnlyEntriesUpToForkPoint_AndHasIndependentState`,
`Recovery_ReopenedStoreResumesFromSavedCheckpointWithMemoIdempotency`,
`Abort_TerminatesParentAndChildrenDurably`.

`examples/` and `tests/` are **untouched** on this branch (`git diff --stat origin/dev -- examples/ tests/`
is empty).

---

## 4. What #9 has implemented

All line numbers are `lib/Durable.cs` on `squad/9-durable-store-provider-interface` @ `4e5ca88`.

| Thing | Location | Notes |
|---|---|---|
| `public enum DurableStoreFormat { Auto, InMemory, Jsonl, LegacyJson }` | `lib/Durable.cs:490` | Matches plan §5.1 exactly. |
| `public sealed class DurableStoreOptions { Path, Format, FileName = "durable-store.jsonl" }` | `lib/Durable.cs:506` | `init` accessors, immutable, matches plan §5.1. |
| `public interface IDurableStoreProvider` | `lib/Durable.cs:524`–`596` | **22 members**, byte-for-byte the same method surface as the plan's §2 contract sketch (verified by diffing the two). Includes `BeginTransaction()`, `Rollback()`, and `UpdateTask(DurableTaskRecord)`. XML docs on the interface and every member. |
| `internal sealed record TaskStateSnapshot` | `lib/Durable.cs:603` | Captures a task's mutable state so a rollback can restore it. |
| `public class InMemoryStoreProvider : IDurableStoreProvider` | `lib/Durable.cs:639` | Holds the collections, the sequence, the task-state snapshots, and the `UndoLog` (`_undo`, `lib/Durable.cs:654`). `Open`/`BeginTransaction`/`Rollback`/`Commit` at `657`/`662`/`665`/`672`. `UpdateTask` at `807`. `Persist` is `protected virtual` at `822`. |
| `internal sealed class LegacyJsonStoreProvider : InMemoryStoreProvider` | `lib/Durable.cs:892` | Reads a `StoreSnapshot` on `Open` (`897`); writes back atomically via `.tmp` + `File.Move(overwrite: true)` in `Persist` (`911`). |
| `public sealed class DurableStore` — now a **pure facade** | `lib/Durable.cs:933` | Only two fields remain: `_provider` and `_gate`. Every read and write delegates to the provider. Grepping the class body for `_documents\|_tasks\|_conversations\|_entries\|_entryRecords\|_sequence\|_undo` returns **zero** hits. |
| `public DurableStore(string? path = null)` | `lib/Durable.cs:939` | **Unchanged** — still the primary entry point; picks `InMemoryStoreProvider` (no path) or `LegacyJsonStoreProvider` (path). |
| `public static DurableStore Create(DurableStoreOptions options)` | `lib/Durable.cs:957` | Selects the provider by `Format`. `Jsonl` throws `NotSupportedException`; `Auto` does not sniff yet. |
| `internal IDurableStoreProvider Provider` | `lib/Durable.cs:1222` | The seam `Transaction` and `DurableHarness` use to write records a transaction owns. |
| `Transaction` now holds `IDurableStoreProvider` | `lib/Durable.cs:1226`–`1230` | Was `DurableStore`. All writes go through the interface, not the facade. |
| `UndoLog.Undo(IDurableStoreProvider provider)` | `lib/Durable.cs:2421` | Behaviour-preserving signature change (see §2.2). |
| `UndoLog.Restore(TaskStateSnapshot, DurableTaskRecord)` | `lib/Durable.cs:2435` | Delegates to `snapshot.ApplyTo(task)`. |
| Restart counter reported to the provider | `lib/Durable.cs:2016` | `Store.Provider.UpdateTask(record)` before the commit, so `record.Restarts` is part of the durable state. |

**Public surface of `DurableStore` is unchanged** apart from the additive `Create` factory:
`DurableStore(string?)`, `Create(DurableStoreOptions)`, `Conversations`, `Tasks`, `Documents`,
`CommitAsync<T>(Func<Transaction, Task<T>>)`, `CommitAsync(Func<Transaction, Task>)`,
`Document(string, string)`, `Task(string)`, `Conversation(string)`, `Entries(string)`.

---

## 5. What #9 still needs — and is it PR-ready?

### Gap list (honest)

| Gap | Belongs to | Severity for #9 |
|---|---|---|
| No `JsonlStoreProvider` class exists (grep count = 0). `DurableStoreFormat.Jsonl` throws `NotSupportedException("The JSONL backend is not available yet...")`. | **#11** | Not a #9 defect — by design. |
| `Auto` format sniffing is **not** implemented. `Auto` currently means "path ⇒ legacy, no path ⇒ in-memory". | **#11 / #13** | Not a #9 defect — by design. |
| No default-path resolution: no `LocalApplicationData`, no `XDG_STATE_HOME`, no `~/.local/state` anywhere in `lib/Durable.cs`. | **#12** | Not a #9 defect — by design. |
| No `examples/31-jsonl-store.cs`. | **#14** | Out of scope. |
| No new xUnit facts (still 4). | **#15** | Out of scope. |
| `DurableStoreOptions` and `DurableStoreFormat` are **public but not yet reachable through a documented, working path** for anything other than `InMemory` / `LegacyJson`. | #11/#12 | Acceptable — the types are additive and the plan puts their behaviour in #11/#12. |
| `LegacyJsonStoreProvider` is `internal`, so an external consumer cannot construct it directly. | #13 | Acceptable — `DurableStore.Create` is the intended entry point. |

### Judgement: **#9 is PR-ready now.**

Every acceptance criterion on issue #9 is satisfied:

- [x] A public store-provider interface exists whose method surface covers open/load, read, write,
      and commit for conversations, tasks, documents, entries, and the write sequence.
- [x] The interface references only the existing public record/state types
      (`ConversationRecord`, `DurableTaskRecord`, `DocumentState`, `EntryRecord`,
      `DocumentVersion`, `ForkPolicy`) — no new public type is required for existing example code
      to compile.
- [x] The interface and its member names match the contract sketch in
      `docs/implementation/ISSUE_1_PLAN.md` §2 — verified by an exact diff of the two member lists
      (22 members, zero differences).
- [x] `dotnet build tests/Durable.Tests` succeeds and `dotnet test tests/Durable.Tests` reports
      **4 passed / 0 failed**.
- [x] `COMPILE_ONLY=1 ./scripts/validate.sh` reports `PASS` for all 31 examples.

**Caveats the PR description must state honestly:**

1. The branch contains **two** commits: `05a9562` (a `wip:` checkpoint that does **not** compile on
   its own) and `4e5ca88` (the commit that completes the refactor and restores compilation). The
   PR is green at the tip, but a reviewer reading the first commit will see a broken build. Either
   squash the two into one commit before opening the PR, or say so in the PR body.
2. The branch also does **more than #9 strictly asks for**: it already performs the #10 facade
   refactor (`DurableStore` is a pure facade; `Transaction` holds the provider). That is
   intentional — the two are not separable without leaving the tree broken — but the PR should say
   so, and #10's scope should be re-scoped to what genuinely remains.
3. `DurableStoreFormat.Jsonl` throws and `Auto` does not sniff. Both are deliberate and land in
   #11/#13. Say so in the PR body so a reviewer does not read them as bugs.

**What #10 still genuinely owes** (because #9 pre-empted part of it): the public-surface
compatibility sweep, the `StoreSnapshot`/`DocumentSnapshot`/`DocumentVersionSnapshot` types staying
as-is until #13, and any remaining facade clean-up. Re-read issue #10 before scoping it.

---

## 6. The remaining spine

Strictly serial: **#9 → #10 → #11 → #12 → #13**. #14 and #15 consume the spine.

| Issue | Title | Owner | Must deliver |
|---|---|---|---|
| **#10** | #1.2 Refactor `DurableStore` into a facade over a backend | Core Engineer | Mostly done by #9. Remaining: confirm the whole public surface of `DurableStore` is source-compatible, keep `StoreSnapshot` and friends as-is until #13, and keep `UndoLog` rollback / the `SemaphoreSlim` gate / the `Transaction` write path semantically identical. |
| **#11** | #1.3 Implement the JSONL append-only backend | Core Engineer | A provider implementing the #9 contract that persists one JSON record per line, flushed per commit, using the `{v, seq, type, payload}` envelope. Torn-tail recovery: an incomplete final line is discarded, earlier records survive, the file is truncated back to the last complete record boundary. Creates the parent directory when missing. |
| **#12** | #1.4 Add `DurableStoreOptions` and per-user default path resolution | Core Engineer | The per-user default path rule — Windows `Environment.SpecialFolder.LocalApplicationData`; elsewhere `$XDG_STATE_HOME` when set and non-empty, else `~/.local/state` — under a `durable` subdirectory. An explicit `Path` always wins. A public helper exposing the resolved default path. The directory is created **on first write, not on open**. |
| **#13** | #1.5 Add a backward-compatible reader for the existing single-file JSON store | Core Engineer | Detect and read the legacy single-file JSON shape without data loss; convert the snapshot into the new backend's state **at open**; preserve the current atomic-write format; leave the `StoreSnapshot` / `DocumentSnapshot` / `DocumentVersionSnapshot` public types in place. |
| **#14** | #1.6 Add `examples/31-jsonl-store.cs` and update the README example table | Docs | New example in house style (`#:package GitHub.Copilot.SDK@1.0.26` / `#:property TargetFramework=net10.0` / `#:property NoWarn=GHCP001` / `#:include ../lib/Durable.cs`), demonstrating an explicit path and the default path, asserting the JSONL file exists and a reopened store returns the same document value. README table row for `31`. Also owns the `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose (per decision #8). |
| **#15** | #1.7 Add xUnit facts for the JSONL backend, path options, and legacy reader | Tester | 5 new `[Fact]`s appended to `tests/Durable.Tests/DurableStoreTests.cs` (the existing 4 stay untouched): `JsonlBackend_SurvivesReopenWithIdenticalState`, `JsonlBackend_DiscardsTruncatedFinalLine`, `DurableStoreOptions_ExplicitPathWinsOverDefaultPath`, `DurableStoreOptions_DefaultPathResolvesUnderPerUserDirectory`, `LegacyJsonStore_OpensAndRoundTrips`. Full-state assertions, not single-field. Each fact uses its own unique temp directory. Target after #15: `dotnet test` reports **9/0**. |

---

## 7. The parallel sandbox line (#16–#19) — a **separate initiative** from #1

These four issues are **not** part of issue #1 and must not be folded into the #1 spine. They are a
distinct sandbox epic, all OPEN, all on the board as `Todo`, all labelled `sandbox`.

| Issue | Title | One line |
|---|---|---|
| **#16** | Add a durable sandbox policy to the durable layer, with a filesystem-policy example | Adds a stable persisted `DurableSandboxPolicy` (deliberately not the experimental `GitHub.Copilot.Rpc.SandboxConfig` wire type), `AgentChoices.Sandbox`, mapping into `BuildSessionConfig`, `DurableConversation.SandboxDecisions`, a permission-handler seam, `lib/SandboxSupport.cs`, and `examples/33-sandbox-sdk-filesystem.cs`. |
| **#17** | Demonstrate the network, credential, dev-tool and MCP/LSP sandbox options | `examples/34-sandbox-sdk-network-credentials.cs` covering network egress policy, credential masking with per-host injection, GitHub CLI / git credential injection, dev-toolchain read access, and MCP/LSP confinement. |
| **#18** | Demonstrate sandbox observability, bypass, working-directory trust and keychain | `examples/35-sandbox-sdk-observability-bypass.cs` — the three-part enforcement proof, decision-event coverage, `AllowBypass` handling, `SandboxPathGrant` / `RequestSandboxPermissive`, `TrustWorkingDirectory`, and macOS-gated keychain access. |
| **#19** | Make sandbox verification deterministic offline and wire it into CI | `Enforced` / `Replayed` run modes resolved in `lib/`, a policy-vs-enforcement assertion split, `scripts/validate.sh` integration with a `SKIP_SANDBOX=1` escape, a `sandboxMode` column in #6's parity manifest, and an advisory CI job across Linux/macOS/Windows. |

Note: #19 depends on #3 (the deterministic mock runtime) and #6 (the parity manifest) for its
vocabulary and manifest work. #16–#19 also assume `examples/33`–`35` numbering, which does not
collide with #14's `examples/31-jsonl-store.cs`.

---

## 8. Board + issue state

**Board:** "github-copilot-sdk-durable Roadmap"
- Project number: **5**
- Owner: **matthewcorven**
- Project id: **`PVT_kwHOAajMkc4BobdZ`**
- Project URL: `https://github.com/users/matthewcorven/projects/5`
- Status field id: **`PVTSSF_lAHOAajMkc4BobdZzhm7oGY`**
- Option ids: Todo **`f75ad846`**, In Progress **`47fc9ee4`**, Done **`98236657`**
- These ids are pinned in `.squad/config/github-board.json`.

**Current status of every item (as of 2026-10-23):**

| Item | Status | Item id |
|---|---|---|
| #1 Add a pluggable durable-store provider abstraction… | **In Progress** | `PVTI_lAHOAajMkc4BobdZzhDDlZw` |
| #2 EF Core-backed durable store providers | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlcQ` |
| #3 Deterministic mock Copilot runtime | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlf4` |
| #4 Port the example suite to TypeScript and Python | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlh0` |
| #5 Port the example suite to Go, Rust and Java | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlkI` |
| #6 Six-language CI matrix and parity manifest | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlmc` |
| #7 Unified extension library | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlpc` |
| #8 Cross-platform runtime host | Todo | `PVTI_lAHOAajMkc4BobdZzhDDlsU` |
| #9 #1.1 Define the durable store provider interface | **In Progress** | `PVTI_lAHOAajMkc4BobdZzhDDyYU` |
| #10 #1.2 Refactor `DurableStore` into a facade over a backend | Todo | `PVTI_lAHOAajMkc4BobdZzhDDybA` |
| #11 #1.3 Implement the JSONL append-only backend | Todo | `PVTI_lAHOAajMkc4BobdZzhDDybo` |
| #12 #1.4 Add `DurableStoreOptions` and per-user default path resolution | Todo | `PVTI_lAHOAajMkc4BobdZzhDDycI` |
| #13 #1.5 Add a backward-compatible reader for the existing single-file JSON store | Todo | `PVTI_lAHOAajMkc4BobdZzhDDyd8` |
| #14 #1.6 Add `examples/31-jsonl-store.cs` and update the README example table | Todo | `PVTI_lAHOAajMkc4BobdZzhDDyfY` |
| #15 #1.7 Add xUnit facts for the JSONL backend, path options, and legacy reader | Todo | `PVTI_lAHOAajMkc4BobdZzhDDyhc` |
| #16 Durable sandbox policy | Todo | `PVTI_lAHOAajMkc4BobdZzhDFUvs` |
| #17 Network / credential / dev-tool / MCP-LSP sandbox options | Todo | `PVTI_lAHOAajMkc4BobdZzhDFUzY` |
| #18 Sandbox observability / bypass / working-directory trust / keychain | Todo | `PVTI_lAHOAajMkc4BobdZzhDFU3g` |
| #19 Deterministic offline sandbox verification + CI | Todo | `PVTI_lAHOAajMkc4BobdZzhDFU7E` |

**Flipping a status.** By name (simplest):

```bash
gh project item-edit 5 --owner matthewcorven \
  --url https://github.com/matthewcorven/github-copilot-sdk-durable/issues/9 \
  --field Status --value "In Progress"
```

By node id (script-friendly, uses the pinned ids):

```bash
gh project item-edit --id PVTI_lAHOAajMkc4BobdZzhDDyYU \
  --project-id PVT_kwHOAajMkc4BobdZ \
  --field-id PVTSSF_lAHOAajMkc4BobdZzhm7oGY \
  --single-select-option-id 47fc9ee4
```

Valid `--value` strings are `Todo`, `In Progress`, `Done`.

**Do not mark anything `Done` yet.** #9 is committed and pushed but **not merged** — it stays
`In Progress`. Nothing in the #1 spine is `Done`.

---

## 9. Gotchas for the next session

1. **The `origin` remote is SSH, and it must stay that way.**
   `git remote origin` = `git@github.com:matthewcorven/github-copilot-sdk-durable.git`. The HTTPS
   token in this environment lacks the `workflow` scope, so pushing `.github/workflows/` over HTTPS
   **fails**. Do not "fix" the remote to HTTPS.

2. **`docs/implementation/ISSUE_1_PLAN.md` was observed to regress in a working tree.** A working-tree
   copy had lost all four "Resolved:" markers and the entire §5 resolutions table that `HEAD` still
   had. It was restored with `git checkout HEAD -- docs/implementation/ISSUE_1_PLAN.md`. **If the
   plan doc looks like the pre-resolution version, run:**
   ```bash
   git checkout HEAD -- docs/implementation/ISSUE_1_PLAN.md
   ```
   The plan doc is the authoritative record of the 8 resolutions — do not let it drift.

3. **`scripts/validate.sh` resolves `examples/<arg>.cs`, not bare numbers.** Invoke it as
   `./scripts/validate.sh 00-conversation`. A bare number produces `MISSING  00.cs` and a
   "1 example(s) failed" line. (Verified: `./scripts/validate.sh 00` → `MISSING  00.cs`.)
   Use `COMPILE_ONLY=1` to build without running, and `SKIP_TESTS=1` to skip the xUnit suite.

4. **`DurableStoreFormat.Jsonl` throwing `NotSupportedException` is by design until #11 lands.**
   It is not a bug to fix in #9. Same for `Auto` not sniffing — that is #11/#13.

5. **`Auto` format sniffing is deliberately not implemented.** `Auto` currently means "path ⇒
   legacy, no path ⇒ in-memory". Do not implement sniffing in #9 or #10.

6. **The `squad/9-durable-store-provider-interface` branch was local-only until this session
   pushed it.** It is now on `origin` and tracks `origin/squad/9-durable-store-provider-interface`.
   Before this session it existed only locally — a fresh clone would not have had it.

7. **The branch has two commits and the first one does not compile.** `05a9562` is a `wip:`
   checkpoint; `4e5ca88` completes the refactor. Squash before opening a PR, or explain it.

8. **`.squad/decisions.md` is written by the Scribe via the state backend.** Do not hand-edit it.
   Use the `squad_decide` tool; the Scribe merges it. The current worktree copy was committed
   directly once because the Scribe's writes had never been committed — that is a one-off, not a
   pattern.

9. **The `IDurableStoreProvider` interface has 22 members, not 24.** If a handoff or plan says 24,
   the plan's §2 sketch is the source of truth and it lists 22. Verified by exact diff.

10. **`examples/` and `tests/` must not be touched** until #14 and #15 respectively. The #9 branch
    leaves both byte-identical to `origin/dev`.

---

## 10. Next actions

1. **Decide on the #9 PR.** The judgement above is that #9 is PR-ready. Recommended sequence:
   ```bash
   cd /Users/core/git/matthewcorven/github-copilot-sdk-durable
   git checkout squad/9-durable-store-provider-interface
   git fetch origin dev
   # optional: squash the wip checkpoint
   git rebase -i origin/dev        # squash 05a9562 into 4e5ca88, keep one commit
   git push --force-with-lease origin squad/9-durable-store-provider-interface
   gh pr create --base dev \
     --title "refactor(storage): define the durable store provider interface (#9)" \
     --body "Closes #9"
   ```
   The PR body must state: the interface matches plan §2 exactly; `DurableStore` is already a pure
   facade (so #10 is partly pre-empted); `Jsonl` throws and `Auto` does not sniff until #11/#13.

2. **Re-scope #10** before routing it. Read issue #10 and the plan's §2/§5, then decide what is
   genuinely left (public-surface compatibility sweep, `StoreSnapshot` types untouched until #13)
   versus what #9 already delivered. Update the issue body if the scope changed.

3. **Route #10 to Core Engineer** with the label `squad:core-engineer` (already applied) and the
   branch convention `squad/10-{slug}` cut from `origin/dev`.

4. **Keep the board honest.** #9 stays `In Progress` until its PR merges; #1 stays `In Progress`;
   nothing becomes `Done`. Flip with the `gh project item-edit` command in §8.

5. **Do not touch `main`.** `main` is release-only and receives promotion from `dev` via a PR.

6. **Keep the sandbox line (#16–#19) separate.** It is a parallel initiative. If the next session
   has capacity, it can be triaged independently — but it must not be folded into the #1 spine or
   used to block #10–#13.

7. **If the next session picks up #11 directly** (skipping #10), the JSONL provider must implement
   the full 22-member interface, honour the `{v, seq, type, payload}` envelope, apply `task`
   records last-wins per task id, and truncate a torn tail **at open** (not at the next commit).
