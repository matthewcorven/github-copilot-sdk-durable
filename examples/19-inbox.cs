// 19 — The inbox.
//
// What happens to submissions while a conversation is busy.
//
// The first answer waits until we let it go, so the conversation stays busy while we submit
// more. A prompt sent while the conversation is busy is queued, not lost: it starts the
// next turn once the current one finishes.
//
// Mirrors the Pi Durable example `19-inbox.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync();

// The first answer waits until we let it go, so the conversation stays busy while
// we submit more.
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var first = root.SubmitAsync("First question: what is 2+2? Answer with just the number.");

// While busy, input queues as a follow-up rather than replacing the running turn.
var followUp = root.SubmitAsync("A follow-up: what is 3+3? Answer with just the number.");

Console.WriteLine($"first submitted: {!first.IsCompleted}");
Console.WriteLine($"follow-up submitted: {!followUp.IsCompleted}");

// The first answer ends the run; the queued follow-up starts the next turn.
release.TrySetResult();

var firstAnswer = await first;
Console.WriteLine($"first: {firstAnswer?.Data.Content}");

var followUpAnswer = await followUp;
Console.WriteLine($"follow-up: {followUpAnswer?.Data.Content}");

// Both prompts are in the transcript: the busy conversation queued the second
// rather than dropping it.
var context = await root.ContextAsync();
var userMessages = context.OfType<UserMessageEvent>().Select(evt => evt.Data.Content).ToList();
Console.WriteLine($"user messages: {userMessages.Count}");
foreach (var message in userMessages)
{
    Console.WriteLine($"  {message}");
}