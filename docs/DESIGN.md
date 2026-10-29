# Design: Durable GitHub Copilot SDK Agents in C#

> Working design note. This maps the **Pi Durable** feature set onto the **GitHub Copilot SDK (.NET)**
> and defines the shape of the example suite in this repository.

## 1. Goal

A suite of **self-contained C# file-based apps** (`dotnet run app.cs`, .NET 10) that demonstrate
durable GitHub Copilot SDK agents, with **breadth equal to the Pi Durable example set**
(37 examples, `00`–`35` and `37`).

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

#### Which provider writes it

The file formats above are the built-in backend. A store can also be written by a provider
that implements the `IDurableStoreProvider` seam, selected through `DurableStoreOptions`:
`Provider` (a factory) wins over `ProviderName` (a name in the process-wide registry, via
`RegisterProvider` / `UnregisterProvider` / `IsProviderRegistered`) which wins over `Format`
(the built-in file formats). A `ProviderName` that nothing registered is refused at open with
an `InvalidOperationException` naming it, rather than silently falling back to a file.

`Durable.EfCore` is that provider for relational databases. It maps the store's state onto
three tables — `sessions`, `turns`, and the append-only `events` envelope log — and
registers itself under the name `efcore`. The model is deliberately provider-neutral:
application-assigned keys, a plain integer concurrency token instead of `rowversion`, JSON
payloads stored as text rather than `jsonb`/`JSON`/`nvarchar(max)`, and no timestamps at
all, so the same `DurableDbContext` is accepted by all four providers with no per-provider
branch.

| Provider | Package (version) | Registered name | Connection-string env var | Migration (checked in) | Needs a server |
|---|---|---|---|---|---|
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | `efcore` | — (the store path *is* the database file) | `lib/Durable.EfCore/Migrations/20261029034106_InitialCreate` | No |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12 | `efcore-sqlserver` | `DURABLE_SQLSERVER_CONNECTION` | `lib/Durable.EfCore.SqlServer/Migrations/20261029135250_InitialCreate` | Yes |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 | `efcore-postgresql` | `DURABLE_POSTGRESQL_CONNECTION` | `lib/Durable.EfCore.PostgreSql/Migrations/20261029135238_InitialCreate` | Yes |
| MySQL | `MySql.EntityFrameworkCore` 10.0.12 | `efcore-mysql` | `DURABLE_MYSQL_CONNECTION` | `lib/Durable.EfCore.MySql/Migrations/20261029135245_InitialCreate` | Yes |

The MySQL row is Oracle's `MySql.EntityFrameworkCore`, not `Pomelo.EntityFrameworkCore.MySql`.
The PostgreSQL provider tracks its own release line, so it is 10.0.3 while the other two are
10.0.12. Each migration is applied by `Database.Migrate()` when the store opens.

**All four providers are wired.** SQLite is the reference provider because it needs no
server: the store path *is* the database file, and the suite and the examples run on a clean
machine with nothing installed. The three server-backed providers are the same model over a
different connection string, each with its own package, `Use*` factory, and checked-in
migration generated from the same `DurableDbContext`. The CI build runs `tests/Durable.Tests`
only, so the server-backed integration tests in `tests/Durable.EfCore.Tests` are run by hand;
on a machine with no server they skip with a message naming the endpoint, the real failure,
and how to un-skip, rather than failing. The skip is decided by opening a real connection with
a two-second timeout rather than by checking a port, because a port that accepts a connection
but speaks another protocol is not a usable server. On a machine with no servers at all that
suite reports 61 passed, 12 skipped, 73 total.

Migrations live in each provider's own assembly rather than in `Durable.EfCore` because EF
Core resolves a context's model snapshot as the first `ModelSnapshot` it finds in the
migrations assembly: four snapshots in one assembly would be ambiguous, and
`Database.Migrate()` would apply every provider's migration to every database. Each provider
therefore sets `MigrationsAssembly` to its own assembly. A new migration is added against the
provider project, with the output directory inside that provider's `Migrations/`:

```
dotnet ef migrations add <Name> --project lib/Durable.EfCore.SqlServer --output-dir Migrations
```

Because the connection string for a server-backed provider is not a file path, that case
goes through `Provider` (or a `DbContextOptions<DurableDbContext>` factory) rather than
`Path` + `ProviderName`; the `Path`-to-`Data Source` convention applies to SQLite only. Each
provider also exposes `ConnectionStringFor(string)`, which normalises the string through its
own builder (`SqlConnectionStringBuilder`, `NpgsqlConnectionStringBuilder`,
`MySqlConnectionStringBuilder`) so a malformed string fails at the convention rather than at
the first query, and a `ConnectionString` property that reads the environment variable above
and otherwise falls back to a localhost container default (a throwaway credential, not a
secret). `Register()` makes the provider selectable by its registered name.

The persistent provider holds the same exclusive single-writer lock on its path that the
JSONL provider does, and replays its `events` log on open, so a reopened store mints past
every sequence it replayed. `examples/37-ef-core-store.cs` is the runnable demonstration.

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

### 3.6 Sandbox policy — a durable, per-conversation grant

The SDK's sandbox is a session-level thing: `SessionConfigBase.SandboxConfig` takes a
`GitHub.Copilot.Rpc.SandboxConfig` and the runtime applies it to the session's processes.
`SessionConfig` itself has no sandbox property. That is the right shape for the SDK and the
wrong shape for us: a durable conversation's choices are stored, forked, and replayed, and a
sandbox grant is part of what a conversation *is*.

So the durable layer owns a policy object and maps it onto the wire type at session-build
time:

- **`DurableSandboxPolicy`** — an `IEquatable` value object holding `SandboxMcpServers`,
  `SandboxLspServers`, `Auth` (`DurableSandboxAuth`: `Gh`, `Git`), `Credentials`
  (`DurableSandboxCredentials`: `EnvVars`), `Filesystem` (`DurableSandboxFilesystem`:
  `ReadwritePaths`, `ReadonlyPaths`, `DeniedPaths`, `ClearPolicyOnExit`), `Network`
  (`DurableSandboxNetwork`: `Proxy`), plus `Enabled`, `AddCurrentWorkingDirectory`,
  `AllowBypass`, `AllowDevToolAccess`, `TrustWorkingDirectory`, and `KeychainAccess`.
- **`AgentChoices.Sandbox`** — the policy rides with the conversation's choices. `null`
  means no sandbox, which is exactly the behaviour every existing conversation had, so the
  feature is additive. `ResolvedAgent.Sandbox` is the resolved form.
- **`DurableHarness.BuildSandboxConfig(policy, workingDirectory)`** — the mapper. It is
  public and static so a host can inspect the wire type without opening a session, and it is
  what the tests assert field-for-field.
- **`{workspace}` token expansion.** Paths may be written as
  `{workspace}/writable` (`DurableSandboxPolicy.WorkspaceToken`). The token is expanded
  against the conversation's `WorkingDirectory` when the session is built, not when the
  policy is stored. A fork into a different working directory therefore gets a different
  resolved policy from the same stored grant — the stored policy describes the *shape* of
  the grant, not one machine's layout. The token never reaches the wire type.
- **`BuildSessionConfig`** wires the mapped config in alongside `TrustWorkingDirectory` and
  `EnableFileHooks`.
- **`SandboxDecisions` / `WaitForSandboxDecisionAsync`** — the conversation exposes the
  `sandbox.decision` events the existing `Attach` subscription already receives, so a caller
  can assert on what the runtime reported without a second subscription.

The split that matters: the durable **permission** policy and the **sandbox** policy are
different layers doing different jobs. A conversation granted `shell` has its shell call
approved by the durable layer; the sandbox then refuses the write itself. A denial in a
permission event would mean the layers disagreed. A denial in a sandbox decision means they
each did their own job. `examples/33-sandbox-sdk-filesystem.cs` draws exactly that line.

Two operational notes, both covered in [`SANDBOX_MATRIX.md`](SANDBOX_MATRIX.md):

- `EnableFileHooks` controls **repository-origin** hooks only. User-level hooks in
  `~/.copilot/hooks/` still run, and they run *inside* the sandbox, so a policy that does
  not grant them denies them. `DurableHarnessOptions.TolerateHostHookFailures` (opt-in,
  default `false`) turns a hook-attributed sandbox denial into the turn's answer instead of
  an exception.
- Sandbox types are annotated `GHCP001` (evaluation only). Examples that touch them carry
  `#:property NoWarn=GHCP001`.

## 4. Example suite (37 examples: Pi `00`–`30` mirrored one-to-one, plus six C#-only additions)

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
| 31 | — (C#-only) | `31-permissions.cs` | A durable permission policy: grants stored with the conversation, fail-closed by default, inherited by forks. |
| 32 | — (C#-only) | `32-jsonl-store.cs` | The store file: explicit path vs. per-user default, and the JSONL append-on-commit format. |
| 33 | — (C#-only) | `33-sandbox-sdk-filesystem.cs` | The sandbox filesystem policy: writable, read-only and denied directories, enforced below the permission layer. |
| 34 | — (C#-only) | `34-sandbox-sdk-network-credentials.cs` | The rest of the sandbox policy: network egress, credential masking and injection, dev-tool access, MCP/LSP confinement. |
| 35 | — (C#-only) | `35-sandbox-sdk-observability-bypass.cs` | Sandbox observability, bypass, working-directory trust and keychain — asserted from enforcement, not configuration. |
| 37 | — (C#-only) | `37-ef-core-store.cs` | The EF Core provider: the SQLite connection-string convention, selection by name and by factory, the migration's tables, reopen-and-read-back, and the four-provider matrix. |

## 5. File-based app conventions

```csharp
#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
```

- `#:include ../lib/Durable.cs` for shared support code.
- `#:project ../lib/Durable.EfCore/Durable.EfCore.csproj` instead of `#:include` when the example
  needs the EF Core provider. It is a separate assembly with its own package references and a
  checked-in migration, so it is consumed the way an app consumes it; `Durable` then arrives
  transitively. That example also sets `#:property PublishAot=false`, because file-based apps
  default to the NativeAOT runtime configuration and EF Core refuses to build a model under it.
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

CI enforces requirements 1 and 2 for the 14 examples that need no Copilot sign-in by running
`SKIP_LIVE=1 SKIP_TESTS=1 ./scripts/validate.sh` after the xUnit suite. The other 23 are
compiled but not run in CI.

Examples that need a live model use the signed-in Copilot CLI credentials. The library's
default model is `gpt-6-luna` rather than `auto`: `auto` re-routes per request, which makes
an example's behaviour less reproducible, and it does not accept a reasoning effort. A
reasoning effort is only sent when `ModelInfo.SupportedReasoningEfforts` says the chosen
model supports one, because the runtime rejects the session outright otherwise.

### 6.1 The deterministic run

`scripts/validate.sh --deterministic` runs the parts of the suite that need no Copilot
sign-in and no credentials. It is a second mode over the same example sources — nothing is
recompiled differently, and no example knows which runtime it is talking to.

**What it runs.**

- The vocabulary check: no source under `examples/` may describe itself with the placeholder
  terms (see below). The same check runs on every invocation, deterministic or not.
- `tests/ReplayRuntime.Tests`: the recorded runtime's fixture loading, framing, host dispatch,
  and tool-round-trip behaviour.
- `ReplayToolDispatchTests` from `tests/Durable.Tests`: a real `CopilotClient` over
  `RuntimeConnection.ForStdio` against the recorded runtime, with a real tool handler whose
  result is not recorded anywhere.
- A recorded conversation end to end: `tools/polyglot-attach/python/attach.py` drives
  `tools/ReplayRuntime` through `connect`, `session.create`, `session.sendMessages`, and
  `session.getMessages` against `tools/ReplayRuntime/fixtures/basic.json`.
- The store-only examples, `32-jsonl-store` and `37-ef-core-store`, which never open a
  Copilot session.

**What it does not run, and why.** The other 35 examples create a conversation, and the
durable layer does not name the session it asks for, so the recorded runtime is handed back a
session id its fixture does not record. It refuses rather than inventing an answer, and the
example fails with `ConnectionLostException`. Lifting that limit means either recording one
transcript per example or letting the recorded runtime allocate a session for an unnamed
`session.create`; both are changes to `tools/ReplayRuntime/`, tracked separately. The mode
prints the count of examples it ran and excluded, so a shrinking deterministic set is visible
rather than silent.

**What it proves.** The example sources and the runtime protocol are exercised with no
network access, no Copilot sign-in, and no ambient state: the recorded runtime answers from a
fixed epoch with sequential event ids, so a passing run is reproducible on any machine.

**What it does not prove.** It is not a substitute for the live runs. It says nothing about
whether a real model answers, whether a prompt produces the expected text, or whether the
signed-in runtime accepts the session configuration. Requirements 1–3 above still mean a live
run; the deterministic mode is the floor CI can enforce everywhere, not the ceiling.

**How the runtime is selected.** `DurableHarnessOptions.RuntimePath`, or the
`DURABLE_RUNTIME_PATH` environment variable when the option is unset, makes the harness
connect over `RuntimeConnection.ForStdio` instead of spawning the bundled runtime. The
variable exists so a script can select a runtime without editing an example:
`DURABLE_RUNTIME_PATH=/path/to/runtime scripts/validate.sh`. A path that names no file fails
the open and names the path. The setting is host configuration, not durable state, so it
never travels with a fork.

**Vocabulary.** A small set of placeholder terms is banned from source under `examples/`,
`lib/`, `docs/`, `README.md`, `tools/`, and `scripts/`; the list lives in `BANNED_TERMS` in
`scripts/validate.sh`. Describe recorded behaviour as *deterministic*, *recorded*, *replay*,
*scripted*, *canned*, or a *fixture*. The script fails when a banned term appears under
`examples/`; the broader rule is enforced in review.

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

