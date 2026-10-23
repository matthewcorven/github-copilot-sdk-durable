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
    void BeginTransaction();                          // start an undo log for the transaction in flight
    void Rollback();                                 // undo everything the failed transaction wrote
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
    void UpdateTask(DurableTaskRecord record);       // task records mutate in place after creation
}
```

Notes:

- **`UpdateTask` is required.** `DurableTaskRecord` is mutated in place after creation — `task.Status`, `task.Checkpoint`, `task.WaitingOn`, `task.Memos` are all assigned directly (`lib/Durable.cs:632-651`, `lib/Durable.cs:703`). `MemoAsync` writes a memo and persists without any transaction (`lib/Durable.cs:703`). A provider that only saw `AddTask` would never learn about those changes, so the JSONL backend would lose task progress across a reopen. `UpdateTask` emits the task's full current state; the JSONL reader applies task records last-wins per task id.
- **The `UndoLog` moves into the provider**, because the provider owns the collections the log protects. `UndoLog.Undo(DurableStore store)` does not actually read its `store` argument — it only replays recorded inverse actions (`lib/Durable.cs:2130-2138`) — so the signature change to `Undo(IDurableStoreProvider provider)` is behaviour-preserving. The `SemaphoreSlim` commit gate stays in the facade.
- The provider is synchronous. `DurableStore.CommitAsync` keeps its async signature and its `await _gate.WaitAsync()` ordering.
- `DurableStore` keeps `public DurableStore(string? path = null)` verbatim and gains a **static factory** `DurableStore.Create(DurableStoreOptions options)` — see §5, question 3.

### 2.1 Provider implementations and format detection

Three providers ship in this epic:

| Provider | Selected when | On-disk format |
|----------|---------------|----------------|
| `InMemoryStoreProvider` | no path given, or `Format = InMemory` | none |
| `JsonlStoreProvider` | `Format = Jsonl`, or `Format = Auto` and the file looks like JSONL | one JSON record per line |
| `LegacyJsonStoreProvider` | `Format = LegacyJson`, or `Format = Auto` and the file looks like a `StoreSnapshot` | one JSON object (today's format) |

`Format = Auto` sniffs the file at open:

1. File does not exist → empty store, no format yet.
2. First non-empty line parses as a JSON object carrying a string `type` property → **JSONL**.
3. Otherwise the whole file parses as a single JSON object → **legacy `StoreSnapshot`**.
4. Otherwise → `InvalidDataException` naming the path and the reason.

Sniffing is unambiguous because the legacy writer emits exactly one JSON object with no `type` property, while every JSONL record carries one. `Auto` never destroys data: a legacy file is opened by the legacy provider and written back in the legacy format.

Forcing `Format = Jsonl` on a path that holds a legacy snapshot is the explicit "convert this store" action: the snapshot is read and converted into memory on open, and the first commit writes JSONL to that path, replacing the legacy file. Forcing `Format = LegacyJson` on a JSONL file is rejected with a clear error rather than silently discarding the JSONL records.

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

**Resolved: the envelope is `{v, seq, type, payload}`.** Rationale for keeping the nesting rather than flattening:

- `type` sits at the top level, so format sniffing (§2.1) can identify a JSONL file from its first line without knowing the payload shape.
- `v` sits at the top level, so a future format version can be detected and migrated without parsing payloads.
- `payload` keeps each record's body in one place, so a reader can deserialize it into the existing public type (`ConversationRecord`, `DurableTaskRecord`, `EntryRecord`, …) with `DurableJson.Options` and no per-type field mapping. A flat envelope would need a bespoke mapper per record kind and would drift from the public types.
- The cost is ~30 bytes of overhead per record. At the scale this store targets (a session transcript, not a log aggregator) that is irrelevant, and the format is append-only and human-inspectable, which is the point of JSONL.

**Record kinds and their payloads:**

| `type` | Payload | Emitted when |
|--------|---------|--------------|
| `conversation` | the `ConversationRecord` | a conversation is created |
| `entry` | `{ "conversationId", "entryId" }` | an entry is appended to a conversation |
| `entry-record` | the `EntryRecord` | an entry is appended (carries the entry's write sequence) |
| `document` | `{ "kind", "ownerId", "fork" }` | a document is first written |
| `document-version` | `{ "kind", "ownerId", "fork", "entryId", "value" }` | a document version is recorded |
| `task` | the `DurableTaskRecord` | a task is created or updated |

`entry` and `entry-record` are separate because the store keeps two structures: the per-conversation ordered id list (`_entries`) and the id→record map (`_entryRecords`). `CopyEntries` rewrites the ordered list without creating new entry records, so the two must be distinguishable.

`task` records are **last-wins per task id**: the reader applies them in file order, so the final state of a task is the last `task` record for that id. This is what makes `UpdateTask` work across a reopen.

### Torn-tail recovery semantics

1. Read the file line by line. A line is **complete** when it is terminated by `\n` **and** parses as a JSON object.
2. If the final line is not complete — no trailing newline (a partial write), or the JSON does not parse — it is **discarded**. Everything before it is kept.
3. The store then rewrites the file truncated at the last complete record boundary, so the next append starts from a clean state and the torn fragment cannot be misread later.
4. Recovery is silent and non-fatal: a torn tail is an expected outcome of a crash, not an error. A file that does not parse as the JSONL format *at all* (e.g. it is the legacy single-file JSON store) is not a torn tail — it routes to the legacy reader (issue #13) or, if that also fails, raises a clear error.
5. No checksum is proposed for v1. Rationale: the append is flushed per commit and a truncated write is the realistic failure mode; a per-line checksum would add cost without changing the recovery outcome. Revisit if partial-but-parseable writes are observed.

**Resolved: no checksum in v1.** The realistic failure mode is a truncated final write, which the newline-termination rule already catches. A checksum would detect silent bit corruption, which is a different threat (disk-level corruption) and not what this epic is solving. Adding it later is a format-version bump, not a breaking change, because `v` is already in the envelope.

**Resolved: the torn tail is truncated on open, not on the next commit.** Truncating at open means the file is immediately clean, so a reader that opens the store read-only sees a valid file, and a crash between open and the first commit cannot leave the fragment to be misread a second time. The truncation is a rewrite of the file to the last complete record boundary — it is safe because the discarded bytes were never a complete record.

## 4. Default path resolution rule

Confirmed against issue #1's *In scope* bullet and `docs/DESIGN.md` §3.1 (which currently documents only the "single JSON-file-backed store per example (or in-memory)" model — it does not yet state a per-user default, so §3.1 needs the new rule added).

| Platform | Default location |
|---|---|
| Windows | `Environment.SpecialFolder.LocalApplicationData` (e.g. `C:\Users\<user>\AppData\Local`) |
| macOS / Linux / other | `$XDG_STATE_HOME` when set and non-empty, otherwise `~/.local/state` |

Under that directory, the store writes a well-known file name (proposed: `durable-store.jsonl` for the JSONL backend). The explicit `Path` in `DurableStoreOptions` always wins; the default is used only when `Path` is `null`.

Environment-variable overrides beyond `XDG_STATE_HOME` are out of scope for this epic.

**Resolved: the default path is per-user, and the file name is `durable-store.jsonl`.** The per-user directory is the right scope for this epic because the issue asks for a "documented per-user default location", and the store's own default is in-memory — a caller who wants persistence opts in, and the opt-in is "give me the standard place". The known downside is that two projects under the same user account share one file unless the caller sets an explicit path; that is acceptable because the default is opt-in and the explicit path is one property away. Per-project scoping is a possible follow-up, not a blocker.

The default path is only used when the caller asks for a persistent store *and* supplies no path. `DurableStoreOptions.Path` being `null` with `Format = InMemory` never touches the filesystem.

## 5. Resolved design decisions

All eight open questions are resolved. The user delegated the remaining judgement calls on 2026-10-23; the resolutions below are the contract the implementation issues build against.

| # | Question | Resolution | Rationale |
|---|---|---|---|
| 1 | Exact record envelope shape | **`{v, seq, type, payload}`**, `v: 1` | `type` and `v` at the top level make format sniffing and future migration possible without parsing payloads; `payload` deserializes straight into the existing public record types with `DurableJson.Options`, so there is no bespoke per-kind mapper to drift. ~30 bytes/record is irrelevant at session-transcript scale. See §3. |
| 2 | When does legacy `StoreSnapshot` conversion happen? | **On open, into memory only.** The legacy file is never rewritten unless the caller explicitly forces `Format = Jsonl` on that path. | Converting on open means the store is immediately usable in the new format and the conversion cost is paid once. Keeping the legacy file untouched means "open" never mutates a user's data. The explicit `Format = Jsonl` opt-in is the documented "convert this store" action. |
| 3 | How does `DurableStoreOptions` coexist with `DurableStore(string? path = null)`? | **Keep `DurableStore(string? path = null)` verbatim; add a static factory `DurableStore.Create(DurableStoreOptions options)`.** No deprecation in this epic. | A constructor overload would be ambiguous for `new DurableStore(null)` — the existing `string?` overload and a `DurableStoreOptions` overload would both match, and the compiler could not pick. A static factory has no overload-resolution interaction at all, so the existing constructor stays byte-for-byte source-compatible. `examples/12-recovery.cs:20`, `examples/22-subagent-background.cs:22` and `examples/30-reload-and-restart.cs:32` need no edits. |
| 4 | Does the legacy reader write back a converted file? | **No implicit write-back.** Read-only by default; conversion happens only when the caller forces `Format = Jsonl`. | "Open" must not mutate a user's file. The conversion path is explicit and documented. |
| 5 | Is the default path per-user, per-project, or per-workspace? | **Per-user** for this epic. | Matches the issue's wording. The default is opt-in and the explicit path is one property away, so the shared-file downside is acceptable. Per-project scoping is a follow-up. |
| 6 | Is a per-line checksum needed? | **No checksum in v1.** | The realistic failure mode is a truncated final write, which the newline-termination rule already catches. A checksum addresses silent bit corruption, a different threat. Adding it later is a `v` bump, not a breaking change. |
| 7 | Should the JSONL backend be the default when no path is given? | **No — in-memory stays the default.** JSONL is opt-in via `DurableStoreOptions`. | Making JSONL the default would write to disk unexpectedly and change the behaviour of every existing caller. The in-memory default is the current contract and stays. |
| 8 | Where do the `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 prose changes land? | **Issue #14 (Docs).** Issue #12's acceptance criteria are amended to drop the doc prose and keep only the path-resolution behaviour. | One owner per document keeps the prose consistent with the README table it sits next to. Splitting the same two sections across two owners invites contradictory wording. |

### 5.1 `DurableStoreOptions` shape

```csharp
public sealed class DurableStoreOptions
{
    /// <summary>The explicit file path. When null, the per-user default path is used.</summary>
    public string? Path { get; init; }

    /// <summary>The on-disk format. Auto sniffs the file at open.</summary>
    public DurableStoreFormat Format { get; init; } = DurableStoreFormat.Auto;

    /// <summary>The file name used under the per-user default directory.</summary>
    public string FileName { get; init; } = "durable-store.jsonl";
}

public enum DurableStoreFormat
{
    Auto,        // sniff the file at open
    InMemory,    // never touch the filesystem
    Jsonl,       // one JSON record per line
    LegacyJson,  // today's single-file StoreSnapshot
}
```

`DurableStoreOptions` is a plain immutable class with `init` accessors, matching the style of the existing public records in `lib/Durable.cs`.

### 5.2 Default path resolution

```csharp
public static string DefaultStorePath(string fileName = "durable-store.jsonl")
{
    var root = OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : (Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"));

    return Path.Combine(root, "durable", fileName);
}
```

The `durable` subdirectory keeps the store out of the way of other state in the shared directory. The directory is created on first write, not on open — opening a store must not create directories as a side effect.

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
