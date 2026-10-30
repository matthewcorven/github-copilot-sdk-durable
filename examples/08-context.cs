// 08 — Context.
//
// Transcript history and model context.
//
// Entries are immutable. The stored transcript is everything that was written; the model
// context is what the model actually sees. The Copilot SDK derives the model context from
// the session's event log: aborted and errored assistant messages are not sent, tool results
// follow their call, and a call without a result gets a synthesized error result.
//
// Mirrors the Pi Durable example `08-context.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

var transcript = await harness.CreateConversationAsync();

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await transcript.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// The stored transcript: every entry this conversation ever wrote.
await transcript.CommitAsync(async tx =>
{
    tx.AppendEntry(transcript.Id, "message", "read a and b");
    tx.AppendEntry(transcript.Id, "message", "I crashed");
    tx.AppendEntry(transcript.Id, "message", "reading");
    tx.AppendEntry(transcript.Id, "message", "a and b look fine");
    tx.AppendEntry(transcript.Id, "note", "display only");
});

// The model context: the session's event log, which is what the model sees.
var reply = await transcript.SubmitAsync("Say exactly: CONTEXT");
Console.WriteLine($"answer: {reply?.Data.Content}");

// The stored transcript is newest-first and includes entries the model never sees.
Console.WriteLine($"raw stored entries: [{string.Join(", ", transcript.Entries(10))}]");

// The model context is the session's event log.
var context = await transcript.ContextAsync();
Console.WriteLine($"context events: {context.Count}");
foreach (var evt in context)
{
    var detail = evt switch
    {
        UserMessageEvent user => $"user: {user.Data.Content}",
        AssistantMessageEvent assistant => $"assistant: {assistant.Data.Content}",
        ToolExecutionStartEvent tool => $"tool start: {tool.Data.ToolName}",
        ToolExecutionCompleteEvent tool => $"tool complete: {tool.Data.Error?.Message ?? "ok"}",
        SessionIdleEvent => "idle",
        _ => evt.Type,
    };
    Console.WriteLine($"  {evt.Type}: {detail}");
}

// A fork at an earlier point sees only the transcript up to that entry.
var first = transcript.Entries(10).Last();
var fork = await harness.ForkAsync(transcript, first);
Console.WriteLine($"fork entries: [{string.Join(", ", fork.Entries(10))}]");
Console.WriteLine($"parent entries: [{string.Join(", ", transcript.Entries(10))}]");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The stored transcript and the model context are different things: the store
// holds the app's entries, the session holds the events the model saw.
Check.Equal("CONTEXT", reply?.Data.Content, "the model answered as asked");
Check.That(transcript.Entries(10).Count >= 1, "the conversation stored at least one entry");

var events = await transcript.ContextAsync();
Check.That(events.Any(e => e is UserMessageEvent), "the model context holds the user message");
Check.That(events.Any(e => e is AssistantMessageEvent), "the model context holds the assistant message");
Check.That(events.Any(e => e is SystemMessageEvent), "the model context holds the system message");

// A fork at an earlier point sees only the transcript up to that entry.
var forkEntries = fork.Entries(10);
Check.That(forkEntries.Count <= transcript.Entries(10).Count, "the fork holds no more entries than the parent");
Check.That(forkEntries.Contains(first), "the fork inherited the entry it was taken at");
Check.Equal(transcript.Id, fork.Record.ParentConversationId, "the fork records its parent");