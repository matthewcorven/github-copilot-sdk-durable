// 07 — Harness conversations.
//
// Conversation handles, typed entries, created conversations, and forks.
//
// Conversation handles are stateless; compare them by id. They bind commits to their
// conversation. `createConversation()` and `fork()` apply the agent choices in the creating
// commit. A fork starts with the agent the parent had at the fork entry.
//
// Mirrors the Pi Durable example `07-harness-conversations.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

var root = await harness.RootAsync(new AgentChoices
{
    Model = "gpt-6-luna",
    ReasoningEffort = "high",
});

// Conversation handles are stateless; compare them by id. They bind commits to
// their conversation. An entry token types an entry kind's `data`.
var hello = await root.CommitAsync(async tx =>
    tx.AppendEntry(root.Id, "message", "hello"));

Console.WriteLine($"typed entry: {hello}");

// createConversation() and fork() apply the agent choices in the creating commit.
// A fork starts with the agent the parent had at the fork entry.
var helper = await harness.CreateConversationAsync(new AgentChoices
{
    Model = "mai-code-1.1-flash",
    ReasoningEffort = "minimal",
});

var retry = await harness.ForkAsync(root, hello);

Console.WriteLine($"helper model: {helper.Agent().Model} thinking: {helper.Agent().ReasoningEffort}");
Console.WriteLine($"fork model: {retry.Agent().Model} thinking: {retry.Agent().ReasoningEffort}");
Console.WriteLine($"lookup: {(store.Conversation(retry.Id)?.Id == retry.Id)}");

// The fork inherits the parent's transcript up to the entry it was taken at.
Console.WriteLine($"fork entries: [{string.Join(", ", retry.Entries(10))}]");
Console.WriteLine($"parent entries: [{string.Join(", ", root.Entries(10))}]");

// Each conversation has its own Copilot session.
Console.WriteLine($"root session: {root.SessionId}");
Console.WriteLine($"helper session: {helper.SessionId}");
Console.WriteLine($"fork session: {retry.SessionId}");