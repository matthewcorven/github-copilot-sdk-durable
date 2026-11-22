// The shape that used to hang: `TaskRuntime.MemoAsync` called from inside a commit callback.
//
// `MemoAsync` takes the store's commit gate (`lib/Durable.cs`, `DurableStore.MemoAsync`), and so
// does the commit that is running this handler. `SemaphoreSlim` is not reentrant, so the inner
// `WaitAsync` waits for a release only the outer call can perform — and the outer call is awaiting
// the inner one. Before the guard the process did not fail, it stopped.
//
// A watchdog turns "hangs for ever" into a distinguishable exit code, so this file can be part of
// an automated check instead of a note. `evidence.sh` stage 8 runs it: exit 7 is the recorded
// pre-guard result this stage was captured with (the evidence log is the record of that run), and
// the guard added for #201 is what turns it into exit 0 today. The call is still the pre-fix
// *shape* — only the released API, no `Transaction.Memo` — so it remains the before half of the
// before/after control.
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
        ["run"] = (_, _, runtime) => runtime.CommitAsync(async (Transaction tx) =>
        {
            Console.WriteLine("inside the transaction; calling runtime.MemoAsync");
            var memo = runtime.MemoAsync("comment/101/abc", DurableJson.ToElement(new { posted = "yes" }));
            var finished = await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(4)));
            if (!ReferenceEquals(finished, memo))
            {
                Console.WriteLine("RESULT: MemoAsync did not return within 4s — the commit gate is not reentrant");
                Environment.Exit(7);
            }

            try
            {
                await memo;
                Console.WriteLine("RESULT: MemoAsync returned from inside the commit");
            }
            catch (Exception error)
            {
                Console.WriteLine($"RESULT: MemoAsync threw {error.GetType().Name} instead of hanging");
            }

            // The documented route, in the same transaction the refused call would have used.
            tx.Memo(runtime.Task.Id, "comment/101/abc", DurableJson.ToElement(new { posted = "yes" }));
            return TaskCommit.Completed(DurableJson.ToElement(new { }));
        }),
    });

registry.Install(new Extension("evidence", tasks: [definition]));
await using var harness = await DurableHarness.OpenAsync(store, registry);
var record = await harness.CreateTaskAsync(definition, DurableJson.ToElement(new Dictionary<string, string>()));
var outcome = await harness.WaitForTaskAsync(record.Id);
Console.WriteLine($"RESULT: completed — {outcome.Status}");
Environment.Exit(7);
