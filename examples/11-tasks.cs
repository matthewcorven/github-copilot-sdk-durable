// 11 — Tasks.
//
// Run a durable task.
//
// A task is a small state machine. Its state, the checkpoint, is saved after every step, so
// after a crash the next open continues from the last saved step. The usual pattern: save
// what you are about to do, do it, then save the result. A crash between doing and saving
// reruns that step, so the step must be safe to repeat; here the local payment service
// ignores a repeated key.
//
// Mirrors the Pi Durable example `11-tasks.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A local payment service: it ignores a repeated key, so a rerun after a crash
// does not charge twice.
var payments = new Dictionary<string, int>();

// The task's input, checkpoint, and result.
var Payment = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
    "example.payment",
    () => new Dictionary<string, string> { ["phase"] = "prepare" },
    new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        // One handler per phase. Each must save progress through runtime.CommitAsync():
        // its callback returns the next checkpoint or the final outcome, and that
        // state is saved in the same commit as everything else the callback wrote.
        ["prepare"] = (input, _, runtime) => runtime.CommitAsync(() => TaskCommit.Running(
            DurableJson.ToElement(new Dictionary<string, string>
            {
                ["phase"] = "charge",
                ["key"] = $"payment-{runtime.Task.Id}",
            }))),

        ["charge"] = (input, checkpoint, runtime) =>
        {
            var key = checkpoint["key"];
            if (!payments.ContainsKey(key))
            {
                payments[key] = input["amount"] * 100;
            }

            var receipt = payments[key];
            return runtime.CommitAsync(() => TaskCommit.Completed(
                DurableJson.ToElement(new Dictionary<string, int> { ["receipt"] = receipt })));
        },
    },
    // Runs instead of the phases after harness.AbortTaskAsync(); it decides the outcome.
    (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

// Extensions bring task code; the harness finds it by name in the registry.
// Nothing runs until resume() or a call that waits for progress, such as
// WaitForTaskAsync().
registry.Install(new Extension("payments", tasks: [Payment]));

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync();

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// Every task names its owner. This one belongs to the conversation; a task can
// also own child tasks and wait for them (23-child-tasks.cs).
var paymentId = await harness.CreateTaskAsync(
    Payment,
    DurableJson.ToElement(new Dictionary<string, int> { ["amount"] = 5 }),
    ownerConversationId: root.Id);

// The finished task record is the durable receipt; WaitForTaskAsync knows its result type.
var paid = await harness.WaitForTaskAsync(paymentId.Id);
Console.WriteLine($"payment outcome: {paid.Status}");
Console.WriteLine($"receipt: {paid.Result!.Value.GetProperty("receipt").GetInt32()}");

// The task record is durable: its status, checkpoint, and outcome are all saved.
var record = store.Task(paymentId.Id)!;
Console.WriteLine($"task status: {record.Status}");
Console.WriteLine($"task checkpoint: {record.Checkpoint.GetProperty("phase").GetString()}");
Console.WriteLine($"payments charged: {payments.Count}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The task ran to completion, and the receipt is the durable result.
Check.Equal("completed", paid.Status, "the task completed");
Check.Equal(500, paid.Result!.Value.GetProperty("receipt").GetInt32(), "the receipt holds the amount that was charged, in cents");
Check.Equal(1, payments.Count, "the payment effect happened exactly once");
Check.Equal(500, payments.Values.Single(), "the charged amount is the one that was asked for, in cents");

// The task record is durable: its status, checkpoint, and outcome are all saved.
Check.Equal(Durable.TaskStatus.Terminal, record.Status, "the saved record is terminal");
Check.Equal("charge", record.Checkpoint.GetProperty("phase").GetString(), "the saved checkpoint is the last phase");
Check.That(record.Outcome is not null, "the saved record holds the outcome");

// A task that is already terminal does not run again.
var rerun = await harness.WaitForTaskAsync(paymentId.Id);
Check.Equal("completed", rerun.Status, "waiting on a finished task returns its outcome");
Check.Equal(1, payments.Count, "waiting on a finished task does not repeat the effect");