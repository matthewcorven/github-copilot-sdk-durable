# Architectural Comparison: github-copilot-sdk-durable vs. Pi Durable

A comparative analysis of the per-durable-agent architecture in this repository (`lib/Durable.cs`) versus the reference TypeScript implementation in Pi Durable (`@earendil-works/durable`).

---

## 1. Storage Engine & Transactions

| Component | Pi Durable (`@earendil-works/durable`) | This Project (`Durable.cs`) | Functional Difference & Why |
|---|---|---|---|
| **Storage Backends** | Pluggable SPI: `MemoryStorage`, `NodeSqliteStorage` (WAL mode), `JsonlStorage`, `IndexedDB`. | Two tiers: in-memory `Dictionary`, or a file selected by `DurableStoreFormat` — JSONL (default) or the legacy single-file JSON snapshot. | **Simplified**: No native SQLite binaries, so standalone file-based scripts (`dotnet run file.cs`) need no restore step. The JSONL backend appends one record per line and truncates a torn final line on replay, which gives crash safety without a WAL. |
| **Store Location** | Caller supplies a path or a storage instance. | Explicit `Path` wins; with no path, `DurableStore.DefaultStorePath()` resolves `%LOCALAPPDATA%` on Windows or `$XDG_STATE_HOME`/`~/.local/state` elsewhere, under `durable/`. | **Added**: Pi leaves location entirely to the caller. A per-user default means an app that says nothing still gets a durable store, and the store does not follow the working directory. |
| **Transaction Model** | ACID single-mutation line; transaction commit writes `entries`, `documents`, and `tasks` in one storage transaction. | Transaction boundary (`Transaction`) with in-memory `UndoLog` rollback. | **Identical externally**: If an operation throws mid-commit, changes are completely rolled back via the undo log. On success, the committed records are appended to the file and flushed. |
| **Snapshot Isolation** | Versioned vector clock & monotonic sequence per entity. | Global monotonic sequence counter (`_sequence`), restored on replay as the maximum of the persisted values. | **Approximated**: Pi maintains vector clocks across distributed shards. In single-process Copilot agent hosts, a monotonic sequence satisfies strict sequential consistency. The counter is only sound because a store file has a single writer: a persistent store holds an exclusive lock on `{path}.lock` for its lifetime, and a second process opening the same path is refused at open. |

---

## 2. Document System

| Component | Pi Durable | This Project | Functional Difference & Why |
|---|---|---|---|
| **History Policies** | `latest` (current state only) or `rewindable` (full version log). | `DocumentHistory.Latest` or `DocumentHistory.Rewindable`. | **Identical**: Rewindable documents retain all historical snapshots indexed by `entryId` and sequence. |
| **Fork Policies** | `asOf` (at fork cut), `current` (at fork time), `initial` (blank/factory). | `ForkPolicy.AsOf`, `ForkPolicy.Current`, `ForkPolicy.Initial`. | **Identical**: Forks copy documents based on these exact policies. |
| **Drafting / Mutation** | `tx.doc(Def, id)` returns a mutable Proxy draft; `save(entryId)` commits. | `tx.Doc(Def, id)` returns `DocumentDraft<T>`; `.Save(entryId)` stages. | **Identical**: Explicit staging per entry. |
| **Reactivity / Watches** | Bounded (100-frame) `WatchHandle<T>` with ring-buffer spill reset. | `WatchHandle<T>` over a bounded frame queue, fed by the store's commit notification. | **Identical**: both start from one stable acquisition revision, deliver to a single consumer one frame at a time, and replace an overflowing buffer with one full-value reset frame. The C# examples hand-roll the delivery loop over `System.Threading.Channels.Channel<T>`; the durable layer itself owns the primitive in both ports. |

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

---

## 7. The Python port (`lib/py/durable.py`)

The Python port follows `docs/DESIGN.md` §3 rather than the C# source's shape, and keeps the C#
type names so a reader can move between the two. Where Python has no equivalent for a C#
construct, the approximation is recorded here rather than left to differ silently.

| C# construct | Python approximation | Why, and what is preserved |
|---|---|---|
| `IDurableStoreProvider` (interface) | `DurableStoreProvider` (concrete base class raising `NotImplementedError`) | Python has no interfaces. The base class documents the same contract and fails loudly on a method a provider forgets, which is what the compiler would have caught. |
| `IDisposable.Dispose()` | `close()` | Python's disposal idiom. `close()` is idempotent, as `Dispose()` is. |
| `CommitTaskAsync` overloads (with and without a `Transaction`) | one `commit_task_async` that dispatches on the callback's arity via `inspect.signature` | Python has no overload resolution. A zero-argument callback runs without a transaction and without the undo log, exactly as the C# overload does. |
| `DocumentDefinition<T>` (generic) | `DocumentDefinition` with a `type` field and `deserialize()` | Python generics are erased at runtime, so the stored value's type has to be carried as data. `deserialize()` is what the C# generic argument gives for free. |
| `JsonElement` | `Any` (plain JSON values) | `System.Text.Json` has no Python counterpart. Values round-trip through `json`, and `DurableJson.to_wire` reproduces the C# serializer's camelCase, null-dropping shape. |
| Enums serialized as numbers | `IntEnum` with the C# numeric values, coerced back on read | The wire format is unchanged, so a store written by the C# layer reads in Python and the reverse. `_from_wire` resolves annotations with `typing.get_type_hints` because `from __future__ import annotations` makes `dataclasses.Field.type` a string. |
| `CancellationToken` | `asyncio.Event` | Python's cooperative cancellation. `_sleep` wakes early when the event is set, and `asyncio.CancelledError` breaks the task loop where `OperationCanceledException` would. |
| `DurableTaskRecord` is a reference type shared by store and runner | the runner re-reads `store.task(task_id)` at the top of every step | Python dataclasses are not shared by reference across the store boundary, so a captured record would never see the checkpoint a commit wrote. DESIGN §3.2 now states the rule as part of the contract. |
| `SemaphoreSlim(1, 1)` commit gate | `asyncio.Lock` | Same mutual exclusion, scoped to the event loop the store is used from. |
| `ConcurrentDictionary` provider registry | `dict` + `threading.Lock`, case-insensitive keys | The registry is process-wide and may be touched from any thread, so it keeps a real lock. |
| `FileStream` + `FileShare` lock | `fcntl.flock` on POSIX, `msvcrt.locking` on Windows, over a `{path}.lock` sidecar | Same guarantee: one writer per store file, and a second opener is refused with the holder named. |
| `Channel<T>` watch (example-level in C#) | `DurableWatch` / `DurableFrame` in the core | The C# core has no watch type; `examples/04-watches.cs` builds one over a `Channel<string>`. The Python port promotes it to a real type with a baseline frame, deltas, a 100-frame reset, and a single-consumer rule, and DESIGN §3.11 now specifies it. |
| `CopilotClient` / `CopilotSession` | official `github-copilot-sdk==1.0.28`, with an optional `client_factory` test seam | `open_async` starts a real SDK client by default. Create/resume use the SDK's keyword configuration and native fork RPC; unsupported runtime methods fail explicitly. `close_async` stops the client; synchronous core/store operations do not start SDK sessions. |

## 8. TypeScript port (`lib/ts/durable.ts`)

The durable core is ported to TypeScript so the same semantics can be exercised on the
JavaScript runtime. The port keeps the C# type names, so a reader can move between the two
files by name. Where TypeScript genuinely lacks an equivalent, the approximation is recorded
here rather than left to differ silently.

### 8.1 Type map

| C# (`lib/Durable.cs`) | TypeScript (`lib/ts/durable.ts`) |
|---|---|
| `JsonElement` / `JsonDocument` | `JsonValue` / `JsonObject`; `DurableJson.toElement` / `fromElement` |
| `ForkPolicy`, `DocumentHistory` | `ForkPolicy`, `DocumentHistory` (string-literal unions) |
| `DocumentDefinition<T>`, `DocumentVersion`, `EntryRecord`, `DocumentState` | same names |
| `TaskStatus`, `DurableTaskRecord`, `TaskOutcome`, `TaskCommit` | same names |
| `DurableTaskDefinition`, `DurableTask.Define` / `DefinePhases`, `TaskRuntime` | `DurableTaskDefinition`, `DurableTask.define` / `definePhases`, `TaskRuntime` |
| `ConversationRecord`, `ForkIntent`, `ForkIntentState`, `SessionBinding`, `InputAdmission`, `ConversationCheckpoint` | same names |
| `DurableRecoveryRequiredException` | `DurableRecoveryRequiredError` |
| `DurableStoreFormat`, `DurableStoreOptions`, `IDurableStoreProvider`, `TaskStateSnapshot` | same names |
| `InMemoryStoreProvider`, `DurableStore` | same names |
| `Transaction`, `DocumentDraft<T>`, `StoreSnapshot`, `DocumentSnapshot`, `DocumentVersionSnapshot` | same names |
| `Extension`, `ToolWrapper`, `PromptSection`, `SectionInput`, `ExtensionRegistry` | same names |
| `UndoLog` | `UndoLog` |
| `Check` | `Check` |
| `DurableHarnessOptions.RuntimePathEnvironmentVariable` | `DurableHarnessOptions.runtimePathEnvironmentVariable` |
| (no library type; hand-rolled in `examples/04-watches.cs`) | `WatchHandle<T>`, `WatchFrame<T>`, `WatchOptions`, `watchDocument` |

### 8.2 Approximations

| Area | C# | TypeScript | Why |
|---|---|---|---|
| **Commit gate** | `SemaphoreSlim(1, 1)` around every commit. | A promise-chain mutex (`gateTail` + `withGate`). | JavaScript has no blocking semaphore. Chaining the tail of the previous commit gives the same one-at-a-time ordering, and a rejected commit does not poison the chain. |
| **Cancellation** | `CancellationToken` / `CancellationTokenSource`. | `AbortSignal` / `AbortController`. | The platform equivalent. `AbortSignal` is one-shot, which matches the way the runner uses a token: a task that is cancelled leaves its checkpoint and is re-entered with a fresh signal. |
| **Durable timer** | `Task.Delay(delay, cancellationToken)`. | `setTimeout` wrapped in a promise that also settles on `abort`. | No ambient timer type. The wrapper clears the timer on abort so a cancelled sleep does not keep the process alive. |
| **Sequence ceiling** | `long.MaxValue` for an unknown entry. | `Number.MAX_SAFE_INTEGER`. | `number` is a double. The store's sequence is a commit counter, so the safe-integer ceiling is far above any reachable value; the sentinel only has to sort last. |
| **JSON boundary** | `JsonSerializer` with `JsonSerializerDefaults.Web` and `WhenWritingNull`. | `DurableJson` clones structurally and drops `undefined`/`null` members on write. | No serializer in the core. `TaskOutcome.toJson` / `fromJson` are the explicit boundary for the one type that is persisted as an object rather than as caller-supplied JSON. |
| **`Transaction.AppendEntry`** | `kind` and `data` are accepted and unused; the entry record is `("entry", null)`. | Same, with `_kind` / `_data` and a comment. | Ported faithfully rather than "fixed", so the two implementations stay comparable. |
| **Watch delivery** | Unbounded `Channel<T>` in the example; the design specifies a bounded 100-frame handle. | Bounded queue with a serialized drain loop; overflow becomes one `reset` frame carrying the dropped count. | The design's contract, implemented once in the library instead of per example. |
| **File backends** | `JsonlStoreProvider`, `LegacyJsonStoreProvider`, `StoreFileLock`, `DetectFormat`. | Shared v1 JSONL envelopes, append/fsync with rollback, exact UTF-8 line offsets and torn-tail recovery. Legacy JSON is explicitly unsupported. | JSONL discards every unterminated final record, even valid JSON, while retaining CRLF/mixed line boundaries. New writes use LF. `Auto` selects JSONL, not legacy migration. File locking requires the optional native `fs-ext` addon and working `flock`; missing/unsupported locking fails explicitly. |
| **Session-bound layer** | `DurableConversation`, `DurableHarness`, `AgentChoices`, `ForkAsync`, `BookkeepingEntries`, `PermissionGrant`. | Official `@github/copilot-sdk@1.0.28` binding, native create/resume/fork, admission and checkpoint documents, prompt sections, tool overrides/wrappers, permission callbacks and sandbox configuration. | SDK handler signatures and generated event shapes are preserved. Visible entries exclude internal document anchors across ancestry, not application entries with similar kinds. Root reuses its original choices; configure replaces the entire choices snapshot; fork copies choices/documents as of its durable boundary. |

### 8.3 SDK compatibility and evidence boundaries (TypeScript and Python)

Both ports serialize submission through the actual SDK `send` method, subscribe before
sending, and wait for root assistant/idle events. The recorded host's legacy completion
reply is incompatible with SDK 1.0.28's convenience `sendAndWait`/`send_and_wait`; the ports
do not manufacture completion events. Tool checkpoints are drained before returning a
successful submission. SDK effective-model events, not requested choices, populate model
reports: missing reports stay null/`NotReported`, including turns without their own model.
Reasoning options are sent only for an explicitly selected non-auto model advertising
reasoning efforts. Durable strings such as the reference's `minimal` pass unchanged:
the TypeScript SDK's narrower static union is not a native runtime capability check.
ReplayRuntime model discovery returns a `list` envelope whereas the TypeScript convenience
method expects `models` and generated RPC types declare an array; the adapter accepts these
wire shapes and rejects malformed replies.

Both SDKs expose sandbox configuration. The ports map filesystem/network/auth/credential
fields, bypass flags, and `userPolicy.seatbelt.keychainAccess` without using deprecated
`experimental.seatbelt`. Wire configuration tests do not prove OS enforcement. TypeScript
observes raw `sandbox.decision` events when delivered, but SDK 1.0.28's generated session
event union does not declare that event. Native context fork uses the SDK's `sessions.fork`
RPC with a null native event boundary, like C#; only durable documents/entries are bounded
by the chosen durable entry. ReplayRuntime does not implement native fork and is not
a substitute for native-context evidence.

Focused tests under each library exercise the real SDK with ReplayRuntime fixtures,
including lifecycle/reopen, model reporting, and tool override/wrapper invocation. The
runtime explicitly marks new session IDs as **synthesised**: these are SDK interoperability
and fixture lifecycle checks, not LIVE model results or independently recorded transcripts.
Replay fixtures without permission requests, sandbox decisions or original system/user
history cannot prove those native assertions. The example range owners validate originals
separately; shared support does not imply that all examples pass.

TypeScript JSONL locking is verified on macOS with `fs-ext@2.1.1`, including second-writer
refusal and release after killing the lock holder. Its source uses POSIX `flock` on Linux/macOS
and Windows `LockFileEx`/`UnlockFileEx`, but Linux/Windows installation and runner execution
are not verified here. A compiled addon and supported local filesystem are required;
unavailable native locking fails explicitly, not through an unlocked fallback. NFS is not
supported by this locking contract. Python retains
its POSIX `fcntl`/Windows `msvcrt` provider locks. Python SDK 1.0.28 currently emits unclosed
subprocess-pipe `ResourceWarning`s on Python 3.15 after passing integration checks; the
adapter calls the public SDK stop method, rather than modifying private SDK process state.
