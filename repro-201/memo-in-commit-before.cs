// Port-independent control for #201 in C#: the same re-entry, with no knowledge of the guard, so it
// compiles against the layer before and after the fix. Its outcomes are "did not return within 4s"
// (exit 7), "threw" (exit 0) or "returned" (exit 0). Against the unmodified layer it exits 7,
// because the gate is non-reentrant.
//
// Run from the repo root:
//   dotnet run repro-201/memo-in-commit-before.cs

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Text.Json;
using Durable;

var store = DurableStore.CreateEphemeral();
var registry = new ExtensionRegistry();

var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "repro.memo-in-commit-before",
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
                catch (Exception error)
                {
                    Console.WriteLine($"RESULT: MemoAsync inside CommitAsync(Transaction) threw {error.GetType().Name}: {error.Message}");
                    Environment.Exit(0);
                }

                Console.WriteLine("RESULT: MemoAsync inside CommitAsync(Transaction) returned normally");
                Environment.Exit(0);
                return TaskCommit.Failed("unreachable");
            });

            var next = await commit;
            Console.WriteLine($"  commit returned status={next.Status}");
            return TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }));
        },
        ["done"] = (_, _, runtime) => runtime.CommitAsync(
            () => TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }))),
    });

registry.Install(new Extension("repro", tasks: [definition]));
await using var harness = await DurableHarness.OpenAsync(store, registry);

var record = await harness.CreateTaskAsync(definition, DurableJson.ToElement(new Dictionary<string, string>()));
var outcome = await Task.WhenAny(
    harness.WaitForTaskAsync(record.Id),
    Task.Delay(TimeSpan.FromSeconds(10)));
Console.WriteLine(outcome is Task<TaskOutcome> ? "RESULT: the task settled" : "RESULT: the task never settled");