# Design: Durable GitHub Copilot SDK Agents in C#

> Working design note. This maps the **Pi Durable** feature set onto the **GitHub Copilot SDK (.NET)**
> and defines the shape of the example suite in this repository.

## 1. Goal

A suite of **self-contained C# file-based apps** (`dotnet run app.cs`, .NET 10) that demonstrate
durable GitHub Copilot SDK agents, with **breadth equal to the Pi Durable example set**
(33 examples, `00`–`32`).

Each example is a single `.cs` file plus, where it needs shared helpers, a `#:include`d
support file. No `.csproj`.

## 2. The two systems

### 2.1 Pi Durable (the reference)

A durable agent harness where:

- A **Session** owns one mutation line and atomically commits immutable **entries**, full **task**
  records, and **documents**. Only committed state is observable.
- A **conversation** is a transcript scope (root id = 1). An **entry** is an immutable transcript
  record with `model` (provider messages), `data` (app payload), `head` (context cut), `edits`.
- A **submission** is user input or a passive write, queued in the `pi.inbox` document.
- A **task** is a durable state machine (`pending|running|waiting|sleeping|completing|terminal`)
  with a checkpoint per step.
- A **fork** inherits a parent's entries through one entry and copies documents per their `fork`
  policy (`asOf|current|initial`).
- A **watch** is a bounded (100-frame) exact-frame `WatchHandle<T>`.
- Storage backends: Memory, SQLite (WAL), JSONL, IndexedDB.

### 2.2 GitHub Copilot SDK (.NET) (the target)

- `CopilotClient` spawns/attaches to the Copilot CLI runtime over JSON-RPC.
- `CopilotSession` is the unit of conversation. Created with `CreateSessionAsync(SessionConfig)`,
  resumed with `ResumeSessionAsync(sessionId, ResumeSessionConfig)`.
- Sessions persist to `COPILOT_HOME/session-state/{sessionId}/` (default `~/.copilot/session-state/`).
- Tools are `AIFunction` declarations built with `CopilotTool.DefineTool(delegate, ...)`.
- Events are a class hierarchy under `SessionEvent`; subscribe with `session.On<T>(handler)`.
- Permissions are handled by `SessionConfig.OnPermissionRequest` returning a `PermissionDecision`.
- Hooks are `SessionConfig.Hooks` (`SessionHooks`) with `OnPreToolUse`, `OnPostToolUse`, etc.
- Sub-agents are configured via `SessionConfig.CustomAgents` / `DefaultAgent`; the runtime
  delegates to them and streams `subagent.*` lifecycle events back to the parent.
- Forking is `client.Rpc.Sessions.ForkAsync(sessionId, toEventId, name)`.
- Background/parallel agents: `session.Rpc.Tasks.*` and `session.Rpc.Fleet.*`.

### 2.3 The mapping

| Pi Durable | Copilot SDK (.NET) | Notes |
|---|---|---|
| `Session` | `CopilotClient` + `CopilotSession` | The client owns the runtime; the session owns the transcript. |
| `Storage` (memory/sqlite/jsonl) | `COPILOT_HOME/session-state/{id}/` | The runtime owns persistence. |
| `Conversation` | `CopilotSession` | One session = one conversation. |
| `Entry` | `SessionEvent` in `events.jsonl` | Immutable transcript records. |
| `Document` | Session workspace files / app-owned JSON | See §3. |
| `Task` | `session.Rpc.Tasks.*` / app-owned task loop | See §3. |
| `Submission` | `MessageOptions` + `SendMode` (`enqueue`/`immediate`) | See §3. |
| `Fork` | `client.Rpc.Sessions.ForkAsync` | Native. |
| `Watch` | `session.On<T>` event stream | Native. |
| `defineTool` | `CopilotTool.DefineTool` | Native. |
| `defineExtension` / `Registry` | App-level composition (plain C# classes) | See §3. |
| `section` (prompt sections) | `SystemMessageConfig` (`append`/`replace`/`customize`) | Native. |
| `hook` | `SessionHooks` | Native. |
| `wrapTool` | App-level decorator over `AIFunction` | See §3. |
| `defineTask` | App-level durable task loop over `Tasks` RPC | See §3. |
| `defineDoc` | App-level JSON document store | See §3. |

## 3. The "Durable" layer we build ourselves

The Copilot SDK gives us a durable **conversation** and a durable **runtime**. Pi Durable adds a
durable **application state machine** on top: documents, tasks, submissions, forks, watches.

We therefore build a small, reusable **Durable** support layer in C# that provides the Pi
concepts the SDK does not have, and compose it with the SDK's native durability.

### 3.1 `DurableStore` — the document + task store

A single JSON-file-backed store per example (or in-memory), holding:

```csharp
sealed record DurableDocument(string Kind, string Scope, string OwnerId, JsonElement Value, long Version);
sealed record DurableTask(string Id, string Name, string Phase, JsonElement Checkpoint, JsonElement Input,
                          string Status, JsonElement? Outcome, long Restarts);
```

The store has two tiers. `DurableStore.CreateEphemeral()` keeps everything in the process and
writes nothing. `DurableStore.Create(new DurableStoreOptions { Path = ... })` writes a file, and
that file is what makes a conversation outlive the process.

#### Where the file lives

An explicit `Path` is honoured exactly. A store created with no path resolves the per-user default
location instead, so an app that says nothing still gets a durable store:

| Platform | Root |
|---|---|
| Windows | `%LOCALAPPDATA%` |
| Linux / macOS | `$XDG_STATE_HOME` when set and non-empty, otherwise `~/.local/state` |

The file is `Path.Combine(root, "durable", fileName)`, where `fileName` defaults to
`durable-store.jsonl`. `DurableStore.DefaultStorePath(fileName)` exposes the resolution so an app
can show the user where its state is. The default is deliberately *not* the current directory: a
store that follows the working directory would give the same app a different history depending on
where it was launched.

#### How the file is written

The file is JSONL — one record per line, appended on every commit. Each line is an envelope
`{"v":1,"seq":<n>,"type":"<kind>","payload":{...}}` where `type` is one of `conversation`,
`entry`, `entry-record`, `document`, `document-version`, or `task`. Appending is what makes a
crash cheap: a process that dies mid-write leaves a torn final line, and the next open replays
every complete record, truncates the file back to the last complete record's boundary, and
continues. A torn tail costs one record, never the store.

A line that fails to parse *and is not the final line* is a different matter — that is corruption
in the middle of the file, and it is reported as an `InvalidDataException` naming the byte offset
rather than silently dropping everything after it.

`DurableStoreFormat` selects the backend explicitly:

| Format | Behaviour |
|---|---|
| `Auto` (default) | Sniff the file. A first non-empty line that is a JSON object with a string `type` is JSONL; a file that parses whole as one JSON object is the legacy snapshot format; a missing or empty file is JSONL. `Auto` never converts — a legacy file is opened and written back in legacy format. |
| `InMemory` | No file at all. |
| `Jsonl` | JSONL, and only JSONL. Opening a legacy snapshot is the explicit "convert this store" case: the snapshot is read into memory on open and the first commit replaces the file with JSONL records. Opening alone never rewrites it. |
| `LegacyJson` | The legacy single-file snapshot, written atomically (temp file + `File.Move` overwrite). Opening a JSONL file in this format is refused at open with an `InvalidDataException`, before the lock is taken. |

This is the C# analogue of Pi's SQLite/JSONL storage, and it is what makes the *task* and
*document* examples meaningful.

#### Concurrency contract

**A store file has exactly one writer.** A persistent store takes an exclusive lock on a sidecar
file next to the store (`{path}.lock`) when it opens, and holds it until the store is disposed.
The operating system releases the lock when the process exits, so a crash does not leave a stale
lock behind. A second process that opens the same path is refused at open with an
`InvalidOperationException` naming the store file, the lock file, and the process id that holds
it. There is no retry, no timeout, and no queue: the second writer fails fast rather than
interleaving appends with the first.

The consequence for application code is that a store's lifetime is a scope. Dispose the store
before another process opens the same path — including before launching a child process that
will read it. `DurableHarness.DisposeAsync()` closes the Copilot client; it does **not** dispose
the store, so a harness that is closed and reopened against the same path must dispose the store
in between.

Because the lock makes the holder the only writer, a store reads its file once at open and then
serves from memory. `DurableStore.RefreshAsync()` is therefore a no-op for a persistent store;
it exists for the ephemeral tier and for a future read-only tier that observes a file another
process writes.

The sequence number is the store's monotonic commit counter. It is minted per record, persisted
with the record, and restored on replay as the maximum of the replayed values, so a reopened
store always mints past every sequence it has already written. A document version carries the
sequence it was minted with, not the sequence of the commit that happened to write it.

#### Permissions

A durable conversation is fail-closed. `AgentChoices.Permissions` is a list of `kind` or
`kind:scope` grants, and `DurableHarness.BuildSessionConfig` installs
`PermissionPolicy.Handler(resolved.Permissions)` as `SessionConfig.OnPermissionRequest` instead of
the SDK's `PermissionHandler.ApproveAll`.

The reason is the same reason the store exists. An approval that is not written down is not
reproducible: the SDK's default approves every request, so the same conversation would be allowed
to do different things depending on who ran it and when. Persisting the grant set with the
conversation's choices makes the agent's authority part of the durable record, which means it
survives a restart and is inherited by a fork as a snapshot.

`PermissionPolicy.Describe` maps each of the SDK's twelve permission request variants to a
`(kind, scope)` pair — `custom-tool` is scoped by tool name, `read`/`write` by resolved path,
`shell` by full command text, `url` by URL, `mcp` by `server/tool`. `PermissionPolicy.IsGranted`
then matches a grant against that pair, treating a scope as a prefix only at a boundary
(`/ \ space : ; ,`) so `read:/tmp/work` does not grant `/tmp/workshop/file.txt`.

Denial uses `PermissionDecisionReject` with a feedback string naming the missing grant. The SDK's
`PermissionDecisionDeniedByRules` and `PermissionDecisionDeniedNoApprovalRuleAndCouldNotRequestFromUser`
serialize to wire variants the Copilot CLI does not accept, so they fail at the runtime boundary
with a malformed-payload error; `Reject` is the variant that actually denies.

Grants are fixed at session creation. `CopilotSession` exposes no per-session permission API, so
`ConfigureAsync` persists a new grant set but cannot apply it to a live session — the next
conversation creation, restore, or fork picks it up.

### 3.2 `DurableTask<TInput, TCheckpoint, TResult>` — the task state machine

Mirrors Pi's `defineTask`:

- `name`, `version`
- `initial()` → starting checkpoint
- `phases` — one handler per phase; each returns the next checkpoint or a terminal outcome
- `abort` — runs instead of the phases after an abort
- `runtime.commit(...)` — atomically persists checkpoint + outcome
- `runtime.memo(key, value)` — idempotency memo, so a rerun after a crash does not repeat an effect
- `runtime.sleepUntil(...)` — a durable timer (checkpoint carries the wake time)

The runner is a plain `async` loop over the store. On restart, a task in `running` state is
re-entered at its last saved checkpoint — exactly Pi's resume semantics.

### 3.3 `DurableRegistry` — extensions, tools, sections, hooks

Pi's `defineExtension` / `createRegistry` / `install` / `uninstall` map to a plain C# registry:

```csharp
sealed class Extension {
    public string Name { get; init; }
    public IReadOnlyList<AIFunction> Tools { get; init; } = [];
    public IReadOnlyList<Func<SectionInput, Task<string?>>> Sections { get; init; } = [];
    public IReadOnlyList<DurableTaskDefinition> Tasks { get; init; } = [];
}
```

`Registry.Install(ext)` replaces an extension **by name** in place (Pi's reload semantics), and
`Registry.Uninstall(name)` removes it. Stored configuration references extensions **by name**, so
stored choices outlive the code — exactly Pi's `06-configuration` / `30-reload-and-restart` behavior.

### 3.4 `DurableConversation` — the conversation handle

Wraps a `CopilotSession` plus the durable store, exposing Pi-shaped operations:

- `SubmitAsync(text, whenBusy)` → a `Submission` with `WaitAsync()`
- `CommitAsync(Func<Transaction, Task<T>>)` → atomic app-state commit
- `SnapshotAsync(doc)` / `SnapshotAsOfAsync(doc, entryId)`
- `ForkAsync(entryId)`
- `EntriesAsync(limit)` → paged transcript
- `ContextAsync()` → the effective model context
- `ConfigureAsync(choices)` → persist agent choices by name
- `AgentAsync()` → resolve stored choices against the registry

### 3.5 `DurableHarness` — the host

- `OpenAsync(store, options)` → harness
- `RootAsync()` → the root conversation (idempotent)
- `CreateConversationAsync(options)`
- `WaitForTaskAsync(id)`, `AbortTaskAsync(id)`, `TaskGraphAsync()`
- `ResumeAsync()` → re-enter running tasks
- `CloseAsync()`

## 4. Example suite (33 examples, mirroring Pi 00–30 plus two C#-only additions)

| # | Pi example | C# example | Demonstrates |
|---|---|---|---|
| 00 | conversation | `00-conversation.cs` | Lowest layer: a session, one conversation, atomic commit. |
| 01 | documents | `01-documents.cs` | Typed, rewindable documents; read value at a past entry. |
| 02 | forks | `02-forks.cs` | Fork a conversation at an entry; independent document copies. |
| 03 | owned-conversations | `03-owned-conversations.cs` | A background task owning a child conversation + a registry doc. |
| 04 | watches | `04-watches.cs` | Watch a document; serialized async delivery. |
| 05 | harness | `05-harness.cs` | Open a harness with a registry of extensions; root conversation. |
| 06 | configuration | `06-configuration.cs` | Per-conversation choices + live harness settings. |
| 07 | harness-conversations | `07-harness-conversations.cs` | Handles, typed entries, created conversations, forks. |
| 08 | context | `08-context.cs` | Transcript history vs. model context; edits, heads, tool results. |
| 09 | registry-reload | `09-registry-reload.cs` | Reload extension code through the registry; tool wrapping. |
| 10 | extension-state | `10-extension-state.cs` | An extension keeping its own per-conversation document. |
| 11 | tasks | `11-tasks.cs` | Run a durable task; checkpointed phases; typed outcome. |
| 12 | recovery | `12-recovery.cs` | Close, reopen, continue where a task stopped (SQLite/JSON store). |
| 13 | chat | `13-chat.cs` | A chat turn: submit, wait, read the transcript. |
| 14 | system-prompt | `14-system-prompt.cs` | System prompt sections and per-conversation instructions. |
| 15 | real-model | `15-real-model.cs` | A real model; stream the answer as it arrives. |
| 16 | coding-tools | `16-coding-tools.cs` | A coding-agent tool turn: read, edit, run commands. |
| 17 | print | `17-print.cs` | Print mode: one prompt, print the answer. |
| 18 | json | `18-json.cs` | JSON mode: stream the run as JSON lines. |
| 19 | inbox | `19-inbox.cs` | What happens to submissions while a conversation is busy. |
| 20 | late-join | `20-late-join.cs` | A client attaches mid-run: snapshot first, then deltas. |
| 21 | subagent-foreground | `21-subagent-foreground.cs` | A foreground subagent tool: child conversation, answer returned. |
| 22 | subagent-background | `22-subagent-background.cs` | Persistent background subagents that survive a restart. |
| 23 | child-tasks | `23-child-tasks.cs` | A task owning child tasks; the task graph. |
| 24 | compaction | `24-compaction.cs` | Compaction: older messages summarized to keep context small. |
| 25 | coding-agent | `25-coding-agent.cs` | A small coding agent: tools, cwd-aware prompt, live settings. |
| 26 | plan-mode | `26-plan-mode.cs` | Plan mode: read-only planning, then switch back. |
| 27 | reviewer | `27-reviewer.cs` | A reviewer agent beside the main one: cheaper model, own checkout. |
| 28 | sandbox-per-conversation | `28-sandbox-per-conversation.cs` | One sandbox per conversation. |
| 29 | tool-override | `29-tool-override.cs` | Replace a tool for some conversations; decorate whichever wins. |
| 30 | reload-and-restart | `30-reload-and-restart.cs` | Reload an extension mid-call, then restart the process. |

## 5. File-based app conventions

```csharp
#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
```

- `#:include ../lib/Durable.cs` for shared support code.
- Top-level statements; `await using` for the client and session.
- Tool handlers return `string` or `Dictionary<string, object?>` by default. **Anonymous types and
  custom records fail** with the SDK's source-generated serializer unless a custom
  `JsonSerializerOptions` with `DefaultJsonTypeInfoResolver` is supplied via
  `AIFunctionFactoryOptions.SerializerOptions`.
- `dotnet run <file>.cs` from the example's directory.

## 6. Verification

Every example must:
1. Compile (`dotnet build` of the file-based app).
2. Run to completion without unhandled exceptions.
3. Print observable output matching the Pi example's observable behavior.

`scripts/validate.sh` runs the whole suite. `SKIP_LIVE=1` skips the examples that call a
model; `COMPILE_ONLY=1` builds without running.

CI enforces requirements 1 and 2 for the 13 examples that need no Copilot sign-in by running
`SKIP_LIVE=1 SKIP_TESTS=1 ./scripts/validate.sh` after the xUnit suite. The other 20 are
compiled but not run in CI.

Examples that need a live model use the signed-in Copilot CLI credentials. The library's
default model is `gpt-6-luna` rather than `auto`: `auto` re-routes per request, which makes
an example's behaviour less reproducible, and it does not accept a reasoning effort. A
reasoning effort is only sent when `ModelInfo.SupportedReasoningEfforts` says the chosen
model supports one, because the runtime rejects the session outright otherwise.

## 7. SDK behaviours worth knowing

These cost the most time to discover and are all exercised by the examples:

- **Tool handlers must be `async`.** A synchronous `.GetAwaiter().GetResult()` inside a
  handler deadlocks the SDK's tool dispatch.
- **Tool return values** must be `string` or `Dictionary<string, object?>`; anonymous types
  and records throw `NotSupportedException` under the SDK's source-generated serializer.
- **Nullable reference parameters** are still `required` in the generated schema unless they
  have a default value.
- **Built-in tool names** (`read`, `read_file`, `edit`, `grep`, `view`, `bash`, `glob`) silently
  shadow a custom tool of the same name: the session starts, the model calls the tool, and
  the built-in answers instead. Rename the tool, or set
  `CopilotToolOptions.OverridesBuiltInTool = true`.
- **`ToolInvocation`** is bound automatically into any tool that declares it, and is excluded
  from the schema. It carries `SessionId`, which is how a shared tool finds the conversation
  that called it.
- **`AssistantStreamingDeltaEvent`** carries no text; the deltas are on
  `AssistantMessageDeltaEvent.Data.DeltaContent`.
- **`ToolExecutionCompleteData`** has no `ToolName`; match on `ToolCallId`.
- **`Console.WriteLine("a:", x)`** drops `x` — that is the format-string overload.

