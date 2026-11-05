// The pre-fix shape: `TaskRuntime.MemoAsync` called from inside a commit callback.
//
// `MemoAsync` takes the store's commit gate (`lib/Durable.cs`, `DurableStore.MemoAsync`), and so
// does the commit that is running this handler. `SemaphoreSlim` is not reentrant, so the inner
// `WaitAsync` waits for a release only the outer call can perform — and the outer call is awaiting
// the inner one. The process does not fail, it stops.
//
// A watchdog turns "hangs for ever" into a distinguishable exit code, so this file can be part of
// an automated check instead of a note. It is run by `evidence.sh` stage 8 and is expected to exit
// 7. It uses only the API that exists *before* the layer change on this branch, which is what makes
// it the before half of the before/after control for that change.
//
// The fix is `Transaction.Memo`, used by `TriageStore.RecordCommentAsync` in this variant.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001;NU1903;CS1591;IL2026;IL3050;CA2266
#:include ../../../../lib/Durable.cs

using Durable;

var store = DurableStore.CreateEphemeral();
var registry = new ExtensionRegistry();

var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "evidence.memo-inside-commit",
    () => new Dictionary<string, string> { ["phase"] = "run" },
    new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["run"] = (_, _, runtime) => runtime.CommitAsync(async tx =>
        {
            Console.WriteLine("inside the transaction; calling runtime.MemoAsync");
            var memo = runtime.MemoAsync("comment/101/abc", DurableJson.ToElement(new { posted = "yes" }));
            var finished = await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(4)));
            if (!ReferenceEquals(finished, memo))
            {
                Console.WriteLine("RESULT: MemoAsync did not return within 4s — the commit gate is not reentrant");
                Environment.Exit(7);
            }

            await memo;
            return TaskCommit.Completed(DurableJson.ToElement(new { }));
        }),
    });

registry.Install(new Extension("evidence", tasks: [definition]));
await using var harness = await DurableHarness.OpenAsync(store, registry);
var record = await harness.CreateTaskAsync(definition, DurableJson.ToElement(new Dictionary<string, string>()));
var outcome = await harness.WaitForTaskAsync(record.Id);
Console.WriteLine($"RESULT: completed — {outcome.Status}");
Environment.Exit(7);
