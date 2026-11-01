// 20 — Late join.
//
// A client attaches while a run is already underway. It gets the current state first, and
// then only what changes after that. An async SDK tool holds generation until the client
// has read native history and installed its subscription; elapsed time is not readiness.
//
// Mirrors the Pi Durable example `20-late-join.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

using var store = new DurableStore();
var registry = new ExtensionRegistry();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var waitForJoin = CopilotTool.DefineTool(
    async (CancellationToken cancellationToken) =>
    {
        started.TrySetResult();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        await release.Task.WaitAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return "The client has joined. Now count from one to five, then say DONE.";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "wait_for_join",
        Description = "Wait for the client to join before generating the counting answer. Call this first.",
    });
registry.Install(new Extension("late-join", tools: [waitForJoin]));
await using var harness = await DurableHarness.OpenAsync(store, registry, deadline.Token);
var root = await harness.RootAsync(new AgentChoices
{
    Streaming = true,
    Extensions = ["late-join"],
    Tools = ["wait_for_join"],
    Permissions = ["custom-tool:wait_for_join"],
}, deadline.Token);

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync(cancellationToken: deadline.Token);
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var run = root.SubmitAsync(
    "First call wait_for_join exactly once, and wait for its result before writing any counting text. " +
    "Then count slowly from one to five, one number per sentence, then say DONE.",
    deadline.Token);

try
{
    // Tool invocation proves the run is underway; its pending result holds final generation.
    await RequirePendingRunAsync(run, started.Task, deadline.Token, "waiting for wait_for_join");
    Console.WriteLine("synchronization tool started; joining while the run is pending");

    // Admission and native history are separate. Read until the actual SDK user event is visible.
    IReadOnlyList<SessionEvent> snapshot;
    using (var snapshotDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
    {
        snapshotDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        do
        {
            var read = root.ContextAsync(snapshotDeadline.Token);
            await RequirePendingRunAsync(run, read, snapshotDeadline.Token, "reading the SDK snapshot");
            snapshot = await read;
            if (snapshot.Any(evt => evt is UserMessageEvent))
            {
                break;
            }

            await RequirePendingRunAsync(
                run, Task.Delay(50, snapshotDeadline.Token), snapshotDeadline.Token,
                "waiting for an actual UserMessageEvent in the SDK snapshot");
        } while (true);
    }
    Console.WriteLine($"snapshot events: {snapshot.Count}");
    foreach (var evt in snapshot)
    {
        Console.WriteLine($"  {evt.Type}");
    }

    // The event client: the snapshot carries the same state; later events apply on top.
    var deltas = new ConcurrentQueue<string>();
    using var subscription = root.Session.On<SessionEvent>(evt =>
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data.DeltaContent):
                deltas.Enqueue(delta.Data.DeltaContent);
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

    // No answer-delta gap: the tool remains blocked until the live subscription exists.
    if (run.IsCompleted)
    {
        await run;
        throw new InvalidOperationException("The run ended before the live subscription was installed.");
    }
    release.TrySetResult();
    Console.WriteLine("live subscription installed; synchronization tool released");

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
}
catch (OperationCanceledException) when (deadline.IsCancellationRequested)
{
    throw new TimeoutException("Late join timed out while awaiting the completed counting answer.");
}
finally
{
    // Cancellation stops exceptional paths; release also unblocks a handler already awaiting us.
    deadline.Cancel();
    release.TrySetResult();
}

static async Task RequirePendingRunAsync(
    Task<AssistantMessageEvent?> run, Task milestone, CancellationToken cancellationToken, string phase)
{
    Task winner;
    try
    {
        winner = await Task.WhenAny(run, milestone).WaitAsync(cancellationToken);
        await winner;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        if (run.IsFaulted)
        {
            await run;
        }

        throw new TimeoutException($"Late join timed out while {phase}.");
    }

    if (winner == run || run.IsCompleted)
    {
        await run;
        throw new InvalidOperationException($"The run ended before joining while {phase}.");
    }
}