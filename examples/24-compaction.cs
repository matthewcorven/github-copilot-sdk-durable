// 24 — Compaction.
//
// A long trip-planning chat whose older messages are summarized so the model context
// stays small.
//
// The Copilot SDK compacts natively: a session with infinite sessions enabled manages the
// context window in the background and emits compaction events.
//
// Mirrors the Pi Durable example `24-compaction.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

// Infinite sessions manage the context window in the background: compaction starts
// at 80% context usage and blocks at 95% until it completes.
var root = await harness.RootAsync(new AgentChoices
{
    InfiniteSessions = new InfiniteSessionConfig
    {
        Enabled = true,
        BackgroundCompactionThreshold = 0.80,
        BufferExhaustionThreshold = 0.95,
    },
});

// The runtime emits compaction events as it works.
var compactions = new List<string>();
using var subscription = root.Session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case SessionCompactionStartEvent:
            compactions.Add("start");
            Console.WriteLine("compaction started");
            break;
        case SessionCompactionCompleteEvent complete:
            compactions.Add("complete");
            Console.WriteLine($"compaction complete: {complete.Data.ConversationTokens} conversation tokens");
            break;
    }
});

// A long chat: the context grows, and the runtime compacts it in the background.
foreach (var question in new[]
{
    "Where should we stay in Lisbon?",
    "What should we eat?",
    "Which day trips are worth it?",
    "Any museums?",
    "Nightlife?",
})
{
    var answer = await root.SubmitAsync(question);
    Console.WriteLine($"asked: {question}");
    Console.WriteLine($"  answered: {(answer?.Data.Content.Length ?? 0)} chars");
}

// The stored transcript keeps every entry; the model context is what the model sees.
var stored = root.Entries(100);
var context = await root.ContextAsync();
Console.WriteLine($"stored entries: {stored.Count}");
Console.WriteLine($"context events: {context.Count}");
Console.WriteLine($"compactions observed: {compactions.Count}");

// The usage checkpoint reports the context the model is working with.
foreach (var evt in context.OfType<SessionUsageCheckpointEvent>())
{
    Console.WriteLine($"usage checkpoint: {evt.Data}");
}