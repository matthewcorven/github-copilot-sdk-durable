// 03 — Owned conversations.
//
// A background task that owns a child conversation.
//
// A typical agent setup: a background task supervises a helper conversation, and the main
// conversation keeps a registry that maps agent names to their conversations. All three are
// created in one commit, so after a crash either all of them exist or none do.
//
// Mirrors the Pi Durable example `03-owned-conversations.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

// A task definition needs a name, a version, the task's starting state, a
// handler for every phase, and an abort handler (11-tasks.ts runs a task).
// This example only creates the task record; a plain harness never runs it.
var Supervisor = DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
    "example.supervisor",
    () => new Dictionary<string, string> { ["phase"] = "ready" },
    (_, _, _) => Task.FromResult(TaskCommit.Completed(DurableJson.ToElement("done"))),
    (_, _, _) => Task.FromResult(TaskCommit.Aborted()));

// "latest" keeps only the current value. "initial" means forks of this
// conversation start without a registry, so a child doesn't inherit its
// parent's list of agents.
var AgentRegistry = new DocumentDefinition<Dictionary<string, Dictionary<string, string>>>(
    "example.agent-registry",
    DocumentHistory.Latest,
    ForkPolicy.Initial,
    () => new Dictionary<string, Dictionary<string, string>>());

var main = await harness.CreateConversationAsync();

var setup = await main.CommitAsync(async tx =>
{
    // `background: true` means the task is side work: waiting for the main
    // conversation to finish does not wait for it.
    var supervisor = tx.CreateTask(
        Supervisor,
        DurableJson.ToElement(new Dictionary<string, string>()),
        ownerConversationId: main.Id,
        background: true);

    // The child records that it belongs to the supervisor task. The task was
    // created a few lines above in this same commit, which is allowed.
    var child = tx.CreateConversation(ownerTaskId: supervisor.Id);

    // requestId is a fixed name for the child's first message. Later code sends
    // that message using this requestId, so a retry after a crash cannot
    // deliver it twice.
    var registry = tx.Doc(AgentRegistry, main.Id);
    registry.Value["researcher"] = new Dictionary<string, string>
    {
        ["conversationId"] = child.Id,
        ["requestId"] = $"researcher:first-message:{supervisor.Id}",
    };
    registry.Save(tx.AppendEntry(main.Id, "agent", "researcher registered"));

    return (SupervisorId: supervisor.Id, Child: child);
});

Console.WriteLine($"supervisor task: {setup.SupervisorId}");
Console.WriteLine($"child conversation: {setup.Child.Id}");
Console.WriteLine($"child owner task: {setup.Child.OwnerTaskId}");
Console.WriteLine($"child owner conversation: {setup.Child.OwnerConversationId ?? "(none)"}");

var registryValue = main.Snapshot(AgentRegistry)!;
Console.WriteLine("registry:");
foreach (var (name, entry) in registryValue)
{
    Console.WriteLine($"  {name}: conversationId={entry["conversationId"]} requestId={entry["requestId"]}");
}

// The supervisor task is background work: it is recorded, but nothing waits for it here.
var task = store.Task(setup.SupervisorId)!;
Console.WriteLine($"supervisor status: {task.Status} (background: {task.Background})");