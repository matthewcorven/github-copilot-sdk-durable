// 23 — Child tasks.
//
// A task that owns child tasks: a checkout charges four payments at once and waits for
// them. The task graph view prints the checkout's tree while its payments run.
//
// Mirrors the Pi Durable example `23-child-tasks.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A fake bank: it declines an expired card at once, and confirms other charges
// after a moment.
var charged = new HashSet<string>();

var Payment = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "example.payment",
    () => new Dictionary<string, string> { ["phase"] = "charge" },
    new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["charge"] = (input, _, runtime) =>
        {
            var card = input["card"];
            if (card.StartsWith("expired", StringComparison.Ordinal))
            {
                return runtime.CommitAsync(() => TaskCommit.Failed($"{card} declined"));
            }

            charged.Add(card);

            // The bank confirms after a moment: wait for it without holding an invocation.
            return runtime.SleepUntilAsync(
                runtime.Now.AddMilliseconds(100),
                () => TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "confirm" })));
        },

        ["confirm"] = (input, _, runtime) =>
            runtime.CommitAsync(() => TaskCommit.Completed(
                DurableJson.ToElement(new Dictionary<string, string> { ["card"] = input["card"] }))),
    },
    // Each payment undoes its own effect when aborted.
    (input, _, runtime) =>
    {
        var refunded = charged.Remove(input["card"]);
        Console.WriteLine($"  payment {input["card"]} aborted{(refunded ? ", refunded" : "")}");
        return runtime.CommitAsync(() => TaskCommit.Aborted());
    });

// The checkout creates its payments as child tasks and waits for all of them.
// With `failFast`, the first payment that fails aborts the others; the checkout
// itself is not aborted and decides its outcome once every payment is done.
var Checkout = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "example.checkout",
    () => new Dictionary<string, string> { ["phase"] = "pay" },
    new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["pay"] = (input, _, runtime) => runtime.CommitAsync(async tx =>
        {
            // The checkout creates its payments as child tasks and waits for all of them.
            // The children are written in the same transaction as the checkpoint that
            // records them.
            var payments = new List<string>();
            foreach (var card in input["cards"].Split(','))
            {
                var child = runtime.CreateChildTask(
                    tx,
                    Payment,
                    DurableJson.ToElement(new Dictionary<string, string> { ["card"] = card }));
                payments.Add(child.Id);
            }

            foreach (var payment in payments)
            {
                runtime.ScheduleTask(payment);
            }

            return TaskCommit.Waiting(
                DurableJson.ToElement(new Dictionary<string, string>
                {
                    ["phase"] = "decide",
                    ["payments"] = string.Join(",", payments),
                }),
                payments);
        }),

        ["decide"] = (input, checkpoint, runtime) => runtime.CommitAsync(async _ =>
        {
            var outcomes = await runtime.OutcomesAsync(checkpoint["payments"].Split(','));
            Console.WriteLine($"  payments: {string.Join(", ", outcomes.Select(o => o.Status))}");

            var paid = outcomes.All(o => o.IsCompleted);
            return paid
                ? TaskCommit.Completed(DurableJson.ToElement("order placed"))
                : TaskCommit.Failed("payment failed");
        }),
    },
    (_, _, runtime) =>
    {
        Console.WriteLine("  checkout aborted");
        return runtime.CommitAsync(() => TaskCommit.Aborted());
    });

registry.Install(new Extension("checkout", tasks: [Payment, Checkout]));

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync();

// One card is declined:
Console.WriteLine("One card is declined:");
var declined = await harness.CreateTaskAsync(
    Checkout,
    DurableJson.ToElement(new Dictionary<string, string> { ["cards"] = "visa-1,expired-2,visa-3,visa-4" }),
    ownerConversationId: root.Id);
Console.WriteLine($"  checkout: {(await harness.WaitForTaskAsync(declined.Id)).Status}");

// The customer cancels:
Console.WriteLine("The customer cancels:");
var cancelled = await harness.CreateTaskAsync(
    Checkout,
    DurableJson.ToElement(new Dictionary<string, string> { ["cards"] = "visa-5,visa-6,visa-7,visa-8" }),
    ownerConversationId: root.Id);
await Task.Delay(20);

// The task graph prints the live tasks as a tree along their owner edges.
PrintGraph(harness.TaskGraph());
await harness.AbortTaskAsync(cancelled.Id);
Console.WriteLine($"  checkout: {(await harness.WaitForTaskAsync(cancelled.Id)).Status}");

// The task graph after the abort.
Console.WriteLine("Task graph:");
PrintGraph(harness.TaskGraph());

/// <summary>Print the live tasks as a tree along their owner edges.</summary>
static void PrintGraph(IReadOnlyList<(DurableTaskRecord Task, string? Owner)> graph)
{
    var nodes = graph.Select(pair => pair.Task).ToList();
    var owners = graph.ToDictionary(pair => pair.Task.Id, pair => pair.Owner);

    void Print(DurableTaskRecord node, int depth)
    {
        var status = node.Status == Durable.TaskStatus.Waiting
            ? $"waiting on {string.Join(", ", node.WaitingOn)}"
            : node.Status.ToString();
        Console.WriteLine($"  {new string(' ', depth * 2)}{node.Name} {node.Id}: {status}");
        foreach (var child in nodes.Where(candidate => owners.GetValueOrDefault(candidate.Id) == node.Id))
        {
            Print(child, depth + 1);
        }
    }

    foreach (var node in nodes.Where(candidate => owners.GetValueOrDefault(candidate.Id) is null))
    {
        Print(node, 0);
    }
}