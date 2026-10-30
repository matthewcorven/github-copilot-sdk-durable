// 00 — Conversations.
//
// The lowest layer of the durable stack: a store, a harness, and one conversation.
//
// A conversation is a transcript scope. It is created inside a transaction, so everything
// written in that transaction is saved together, or discarded if the transaction throws.
//
// Mirrors the Pi Durable example `00-conversation.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

// An in-memory store keeps everything in memory; a path-backed store keeps it on disk.
var store = new DurableStore();
var registry = new ExtensionRegistry();

await using var harness = await DurableHarness.OpenAsync(store, registry);

// Every write happens inside a transaction. The callback receives a transaction `tx`;
// everything it writes is saved together when the callback returns, or discarded if it throws.
// "ownerless" means no task created this conversation.
var standalone = await harness.Store.CommitAsync(tx =>
    Task.FromResult(tx.CreateConversation()));

Console.WriteLine($"standalone conversation: {standalone.Id}");
Console.WriteLine($"  parent: {standalone.ParentConversationId ?? "(none)"}");
Console.WriteLine($"  owner: {standalone.OwnerTaskId ?? "(none)"}");

// The conversation is also a live Copilot session, so it can hold a real transcript.
var conversation = await harness.CreateConversationAsync();
Console.WriteLine($"copilot session: {conversation.SessionId}");
Console.WriteLine($"conversation count: {harness.Store.Conversations.Count}");

// The model the runtime reported for this session. It is the runtime's answer, not the
// model the host asked for, so a substitution would be visible here.
var effectiveModel = await conversation.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// A conversation created in a transaction is durable: it is in the store, it
// has an id, and it is ownerless because no task created it.
Check.That(standalone.Id.Length > 0, "a conversation has an id");
Check.Equal<string?>(null, standalone.ParentConversationId, "a standalone conversation has no parent");
Check.Equal<string?>(null, standalone.OwnerTaskId, "a standalone conversation has no owner task");
Check.That(harness.Store.Conversations.Any(c => c.Id == standalone.Id), "the conversation is in the store");
Check.That(harness.Store.Conversations.Any(c => c.Id == conversation.Id), "the live conversation is in the store");
Check.Equal(2, harness.Store.Conversations.Count, "both conversations were created");

// A transaction that throws writes nothing. This is the atomicity guarantee.
var before = harness.Store.Conversations.Count;
try
{
    await harness.Store.CommitAsync(async tx =>
    {
        tx.CreateConversation();
        tx.CreateConversation();
        throw new InvalidOperationException("boom");
    });
}
catch (InvalidOperationException ex) when (ex.Message == "boom")
{
    // Expected.
}

Check.Equal(before, harness.Store.Conversations.Count, "a failed transaction writes nothing");