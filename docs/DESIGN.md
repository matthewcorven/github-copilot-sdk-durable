# Design: Durable GitHub Copilot SDK Agents in C#

> Working design note. This maps the **Pi Durable** feature set onto the **GitHub Copilot SDK (.NET)**
> and defines the shape of the example suite in this repository.

## 1. Goal

A suite of **self-contained C# file-based apps** (`dotnet run app.cs`, .NET 10) that demonstrate
durable GitHub Copilot SDK agents, with **breadth equal to the Pi Durable example set**
(38 examples, `00`–`37`).

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

The loop reads the task's **committed** record at the top of every step, not a record it captured
when the run started. A commit replaces the stored checkpoint, and the next step must see the
checkpoint the previous step committed; a runner that holds one record for the whole run would
re-enter the same phase forever. In a language where the record is a reference the store mutates
in place the two are the same object, so the rule is invisible there — it is stated here because
it is part of the contract, not an implementation detail of one port.

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

Hook composition is a read of the caller's choices, not a write to them. `BuildSessionConfig`
copies the caller's `SessionHooks` and composes the durable prompt section onto the copy, so
`AgentChoices.Hooks` is never mutated: building a configuration twice composes once, and a host
that keeps its own hook set keeps it. `SessionHooks` has no copy constructor, so the copy is
explicit and covers every hook the type declares — a hook the copy forgot would be silently
dropped from the session.

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

### 3.6 Session identity — the id is chosen before the session exists

A durable conversation is the identity; the native session is what serves it. Resuming reads
`durable.session-binding` and asks the runtime for the session it names. That only works if
the durable layer can *name* the session, and the SDK allocates an id server-side unless the
caller asks for one. `SessionConfig.SessionId` is that request, and the durable layer always
sets it.

The id is **generated once and written to the binding before the create RPC**, not derived
from the conversation id:

- The store then holds exactly the id the runtime was asked for. A derived id would be a
  second, parallel source of truth that has to keep agreeing with the first.
- The derivation scheme can change without orphaning conversations already on disk. A
  derived id is welded to the conversation id forever, so any later change to the scheme
  makes every existing record unresumable.
- A crash between the write and the RPC leaves a binding naming the session the next attempt
  will ask for, which is the same state a crash after the RPC leaves.

`SessionBinding.Pinned` records which of the two happened. A pinned id is one the durable
layer chose and can therefore ask for again: if the runtime does not hold it, the session is
re-created under the same id and the conversation keeps its identity. An id the runtime
allocated is one the durable layer only learned after the fact, so a runtime that does not
hold it has lost the transcript, and `ResumeConversationAsync` throws
`DurableRecoveryRequiredException` rather than quietly starting a fresh session. Records
written before pinning existed, and bindings a host wrote by hand, are in the second shape.

Pinning is applied only when the configuration is a `SessionConfig`: `ResumeSessionConfig`
has no `SessionId`, and a resume names its session in the call itself. A fork is a new
conversation and pins its own id — `durable.session-binding` is declared `ForkPolicy.Initial`
precisely so a fork never inherits its parent's session.

### 3.7 Sandbox policy — a durable, per-conversation grant

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
  `EnableFileHooks`. It is the one place a session configuration is built, so the harness and
  the facade cannot disagree about whether the stored policy was applied: both reach it, and
  the resume path copies its result into the shape resuming takes.
- **`DurableSandboxPolicyException`** — a stored policy that cannot be applied faithfully
  stops the session instead of being narrowed. The rules are the ones the mapping would
  otherwise have to drop: a policy that disables the sandbox while still naming credential,
  filesystem, network, or keychain restrictions; a credential under an empty environment
  variable name; a credential with no inject hosts, an empty inject host, or a bare `*` host.
  The exception carries the `Reason` and the `Policy`, and the session is neither created nor
  resumed — a conversation whose policy is rejected is not written to the store at all.
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

### 3.8 The builder, the facade, and the boundary between them

`DurableClientBuilder` is a description and `DurableCopilotClient` is a host. The builder records
what a host already has; `Build()` validates that description and constructs the host. That is
all it does: no store is opened, no SDK client process is started, no task is resumed, and no
prompt section is rendered. Construction is cheap and fallible in exactly one way — an option
that cannot describe a host — which is why it is safe to call during container construction.
Everything that touches the outside world is deferred to the first operation that needs it.

- **`DurableClientBuilder`** — `UseStore(DurableStore)`, `UseStore(DurableStoreOptions)`,
  `UseClient(CopilotClient)`, `UseClient(CopilotClientOptions?)`, `UseRegistry(ExtensionRegistry)`,
  `UseHarnessOptions(DurableHarnessOptions)`, `Configure(Action<DurableClientOptions>)`, `Options`,
  `Build()`. Each call replaces the previous choice, so the last write wins and ordering never
  matters; `UseStore` and `UseClient` clear the competing source, because a store is supplied
  either as an object or as options that create one, never both.
- **`DurableClientOptions`** — the description `Build()` reads: `Store`, `StoreOptions`, `Client`,
  `ClientOptions`, `Registry`, `Harness`, `OwnsStore`, `OwnsClient`.
- **`Build()`** refuses a description that cannot be a host, with an `InvalidOperationException`
  naming the call that fixes it: no store source, two store sources, two SDK client sources, an
  ownership flag with nothing to own, or a null harness. It resolves the registry once
  (`Options.Registry ??= new ExtensionRegistry()`), so the options and the client report the same
  instance and an extension installed after the build is visible to the next configuration.
- **`DurableCopilotClient`** — `InitializeAsync`, `CreateConversationAsync`, `RootAsync`,
  `ResumeConversationAsync`, `ForkAsync`, `FindConversation(string)`,
  `FindConversation(Func<DurableConversation, bool>)`, `GetSessionConfig`, `GetSessionConfigAsync`,
  `ResumeTasksAsync`, `WaitForTaskAsync`, `DisposeAsync`, plus `Options`, `Store`, `Registry`,
  `NativeClient`, and `IsInitialized`.

**Initialization.** A built client is inert. The first operation that needs the store or the SDK
client initializes it, and concurrent first operations share one attempt: the work happens once
and every caller awaits the same task. Reading `Store` or `NativeClient` before that is an
`InvalidOperationException` naming `InitializeAsync`, rather than a null a caller has to notice.
A failed initialization disposes whatever it created — the client it started, then the store it
opened — so a retry starts from a clean slate rather than from a half-attached runtime.

**Ownership.** Disposal follows who created the resource. A store or client the caller supplied
is *borrowed*: the facade drives it and leaves it alone on disposal. One the builder created is
*owned*: the facade disposes it. `OwnsStore` and `OwnsClient` override the flag for a resource
the caller supplied; asking to own a resource the builder would create anyway is refused, because
it is already owned. The order matters beyond tidiness. A persistent store holds an exclusive
writer lock for its lifetime (section 3.1), and the runtime releases the client before the store,
so the only safe order — store after client — is the one the runtime performs. Disposal is
idempotent and unbinds first, so a second `DisposeAsync` is a no-op and a disposed facade reports
itself as uninitialized.

**One store, one runtime.** A store has one task runner. A second runtime attaching to the same
store is refused with an `InvalidOperationException` that says so, because the two would compete
for the same tasks and interleave the same documents. The supported shape for many callers is one
runtime shared by all of them, not one runtime each.

**Precedence.** The stored agent choices are authoritative, and `GetSessionConfig(choices,
conversation)` applies that rule: the conversation's own choices win, and the argument only
supplies defaults for a conversation that has none stored. Where a stored choice is unset, the
SDK configuration — or the runtime's default model, `gpt-6-luna` — applies. The rule exists
because the store is what makes a conversation reproducible: a setting that lives only in host
configuration is not part of the durable record, so it would change across a restart and travel
badly across a fork. Host-level options are deliberately outside the merge, because they describe
the process rather than the conversation: `DurableHarnessOptions.PermissionHandler`,
`EnableFileHooks`, and `TolerateHostHookFailures`. Supplying a permission handler replaces the
fail-closed durable policy for every conversation, which moves the agent's authority out of the
store and out of these guarantees. `GetSessionConfigAsync(conversationId)` is the same rule for a
conversation with no live handle: it reads `durable.agent`, builds the configuration the runtime
would use, and creates no session and no sandbox.

**Checkpoints.** `ConversationCheckpoint` is the durable record of one thing that finished — a
turn or a tool call — with `Kind`, `EventId`, `Content`, `ToolName`, `Success`, `Error`, and
`RecordedAt` among its fields. Each checkpoint is a new version of the conversation's checkpoint
document, so the document's history is the checkpoint log, and `conversation.Checkpoint()` is the
latest one. `EventId` is the deduplication key: a checkpoint whose key is already the document's
latest value is not written again, so an event the runtime delivers twice does not produce two
checkpoints. Checkpoints are recorded by the permanent subscription the conversation installs, so
they are written whether or not the caller is watching.

**Recovery.** `ResumeConversationAsync` returns the live handle when one exists. Otherwise it
reads the conversation record, `durable.agent`, and `durable.session-binding`, and asks the
runtime for the session the binding names. What happens next is section 3.6: a pinned id the
runtime does not hold is re-created under the same id, and an id the runtime allocated that the
runtime no longer holds throws `DurableRecoveryRequiredException` rather than continuing with an
empty session. The exception is deliberate: the durable conversation is intact — entries,
documents, and choices are all in the store — and what is missing is the runtime's own
transcript, so the host decides whether to re-create the session or surface the loss.

**Forks.** `ForkAsync(parent, durableEntryId)` commits the durable half before it asks the
runtime to fork, and an interrupted fork is recovered from `durable.fork` the next time the store
is opened. Each document is copied according to its `ForkPolicy` — `AsOf` (the value at the fork
entry), `Current` (the latest), or `Initial` (the first) — and the choices are inherited as a
snapshot, so a later change to the parent does not reach the child. `durable.session-binding` is
declared `Initial` precisely so a fork never inherits its parent's session and pins its own. The
transcript a fork exposes excludes the durable layer's own bookkeeping: `Entries()` hides the
document kinds listed in `InternalDocumentKinds`, so the durable machinery is not part of what a
caller reads or a fork copies as transcript.

**The raw-SDK escape hatch.** `NativeClient` returns the underlying `CopilotClient`. Calls made
through it — creating a session, resuming one, forking one, or any other SDK operation the facade
does not expose — are the caller's own and sit outside these guarantees. A session created that
way is not a `DurableConversation`: its choices are not stored, its documents are not bound, its
grants are not enforced by the durable permission policy, and it does not appear in
`FindConversation(Func<DurableConversation, bool>)`. Reach for it to configure the SDK, not to
route around the facade.

The facade is not a second implementation. It drives the same runtime `DurableHarness` drives —
the same binding, the same configuration building, the same task coordination, the same lifecycle
— so a conversation created through the facade is indistinguishable from one created through the
harness, and the two can be used together over one store. Sharing the configuration helpers alone
would not be enough: the runtime, not the helpers, is what holds the bindings.
`examples/36-durable-client-builder.cs` is the runnable demonstration; everything it asserts is
observable without a session.

### 3.9 The runtime host — `src/Durable.Runtime`

`Durable.Runtime` is the process that turns a store into a running agent. It is a thin host, not a
second implementation: it opens a store, claims it, imports an agent definition, opens the
conversation that definition owns, submits one prompt, and prints what the runtime reported. Every
durable guarantee above is the library's; the host only decides *which* store, *which* agent, and
*what to print*.

- **Commands.** `run` executes one turn and exits. `serve` serves turns over one transport —
  HTTP (§3.12) or JSON lines over standard input and output (§3.15) — and requires exactly one of
  `--http` or `--rpc`. `schedule` registers and drives a recurring durable task (§3.13). `--help`
  documents all three and every flag.
- **Exit codes.** `0` succeeded, `1` the agent or the host failed, `2` a usage error, `3` the turn
  completed but did not succeed, `4` the command is not implemented in this build.
- **Store ownership before the client.** `DurableHarness.OpenAsync` starts the SDK client *before*
  it constructs the harness, and the harness is what claims the store's task scheduler. A host that
  opened the client first and discovered the store was taken would leave an orphaned runtime
  process behind. The host therefore claims and releases the scheduler as a pre-flight probe before
  `OpenAsync`; the cross-process half is already covered, because `DurableStore.Create` takes the
  store file's exclusive writer lock. A refused host exits non-zero naming the store and its owner
  and starts nothing.
- **Agent definitions.** An agent is a document of kind `durable.agent-definition`, owner id
  `agent:{name}`, `ForkPolicy.Initial`, listed in `InternalDocumentKinds` so it is bookkeeping
  rather than transcript. A file passed with `--definition` is an *import surface*: it is validated,
  committed, and thereafter the store is the source of truth. Validation runs at import, at host
  startup, and on every invocation, and refuses with a message naming the agent: a name that does
  not match `^[a-z0-9][a-z0-9._-]*$`, an extension the host does not have installed, a tool the
  selected extensions do not provide, a sandbox policy that cannot be applied, or a permission
  grant that does not parse. Extension names are resolved with `ExtensionRegistry.Find`, not
  `ResolveExtensions`, because the latter silently drops names it cannot resolve.
- **One conversation per agent.** The conversation is found by its `OwnerConversationId`
  (`agent:{name}`), so every occurrence of an agent is an admission and a checkpoint inside the same
  conversation, and the conversation id is stable across runs. `--new-conversation` is the opt-out.
- **Containment.** Every run reports how the agent's authority is bounded: `Sandboxed` when the
  definition enables a sandbox, `PermissionGated` when no sandbox is set and the durable fail-closed
  policy decides each request against the agent's grants, and `None` when the host supplied its own
  permission handler — including a careful one, because an unverifiable handler reads as the worst
  case. `run` proceeds with a warning; `serve` and `schedule` refuse to start with `None` unless
  `--allow-uncontained` is passed.
- **The startup report.** Before serving, the host writes two lines to stderr: the store it resolved
  (`store: jsonl at <path>`, or `store: in-memory (ephemeral)`) and then the containment
  classification. The store line names the format the store *settled on*, not the one requested, so
  `--format auto` over a fresh path reports `jsonl`. There is no platform line: the core path is
  deliberately free of platform-detection APIs so it stays portable (criterion 6,
  `CorePath_HasNoPlatformSpecificApis`), and the host reports only what it can observe without them.
- **The effective model.** The model an agent *requests* is not the model the runtime *reported*.
  The host reads `WaitForEffectiveModelAsync` and prints what it returns. When the runtime reports
  nothing, the result carries `effectiveModel: null` and `modelReported: false` and the host prints
  `(no model reported)`; it never substitutes the requested model or the harness default.
  `--require-effective-model` turns that absence into a failure.
- **The run result.** One JSON object on stdout, and nothing else; diagnostics go to stderr. It is
  the single shape every transport serializes: `agent`, `conversationId`, `admissionId`,
  `checkpointId`, `effectiveModel` (`model` + `source`, or null), `modelReported`, `containment`,
  `containmentReason`, `outcome` (`succeeded`, `turnUnsuccessful`, or `agentFailed`), and `error`.
  `checkpointId` is the entry id of the latest version of the conversation's checkpoint document —
  the durable entry, not the checkpoint's own identifier — so it names a position in the store's
  history that a caller can read back.

### 3.10 The TypeScript port

The durable core is also ported to TypeScript in `lib/ts/durable.ts`, so the same semantics can
be exercised on the JavaScript runtime. The port is a translation, not a second design: the six
model areas below keep their C# names, and §3.1–§3.8 remain the language-neutral contract for
both.

| Model area | Types |
|---|---|
| Conversations | `ConversationRecord`, `ForkIntent`, `SessionBinding`, `InputAdmission`, `ConversationCheckpoint`, `conversationLineage` |
| Documents | `DocumentDefinition<T>`, `DocumentHistory`, `DocumentVersion`, `EntryRecord`, `DocumentState`, `ForkPolicy` |
| Durable tasks | `DurableTaskDefinition`, `DurableTask`, `DurableTaskRecord`, `TaskStatus`, `TaskCommit`, `TaskOutcome`, `TaskRuntime`, `DurableTaskRunner` |
| Extension registry | `Extension`, `ExtensionRegistry`, `ToolWrapper`, `PromptSection`, `SectionInput` |
| Transactions | `Transaction`, `DocumentDraft<T>`, `UndoLog` |
| Watch stream | `WatchHandle<T>`, `WatchFrame<T>`, `WatchOptions`, `watchDocument` |

The port is type-checked under `lib/ts/tsconfig.json` (strict) with `npm run typecheck` from
`lib/ts`. Where TypeScript has no direct equivalent — the commit gate, cancellation, the durable
timer, the JSON boundary — the approximation is recorded in `docs/COMPARATIVE_ANALYSIS.md` §8
rather than left to differ silently. The file backends and the session-bound layer are not part
of the port; §8.2 says so explicitly.
### 3.11 Watches — the durable change feed

A watch is a **single-consumer** stream of what the store has committed, over documents and
entries rather than over SDK session events. It is the durable layer's own type: the SDK's
`session.On<T>` stream reports what the runtime did, and a watch reports what the store wrote.

- **Baseline first.** A subscriber's first frame is a snapshot of the committed state at the
  moment the watch was acquired, so a subscriber never has to replay the store to catch up.
- **Deltas after.** Each later frame carries only what was committed since the previous frame:
  the new entry ids and the new document versions, as `(kind, ownerId, entryId)` triples.
- **Bounded.** A subscriber that falls more than 100 frames behind has its accumulated deltas
  replaced by one full-value `reset` frame instead of an unbounded queue. The bound is the same
  100 frames Pi's `WatchHandle<T>` uses.
- **One consumer.** Two readers of one queue would each see half the frames, so a second
  subscription fails rather than silently splitting the stream. A second reader opens a second
  watch.
- **Committed only.** A frame is produced from committed state, so a value that a transaction
  has staged but not committed is never delivered.

A watch is a convenience over the store, not a second source of truth: everything a frame
reports is already readable through `Entries` and `Snapshot`, and a host that never opens a
watch loses no state.

### 3.12 The HTTP transport — `serve --http`

`serve --http` is the same host as `run`, with the turn reached over a socket instead of the
command line. It opens the store, claims it, imports and validates the definition, and classifies
containment exactly as `run` does — including refusing to start with `None` unless
`--allow-uncontained` is passed — and then serves one durable turn per request. The turn itself is
the same code path: `run` and `serve` both call one shared turn executor, so a turn served over
HTTP produces the same admission, the same checkpoint, and the same run result as a turn run from
the command line.

- **Binding.** `--port <port>` selects the port; the default is `0`, which asks the operating
  system for an unused one. The host binds loopback only and prints the port it bound to stderr as
  `listening on http://127.0.0.1:{port}` before it accepts anything, so a caller never has to guess
  a port and two hosts can run at once. `--http` selects this transport: `serve` needs exactly one
  of `--http` or `--rpc`, and neither or both is a usage error rather than a silent no-op.
- **Request.** `POST /` (or `POST /turns`) with a JSON object body:

  | Property | Required | Meaning |
  |---|---|---|
  | `input` | yes | The prompt for this turn. Must be a non-empty string. |
  | `agent` | no | The agent to run. When present it must name the agent this host serves; a different name is refused rather than silently ignored. |
  | `newConversation` | no | `true` starts a fresh conversation instead of resuming the agent's existing one. Defaults to `false`. |

  ```json
  { "input": "review the change", "newConversation": false }
  ```

- **Response.** `200 OK`, `Content-Type: application/x-ndjson`, chunked. The body is newline-
  delimited JSON: zero or more event lines, then exactly one result line.

  ```json
  {"type":"event","event":{ ... }}
  {"type":"event","event":{ ... }}
  {"type":"result","result":{ ... }}
  ```

  An event line carries the session event as the runtime reported it, under `event` — the same
  shape `examples/18-json.cs` prints. A result line carries the run result of §3.9 under `result`,
  compact rather than indented because it shares a line with the framing. The result is the last
  line and the only line of its type, so a client can stop reading at it.
- **Streaming.** Events are written as the turn produces them, not collected and flushed at the
  end: chunked transfer encoding is what makes each line reach the client while the turn is still
  running. A client therefore observes a turn in progress — the session starting, the assistant
  speaking, the session going idle — rather than only its outcome. Events are queued through a
  channel and written by a pump task, because the runtime delivers them on its own event thread and
  a slow client must not stall it. Turns are serialized: a second request waits for the first to
  finish, because a conversation admits one turn at a time.
- **Refusals.** A bad request is a response, never a crash, and never takes the server down:

  | Status | When | Body |
  |---|---|---|
  | `400` | The body is not JSON, is not an object, has no `input`, has an empty `input`, or names an agent this host does not serve. | `{"error": "..."}` naming the problem, or the agent. |
  | `404` | The path is not `/` or `/turns`. | `{"error": "..."}` naming the route. |
  | `405` | The method is not `POST`. | `{"error": "..."}` naming the method. |

- **Graceful shutdown.** `SIGTERM` and `SIGINT` stop the accept loop and nothing else. The signal
  is cancelled so the process is not torn down underneath a turn, connections already accepted keep
  running, and the host waits up to 60 seconds for them before exiting `0`. A turn in flight
  therefore reaches its checkpoint and settles its admission instead of being abandoned: cancelling
  the turn would skip both the turn checkpoint and the admission settle, leaving an admission
  `admitted` and a conversation a later `run` could not resume. The durable layer exists so a turn
  can be resumed; a server that killed one on `SIGTERM` would defeat it. A turn that outlives the
  grace period is reported on stderr and the process exits anyway.
- **Out of scope.** Authentication, TLS termination, and multi-tenancy are not part of this
  transport; it binds loopback and assumes a trusted caller. The JSON-lines transport (§3.15) and
  the scheduler (§3.13) are separate work.

### 3.13 The persistent scheduler — `schedule`

`schedule` is the command that makes an agent recur. The interesting part is not the loop; it is
that the loop holds nothing. A `System.Threading.Timer` would die with the process and take the
schedule with it, so the schedule is a document in the store and the loop is a reader of that
document. A host that restarts discovers the same schedule, at the same `nextDue`, and either
resumes the sleep or applies the missed-run policy. Nothing about the cadence lives in memory.

- **The schedule record.** A document of kind `durable.schedule`, owner id `schedule:{agent}`,
  `ForkPolicy.Initial`, listed in `InternalDocumentKinds` so it is bookkeeping rather than
  transcript. The owner id keeps the schedule out of every conversation and out of every fork, so
  a forked conversation does not inherit a second copy of the cadence. The value carries `agent`,
  `every` (as written, for display), `everyMilliseconds` (so the cadence never depends on
  re-parsing text), `missedRun`, `nextDue`, `lastRun`, `runs`, and `enabled`. `nextDue` is the
  whole state machine: it is the only thing a restart needs in order to know what is owed.
- **Registration keeps the cadence.** `schedule --every 5m --agent reviewer` writes the record and
  stops, because the first occurrence is an interval away. Re-running the same command against the
  same store keeps the existing `nextDue` rather than pushing it out by a whole interval, so a
  restart with the same command is harmless. Only a changed interval or policy starts a fresh
  cadence. `--passes 0` registers without driving; omitting `--passes` drives until interrupted.
- **The loop re-reads the store every pass.** `Scheduler` holds no schedule of its own: each pass
  calls `ScheduleStore.Enabled(store)` and filters to the agent. A schedule added, changed, or
  removed by another process is therefore picked up on the next pass, and a store with no schedule
  record drives nothing at all. The wait between passes is clamped to the earliest durable
  `nextDue`, bounded above by 30 seconds so the store is re-read even when the next occurrence is
  hours away, and below by 100 milliseconds so a due schedule cannot spin the process.
- **An occurrence is claimed before it runs.** A pass commits the advanced schedule and only then
  runs the turn. A host that dies mid-occurrence loses that occurrence rather than repeating it,
  which is what "one occurrence per scheduled instant" means here: the durable `nextDue` has
  already moved past the instant, so the restarted host finds nothing due. The alternative —
  running first and committing after — would double-run every occurrence that straddled a crash.
- **The three missed-run policies.** The host was down across one or more scheduled instants. Each
  policy answers that differently, and the difference is observable in `nextDue`, `lastRun`,
  `runs`, and the number of occurrences the pass reports. With a 5-minute schedule registered
  at `00:00` and a host that comes back at `00:17` — three instants missed, `00:05`, `00:10`,
  `00:15`:

  | policy | runs | `lastRun` | `nextDue` | meaning |
  |---|---|---|---|---|
  | `skip` | 0 | `null` | `00:20` | Abandon the missed window. The cadence resumes at the first instant after now. |
  | `coalesce` | 1 | `00:17` | `00:20` | Collapse the whole window into one occurrence, run now, resume after now. |
  | `run-once` | 1 | `00:05` | `00:10` | Run the *oldest* missed instant and advance by one interval, so the backlog is still owed. |

  `skip` and `coalesce` agree on where the cadence lands and differ on whether anything ran;
  `coalesce` and `run-once` agree on running once and differ on which instant the run stands for
  and where the cadence lands. `run-once` is the only policy that leaves work outstanding, so a
  long outage is caught up one occurrence per pass rather than all at once. `skip` is the default,
  because a schedule that silently runs a backlog is a schedule that surprises its owner.
- **Removal is a disabled version, not a delete.** The store is append-only, so `--remove` commits
  the record with `enabled: false`. A disabled schedule is driven exactly like an absent one: the
  instant that would have been due runs nothing, and the record remains readable as history.
  Deleting the record's lines from the store is equally effective, because discovery is a read.
- **Intervals and clocks.** `--every` accepts `ms`, `s`, `m`, and `h` suffixes, with a bare number
  read as seconds; a non-positive interval or an unknown unit is a usage error. `--now` pins the
  clock to an ISO 8601 instant, which is how the tests stand at a chosen moment instead of sleeping
  through a real interval. The production path uses the wall clock; the tests use `--now`, so the
  suite is fast and deterministic while the host still honours the real interval.
- **What this is not.** No cron expressions, no calendar awareness (weekends, holidays), and no
  time zones beyond UTC and local. Two hosts scheduling the same store is a coordination question
  the store's ownership rules answer, not this command.

### 3.14 Dev Tunnels and host portability

`serve --http --dev-tunnel` opts into exposure through the optional `devtunnel` executable on
`PATH`. It runs exactly `devtunnel host --port <port>`, with the port read from the bound socket
so `--port 0` works. The child process's stdout and stderr are drained concurrently; the actual
HTTP(S) URL reported under `*.devtunnels.ms` is printed as `dev tunnel: <url>` on host stderr.
It is never guessed from a tunnel name, and all child diagnostics are prefixed `devtunnel:`.
Standard output remains reserved for the transport contract.

The flag is off by default and valid only with `serve --http`. Discovery and execution use
`ProcessStartInfo` with an argument list and no shell. Missing tools and execution failures warn
without changing HTTP readiness or the exit code; a CLI that exits later also warns while local
serving continues. The child runs asynchronously after the listener binds: authentication waits,
missing URL output, or a slow CLI cannot delay requests. Host disposal stops its child process
tree, with a bounded five-second drain. No installation, provisioning, CLI configuration, consumer
authentication, or containment override is provided. Explicit exposure therefore requires a
trusted caller; D1/D2, ownership-before-harness-open, and containment gates still run first.

Portability is enforced by `RuntimeHostPortabilityTests.CorePath_HasNoPlatformSpecificApis` over
the actual runtime source files copied by the test project. All runtime orchestration, validation,
turn execution, transports, scheduling and tunnel launch code is checked for OS probes, native
imports, registry/Windows identity APIs, POSIX signals and drive-letter paths. The only excluded
file is the named `HostShutdown.cs` infrastructure adapter: it maps POSIX SIGTERM/SIGINT and
Windows console cancellation to the same cancellation token, leaving accepted turns to finish.
The adapter is necessary because Windows does not support POSIX SIGTERM registration.

`.github/workflows/build.yml` runs host smoke jobs on `ubuntu-latest` and `windows-latest`,
building the host and running all `RuntimeHost*` tests against the recorded runtime. These inspect
real wire results, persisted checkpoints, tool arguments, reported URLs, and warning-only
fallbacks. The controlled tunnel fixture reports a reserved test URL and never opens a tunnel;
tests restrict `PATH` so even a locally installed CLI cannot be used inadvertently. The POSIX
signal-specific graceful-shutdown test runs only on POSIX, not as evidence of Windows signal
handling. Each smoke job uploads its TRX results.

The smoke jobs also run JSONL storage regressions because persistence is part of host portability.
New records use LF on every platform. Replay accepts LF and existing CRLF records, retains the
CR while calculating UTF-8 byte offsets, and discards unterminated tails before applying them.
Recovery truncates only at the exact last complete record boundary; reopening a valid Windows
store therefore cannot trim bytes from its final record. Tests compare exact bytes through
recovery, append and reopen, including mixed terminators and multi-byte UTF-8 identifiers.

### 3.15 The JSON-lines transport — `serve --rpc`

`serve --rpc` is the same host as `run` and `serve --http`, with the turn reached over the
process's own standard streams instead of a socket or the command line. It opens the store, claims
it, imports and validates the definition, and classifies containment exactly as `run` does —
including refusing to start with `None` unless `--allow-uncontained` is passed — and then serves
one durable turn per request line. The turn is the same shared executor the other two commands
call, so a turn served over standard input produces the same admission, the same checkpoint, and
the same run result as a turn run from the command line.

The transport exists because a socket is not always available. A parent process that already owns
a child's pipes — an editor extension, a test harness, a supervisor — can drive the host without
allocating a port, discovering it, and racing to connect. Standard input and output are the
channel the parent already has.

- **Framing.** Newline-delimited JSON in both directions: one request per line on standard input,
  and the lines a request produces on standard output. A line is one JSON object and never spans
  lines, so a reader needs no length prefix and no parser state. Blank lines are ignored, which
  makes a trailing newline harmless. Standard output carries protocol lines only; diagnostics go to
  standard error, so a client can parse every line it reads.
- **Request.** One JSON object per line:

  | Property | Required | Meaning |
  |---|---|---|
  | `id` | yes | The caller's correlation id. Must be a non-empty string. |
  | `input` | yes | The prompt for this turn. Must be a non-empty string. |
  | `agent` | no | The agent to run. When present it must name the agent this host serves; a different name is refused rather than silently ignored. |
  | `newConversation` | no | `true` starts a fresh conversation instead of resuming the agent's existing one. Defaults to `false`. |

  ```json
  {"id":"1","input":"review the change","newConversation":false}
  ```

  The properties are the HTTP transport's (§3.12) plus `id`, because a stream has no request
  boundary to correlate on and a socket does.
- **Response.** Zero or more event lines, then exactly one result line, then — for a request that
  could not be run at all — one error line instead of the result. Every line a request produces
  carries that request's `id` as `requestId`:

  ```json
  {"type":"snapshot","requestId":"1","events":4}
  {"type":"event","requestId":"1","event":"assistant.message","id":"...","agentId":"...","data":"..."}
  {"type":"result","requestId":"1","result":{ ... }}
  ```

  | `type` | Meaning |
  |---|---|
  | `snapshot` | The conversation's transcript length before the turn, so a client knows what it is about to be told about. Always first. |
  | `event` | One session event as the runtime reported it: `event` is the event type, `id` and `agentId` are the runtime's, and `data` is the event's text — the user message's content, the assistant message's content, or a tool call's name, and `null` for an event that carries none. |
  | `result` | The run result of §3.9 under `result`, compact rather than indented because it shares a line with the framing. Always last. |
  | `error` | The request was refused. Carries `line` (the 1-based input line), `lineText` (the line as received), and `error` (what was wrong with it). |

- **The correlation key is `requestId`, not `id`.** An event line already uses `id` for the
  *session event's* id — that is the shape `examples/18-json.cs` prints, and the transport keeps it
  rather than renaming the runtime's own field. The request id therefore travels beside it as
  `requestId`, on every line, so a client can attribute a line without knowing which request is
  currently running. `examples/18-json.cs` is the protocol's definition: the snapshot, event, and
  result lines carry exactly its properties, and a test asserts that agreement by reading the
  example and checking each property it names against a live host's output.
- **Interleaving.** A request's lines are contiguous and ordered: `snapshot`, then its events in
  the order the runtime emitted them, then its result. The result is the last line and the only
  line of its type, so a client can stop reading a request at it. Events are written as the turn
  produces them, not collected and flushed at the end, so a client observes a turn in progress —
  the session starting, the assistant speaking, the session going idle — rather than only its
  outcome. Events are queued through a channel and written by a pump task, because the runtime
  delivers them on its own event thread and a slow reader must not stall it.
- **Pipelining.** Requests are read and answered one at a time, in the order they arrive: a second
  request waits for the first to finish, because a conversation admits one turn at a time. A client
  may therefore write several request lines before reading anything, and receives each request's
  lines as a contiguous block in request order. Correlation is by `requestId` rather than by
  position, so a client that does read ahead is not required to count.
- **Refusals.** A bad line is a response, never a crash, and never stops the host:

  | When | `requestId` | `error` names |
  |---|---|---|
  | The line is not JSON, or is not a JSON object. | `null` | The parse failure. |
  | `id` is missing, empty, or not a string. | `null` | `id`. |
  | `input` is missing, empty, or not a string. | The request's `id`. | `input`. |
  | `agent` is not a string, or `newConversation` is not a boolean. | The request's `id`. | The property. |
  | `agent` names an agent this host does not serve. | The request's `id`. | The agent. |

  The id is salvaged wherever the line carried one, because a client that pipelined requests needs
  to know which of them was refused, and a line can be wrong in its `input` and still be right in
  its `id`. The host keeps reading after every refusal: the next line is served normally.
- **Shutdown.** End of standard input ends the host: the read loop returns, the host waits up to 60
  seconds for a turn already in flight, and exits `0`. `SIGTERM` and `SIGINT` do the same, and the
  signal is cancelled so the process is not torn down underneath a turn — a turn in flight reaches
  its checkpoint and settles its admission instead of being abandoned, for the reason §3.12 gives.
  A turn that outlives the grace period is reported on standard error and the process exits anyway.
- **Out of scope.** Authentication and multi-tenancy are not part of this transport; it assumes the
  parent process is a trusted caller, which is what owning the pipes means. The HTTP transport
  (§3.12) and the scheduler (§3.13) are separate work.

### 3.15.1 Language ports and the parity contract

The TypeScript and Python adapters bind to the official `@github/copilot-sdk` and
`github-copilot-sdk` packages (declared version `1.0.28`). The numbered parity scope is
exactly C# `00`–`35`, including the five additions after the original `00`–`30` suite;
newer C# `36` and `37` are not silently counted as ports. The language-neutral invariants
in §3.1–§3.8 still govern both adapters; the JSON-lines transport above is separate from
their in-process store and task APIs.

| Concept | C# | TypeScript | Python |
|---|---|---|---|
| Store and atomic transaction | `DurableStore.CommitAsync`, `Transaction` | `DurableStore.commitAsync`, `Transaction` | `DurableStore.commit_async`, `Transaction` |
| Explicit ephemeral / persistent tier | `CreateEphemeral` / `Create(DurableStoreOptions)` | `createEphemeral` / `create(DurableStoreOptions)` | `create_ephemeral` / `create(DurableStoreOptions)` |
| Versioned document / fork policy | `DocumentDefinition<T>`, `DocumentHistory`, `ForkPolicy` | `DocumentDefinition<T>`, `DocumentHistory`, `ForkPolicy` | `DocumentDefinition[T]`, `DocumentHistory`, `ForkPolicy` |
| Draft and immutable snapshot | `tx.Doc`, `draft.Save`, `conversation.Snapshot` | `tx.doc`, `draft.save`, `conversation.snapshot` | `tx.doc`, `draft.save`, `conversation.snapshot` |
| Conversation / SDK transcript | `DurableConversation`, `ContextAsync`, `Entries` | `DurableConversation`, `contextAsync`, `entries` | `DurableConversation`, `context_async`, `entries` |
| Checkpointed task and memo | `DurableTask`, `TaskRuntime.MemoAsync`, `TaskCommit` | `DurableTask`, `TaskRuntime.memoAsync`, `TaskCommit` | `DurableTask`, `TaskRuntime.memo_async`, `TaskCommit` |
| Scheduler / recover / abort tree | `DurableHarness.ResumeAsync`, `AbortTaskAsync` | `DurableTaskRunner.resumeAsync`, `abortTaskAsync` (also harness delegates) | `DurableHarness.resume_async`, `abort_task_async` |
| Named extension registry / tools | `ExtensionRegistry`, `Extension`, `ToolWrapper` | `ExtensionRegistry`, `Extension`, `ToolWrapper` | `ExtensionRegistry`, `Extension`, `ToolWrapper` |
| Watch stream | `conversation.Watch` | `watchDocument`, `WatchHandle` | `DurableWatch.frames` |
| Native sandbox wire policy | `DurableSandboxPolicy`, SDK `SandboxConfig` | `DurableSandboxPolicy`, `buildSandboxConfig` | `DurableSandboxPolicy`, `build_sandbox_config` |

`entries` is the app-written transcript, not native SDK history. In particular, corrected
example `13` has **exactly zero** durable plain entries after an SDK-only chat turn; its
user/assistant/system transcript assertions remain LIVE. Fork parity checks inherited
state at the boundary, an independent fork edit, and **exactly two** filtered entries
(inherited + own), excluding inherited and own durable bookkeeping. The unit fact uses
the real store fork primitives without claiming native `sessions.fork` execution.

The recovery fact starts separate processes against an actual JSONL file: the initial
run records a memo and exits abruptly, the first recovery exits again, and the second
recovery completes. A physical side-effect file must contain exactly one line, the memo
must survive reopening, and the terminal record must report two restarts. This proves
the memo guard for this checkpoint scenario, not general exactly-once delivery of an
arbitrary external service. Rollback must restore entries, documents, and conversation
inventory; abort must visit the parent and two owned children once and persist all
three exact terminal/aborted outcomes. Mutations change those **real library sources in
isolated copies**, not an alternate implementation or another agent's worktree.

**Manifest schema.** `examples/parity.json` is the checked-in, generated version-1
contract. `python examples/parity_manifest.py` validates it; `--write` regenerates it
only after a range owner's explicit handoff. It combines all three range declarations
in each language (`parity-00-10.json`, `parity-11-21.json`, `parity-22-35.json`). A change
to source inventory, range evidence, status, or expected output makes the runners fail
until the contract is deliberately updated.

| Field | Meaning / required invariant |
|---|---|
| `schemaVersion`, `scope`, `reference` | `1`, `[0,35]`, `scripts/validate.sh`; 36 unique entries in numeric order. |
| `examples[].id`, `source` | Two-digit ID and exact C# basename, each present once. |
| `live` | Exact membership in C# `LIVE_EXAMPLES`; not an implementation status. |
| `deterministic`, `liveReason` | Exact C# deterministic selection and verbatim exclusion reason, or `null` for an included scenario. These are independent of `live` (`17`, `18`, `35` have both flags). |
| `expectedOutput` | Checked-in actual C# stdout path for `00`, `01`, `03`, `04`; `null` elsewhere. |
| `languages.ts` / `languages.py` | Exact source `path`, `status`, `sandboxMode`, `rangeManifest`, and the unchanged range row as `evidence`. |
| `status` | `ported` means implemented; **does not mean LIVE-validated**. `pending-native-api` requires an actionable `evidence.pendingReason` and fails when selected, rather than becoming an exclusion or a pass. |
| `sandboxMode` | `supported`, `unsupported`, or `not-applicable`; unsupported requires specific proven `evidence.sandboxReason`, never a blanket claim that the SDK lacks sandbox configuration. |
| `evidence` | Preserves validation status, original assertions, conditional `liveAssertions`, known limits, fixture configuration, and optional positive-integer `timeoutSeconds`. Reasons must be nonempty strings. |

**Runner modes and evidence.** `npm run examples:ts` and `python examples/run_all.py`
default to `--all-available`: run the full inventory with the signed-in native CLI,
request `--live` for supported sandbox examples, and fail on every assertion, pending
API, missing file, or subprocess failure. Replay overrides are refused in this mode.
`--deterministic` runs exactly
`00,01,03,04,05,06,09,11,12,14,17,18,23,32,35` with the built ReplayRuntime, printing
each other ID and its exact C# exclusion reason. `--list` validates and prints selection
only; it is not execution evidence. Optional IDs narrow, never widen, the selected mode.
Example `30` retains its real startup delay and a 300-second runner limit.

Sandbox `28`, `33`, and `34` have default policy-only checks and separate required LIVE
effects; passing the former does not prove the latter. `35` has a deterministic SDK
observation path and conditional backend/enforcement/bypass assertions. The SDK exposes
native sandbox configuration in both languages: availability of that wire API is not
proof of native enforcement, credential injection, or a granted bypass on a given host.
Likewise, replay-generated session IDs carry explicit **synthesised, NOT recorded
transcript** markers. Fixture interoperability proves the tested durable/SDK protocol
scenario, not a LIVE model/tool selection.

Native evidence is preserved from the range owners, including historical timing
failures and subsequent synchronized example `20` runs. Its readiness barrier uses
the real SDK tool handler; post-open failure races and cleanup have focused tests.
SDK bootstrap remains noninterruptible, so a never-settling SDK open is not proven
bounded by the 90-second example deadline. Example `21` still has a model/tool-choice
acceptance gap. Example `35` retains the unexercised host-decline path when no escalation
was offered, and macOS native evidence does not certify Linux/Windows enforcement.
Corrected examples `24` and `31` distinguish SDK context from zero public app entries;
permission review uses an app-owned document and verifies fork-boundary grants rather
than treating internal checkpoint/admission anchors as public transcript entries.

**Output comparison.** Deterministic runs compare the entire normalized stdout of four
actual ported examples per language against `examples/expected/*.txt`. The files were
captured by executing the same C# sources with ReplayRuntime `basic.json`, not invented
from the port. `tests/parity/capture_reference.py` repeats the C# capture and compares it
without rewriting; `--write` is the explicit recapture operation. Provenance includes
exact commands and SHA-256 hashes of the C# source, fixture, and expected stdout.

Both normalizers mirror `scripts/validate.sh`: volatile temporary paths, session IDs,
GUIDs and timestamps become placeholders; they additionally handle Windows temporary
paths, hyphenated SDK session IDs, CRLF, and generated `c`/`e`/`t` numeric identities
whose allocation differs between languages. Numeric counts, enum/boolean casing,
labels, ordering, model reports and all other text remain unchanged. Diagnostics on
stderr remain visible. Incorrect actual stdout, a wrong expected file (even with a
refreshed hash), a removed inventory entry, and a failing original assertion are each
covered by real-runner mutation gates. Source-only/typecheck/LIVE-exclusion success is
not a substitute for these comparisons.

## 4. Example suite (38 examples: Pi `00`–`30` mirrored one-to-one, plus seven C#-only additions)

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
| 36 | — (C#-only) | `36-durable-client-builder.cs` | The builder/facade boundary: `Build()` validates and connects to nothing, ownership decides what disposal releases, one store takes one runtime, stored choices are authoritative. |
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

CI enforces requirements 1 and 2 for the 15 examples that need no Copilot sign-in by running
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
- The deterministic examples: seventeen of the thirty-eight. Three of them, `32-jsonl-store`,
  `36-durable-client-builder`, and `37-ef-core-store`, never open a Copilot session. The other
  fourteen create one, and the recorded runtime serves it from the recording (see below).
- The effective-model check: every example that opens a session must print
  `effective model: <id>`, and the id must be the model the fixture records. See below.
- The determinism check: each of those examples is run a second time and its output compared
  against the first run after normalisation. See below.

**The effective model.** A session-opening example prints the model the runtime reported for
its session, read from `DurableConversation.EffectiveModel` — never from the harness's
requested default, which is what the host asked for rather than what the runtime answered. The
runtime reports it on `session.start`, which the recorded runtime emits from the fixture
session's `model` field, so a recording that names a model is a runtime that reports one.

The print alone proves nothing, because it comes from the library. `scripts/validate.sh
--deterministic` therefore reads the model out of `tools/ReplayRuntime/fixtures/basic.json`
itself and compares: a printed id that differs from the recording fails the run, an example
that opens a session but prints no line fails the run, and an example that opens no session but
prints a line fails the run. Whether an example opens a session is derived from its source, so
an example added later is covered without being listed anywhere.

**The determinism check.** The same example, run twice, must say the same thing. Three values
legitimately differ between runs — the temporary workspace each run creates, the session id the
runtime hands out, and any timestamp — so the check normalises them before comparing. The
normalisation lives in `scripts/validate.sh`, not in the examples: an example keeps printing
what it actually saw. An example that has to print a live state waits for that state to be
observable rather than for a duration — `23-child-tasks` prints its task graph once the
checkout is waiting on its payments and every payment has committed the checkpoint that says
it is waiting for the bank, so the print is the same on every run.

**The exclusion list is empty, and stays a record of known-bad examples.** An example that
varies between runs is a bug in that example, so the fix belongs in the example and never in a
new entry here: an entry added to silence a failure hides it on every later run. The list
exists so that a genuinely unavoidable variation can be excluded with its reason printed, not
so that a check can be quietened.

**In CI.** `.github/workflows/build.yml` runs this mode on every push and pull request to
`dev` and `main`, as a `Validate examples (deterministic)` step after the plain
`SKIP_LIVE=1 SKIP_TESTS=1 ./scripts/validate.sh` step. The plain step stays because it covers
the live-gated examples this mode excludes; the deterministic step is separate rather than a
matrix leg so that a non-deterministic example fails one named step beside the plain pass it
complements. The determinism comparison is the only thing in either step that runs an example
twice, so a regression that prints a fresh id, a fresh timestamp, or reordered lines fails CI
rather than passing it.

**Synthesised sessions.** The durable layer names the session it asks for (§3.6), and the name
is generated per conversation, so a `session.create` arrives with an id no fixture can record.
The recorded runtime no longer refuses: it serves the request from the fixture's first recorded
session and says so.
The marker is the phrase `synthesised session`, written to stderr and, when
`REPLAY_MARKER_PATH` names a file, appended to that file — the SDK captures the runtime's
stderr and discards it on success, so a caller that needs to know whether a pass was recorded
needs a sink it controls. `scripts/validate.sh --deterministic` sets that variable and prints

```
PASS     00-conversation.cs (synthesised session — not a recorded transcript)
```

for a synthesised pass, and `(recorded transcript)` for a pass that used a session the
fixture names. A synthesised pass can therefore never be mistaken for a recorded one.

**What it does not run, and why.** Twenty-one examples are excluded, and the mode names each
one with its reason rather than skipping it silently. The reasons fall into four groups:

- **A request the runtime does not answer.** `02-forks` and `08-context` need `sessions.fork`;
  `22-subagent-background` needs a subagent request; `24-compaction` needs a compaction
  request; `07-harness-conversations` sets a reasoning effort, which makes the harness query
  `models.list` for a model the fixture does not record. The runtime refuses an unrecorded
  method rather than inventing a response, and the example fails with
  `ConnectionLostException`.
- **An assertion about a tool call.** `10`, `16`, `21`, `25`, `26`, `27`, `28`, `29`, `33`, and
  `34` assert that a tool ran, what it wrote, or what it saw. The fixture records the model's
  side of the conversation only, so no tool call happens and the assertion fails. A
  synthesised session cannot fix this: the missing thing is the tool round trip, not the
  session.
- **An assertion about model output.** `13-chat` and `15-real-model` assert the model's answer
  and its streaming deltas; `19-inbox` and `20-late-join` assert the shape of the transcript.
  The recording has one canned answer, so these assertions are about a real model.
- **An assertion about the host.** `30-reload-and-restart` waits for a runtime restart the
  recording does not perform; `31-permissions` asserts a permission request the recording does
  not contain.

Recording one transcript per example would not lift most of these: the fixture would still
have to contain tool calls, model answers, and host events that only a live run produces. The
honest split is the one above — the examples whose own code paths the recording can drive run
deterministically, and the rest are named with the reason they cannot.

**What it proves.** The example sources and the runtime protocol are exercised with no
network access, no Copilot sign-in, and no ambient state: the recorded runtime answers from a
fixed epoch with sequential event ids, so a passing run is reproducible on any machine. A
synthesised pass proves the example's own code paths — its store, its harness wiring, its
event handling — and not a recorded transcript.

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
