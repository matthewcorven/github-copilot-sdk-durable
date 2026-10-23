# Implementation Plan — Issue #1

**Epic:** [#1 Add a pluggable durable-store provider abstraction with a path-configurable JSONL backend](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/1)
**Plan date:** 2026-10-23
**Status:** planned — no implementation started
**Plan owner:** Lead

## 1. Task list (dependency-ordered)

| # | Task | Owner | Issue | Depends on | Acceptance criteria (summary) |
|---|---|---|---|---|---|
| 1 | Define the durable store provider interface | Core Engineer | [#9](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/9) | #1 | Public provider interface exists; no behaviour change; 4/4 existing facts pass; 31/31 examples compile |
| 2 | Refactor `DurableStore` into a facade over a backend | Core Engineer | [#10](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/10) | #9 | `DurableStore` delegates to a provider; public surface source-compatible; `examples/12`, `22`, `30` unchanged; 4/4 facts pass |
| 3 | Implement the JSONL append-only backend | Core Engineer | [#11](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/11) | #9, #10 | One JSON record per line, flushed per commit; torn tail discarded on reopen; earlier records survive |
| 4 | Add `DurableStoreOptions` and per-user default path resolution | Core Engineer | [#12](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/12) | #9, #10 | Explicit `Path` wins; default path resolves per platform rule; `DurableStore(string? path = null)` still compiles |
| 5 | Add a backward-compatible reader for the existing single-file JSON store | Core Engineer | [#13](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/13) | #9, #10, #11 | Legacy `StoreSnapshot` file opens and round-trips; legacy file is read-only by default; clear error on unparsable file |
| 6 | Add `examples/31-jsonl-store.cs` and update the README example table | Docs | [#14](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/14) | #10, #11, #12 | New example asserts JSONL file exists and reopened store returns the same document value; README row for `31`; 32/32 examples compile |
| 7 | Add xUnit facts for the JSONL backend, path options, and legacy reader | Tester | [#15](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/15) | #11, #12, #13 | 5 new named facts in `tests/Durable.Tests/DurableStoreTests.cs`; `dotnet test` reports 9/0 |

Tasks 1–5 are the implementation spine (one owner, strictly serial: #9 → #10 → #11 → #12 → #13). Tasks 6 and 7 are consumers of that spine and start once their dependencies land.

### New xUnit fact names (issue #15)

- `JsonlBackend_SurvivesReopenWithIdenticalState`
- `JsonlBackend_DiscardsTruncatedFinalLine`
- `DurableStoreOptions_ExplicitPathWinsOverDefaultPath`
- `DurableStoreOptions_DefaultPathResolvesUnderPerUserDirectory`
- `LegacyJsonStore_OpensAndRoundTrips`

Existing facts that must keep passing unchanged: `FailedTransaction_RollsBackAllWrites`, `Fork_InheritsOnlyEntriesUpToForkPoint_AndHasIndependentState`, `Recovery_ReopenedStoreResumesFromSavedCheckpointWithMemoIdempotency`, `Abort_TerminatesParentAndChildrenDurably`.

## 2. Provider interface contract (method surface only)

Shape sketch, not final names — the names below are the contract that issues #9–#13 implement. Types reuse the existing public records (`ConversationRecord`, `DurableTaskRecord`, `DocumentState`, `EntryRecord`, `DocumentVersion`, `ForkPolicy`).

```csharp
public interface IDurableStoreProvider
{
    // ── lifecycle ──────────────────────────────────────────────────────────
    void Open(DurableStoreOptions options);          // load committed state (or start empty)
    void Commit();                                   // flush the committed transaction to the backend

    // ── read surface ──────────────────────────────────────────────────────
    long Sequence { get; }
    IReadOnlyCollection<ConversationRecord> Conversations { get; }
    IReadOnlyCollection<DurableTaskRecord> Tasks { get; }
    IReadOnlyCollection<DocumentState> Documents { get; }
    ConversationRecord? Conversation(string id);
    DurableTaskRecord? Task(string id);
    DocumentState? Document(string kind, string ownerId);
    IReadOnlyList<string> Entries(string conversationId);
    long SequenceOf(string entryId);

    // ── write surface (called by Transaction / UndoLog) ────────────────────
    long NextSequence();
    void AddConversation(ConversationRecord record);
    void AddEntry(string conversationId, string entryId);
    void CopyEntries(string fromConversationId, string toConversationId, string? forkEntryId);
    DocumentState EnsureDocument(string kind, string ownerId, ForkPolicy fork);
    void RecordDocumentVersion(DocumentState state, string entryId, JsonElement value);
    void CopyDocument(DocumentState source, string toOwner, string? forkEntryId);
    DurableTaskRecord AddTask(DurableTaskRecord record);
}
```

Notes:

- `UndoLog` and the `SemaphoreSlim` commit gate stay in the facade, **not** in the provider — rollback is a facade concern.
- The provider is synchronous. `DurableStore.CommitAsync` keeps its async signature and its `await _gate.WaitAsync()` ordering.
- `DurableStore` keeps `public DurableStore(string? path = null)` and gains an additive overload taking `DurableStoreOptions`.

## 3. JSONL record format

One JSON object per line, UTF-8, `\n`-terminated, appended in write-sequence order. Every line is a self-contained record; a reader never needs a record before it to interpret it.

Proposed envelope:

```json
{"v":1,"seq":42,"type":"document-version","payload":{ ... }}
```

| Field | Meaning |
|---|---|
| `v` | Record format version (`1`). Lets a future reader migrate. |
| `seq` | The store's monotonic write sequence at the time the record was written. |
| `type` | Record kind: `conversation`, `task`, `entry`, `document`, `document-version`. |
| `payload` | The record body; shape matches the existing public type for that kind. |

`document-version` payloads carry `{ "kind", "ownerId", "fork", "entryId", "value" }` — a version is append-only, so a new version is a new record rather than an update of an earlier one. A `document` record carries the document's identity (`kind`, `ownerId`, `fork`) and is emitted once, before its first version.

### Torn-tail recovery semantics

1. Read the file line by line. A line is **complete** when it is terminated by `\n` **and** parses as a JSON object.
2. If the final line is not complete — no trailing newline (a partial write), or the JSON does not parse — it is **discarded**. Everything before it is kept.
3. The store then rewrites the file truncated at the last complete record boundary, so the next append starts from a clean state and the torn fragment cannot be misread later.
4. Recovery is silent and non-fatal: a torn tail is an expected outcome of a crash, not an error. A file that does not parse as the JSONL format *at all* (e.g. it is the legacy single-file JSON store) is not a torn tail — it routes to the legacy reader (issue #13) or, if that also fails, raises a clear error.
5. No checksum is proposed for v1. Rationale: the append is flushed per commit and a truncated write is the realistic failure mode; a per-line checksum would add cost without changing the recovery outcome. Revisit if partial-but-parseable writes are observed.

## 4. Default path resolution rule

Confirmed against issue #1's *In scope* bullet and `docs/DESIGN.md` §3.1 (which currently documents only the "single JSON-file-backed store per example (or in-memory)" model — it does not yet state a per-user default, so §3.1 needs the new rule added).

| Platform | Default location |
|---|---|
| Windows | `Environment.SpecialFolder.LocalApplicationData` (e.g. `C:\Users\<user>\AppData\Local`) |
| macOS / Linux / other | `$XDG_STATE_HOME` when set and non-empty, otherwise `~/.local/state` |

Under that directory, the store writes a well-known file name (proposed: `durable-store.jsonl` for the JSONL backend). The explicit `Path` in `DurableStoreOptions` always wins; the default is used only when `Path` is `null`.

Environment-variable overrides beyond `XDG_STATE_HOME` are out of scope for this epic.

## 5. Risks / open questions (need a human decision)

| # | Question | Why it matters | Proposal |
|---|---|---|---|
| 1 | Exact record envelope shape | The `{"v","seq","type","payload"}` envelope above is a proposal. A flatter shape (type-tagged fields, no `payload` nesting) is equally valid and changes the on-disk format permanently once shipped. | Accept the envelope above, `v: 1`. |
| 2 | When does legacy `StoreSnapshot` conversion happen — on open, or on first commit? | Converting on open rewrites state eagerly (and would need to write back); converting on first commit leaves the legacy file untouched longer but makes "the store is now JSONL" non-obvious. | Convert **on open, into memory only**; the legacy file is never rewritten unless the caller opts in. |
| 3 | How does `DurableStoreOptions` coexist with `DurableStore(string? path = null)`? | The existing constructor is used by `examples/12-recovery.cs:20`, `examples/22-subagent-background.cs:22`, `examples/30-reload-and-restart.cs:32` and possibly downstream code. Changing or removing it is a breaking change; keeping both risks ambiguous overload resolution. | Keep `DurableStore(string? path = null)` verbatim; add an **additive** `DurableStore(DurableStoreOptions options)` overload. No deprecation in this epic. |
| 4 | Does the legacy reader write back a converted file? | If it silently rewrites `session.json` into JSONL, a user's existing file is mutated by an "open". | No implicit write-back. Read-only by default. |
| 5 | Is the default path per-user, per-project, or per-workspace? | A single per-user default means two different projects in the same user account share one store file unless the caller sets an explicit path. | Per-user for this epic (matches the issue). Per-project scoping is a possible follow-up. |
| 6 | Is a per-line checksum needed? | See §3.5. A checksum would detect silent bit corruption, not just truncation. | Defer; a torn tail is the realistic failure mode. |
| 7 | Should the JSONL backend be the default when no path is given? | The current default is in-memory. Making JSONL the default would surprise callers (and write to disk unexpectedly). | Keep in-memory as the default. JSONL is opt-in via options or a path. |
| 8 | Where do `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose changes land? | Issue #12's acceptance criteria reference the doc updates, but issue #14 owns docs-adjacent work. Currently split across two owners. | Assign the §3.1 / §1 prose to the Docs owner (issue #14) so the doc reads consistently with the README table. |

## 6. Branch & PR workflow

This epic follows the repo's `dev` integration branch model:

| Branch | Purpose | Work lands here? |
|--------|---------|------------------|
| `dev` | Integration branch — all feature work merges here | **Yes** |
| `main` | Release branch — released/tagged code only | Only via promotion from `dev` |

Per child issue (#9–#15):

```bash
git fetch origin dev
git checkout dev && git pull origin dev
git checkout -b squad/{issue-number}-{slug} origin/dev
# ... work, commit with issue reference ...
git push -u origin squad/{issue-number}-{slug}
gh pr create --base dev --title "{description}" --body "Closes #{issue-number}" --draft
```

Release tags are cut after `dev` is promoted forward to `main` — tags are never cut from a feature branch.

**Enforcement (applied 2026-10-23):**

| Setting | Value |
|---------|-------|
| Repository default branch | `dev` |
| `dev` protection | PR required; no force-push; no deletion |
| `main` protection | PR required; no force-push; no deletion |
| Coding-agent base branch | `dev` (falls back to the repo default if `dev` is absent) |

## 7. Verification gates

- `dotnet build tests/Durable.Tests` succeeds.
- `dotnet test tests/Durable.Tests` — 4/0 before any change; 9/0 after issue #15.
- `COMPILE_ONLY=1 ./scripts/validate.sh` — `PASS` for 31 examples before the change, 32 after issue #14. Invoke with a name (`./scripts/validate.sh 00-conversation`); the validator resolves `examples/<arg>.cs`, not bare numbers.
- No change to durable semantics: fork policies, memo idempotency, and `UndoLog` rollback stay byte-for-byte equivalent.
