// 20 — Late join.
//
// A client attaches while a run is already underway. It gets the current state first, and
// then only what changes after that.
//
// Mirrors the Pi Durable example `20-late-join.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync(new AgentChoices { Streaming = true });

// Start a run that streams its answer.
var run = root.SubmitAsync("Count slowly from one to five, one number per sentence, then say DONE.");

// Join while the run is already underway.
await Task.Delay(1500);

// The structural client: the current state first.
var snapshot = await root.ContextAsync();
Console.WriteLine($"snapshot events: {snapshot.Count}");
foreach (var evt in snapshot)
{
    Console.WriteLine($"  {evt.Type}");
}

// The event client: the snapshot carries the same state; later events apply on top.
var deltas = new List<string>();
using var subscription = root.Session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data.DeltaContent):
            deltas.Add(delta.Data.DeltaContent);
            Console.WriteLine($"event text delta: {delta.Data.DeltaContent}");
            break;
        case AssistantStreamingDeltaEvent streaming:
            Console.WriteLine($"event: streamed {streaming.Data.TotalResponseSizeBytes} bytes");
            break;
        case AssistantMessageEvent assistant:
            Console.WriteLine($"event: final message ({assistant.Data.Content.Length} chars)");
            break;
        default:
            Console.WriteLine($"event: {evt.Type}");
            break;
    }
});

var answer = await run;
Console.WriteLine($"answer: {answer?.Data.Content}");
Console.WriteLine($"deltas received after joining: {deltas.Count}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// A late joiner gets the current state first, then the live events.
Check.That(snapshot.Count >= 1, "the late joiner received the current state");
Check.That(snapshot.Any(evt => evt is UserMessageEvent), "the snapshot holds the user message");
Check.That(deltas.Count >= 1, "the late joiner received live deltas");
Check.That(answer is not null, "the run finished");
Check.That(answer!.Data.Content.Trim().Length > 0, "the answer is not empty");