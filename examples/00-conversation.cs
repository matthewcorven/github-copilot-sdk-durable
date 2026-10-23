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