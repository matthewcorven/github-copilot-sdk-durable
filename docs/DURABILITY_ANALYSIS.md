# Why this Layer is Necessary: Durability in the GitHub Copilot SDK

An analysis of what the GitHub Copilot SDK provides natively, what it lacks for autonomous agent durability, and what this library proves and accomplishes.

---

## 1. What GitHub Copilot SDK Natively Provides

The GitHub Copilot SDK is an RPC client layer managing sessions hosted by the GitHub Copilot CLI runtime (`copilot`). Its native persistence features focus on **chat conversation persistence**:

* **Session Event Log (`~/.copilot/session-state/{sessionId}/events.jsonl`)**:
  * An append-only log of granular conversational events (`session.start`, `user.message`, `assistant.message`, `tool.execution_start`, `tool.execution_complete`, `hook.start`, etc.).
* **`ResumeSessionAsync(sessionId, resumeConfig)`**:
  * Rebuilds conversational transcript context from `events.jsonl`, allowing subsequent requests to retain prior conversation history without re-sending tokens over the wire.
* **`client.Rpc.Sessions.ForkAsync(sessionId, toEventId, name)`**:
  * Clones a session's event log up to a specific event ID to branch the conversation.
* **`InfiniteSessions` (Session Compaction)**:
  * Manages context window limits by summarizing older messages into compaction events.

---

## 2. What the Copilot SDK Natively Lacks (The Durability Gap)

While the SDK provides durable **transcripts**, it provides no durable **application or agent state**:

| Durability Dimension | Native Copilot SDK Behavior | What This Library Adds |
|---|---|---|
| **Application Document State & Versioning** | **None.** The SDK only records raw tool arguments and string returns in the event log. It has no concept of structured domain documents attached to a session. | Typed documents (`DocumentDefinition<T>`) versioned across turns, with explicit fork inheritance semantics (`AsOf`, `Current`, `Initial`) and point-in-time rewind capabilities. |
| **Task State Machines & Checkpoints** | **Transient / in-memory only.** The SDK exposes ephemeral tool execution tasks. If the host process crashes mid-task, that state machine is lost; on restart, the SDK can only replay the prompt from scratch. | Phase-based state machines (`DurableTask`) where each phase commits an explicit checkpoint. Crashes resume from the last committed phase without re-running earlier steps. |
| **Idempotency Memos Across Crashes** | **None.** If a tool execution or external side-effect (e.g., booking a resource, charging a card, modifying a filesystem) crashes before the turn finishes, resuming the session either retries the entire turn or leaves side-effects unrecorded. | `runtime.MemoAsync(key, computation)`: Memos persist durably across crashes. On restart, completed effects are retrieved from storage rather than repeated. |
| **Transactional Multi-Entity Writes** | **None.** Tool calls, workspace edits, and session events are decoupled. There is no ACID boundary uniting a conversation turn, a task checkpoint, and an application document change. | Atomic transactions (`DurableStore.CommitAsync`) using in-memory undo logging and atomic disk persistence (`File.Move` temp-file replacement). |
| **Dynamic Tool Overrides & Runtime Hot-Reload** | **Session-lifecycle bound.** Tools and instructions are statically configured at session creation. Tool sets cannot dynamically react to document-level mode changes or hot-reloaded code without dropping session identity. | `ExtensionRegistry` + `DurableRegistry`: Extensions, prompt sections, and tools dynamically resolve against stored agent choices (`AgentDocument`) and sync via `Session.SetToolsAsync`. |

---

## 3. Was this Work Actually Necessary?

**Yes.** Without this layer, the Copilot SDK is an LLM client with persistent chat history, but cannot support fault-tolerant autonomous agent workflows.

This library bridges two distinct planes:
1. **The LLM Plane (Copilot SDK):** Persists chat tokens, system messages, and JSON-RPC tool events.
2. **The Application Plane (This Library):** Persists domain documents, phase checkpoints, idempotency memos, and fork isolation policies.

When a session forks via `ForkAsync`, the SDK duplicates the conversation transcript, while `lib/Durable.cs` branches the domain documents according to their configured fork policies.

---

## 4. Concrete Accomplishments & Discovered SDK Behaviors

Building and verifying this suite established key durability guarantees and resolved several critical SDK behaviors:

1. **Transaction Atomicity:** Added an `UndoLog` ensuring that if an exception occurs mid-transaction, all partially created conversations, tasks, and document revisions roll back completely.
2. **Cascading Abort Guarantees:** Ensured that aborting a parent task cascades to abort its children, triggers their abort handlers (e.g., executing refunds), and marks terminal outcomes that cannot be overwritten by delayed timer wakeups.
3. **Resumption with Memo Idempotency:** Validated that interrupted multi-step workflows resume deterministically from their last saved phase without duplicating external side effects.
4. **Resolved Silent Tool Shadowing:** Discovered that CLI built-in tools (`read`, `read_file`, `edit`, `grep`, `view`, `bash`, `glob`) silently take precedence over user-defined tools of the same name unless explicitly configured with `CopilotToolOptions.OverridesBuiltInTool = true`.
5. **AIFunction Async Dispatch Deadlock:** Identified that synchronous blocking (`.GetAwaiter().GetResult()`) inside tool handlers deadlocks the SDK's JSON-RPC message pump, establishing that tool handlers must be pure `async`.
