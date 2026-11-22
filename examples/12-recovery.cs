// 12 — Recovery.
//
// Close, reopen, and continue where a task stopped.
//
// Everything a task needs to continue is in storage, so a new harness over the same storage
// picks up where the last one stopped. This example keeps its storage in a file so it
// survives closing.
//
// Mirrors the Pi Durable example `12-recovery.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var directory = Path.Combine(Path.GetTempPath(), $"durable-example-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "session.json");

// The ticker counts to 5, saving its intent before each effect. A memo keeps the
// first value written under its name, so if the process dies after printing but
// before the next checkpoint is saved, the rerun sees the memo and does not
// print the same tick twice.
var printed = new List<int>();
Action<int> reachedTick = _ => { };

var Ticker = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
    "example.ticker",
    () => new Dictionary<string, string> { ["phase"] = "tick", ["n"] = "1" },
    new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["tick"] = (input, checkpoint, runtime) =>
        {
            var n = int.Parse(checkpoint["n"]);

            // Save the intent before the effect. A memo keeps the first value
            // written under its name, so if the process dies after printing but
            // before the next checkpoint is saved, the rerun sees the memo and
            // does not print the same tick twice.
            var memo = runtime.MemoAsync($"printed-{n}").GetAwaiter().GetResult();
            if (memo is null)
            {
                runtime.MemoAsync($"printed-{n}", DurableJson.ToElement(true)).GetAwaiter().GetResult();
                printed.Add(n);
                Console.WriteLine($"tick {n}");
            }

            reachedTick(n);

            // Save the outcome: the final result, or the next tick after a short
            // sleep. The sleep is stored with the checkpoint, so the task holds no
            // invocation meanwhile, and a harness opened later continues it once
            // its time has come.
            if (n == input["to"])
            {
                return runtime.CommitAsync(() => TaskCommit.Completed(
                    DurableJson.ToElement($"counted to {n}")));
            }

            return runtime.SleepUntilAsync(
                runtime.Now.AddMilliseconds(50),
                () => TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string>
                {
                    ["phase"] = "tick",
                    ["n"] = (n + 1).ToString(),
                })));
        },
    },
    (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

var registry = new ExtensionRegistry();
registry.Install(new Extension("ticker", tasks: [Ticker]));

// First run: start counting to 5, and stop the harness right after tick 2 is
// printed, before its next checkpoint is saved. That is the same situation as
// a crash between the effect and saving its outcome.
var firstStore = new DurableStore(storePath);
var firstRun = await DurableHarness.OpenAsync(firstStore, registry);
var firstRunCancellation = new CancellationTokenSource();
var firstRunRoot = await firstRun.RootAsync();
var tickerId = await firstRun.CreateTaskAsync(
    Ticker,
    DurableJson.ToElement(new Dictionary<string, int> { ["to"] = 5 }),
    ownerConversationId: firstRunRoot.Id,
    cancellationToken: firstRunCancellation.Token);

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await firstRunRoot.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var tickTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
reachedTick = n =>
{
    if (n == 2)
    {
        tickTwo.TrySetResult();
    }
};

await tickTwo.Task;

// Cancel the task mid-flight and close the harness. The tick 2 effect happened,
// but its next checkpoint was never saved.
await firstRunCancellation.CancelAsync();
await firstRun.DisposeAsync();
firstStore.Dispose();
reachedTick = _ => { };

// Read the saved record through a harness that never resumes, so nothing runs.
var readerStore = new DurableStore(storePath);
var reader = await DurableHarness.OpenAsync(readerStore, registry);
var saved = reader.Store.Task(tickerId.Id)!;
Console.WriteLine($"closed; saved checkpoint: {saved.Checkpoint.GetProperty("phase").GetString()} n={saved.Checkpoint.GetProperty("n").GetString()}");
Console.WriteLine($"saved memos: {saved.Memos.Count}");
await reader.DisposeAsync();
readerStore.Dispose();

// Second run: nothing to restart by hand. Waiting for the unfinished task
// enables scheduling and continues it. Tick 2 runs again because its outcome
// was never saved, but its memo says it was already printed.
var secondStore = new DurableStore(storePath);
var secondRun = await DurableHarness.OpenAsync(secondStore, registry);
var counted = await secondRun.WaitForTaskAsync(tickerId.Id);
Console.WriteLine($"after reopen: {counted.Status} {counted.Result!.Value.GetString()}");
Console.WriteLine($"ticks printed in total: [{string.Join(", ", printed)}]");
await secondRun.DisposeAsync();
secondStore.Dispose();

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The task resumed from its saved checkpoint and finished.
Check.Equal("completed", counted.Status, "the reopened task finished");
Check.Equal("counted to 5", counted.Result!.Value.GetString(), "the reopened task produced its result");

// The durability guarantee: the memo stopped tick 2 from being printed twice,
// even though its effect happened before the crash and its checkpoint was never
// saved.
Check.Sequence(new[] { 1, 2, 3, 4, 5 }, printed, "no tick was printed twice, and none was skipped");

// The saved checkpoint was the one the task was interrupted at, and the memos
// that made the rerun idempotent were saved with it.
Check.Equal("tick", saved.Checkpoint.GetProperty("phase").GetString(), "the saved checkpoint is the interrupted phase");
Check.Equal("2", saved.Checkpoint.GetProperty("n").GetString(), "the saved checkpoint is the interrupted tick");
Check.Equal(2, saved.Memos.Count, "the memos for the two printed ticks were saved");

Directory.Delete(directory, recursive: true);