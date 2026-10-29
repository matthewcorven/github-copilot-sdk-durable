# The durable client API — final signatures (#7.1)

**Issue:** #35 — "#7.1 Finalize the approved public signatures"
**Parent:** #7 — the approved public API direction
**Branch:** `squad/35-finalize-durable-client-signatures`
**Date:** 2026-10-28
**Status:** Declarations and docs only. Ready for Lead review before #7.2 starts.

This document records the signatures that now exist in `lib/Durable.cs` and
`lib/Durable.DependencyInjection.cs`, the two rules that are part of the contract rather
than of the implementation (ownership and configuration precedence), and the boundary each
downstream issue has to fill in. It does not re-open anything settled in #7.

---

## 1. Scope of this change

**In:** declarations, XML documentation, the DI registration shape, and this note.

**Out:** every behavior. Each unimplemented member throws `NotImplementedException` naming
the issue that implements it, so an accidental call fails loudly at the call site instead
of silently doing half the work. #7.2–#7.5 replace those throws; they do not change the
signatures without going back to the owner-approved direction.

The full public surface is declared and compiles:

```bash
dotnet build lib/Copilot.Durable.csproj   # 0 warnings, 0 errors
```

`Durable.cs` stays a single file for the runtime types it already holds. The DI
registration lives in `Durable.DependencyInjection.cs` because `Durable.cs` uses a
file-scoped namespace and C# does not allow a file-scoped namespace to share a file with a
nested one (`CS8955`). The project compiles an explicit file list
(`EnableDefaultCompileItems=false`), so both files are listed in
`lib/Copilot.Durable.csproj`.

---

## 2. Final public signatures

### 2.1 `DurableClientOptions`

Everything a `DurableCopilotClient` is built from. Mutable by design: a host states what it
already has, and the builder fills in the rest.

```csharp
namespace Durable;

public sealed class DurableClientOptions
{
    public DurableStore? Store { get; set; }
    public DurableStoreOptions? StoreOptions { get; set; }
    public ExtensionRegistry? Registry { get; set; }
    public CopilotClient? Client { get; set; }
    public CopilotClientOptions? ClientOptions { get; set; }
    public DurableHarnessOptions Harness { get; set; } = new();
    public bool? OwnsStore { get; set; }
    public bool? OwnsClient { get; set; }
}
```

| Member | Meaning |
|---|---|
| `Store` | A store the caller already opened. **Borrowed.** |
| `StoreOptions` | How to create the store when `Store` is `null`. **Owned** by the durable layer, created during `InitializeAsync`. |
| `Registry` | This process's extension code. Never persisted, never disposed. `null` means an empty registry. |
| `Client` | A Copilot SDK client the caller already has. **Borrowed**, and never started or disposed by the durable layer. |
| `ClientOptions` | How to construct the SDK client when `Client` is `null`. **Owned**, constructed and started during `InitializeAsync`. |
| `Harness` | Host-level options applied to every session: permission handler, file hooks, hook-failure tolerance. |
| `OwnsStore` / `OwnsClient` | `null` means the default policy (see §4). `true`/`false` override it. |

Exactly one of `Store` / `StoreOptions` must be set by the time `Build()` runs. `Client` and
`ClientOptions` may both be `null` — the durable layer then constructs a client from the
SDK's defaults, and owns it.

### 2.2 `DurableClientBuilder`

```csharp
public sealed class DurableClientBuilder
{
    public DurableClientBuilder();
    public DurableClientOptions Options { get; }

    public DurableClientBuilder UseStore(DurableStore store);
    public DurableClientBuilder UseStore(DurableStoreOptions options);
    public DurableClientBuilder UseClient(CopilotClient client);
    public DurableClientBuilder UseClient(CopilotClientOptions? options);
    public DurableClientBuilder UseRegistry(ExtensionRegistry registry);
    public DurableClientBuilder UseHarnessOptions(DurableHarnessOptions options);
    public DurableClientBuilder Configure(Action<DurableClientOptions> configure);

    public DurableCopilotClient Build();
}
```

**`Build()` returns `DurableCopilotClient`, not `CopilotClient`.** What the caller gets back
is the durable host; the SDK client is one of the things it drives, reachable through
`DurableCopilotClient.NativeClient` (§5).

The two `UseStore` overloads and the two `UseClient` overloads are mutually exclusive by
last-write-wins: `UseStore(store)` clears `StoreOptions`, and `UseStore(options)` clears
`Store`. That makes "borrowed or created" a single decision with a single winner, instead of
a combination the runtime has to reconcile. The same holds for the client overloads;
`UseClient(null)` is legal and means "construct one from the SDK defaults, and own it".

Every `Use…` method rejects `null` where `null` would be ambiguous — `null` is never a
silent "forget what I said" for a supplied instance.

`Configure` is the escape hatch for option fields no `Use…` method names, the ownership
flags in particular. It runs immediately, not at build time, so a host can inspect
`Options` and see the effect.

### 2.3 `DurableCopilotClient`

```csharp
public sealed class DurableCopilotClient : IAsyncDisposable
{
    public DurableClientOptions Options { get; }
    public DurableStore Store { get; }
    public ExtensionRegistry Registry { get; }
    public CopilotClient NativeClient { get; }
    public bool IsInitialized { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default);

    public Task<DurableConversation> CreateConversationAsync(
        AgentChoices? choices = null,
        string? ownerTaskId = null,
        string? ownerConversationId = null,
        CancellationToken cancellationToken = default);

    public Task<DurableConversation> RootAsync(
        AgentChoices? choices = null,
        CancellationToken cancellationToken = default);

    public Task<DurableConversation> ResumeConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    public Task<DurableConversation> ForkAsync(
        DurableConversation parent,
        string entryId,
        CancellationToken cancellationToken = default);

    public DurableConversation? FindConversation(string conversationId);
    public DurableConversation? FindConversation(Func<DurableConversation, bool> match);

    public SessionConfig GetSessionConfig(
        AgentChoices? choices = null,
        DurableConversation? conversation = null);

    public Task<SessionConfig> GetSessionConfigAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<TaskOutcome>> ResumeTasksAsync(
        CancellationToken cancellationToken = default);

    public Task<TaskOutcome> WaitForTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default);

    public ValueTask DisposeAsync();
}
```

`Store` and `NativeClient` are readable only once the resource exists. Before
initialization they throw `InvalidOperationException` with a message naming
`InitializeAsync`, rather than returning `null` and turning a lifetime bug into a
`NullReferenceException` somewhere else. `Registry` is never unavailable — a client with
none supplied gets an empty one.

`IsInitialized` is the non-throwing probe.

The constructor is `internal`: a client comes from `DurableClientBuilder.Build()` or from
the DI registration, never from `new`.

### 2.4 `DurableConversation` is the durable session handle

There is **no** competing durable-session type. `CreateConversationAsync`, `RootAsync`,
`ResumeConversationAsync`, and `ForkAsync` all return the existing
`DurableConversation` — the same type `DurableHarness` returns — so a facade-created
conversation and a harness-created conversation are interchangeable, and `FindConversation`
returns handles rather than a projection of them.

Consequences a downstream issue must preserve:

- The handle's documents, entries, answers, and forks are the store's, not a copy.
- Its session configuration is built by the shared runtime (§6), so `Choices()`,
  `Agent()`, and the sandbox policy resolve the same way whichever entry point made it.
- `DurableConversation.ForkAsync` and `DurableCopilotClient.ForkAsync` are the same
  operation; the facade overload exists because the client is where the runtime's
  coordination lives.

### 2.5 Dependency injection

```csharp
namespace Durable.DependencyInjection;

public static class DurableServiceCollectionExtensions
{
    public static IServiceCollection AddDurableCopilotClient(this IServiceCollection services);

    public static IServiceCollection AddDurableCopilotClient(
        this IServiceCollection services,
        Action<DurableClientOptions> configure);
}
```

The extension methods live in `Durable.DependencyInjection`, a namespace the package owns,
so `using Durable.DependencyInjection;` is an explicit opt-in and a host that never uses DI
never sees the method.

The registration follows the standard .NET options/ownership conventions:

- The `Action<DurableClientOptions>` overload is the options pattern — the callback
  configures the same `DurableClientOptions` a manual build would, so
  `services.AddDurableCopilotClient(o => { o.StoreOptions = …; o.Registry = …; })` and
  `new DurableClientBuilder().UseStore(…).Build()` describe the same client.
- Registration is idempotent: a second `AddDurableCopilotClient` call does not register a
  second client.
- `DurableCopilotClient` is registered as a singleton, and the container disposes it,
  which runs the same exactly-once disposal described in §4.
- A supplied `DurableStore` or `CopilotClient` stays borrowed exactly as it would be in a
  manual build. The registration never takes ownership of an instance the container did not
  create.

**This is not a second implementation.** The methods delegate to `DurableClientBuilder` and
return the same `DurableCopilotClient` over the same internal runtime (§6). Mixing styles in
one application is safe as long as the store is shared deliberately rather than opened
twice — a persistent store holds an exclusive writer lock, so a second open of the same path
is a bug however the second client was built.

---

## 3. `Build()` validates and constructs — nothing else

`Build()` does **not** connect, and it performs **no persistence I/O**:

- It opens no store file and acquires no writer lock.
- It starts no SDK client and spawns no Copilot CLI process.
- It resumes no task, renders no prompt section, and resolves no extension code.

It validates the options — is exactly one store source set, do the ownership flags
contradict a supplied instance, is the harness options object present — and constructs the
facade around them. Construction failure is limited to "the options cannot describe a
client", thrown as `InvalidOperationException` from `Build()`.

Everything that leaves the process happens in `InitializeAsync`, or implicitly in the first
operation that needs the store or the SDK client. That split is what makes it safe to build
a client during dependency-injection container construction, or at the top of a program that
may never do any work: construction is cheap, and startup cost is paid where the host
chooses to pay it.

### 3.1 Initialization is asynchronous and coordinated

`InitializeAsync` is asynchronous because it may open a store, start an SDK client process,
and re-enter running tasks — and because waiting synchronously for any of those risks a
deadlock in a host with a synchronization context.

Concurrent first operations share **one** initialization. The work happens once, and every
caller awaits the same result. A host therefore does not need a lock, an `Interlocked`
guard, or a "did I call this already" flag around its startup path; calling
`InitializeAsync` from several places is correct by construction.

A failed initialization is not retried implicitly. The fault is observed by every caller
waiting on that attempt, and the caller decides whether to try again — the facade does not
guess whether a transient CLI start failure should be retried.

---

## 4. Ownership contract

The rule is one sentence: **borrow what the caller supplied, own what the durable layer
created.**

| Situation | Owner | Disposed by the client? |
|---|---|---|
| `Store` supplied by the caller | Caller | No |
| Store created from `StoreOptions` | Durable layer | Yes |
| `Client` supplied by the caller | Caller | No — and never started by the durable layer either |
| Client created from `ClientOptions` or from defaults | Durable layer | Yes |
| `Registry` | Caller (never disposed) | No |
| `DurableHarnessOptions` | Caller (plain options object) | No |

`OwnsStore` and `OwnsClient` are `bool?`. `null` — the default — applies the rule above.
`true` says "this client obtained it for itself and should release it", which is what a host
that created a store with `DurableStore.Create(new DurableStoreOptions())` for one client's
exclusive use wants. `false` says "keep it open past this client's lifetime".

Disposal is **exactly once**:

- `DisposeAsync` is idempotent. The second call is a no-op, so a `using` statement, an
  explicit call, and a container's disposal can all run against one client without
  double-disposing anything.
- A created store's disposal releases its exclusive writer lock, which is why a persistent
  store must be disposed before another process opens the same path.
- A borrowed store is not disposed, so the order in which a host disposes a borrowed store
  against this client is the host's choice.
- A borrowed client is not disposed and was never started here, so it is not left in a state
  this layer changed.

This differs from `DurableHarness`, which disposes its client unconditionally because it
always created it, and never disposes a store. The facade cannot inherit that behavior: a
borrowed SDK client disposed by the durable layer would tear down a process the host is
still using.

---

## 5. The native SDK escape hatch

`DurableCopilotClient.NativeClient` returns the underlying `CopilotClient`. It is an explicit
escape hatch and it is documented in three places: on the property, on the builder's
`Build()`, and here.

**Everything reached through `NativeClient` is outside the facade's durability
guarantees.** A session created, resumed, or forked through the raw SDK:

- is not a `DurableConversation`: no stored choices, no bound documents, no entries;
- does not have its grants enforced by the durable permission policy;
- does not appear in `FindConversation`;
- is not re-entered by `ResumeTasksAsync` and cannot be forked with `ForkAsync`'s isolation;
- will not be reproduced after a restart, because nothing about it was written to the store.

The sanctioned uses are the ones that configure the SDK client itself — models, telemetry,
logging, transports, host process settings — and the ones that read SDK state the facade
does not project. The unsanctioned use is routing around the facade to get a session
quickly: it works, and everything the durable layer promises stops applying at that call.

`NativeClient` is a `CopilotClient` the durable layer owns or borrows per §4. Using the
escape hatch does not transfer ownership; a borrowed client is still the caller's to
dispose, and a created one is still disposed by the facade.

---

## 6. One runtime, shared with the harness

The facade and `DurableHarness` drive **the same internal runtime**: session binding,
configuration building, task coordination, and lifecycle. Sharing configuration helpers
alone would be insufficient — the runtime, not the helpers, is what holds the bindings — so
the facade-created conversation and the harness-created conversation are the same object
graph over the same store.

What that means for the implementation issues:

- `DurableHarness` currently creates its own `CopilotClient` in `OpenAsync` and disposes it
  in `DisposeAsync`. #7.2 has to extract a runtime that can be handed a client instead, and
  that can be told whether it owns it. Until then, `DurableClientOptions.Client` cannot
  actually be honored, which is why `Build()` still throws.
- `DurableHarness` has no notion of an owned store. The ownership policy in §4 belongs to
  the shared runtime, not to the facade, or the harness path will diverge.
- Anything the runtime learns about persistence, resume, and fork has to be reachable from
  both entry points from the start; retrofitting one onto the other is what this bullet is
  here to prevent.

---

## 7. Configuration precedence

Two `GetSessionConfig` overloads make the merge visible without creating anything:

```csharp
public SessionConfig GetSessionConfig(
    AgentChoices? choices = null,
    DurableConversation? conversation = null);

public Task<SessionConfig> GetSessionConfigAsync(
    string conversationId,
    CancellationToken cancellationToken = default);
```

The first is for a live handle; the second reads a stored conversation that has no session
attached. Both apply the same rule and neither creates a session or a sandbox.

### The rule

**Persisted agent choices are authoritative.** Where a stored choice is set, it wins over
anything SDK configuration says about the same thing. Where a stored choice is unset, the
SDK configuration's value — or the runtime's default when the configuration is silent too —
applies.

The stored choices are the durable record of what a conversation *is*: model, reasoning
effort, working directory, streaming flag, extension and tool selection, permission grants,
sandbox policy, and instructions. They live in the `durable.agent` document, declared
`DocumentHistory.Rewindable` with `ForkPolicy.AsOf`, so the record is versioned and a fork
inherits the parent's choices as a snapshot.

Reading the configuration back through these overloads is how a host sees the effective
result — the stored choices already resolved against the registry's code and the runtime's
defaults — instead of reconstructing the merge itself.

### Why

A grant that lives only in host configuration is not part of the durable record. It would
change across a restart and travel badly across a fork — which is precisely the failure mode
the store exists to prevent. SDK configuration is therefore a source of *defaults for a
conversation that has not decided yet*, never an override of one that has.

### What is not in the merge

`DurableHarnessOptions.PermissionHandler`, `.EnableFileHooks`, and
`.TolerateHostHookFailures` describe the **process**, not the conversation. They cannot be
overridden by a stored choice, and they are not part of the precedence order. Supplying a
permission handler replaces the fail-closed durable policy for every conversation at once,
which moves the agent's authority out of the store and out of these guarantees — the one
place a host can deliberately opt out of the model, and it should be a deliberate act.

`AgentChoices` may be `null`, meaning "no overrides": the runtime's resolved defaults and
the registry's code decide everything. `conversation` may be `null` too, meaning the
configuration is being inspected outside a conversation; then the sections that read durable
documents are absent rather than empty, because they need somewhere to read from.

---

## 8. What #7.2–#7.5 must implement

The declarations in §2 are final. These are the bodies, in dependency order.

**#7.2 — runtime extraction, `Build()`, `InitializeAsync`**

- Extract the shared internal runtime from `DurableHarness` so it accepts a caller-supplied
  `CopilotClient` and an ownership decision, instead of always creating and always disposing.
- Implement `DurableClientBuilder.Build()`: validate the store source and the ownership
  flags, construct the facade, connect nothing.
- Implement `InitializeAsync`: create the store (and acquire the writer lock) and the SDK
  client if they are owned; start the client; make concurrent callers share one attempt; do
  not retry a failure implicitly.
- Implement `IsInitialized`, and the `Store` / `NativeClient` availability rules.

**#7.3 — conversation operations on the shared runtime**

- `CreateConversationAsync`, `RootAsync`, and both `FindConversation` overloads: write the
  choices before the session exists, then bind the handle.
- `GetSessionConfig` and `GetSessionConfigAsync`: implement §7's merge and expose it.
- Conversation binding must be the runtime's, so a handle created through the facade and one
  created through the harness are visible to the same lookups.

**#7.4 — resume and fork isolation**

- `ResumeConversationAsync`: read stored choices first, build the session from them, attach.
- `ForkAsync`: copy the transcript to the fork entry, apply each document's `ForkPolicy`,
  inherit choices as a snapshot, and guarantee independent copies — no shared mutable state
  that would let a commit in one conversation appear in the other.

**#7.5 — lifecycle, DI, and tasks**

- `ResumeTasksAsync` and `WaitForTaskAsync` over the runtime's task coordination.
- `DisposeAsync`: idempotent, dispose exactly what is owned per §4, in an order that lets a
  created store's lock go last.
- `AddDurableCopilotClient` (both overloads): delegate to the builder, register a singleton,
  wire container disposal, stay idempotent, never take ownership of a supplied instance.
- Extension and tool resolution stays in the shared runtime, which is what satisfies §6:
  a facade-created session resolves the same code a harness-created one does.

---

## 9. Acceptance criteria — where each is satisfied

| Criterion | Where |
|---|---|
| `Build()` returns `DurableCopilotClient`; surface compiles under `dotnet build` | §2.2, §1 |
| `DurableConversation` reused; no competing durable-session type | §2.4 |
| Ownership explicit in signatures; borrowed vs owned; dispose exactly once | §2.1, §4 |
| `Build()` validate-and-construct only; async coordinated initialization | §3, §3.1 |
| Persisted-choice vs SDK-config precedence documented with the overloads | §7, §2.3 |
| `AddDurableCopilotClient()` follows options/ownership conventions; same runtime | §2.5, §6 |
| Raw native SDK escape hatch outside durability guarantees | §5 |
| This document exists and is ready for Lead review | this file |
