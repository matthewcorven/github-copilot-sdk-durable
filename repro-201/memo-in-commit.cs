// Standalone repro for #201: `TaskRuntime.MemoAsync` called from inside
// `CommitAsync(Func<Transaction, Task<TaskCommit>>)`.
//
// Before the fix this never returns: `CommitTaskAsync` holds the store's non-reentrant
// `_gate` for the whole callback, and `MemoAsync` waits for the same gate. After the fix the
// inner call throws `DurableCommitReentryException` immediately. A watchdog turns the hang into
// a distinguishable exit so this can be run as evidence rather than as a hang.
//
// Run from the repo root:
//   dotnet run repro-201/memo-in-commit.cs

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Text.Json;
using Durable;

var store = DurableStore.CreateEphemeral();
var registry = new ExtensionRegistry();

var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "repro.memo-in-commit",
    () => new Dictionary<string, string> { ["phase"] = "run" },
    new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["run"] = async (_, _, runtime) =>
        {
            Console.WriteLine("handler entered; committing a checkpoint and a memo together");

            var commit = runtime.CommitAsync(async tx =>
            {
                Console.WriteLine("  inside the transaction; calling MemoAsync");

                var memoTask = runtime.MemoAsync("k", DurableJson.ToElement("written"));
                var winner = await Task.WhenAny(memoTask, Task.Delay(TimeSpan.FromSeconds(4)));
                if (!ReferenceEquals(winner, memoTask))
                {
                    Console.WriteLine("RESULT: MemoAsync inside CommitAsync(Transaction) did NOT return within 4s -> HANG reproduced");
                    Environment.Exit(7);
                }

                try
                {
                    await memoTask;
                }
                catch (DurableCommitReentryException error)
                {
                    Console.WriteLine($"RESULT: MemoAsync inside CommitAsync(Transaction) threw {error.GetType().Name}");
                    Console.WriteLine($"  message: {error.Message}");

                    // The transaction is still usable: the guard rejected the call before it
                    // touched any state, so the caller can take the documented route instead.
                    tx.Memo(runtime.Task.Id, "k", DurableJson.ToElement("written"));
                    return TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "done" }));
                }

                Console.WriteLine("RESULT: MemoAsync inside CommitAsync(Transaction) returned normally -> no guard");
                Environment.Exit(8);
                return TaskCommit.Failed("unreachable");
            });

            var next = await commit;
            Console.WriteLine($"  commit returned status={next.Status}");

            // Proves the memo the documented route wrote is the one that committed.
            return TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }));
        },
        ["done"] = (_, _, runtime) => runtime.CommitAsync(
            () => TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }))),
    });

registry.Install(new Extension("repro", tasks: [definition]));
await using var harness = await DurableHarness.OpenAsync(store, registry);

var record = await harness.CreateTaskAsync(definition, DurableJson.ToElement(new Dictionary<string, string>()));
var outcome = await harness.WaitForTaskAsync(record.Id);

Console.WriteLine($"outcome={outcome.Status} memo={store.Task(record.Id)!.Memos["k"].GetString()}");
Console.WriteLine(outcome.Status == "completed" && store.Task(record.Id)!.Memos["k"].GetString() == "written"
    ? "RESULT: the documented route committed the memo with the checkpoint"
    : "RESULT: unexpected outcome");