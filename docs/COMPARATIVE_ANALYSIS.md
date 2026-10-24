# Architectural Comparison: github-copilot-sdk-durable vs. Pi Durable

A comparative analysis of the per-durable-agent architecture in this repository (`lib/Durable.cs`) versus the reference TypeScript implementation in Pi Durable (`@earendil-works/durable`).

---

## 1. Storage Engine & Transactions

| Component | Pi Durable (`@earendil-works/durable`) | This Project (`Durable.cs`) | Functional Difference & Why |
|---|---|---|---|
| **Storage Backends** | Pluggable SPI: `MemoryStorage`, `NodeSqliteStorage` (WAL mode), `JsonlStorage`, `IndexedDB`. | Pluggable providers behind `DurableStore` (`IDurableStoreProvider`): in-memory (`InMemoryStoreProvider`, the ephemeral tier), append-only JSONL (`JsonlStoreProvider`, the canonical durable tier), and the legacy single-file JSON snapshot (`LegacyJsonStoreProvider`, read backward-compatibly behind `new DurableStore(path)`). | **Parallel**: The JSONL provider is the direct analogue of Pi's `JsonlStorage` — one UTF-8 JSON record per line (envelope `{"v":1,"seq":N,"type":"<kind>","payload":{...}}`), appended on every commit and flushed before the commit returns; a torn trailing line is discarded on open and the file is truncated back to the last complete record. The legacy provider keeps the original atomic temp-file write + rename (`File.Move(tmp, path, overwrite: true)`), so no native SQLite binaries are needed in standalone file-based scripts (`dotnet run file.cs`). An explicit `Path` always wins; without one, a persistent store resolves the per-user default (`LocalApplicationData` on Windows; `$XDG_STATE_HOME` else `~/.local/state` elsewhere), in a `durable` subdirectory, named by `DurableStoreOptions.FileName`. |
| **Transaction Model** | ACID single-mutation line; transaction commit writes `entries`, `documents`, and `tasks` in one storage transaction. | Transaction boundary (`Transaction`) with in-memory `UndoLog` rollback; the facade owns the commit gate. | **Identical externally**: If an operation throws mid-commit, changes are completely rolled back via the undo log. On success, the JSONL backend appends the transaction's records to the file flushed; the legacy backend serializes the whole snapshot atomically. |
| **Snapshot Isolation** | Versioned vector clock & monotonic sequence per entity. | Global monotonic sequence counter (`_sequence`). | **Approximated**: Pi maintains vector clocks across distributed shards. In single-process Copilot agent hosts, a monotonic sequence satisfies strict sequential consistency. |

---

## 2. Document System

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **History Policies** | `latest` (current state only) or `rewindable` (full version log). | `DocumentHistory.Latest` or `DocumentHistory.Rewindable`. | **Identical**: Rewindable documents retain all historical snapshots indexed by `entryId` and sequence. |
| **Fork Policies** | `asOf` (at fork cut), `current` (at fork time), `initial` (blank/factory). | `ForkPolicy.AsOf`, `ForkPolicy.Current`, `ForkPolicy.Initial`. | **Identical**: Forks copy documents based on these exact policies. |
| **Drafting / Mutation** | `tx.doc(Def, id)` returns a mutable Proxy draft; `save(entryId)` commits. | `tx.Doc(Def, id)` returns `DocumentDraft<T>`; `.Save(entryId)` stages. | **Identical**: Explicit staging per entry. |
| **Reactivity / Watches** | Bounded (100-frame) `WatchHandle<T>` with ring-buffer spill reset. | `System.Threading.Channels.Channel<T>` single-consumer stream. | **Language runtime difference**: Pi implements a custom 100-frame dropped-frame recovery protocol. C# uses unbounded thread-safe `Channel<T>` ensuring non-overlapping asynchronous deliveries. |

---

## 3. Task Runtime & Scheduling

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **Task State Machine** | Checkpointed state machine (`pending`, `running`, `waiting`, `sleeping`, `terminal`). | `TaskStatus.Pending`, `Running`, `Waiting`, `Sleeping`, `Terminal`. | **Identical**: Tasks advance through named phases, checkpointing JSON states at each step. |
| **Idempotency Memos** | `runtime.memo(key, compute)` stored in task state. | `runtime.MemoAsync(key, value)` stored in `DurableTaskRecord.Memos`. | **Identical**: Memos survive host crashes, preventing side effects from repeating upon resume. |
| **Durable Timers / Sleep** | `runtime.sleepUntil(deadline, nextPhase)` stores sleep in checkpoint. | `runtime.SleepUntilAsync(deadline, nextCheckpoint)` persists wake time. | **Identical**: Wake-up deadline is stored with the checkpoint. |
| **Child Tasks & Aborts** | Parent waits on children; first failure aborts siblings (`failFast`). | `TaskCommit.Waiting` + `WaitForChildrenAsync` + cascading `AbortTaskAsync`. | **Identical**: Aborting a parent cascades to cancel all child tasks and execute their abort handlers. Terminal states cannot be overwritten. |

---

## 4. Conversation & LLM Integration

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **LLM Provider** | Pluggable `Models` abstraction (`faux`, Anthropic, OpenAI, custom). | `CopilotClient` over JSON-RPC to GitHub Copilot CLI runtime. | **Architectural difference**: Pi controls the LLM generation loop natively (`GenerationTask`). Here, the Copilot SDK owns the generation loop and session socket; our layer drives it via `SessionConfig`, hooks, and `Session.SetToolsAsync`. |
| **Context vs. Store** | Dual-plane: store contains application documents/entries; model context is a bounded sliding view. | Dual-plane: `DurableStore` contains application documents/entries; `CopilotSession` contains SDK events (`events.jsonl`). | **Identical concept**: Decouples application state persistence from the model's chat history. |
| **Prompt Sections** | `section(name, (input) => ...)` rendered before each generation step. | `PromptSection` rendered via `SessionHooks.OnUserPromptSubmitted` into `AdditionalContext`. | **SDK constraint**: Copilot SDK creates sessions with a base system message. Dynamic sections that read live documents must be injected per turn via the `OnUserPromptSubmitted` hook. |

---

## 5. Extension & Tool Registry

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **Registry & Precedence** | Extensions installed by name; later installed tool with same name replaces earlier in-place. | `ExtensionRegistry` with "later wins" resolution order. | **Identical**: Stored choices are strings (names); replacing an extension dynamically binds to the newly installed code. |
| **Tool Wrapping** | `wrapTool(base, (t) => wrapped)` wraps whichever tool won. | `ToolWrapper` subclassing `AIFunction` decorating whichever tool won. | **Identical**: The wrapper decorates the tool that won without capturing a base instance. |

---

## 6. Submissions & Inbox

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **Submission Model** | Submissions are durable documents in the `pi.inbox` namespace. | Handled via in-flight turn gating (`_busy` flag) and SDK queuing. | **Approximated**: Pi tracks submission receipts inside durable documents. Here, turn coordination tracks busy state while the underlying Copilot session serializes messages. |
