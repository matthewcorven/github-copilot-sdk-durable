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

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// A created conversation applies its own choices; a fork starts with the agent
// the parent had at the fork entry.
Check.Equal("mai-code-1.1-flash", helper.Agent().Model, "the created conversation uses its own model");
Check.Equal("minimal", helper.Agent().ReasoningEffort, "the created conversation uses its own reasoning effort");
Check.Equal(root.Agent().Model, retry.Agent().Model, "the fork inherits the parent's model");
Check.Equal(root.Agent().ReasoningEffort, retry.Agent().ReasoningEffort, "the fork inherits the parent's reasoning effort");

// The fork is a real conversation in the store, and it is a different one.
Check.NotNull(store.Conversation(retry.Id), "the fork is in the store");
Check.That(retry.Id != root.Id, "the fork is a different conversation");
Check.Equal(root.Id, retry.Record.ParentConversationId, "the fork records its parent");

// Each conversation has its own Copilot session.
Check.That(root.SessionId != helper.SessionId, "each conversation has its own session");
Check.That(root.SessionId != retry.SessionId, "the fork has its own session");

// The fork inherits the parent's transcript up to the entry it was taken at.
Check.That(retry.Entries(10).Contains(hello), "the fork inherited the fork entry");
Check.Equal(root.Entries(10).Count, retry.Entries(10).Count, "the fork inherited the parent's entries");