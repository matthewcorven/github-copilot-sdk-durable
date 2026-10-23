# Durable GitHub Copilot SDK agents in C#

A suite of 31 self-contained C# examples that build **durable** agents on the
[GitHub Copilot SDK](https://github.com/github/copilot-sdk). Every example is a single
file-based .NET app — no `.csproj`, no solution, no build step beyond `dotnet run`.

The suite mirrors the breadth of the [Pi Durable](https://github.com/earendil-works/pi/tree/main/packages/durable)
example set: conversations, documents, forks, watches, tasks, recovery, subagents,
compaction, plan mode, tool overrides, and hot reload. Each example names the Pi example
it mirrors.

The idea behind all of them: **the agent's state lives in your store, not in the model's
context.** A conversation is a durable transcript scope. Documents are versioned JSON
attached to a conversation. Tasks are checkpointed state machines. Extensions are named
bundles of code that a conversation selects *by name*, so the selection survives a
restart and binds to whatever code the new process installs.

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 or later | File-based apps (`dotnet run file.cs`) need .NET 10 |
| GitHub Copilot CLI | 1.0.103 or later | The SDK drives the CLI over RPC |
| GitHub Copilot access | an active subscription | The examples make live model calls |

Check both:

```bash
dotnet --version
copilot --version
```

The SDK authenticates through the Copilot CLI, so sign in first:

```bash
copilot
```

---

## Running an example

Every example is one file. Run it from the `examples/` directory:

```bash
cd examples
dotnet run 00-conversation.cs
```

Or from the repository root:

```bash
dotnet run examples/00-conversation.cs
```

Examples that call a model take 30–90 seconds. The ones that only exercise the durable
machinery run in under a second.

---

## The examples

### Foundations

| # | Example | What it shows |
|---|---|---|
| [00](examples/00-conversation.cs) | Conversations | A store, a harness, and one conversation. A conversation is created inside a transaction, so everything about it appears at once. |
| [01](examples/01-documents.cs) | Documents | Versioned JSON attached to a conversation. `Rewindable` keeps old values; `Latest` keeps only the current one. |
| [02](examples/02-forks.cs) | Forks | A new conversation that continues from one entry of another, inheriting the parent's transcript and documents up to that point. |
| [03](examples/03-owned-conversations.cs) | Owned conversations | A background task that supervises a child conversation. All three records are created in one commit: after a crash, either all exist or none do. |
| [04](examples/04-watches.cs) | Watches | Serializing asynchronous document work. A watch starts from one stable revision; slow callbacks never overlap. |
| [05](examples/05-harness.cs) | The harness | A store plus conversation handles plus durable tasks. Extension code is installed by name and can be replaced in place. |
| [06](examples/06-configuration.cs) | Configuration | Per-conversation agent choices (model, reasoning effort, extensions, tools, working directory) and harness-wide run policy that is read at every use and never stored. |
| [07](examples/07-harness-conversations.cs) | Harness conversations | Conversation handles are stateless — compare them by id. Handles bind commits to their conversation and can be forked. |

### Context and code

| # | Example | What it shows |
|---|---|---|
| [08](examples/08-context.cs) | Context | The stored transcript versus the model context. Entries are immutable; the model sees a window over them. |
| [09](examples/09-registry-reload.cs) | Registry reload | Reloading is installing a new object with the same name. It replaces the old one in one publication, so no conversation ever sees it missing. |
| [10](examples/10-extension-state.cs) | Extension state | An extension that keeps its own per-conversation document. The tool writes it; a prompt section shows it to the model before every request. |
| [11](examples/11-tasks.cs) | Tasks | A durable task: a small state machine whose checkpoint is saved after every step. |
| [12](examples/12-recovery.cs) | Recovery | Close, reopen, and continue where a task stopped. Everything a task needs is in storage. |
| [13](examples/13-chat.cs) | Chat | A chat turn. Input is durably admitted before the model sees it. |
| [14](examples/14-system-prompt.cs) | System prompt | Prompt sections and per-conversation instructions. The app stores no prompt state: sections are rendered per request. |

### Running the agent

| # | Example | What it shows |
|---|---|---|
| [15](examples/15-real-model.cs) | A real model | Streaming an answer from a real model, with the final message arriving after the deltas. |
| [16](examples/16-coding-tools.cs) | Coding tools | A coding-agent turn: the model reads, edits, and runs commands, then answers. Tools reach files only through the conversation's working directory. |
| [17](examples/17-print.cs) | Print mode | Submit one prompt and print its answer, like `pi -p`. |
| [18](examples/18-json.cs) | JSON mode | Stream one prompt's run as JSON lines, like `pi --mode json`. |
| [19](examples/19-inbox.cs) | The inbox | What happens to submissions while a conversation is busy. The conversation stays busy until the first answer is released. |
| [20](examples/20-late-join.cs) | Late join | A client attaches while a run is already underway. It gets the current state first, then the live events. |
| [21](examples/21-subagent-foreground.cs) | A foreground subagent | The parent's tool call creates a child conversation it owns, and waits for it. |
| [22](examples/22-subagent-background.cs) | Background subagents | Persistent background subagents: one `subagent` tool starts named helpers that outlive the turn. |
| [23](examples/23-child-tasks.cs) | Child tasks | A task that owns child tasks, waits for them, and fails fast if one fails. |

### Advanced

| # | Example | What it shows |
|---|---|---|
| [24](examples/24-compaction.cs) | Compaction | A long chat whose older messages are summarized. The Copilot SDK compacts natively, so the app never has to. |
| [25](examples/25-coding-agent.cs) | A coding agent | Built-in coding tools, a prompt that knows the working directory, settings backed by a file the app watches, and an environment that follows the conversation's directory. |
| [26](examples/26-plan-mode.cs) | Plan mode | The user switches a conversation into a read-only planning mode. The tool list changes immediately; the plan is written to a document. |
| [27](examples/27-reviewer.cs) | A reviewer agent | A cheaper model, a review role, a review loop, read-only tools, and its own checkout. Everything the reviewer is, is stored on its conversation. |
| [28](examples/28-sandbox-per-conversation.cs) | One sandbox per conversation | The app records each conversation's sandbox in its own document, and the tool looks it up for every call. |
| [29](examples/29-tool-override.cs) | Tool override | Two extensions define a tool with the same name; the later one wins. A third wraps whichever won, so reloading either implementation keeps the wrapper. |
| [30](examples/30-reload-and-restart.cs) | Reload and restart | Reload an extension while a call runs, then restart the process. Running work finishes on the code it started with; stored choices are names, so they bind to the new code. |

---

## The shared library

Every example includes one file, [`lib/Durable.cs`](lib/Durable.cs) (~2000 lines), with
`#:include ../lib/Durable.cs`. It is the durable layer that the examples share; the
examples themselves contain all the product code.

```csharp
#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs
```

### Core types

| Type | Purpose |
|---|---|
| `DurableStore` | The durable state. Persists to a single JSON file atomically (temp file + move), or stays in memory. |
| `DurableHarness` | Owns the client, the store, the registry, and the conversations. `OpenAsync`, `RootAsync`, `CreateConversationAsync`, `ForkAsync`, `CreateTaskAsync`. |
| `DurableConversation` | A Copilot session bound to durable app state. `SubmitAsync`, `Snapshot`, `CommitAsync`, `ConfigureAsync`, `Agent`, `ForkAsync`. |
| `Transaction` | The unit of atomicity. `CreateConversation`, `AppendEntry`, `Doc`, `CreateTask`. Everything written inside commits together, or not at all. |
| `DocumentDefinition<T>` | A document's kind, history policy, fork policy, and initial value. |
| `DocumentDraft<T>` | An editable copy of a document. Assign to `.Value`, then `.Save(entryId)`. |
| `Extension` | A named bundle of tools, prompt sections, durable tasks, and tool wrappers. |
| `ExtensionRegistry` | Installs extensions by name. Installing the same name replaces the extension in place. |
| `PromptSection` | A named piece of the system prompt, rendered per request. |
| `AgentChoices` | The per-conversation agent configuration: model, reasoning effort, extension names, tool names, working directory, instructions. |
| `DurableTask` / `TaskRuntime` / `TaskCommit` | Durable tasks: a checkpointed state machine with idempotent memos and durable timers. |
| `ToolWrapper` | Wraps a tool *by name*, so it decorates whichever implementation wins. |

### Documents

A document is versioned JSON attached to a conversation. Two policies decide what a fork
inherits:

```csharp
// Rewindable + AsOf: a fork starts with the values its parent had at the fork entry.
var Todos = new DocumentDefinition<List<string>>(
    "example.todos",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => []);

// Latest + Initial: a fork starts empty, so a child does not inherit its parent's list.
var Registry = new DocumentDefinition<Dictionary<string, string>>(
    "example.registry",
    DocumentHistory.Latest,
    ForkPolicy.Initial,
    () => new Dictionary<string, string>());
```

### Writing a document

```csharp
await conversation.CommitAsync(async tx =>
{
    var draft = tx.Doc(Todos, conversation.Id);
    draft.Value.Add("fix the build");
    draft.Save(tx.AppendEntry(conversation.Id, "todo", "fix the build"));
});
```

### Extensions and tools

```csharp
var todo = CopilotTool.DefineTool(
    ([Description("Item to add")] string item) => { /* ... */ return $"added {item}"; },
    factoryOptions: new AIFunctionFactoryOptions { Name = "todo", Description = "Add an item" });

registry.Install(new Extension(
    "todo",
    tools: [todo],
    sections: [new PromptSection("todos", input => Task.FromResult<string?>("..."))]));
```

A conversation selects extensions **by name**:

```csharp
await conversation.ConfigureAsync(new AgentChoices
{
    Extensions = ["todo"],
    Tools = ["todo"],
    Model = "gpt-6-luna",
});
```

Because the selection is names, it survives a restart and binds to whatever code the new
process installs — that is the whole point of example 30.

---

## Pi Durable → Copilot SDK mapping

| Pi Durable | C# here | Notes |
|---|---|---|
| `Harness` | `DurableHarness` | Owns the store, the registry, and the conversations. |
| `Conversation` | `DurableConversation` | A transcript scope bound to a `CopilotSession`. |
| `defineDoc` | `DocumentDefinition<T>` | `history` → `DocumentHistory`, `fork` → `ForkPolicy`. |
| `tx.doc(...)` | `tx.Doc(...)` | Returns a `DocumentDraft<T>`; `.Save(entryId)` stages the write. |
| `createRegistry` / `defineExtension` | `ExtensionRegistry` / `Extension` | Installed by name; same name replaces in place. |
| `section(...)` | `PromptSection` | Rendered per request, not once at session creation. |
| `GenerationTask` | `CopilotSession` + `SessionHooks` | The SDK's own generation loop, with hooks for review loops and tool interception. |
| `defineTask` / `TaskRuntime` | `DurableTask.Define` / `TaskRuntime` | Checkpointed state machines with `MemoAsync` and `SleepUntilAsync`. |
| `wrapTool` | `ToolWrapper` | Wraps by name, so it decorates whichever implementation wins. |
| `MemoryStorage` / `openNodeSqliteStorage` | `DurableStore` | In memory, or a single JSON file written atomically. |
| `fork` | `harness.ForkAsync` | Inherits the parent's transcript and documents up to the fork entry. |

---

## Validating the suite

Validation runs in two complementary layers:

1. **Self-asserting examples:** Every single example includes explicit `Check.*` assertions testing the durable invariants it claims (no duplicate executions across crash restarts, transaction rollback on mid-commit failure, document fork isolation, child-task abort cascade, etc.). An unhandled exception or failed assertion causes `validate.sh` to fail immediately.
2. **xUnit test suite (`tests/Durable.Tests`):** Focused unit and integration tests verifying `DurableStore` rollback semantics, fork entry filtering and state independence, store crash recovery with memo idempotency, and abort propagation.

```bash
# Every example: compile, run xUnit suite, and run all 31 examples.
./scripts/validate.sh

# Fast offline validation: run xUnit suite + examples that do not call a model.
SKIP_LIVE=1 ./scripts/validate.sh

# Run only the xUnit test suite for the library.
dotnet test tests/Durable.Tests

# Compile only, no model calls at all.
COMPILE_ONLY=1 ./scripts/validate.sh

# A few specific examples.
./scripts/validate.sh 00 05 12
```

The script reports `PASS`/`FAIL` per example and prints the output of each one.

---

## Model selection and costs

The examples focus on durable mechanics rather than model quality, defaulting to inexpensive models (primarily `gpt-6-luna`). 

For token pricing tables, reasoning effort constraints, and SDK-specific tool-authoring gotchas (tool shadowing, serializer limitations, event models), see [**`COPILOT_SDK_NOTES.md`**](COPILOT_SDK_NOTES.md).

---

## Repository layout

```
examples/            31 self-contained file-based C# apps
lib/Durable.cs       the shared durable layer every example includes
tests/Durable.Tests  xUnit test suite for the durable library
scripts/validate.sh  compile-and-run validation for the whole suite
COPILOT_SDK_NOTES.md Copilot SDK gotchas, tool shadowing rules, and pricing
DESIGN.md            the architecture mapping this suite was built from
```

## License

MIT. See [LICENSE](LICENSE).
