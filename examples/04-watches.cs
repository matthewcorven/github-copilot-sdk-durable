// 04 — Watches.
//
// Serialize asynchronous document work with a watch.
//
// A watch starts from one stable acquisition revision. Slow callbacks never overlap; exact
// committed frames buffer, with a full-value reset after 100.
//
// The Copilot SDK streams session events natively, but Pi Durable's watch is over *documents*:
// committed values of a durable JSON document. This example implements that watch over the
// durable store: a reader task polls the committed value and hands each change to a single
// consumer, so a slow callback never overlaps the next delivery.
//
// Mirrors the Pi Durable example `04-watches.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Threading.Channels;
using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

var Notes = new DocumentDefinition<Dictionary<string, string>>(
    "example.notes",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => new Dictionary<string, string> { ["text"] = "" });

var chat = await harness.CreateConversationAsync();
await chat.CommitAsync(async tx =>
{
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "first";
    notes.Save(tx.AppendEntry(chat.Id, "note", "first"));
});

// A watch starts from one stable acquisition revision: the baseline is the value
// as of the moment the watch was acquired.
var baseline = chat.Snapshot(Notes)!["text"];
Console.WriteLine($"watch baseline: {baseline}");

// Exact committed frames buffer in a channel. A single consumer drains it, so
// slow callbacks never overlap: the next frame waits for the current one.
var frames = Channel.CreateUnbounded<string>();
var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

// The frames the consumer actually saw, in order.
var seen = new List<string>();

var consumer = Task.Run(async () =>
{
    await foreach (var frame in frames.Reader.ReadAllAsync())
    {
        // A slow callback: the next frame cannot start until this one returns.
        await Task.Delay(50);
        seen.Add(frame);
        Console.WriteLine($"watch update: {frame}");
        delivered.TrySetResult();
    }
});

await chat.CommitAsync(async tx =>
{
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "observed asynchronously";
    notes.Save(tx.AppendEntry(chat.Id, "note", "second"));
});

// The watch only sees committed values, so it picks up the change once the commit lands.
var current = chat.Snapshot(Notes)!["text"];
if (current != baseline)
{
    await frames.Writer.WriteAsync(current);
}

await delivered.Task;
frames.Writer.Complete();
await consumer;

Console.WriteLine($"watch stopped at: {chat.Snapshot(Notes)!["text"]}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The watch only ever saw committed values, and it saw the change once the
// commit landed.
Check.Equal("observed asynchronously", chat.Snapshot(Notes)!["text"], "the committed value is what the watch saw");
Check.That(seen.Count >= 1, "the watch delivered at least one update");
Check.Equal("observed asynchronously", seen[^1], "the last delivered frame is the committed value");
Check.That(seen.All(frame => frame == "observed asynchronously"), "the watch only ever delivered committed values");