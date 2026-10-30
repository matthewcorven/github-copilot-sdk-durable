// 36 — The durable client builder.
//
// One call assembles a durable host, and the facade it returns is the whole surface an app needs.
//
// `DurableClientBuilder` is a description: it records the pieces a host already has — its store,
// its SDK client, its registry, its host options — and `Build()` validates that description and
// constructs the host. It connects to nothing. No store file is opened, no SDK client process is
// started, no task is resumed, and no prompt section is rendered, so a client can be built during
// container construction without doing I/O there.
//
// The first operation that needs the store or the SDK client initializes them, and concurrent
// first operations share one attempt. That split is the builder/facade boundary: construction is
// cheap and fallible in exactly one way — a missing or contradictory option — while
// initialization is slow and fallible in many.
//
// Two invariants are worth learning here, because they decide whether a host's process exits
// cleanly. The first is ownership: a store or client the caller supplied is borrowed and left
// alone, and one the builder created is owned and disposed. The second is that a store has one
// runtime: a second host over the same store is refused, because the two would compete for the
// same task runner.
//
// Nothing here opens a session, so this example needs no Copilot sign-in and no runtime at all.
// Recovery, forks, and checkpoints are facade operations too, but each one needs a session to
// answer; docs/DESIGN.md section 3.8 documents them.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

// ─── One call, and nothing leaves the process ───────────────────────────────
//
// The builder takes the pieces the host already has and supplies the rest. The registry is held
// by reference rather than copied, so an extension installed after the build is visible to the
// next configuration the client performs.

var store = DurableStore.CreateEphemeral();
var registry = new ExtensionRegistry();
var sharedClient = UnstartedClient();

var client = new DurableClientBuilder()
    .UseStore(store)
    .UseClient(sharedClient)
    .UseRegistry(registry)
    .UseHarnessOptions(new DurableHarnessOptions())
    .Build();

Check.That(!client.IsInitialized, "a built client is inert: it has not connected to anything");
Check.That(
    ReferenceEquals(client.Options.Registry, registry),
    "the options report the registry the builder was given");
Check.That(
    ReferenceEquals(client.Registry, registry),
    "the registry is resolved once, so the options and the client report the same instance");

// The store and the SDK client are the two things that leave the process, and neither exists
// yet. Reading them is an error that names the operation that creates them, rather than a null
// the caller has to notice.
var uninitializedStore = Capture(() => client.Store);
Check.That(uninitializedStore is InvalidOperationException, "the store is not readable before initialization");
Check.Contains(
    uninitializedStore!.Message,
    "InitializeAsync",
    "the error names the operation that creates the store");

var uninitializedClient = Capture(() => client.NativeClient);
Check.That(uninitializedClient is InvalidOperationException, "the SDK client is not readable before initialization");
Check.Contains(
    uninitializedClient!.Message,
    "InitializeAsync",
    "the error names the operation that starts the SDK client");

Console.WriteLine("build: inert client, borrowed store, registry held by reference");

// ─── Build validates; it does not connect ───────────────────────────────────
//
// Every way a description can be wrong is caught here, before anything is opened. These are
// programming errors — the options cannot describe a host — so they throw rather than returning
// a result a caller might ignore.

var noStore = Capture(() => new DurableClientBuilder().Build());
Check.That(noStore is InvalidOperationException, "a client with no store source cannot be built");
Check.Contains(noStore!.Message, "UseStore", "the error names the call that supplies a store");

// `UseStore` replaces the previous choice, so two store sources can only appear when the options
// were adjusted directly. The build refuses rather than picking one.
var twoStoreSources = Capture(() => new DurableClientBuilder()
    .UseStore(store)
    .Configure(options => options.StoreOptions = new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
    .Build());
Check.That(twoStoreSources is InvalidOperationException, "two store sources cannot be built");
Check.Contains(twoStoreSources!.Message, "one store source", "the error names the contradiction");

// An ownership flag only decides the fate of a resource the caller supplied. A resource the
// durable layer creates is always owned, so asking to own a store it will create anyway is a
// misunderstanding worth refusing.
var ownCreatedStore = Capture(() => new DurableClientBuilder()
    .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
    .Configure(options => options.OwnsStore = true)
    .Build());
Check.That(ownCreatedStore is InvalidOperationException, "owning a store the builder would create is refused");
Check.Contains(ownCreatedStore!.Message, "OwnsStore", "the error names the flag");

// The builder is a description, so the last write wins and ordering never matters.
var lastWriteWins = new DurableClientBuilder()
    .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
    .UseStore(store);
Check.That(
    ReferenceEquals(lastWriteWins.Options.Store, store) && lastWriteWins.Options.StoreOptions is null,
    "the last UseStore call is the one that counts");

Console.WriteLine("build: four contradictory descriptions refused, last write wins");

// ─── The first operation initializes; the attempt is shared ─────────────────

var started = client.InitializeAsync();
var joined = client.InitializeAsync();
Check.That(
    ReferenceEquals(started, joined),
    "a second caller joins the attempt in progress instead of starting another");
await started;
Check.That(client.IsInitialized, "the client is initialized once the attempt completes");

// The borrowed resources are the ones the host supplied — not copies, not replacements.
Check.That(ReferenceEquals(client.Store, store), "the client reads and writes through the borrowed store");

Console.WriteLine("init: one attempt, shared by every caller");

// ─── Borrowed or owned: disposal follows who created it ─────────────────────
//
// A borrowed store is the caller's, so the client's disposal leaves it open. A store the builder
// created is the client's, so disposal releases it. That matters beyond tidiness: a persistent
// store holds an exclusive writer lock for its lifetime, and the client disposes the store it owns
// after it has finished with it, which is the only safe order.

var borrowStore = DurableStore.CreateEphemeral();
var borrowedClient = UnstartedClient();
var borrowing = new DurableClientBuilder()
    .UseStore(borrowStore)
    .UseClient(borrowedClient)
    .Build();

await borrowing.InitializeAsync();
Check.That(ReferenceEquals(borrowing.Store, borrowStore), "a supplied store is the one the client uses");
Check.That(
    ReferenceEquals(borrowing.NativeClient, borrowedClient),
    "a supplied SDK client is the one the client drives, not a second one it created");

await borrowing.DisposeAsync();
Check.That(!borrowing.IsInitialized, "disposal unbinds the client, so it reports itself as uninitialized");

var storeSurvived = await CaptureAsync(() => borrowStore.CommitAsync(tx => Task.FromResult(tx.CreateConversation())));
Check.That(storeSurvived is null, "a borrowed store outlives the client that used it");

var owning = new DurableClientBuilder()
    .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
    .UseClient(UnstartedClient())
    .Build();

await owning.InitializeAsync();
var created = owning.Store;
Check.That(!created.IsPersistent, "the store the builder created is the one the options described");
Check.That(!ReferenceEquals(created, borrowStore), "a store the builder created is not the caller's store");

// Disposal is idempotent, so a second call is a no-op rather than a double release.
await owning.DisposeAsync();
await owning.DisposeAsync();

var createdStoreReleased = await CaptureAsync(() =>
    created.CommitAsync(tx => Task.FromResult(tx.CreateConversation())));
Check.That(
    createdStoreReleased is ObjectDisposedException,
    "a store the client owned is released when the client is disposed");

var afterDisposal = await CaptureAsync(() => owning.InitializeAsync());
Check.That(
    afterDisposal is ObjectDisposedException,
    "a disposed client cannot be initialized again");

Console.WriteLine("dispose: borrowed store open, owned store released, disposal idempotent");

// ─── One store, one runtime ─────────────────────────────────────────────────
//
// A store has one task runner, so a second host over the same store would compete with the first
// for the same tasks. Sharing the runtime — one client, many callers — is the supported shape.

var sharedStore = DurableStore.CreateEphemeral();
var primary = new DurableClientBuilder()
    .UseStore(sharedStore)
    .UseClient(UnstartedClient())
    .Build();

await primary.InitializeAsync();

var secondary = new DurableClientBuilder()
    .UseStore(sharedStore)
    .UseClient(UnstartedClient())
    .Build();

var secondRuntime = await CaptureAsync(() => secondary.InitializeAsync());
Check.That(secondRuntime is InvalidOperationException, "a second runtime over one store is refused");
Check.Contains(
    secondRuntime!.Message,
    "already driven by another durable runtime",
    "the error says why the second runtime cannot attach");

await secondary.DisposeAsync();
await primary.DisposeAsync();

Console.WriteLine("attach: one store takes one runtime; a second is refused");

// ─── Stored choices are authoritative; the facade reads them without a session ─
//
// The precedence rule is what makes a conversation reproducible: a stored choice is the record
// of what the conversation is, so it wins over anything the host passes. The argument to
// GetSessionConfig is a default for a conversation that has none stored, not an override.
//
// GetSessionConfigAsync answers for a conversation that is not bound to a live handle. It reads
// the store and builds the configuration the runtime would use, and it creates no session.

var precedenceStore = DurableStore.CreateEphemeral();
var storedId = await SeedAsync(precedenceStore, new AgentChoices
{
    Model = "stored-model",
    WorkingDirectory = "/srv/stored",
});

var reading = new DurableClientBuilder()
    .UseStore(precedenceStore)
    .UseClient(UnstartedClient())
    .Build();

Check.Equal(
    "caller-model",
    reading.GetSessionConfig(new AgentChoices { Model = "caller-model" }).Model,
    "with no conversation to consult, the caller's choices are what is built");
Check.Equal(
    DurableHarness.DefaultModel,
    reading.GetSessionConfig().Model,
    "with no choices at all, the runtime's default model applies");

await reading.InitializeAsync();

var resumedConfig = await reading.GetSessionConfigAsync(storedId);
Check.Equal("stored-model", resumedConfig.Model, "the stored model wins over the caller's default");
Check.Equal("/srv/stored", resumedConfig.WorkingDirectory, "the stored working directory is carried through");

Check.That(
    reading.FindConversation(storedId) is null,
    "the facade binds only conversations it created or resumed, so a store read binds nothing");
Check.Equal(
    1,
    precedenceStore.Conversations.Count,
    "reading the configuration created no conversation and no session");

Console.WriteLine("precedence: stored model and directory authoritative, read without a session");

// ─── Conversations are isolated in the store ────────────────────────────────
//
// Isolation is a property of the store, not of the handle: each conversation owns its documents
// and its entries, so a write in one is invisible in the other.

var isolationStore = DurableStore.CreateEphemeral();
var leftId = await SeedAsync(isolationStore, new AgentChoices { Model = "left-model" });
var rightId = await SeedAsync(isolationStore, new AgentChoices { Model = "right-model" });

var note = new DocumentDefinition<Dictionary<string, string>>("example.note");
await isolationStore.CommitAsync(tx =>
{
    var draft = tx.Doc(note, leftId);
    draft.Value = new Dictionary<string, string> { ["text"] = "left only" };
    draft.Save(tx.AppendEntry(leftId, "note.write"));
    return Task.CompletedTask;
});

Check.NotNull(isolationStore.Document(note.Kind, leftId), "the document was written to its own conversation");
Check.That(
    isolationStore.Document(note.Kind, rightId) is null,
    "the sibling conversation cannot see it");
Check.That(
    !isolationStore.Entries(leftId).SequenceEqual(isolationStore.Entries(rightId)),
    "each conversation keeps its own transcript");

var isolated = new DurableClientBuilder()
    .UseStore(isolationStore)
    .UseClient(UnstartedClient())
    .Build();

Check.Equal("left-model", (await isolated.GetSessionConfigAsync(leftId)).Model, "the left conversation reports its own choices");
Check.Equal("right-model", (await isolated.GetSessionConfigAsync(rightId)).Model, "the right conversation reports its own choices");

await isolated.DisposeAsync();
await reading.DisposeAsync();
await client.DisposeAsync();
await sharedClient.DisposeAsync();
await borrowedClient.DisposeAsync();

Console.WriteLine("isolation: two conversations, two documents, two transcripts");
Console.WriteLine("builder: validates and constructs; the facade owns the lifecycle");

// A builder is a description and a facade is a host. Build() never connects, the first operation
// initializes once, ownership decides what disposal releases, and a store accepts one runtime.

// ─── Helpers ────────────────────────────────────────────────────────────────

// The helpers are functions declared at the end of the file; the statements above are the
// program.

/// <summary>A client that was never started: no process is spawned and no RPC is issued.</summary>
static CopilotClient UnstartedClient() => new(new CopilotClientOptions
{
    Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
});

/// <summary>Commits a conversation and its agent choices without opening a session.</summary>
static async Task<string> SeedAsync(DurableStore store, AgentChoices choices)
{
    var agent = new DocumentDefinition<AgentChoices>("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);
    var record = await store.CommitAsync(tx =>
    {
        var created = tx.CreateConversation();
        var draft = tx.Doc(agent, created.Id);
        draft.Value = choices;
        draft.Save(tx.AppendEntry(created.Id, "agent"));
        return Task.FromResult(created);
    });

    return record.Id;
}

/// <summary>Runs an operation and returns the exception it threw, or null when it succeeded.</summary>
static Exception? Capture(Func<object?> operation)
{
    try
    {
        _ = operation();
        return null;
    }
    catch (Exception error)
    {
        return error;
    }
}

/// <summary>Runs an asynchronous operation and returns the exception it threw, or null.</summary>
static async Task<Exception?> CaptureAsync(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception error)
    {
        return error;
    }
}
