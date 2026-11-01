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

using System.Globalization;
using System.Text.Json;
using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A local bank: it answers each card at the moment the checkout names for it, declining an
// expired card and confirming the others.
var charged = new HashSet<string>();

// The cards the abort fan-out reached. The fan-out walks the siblings in whatever order
// they finish, so the abort handler records the card instead of printing it: the example
// prints the set once, sorted, after the fan-out settles.
var aborted = new HashSet<string>();

// The checkout names the moment the bank answers each card and spaces the moments out, so
// the order the payments reach a terminal state is fixed by the checkout rather than by the
// scheduler. The spacing is wide enough that a sibling is still waiting when the fail-fast
// abort reaches it.
const int FirstAnswerMilliseconds = 1000;
const int AnswerSpacingMilliseconds = 1000;

var Payment = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
    "example.payment",
    () => new Dictionary<string, string> { ["phase"] = "charge" },
    new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
    {
        ["charge"] = (input, _, runtime) =>
        {
            var card = input["card"];
            var declined = card.StartsWith("expired", StringComparison.Ordinal);

            // Charging the card and saying that the payment is waiting for the bank is one
            // commit, so the charge is never observable without the checkpoint that
            // explains it.
            return runtime.CommitAsync(() =>
            {
                if (!declined)
                {
                    charged.Add(card);
                }

                return TaskCommit.Running(
                    DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "confirm" }));
            });
        },

        // The bank answers at the moment the checkout named for this card: the answer is
        // not a delay measured from whenever the payment happened to start, so the order
        // of the answers is fixed by the checkout rather than by the scheduler.
        ["confirm"] = (input, _, runtime) =>
            runtime.SleepUntilAsync(
                DateTimeOffset.FromUnixTimeMilliseconds(
                    long.Parse(input["answerAt"], CultureInfo.InvariantCulture)),
                () => input["card"].StartsWith("expired", StringComparison.Ordinal)
                    ? TaskCommit.Failed($"{input["card"]} declined")
                    : TaskCommit.Completed(
                        DurableJson.ToElement(new Dictionary<string, string> { ["card"] = input["card"] }))),
    },
    // Each payment undoes its own effect when aborted. The card is recorded inside the
    // commit, so a commit that arrives after the payment already finished records nothing:
    // the set holds the payments this abort actually reached.
    (input, _, runtime) =>
        runtime.CommitAsync(() =>
        {
            aborted.Add(input["card"]);
            charged.Remove(input["card"]);
            return TaskCommit.Aborted();
        }));

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
            //
            // The checkout names the moment the bank answers each card, and spaces the
            // moments out. The declined card answers before its siblings, while they are
            // still waiting, so the fail-fast abort reaches them instead of racing a
            // sibling that has already finished.
            var answerAt = runtime.Now.AddMilliseconds(FirstAnswerMilliseconds);

            var payments = new List<string>();
            foreach (var card in input["cards"].Split(','))
            {
                var child = runtime.CreateChildTask(
                    tx,
                    Payment,
                    DurableJson.ToElement(new Dictionary<string, string>
                    {
                        ["card"] = card,
                        ["answerAt"] = answerAt
                            .ToUnixTimeMilliseconds()
                            .ToString(CultureInfo.InvariantCulture),
                    }));
                payments.Add(child.Id);
                answerAt = answerAt.AddMilliseconds(AnswerSpacingMilliseconds);
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

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// One card is declined:
Console.WriteLine("One card is declined:");
var declined = await harness.CreateTaskAsync(
    Checkout,
    DurableJson.ToElement(new Dictionary<string, string> { ["cards"] = "visa-1,expired-2,visa-3,visa-4" }),
    ownerConversationId: root.Id);
var declinedOutcome = await harness.WaitForTaskAsync(declined.Id);
Console.WriteLine($"  checkout: {declinedOutcome.Status}");

// The fail-fast abort reached the siblings of the declined card that were still waiting at
// the bank. The set is printed once, sorted, rather than from the abort handler: the
// handler runs in whatever order the fan-out reaches the siblings.
var declinedAborted = aborted.Order(StringComparer.Ordinal).ToList();
Console.WriteLine($"  aborted payments: {string.Join(", ", declinedAborted)}");

// The customer cancels:
Console.WriteLine("The customer cancels:");
var cancelled = await harness.CreateTaskAsync(
    Checkout,
    DurableJson.ToElement(new Dictionary<string, string> { ["cards"] = "visa-5,visa-6,visa-7,visa-8" }),
    ownerConversationId: root.Id);

// The task graph prints the live tasks as a tree along their owner edges. The graph is
// printed once the checkout is waiting on its payments and every payment has reached the
// bank: the state is observed, not guessed from a duration, so the print is the same on
// every run.
Console.WriteLine("Task graph:");
await WaitForBankAsync(harness, cancelled.Id);
PrintGraph(harness.TaskGraph());

await harness.AbortTaskAsync(cancelled.Id);
Console.WriteLine($"  checkout: {(await harness.WaitForTaskAsync(cancelled.Id)).Status}");
var cancelledAborted = aborted.Order(StringComparer.Ordinal).ToList();
Console.WriteLine($"  aborted payments: {string.Join(", ", cancelledAborted)}");

// The task graph after the abort.
Console.WriteLine("Task graph after the abort:");
PrintGraph(harness.TaskGraph());

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The checkout owns four payment children, and the graph shows the ownership
// edges.
var graph = harness.TaskGraph();
var checkoutChildren = graph.Where(pair => pair.Owner == declined.Id).ToList();
Check.Equal(4, checkoutChildren.Count, "the checkout owns four payment children");
Check.That(checkoutChildren.All(pair => pair.Task.Name == "example.payment"), "the children are payments");
Check.That(graph.Any(pair => pair.Task.Id == declined.Id && pair.Owner == root.Id), "the checkout is owned by the conversation");

// One card was declined, so the checkout failed rather than completing.
Check.Equal("failed", declinedOutcome.Status, "a declined payment fails the checkout");
Check.That(declinedOutcome.Error is not null, "the failed checkout carries an error");

// The declined card was never charged, and the other cards were.
Check.That(!charged.Contains("expired-2"), "the declined card was never charged");
Check.That(charged.Contains("visa-1"), "the other cards were charged");

// The declined card is the second payment, so the checkout has already waited for
// visa-1 to complete when it observes the failure. The fail-fast abort then reaches the
// two siblings that are still waiting at the bank. The set is asserted, not the order the
// fan-out walked it in.
Check.Sequence(
    ["visa-3", "visa-4"],
    declinedAborted,
    "the declined card aborts the siblings still waiting at the bank");

// The customer cancels: the checkout is aborted, and its children are aborted
// with it, so their effects are undone.
var cancelledOutcome = await harness.WaitForTaskAsync(cancelled.Id);
Check.Equal("aborted", cancelledOutcome.Status, "the cancelled checkout is aborted");
Check.That(!charged.Contains("visa-5"), "an aborted checkout undoes its effects");
Check.That(!charged.Contains("visa-6"), "every aborted payment undoes its effect");

var cancelledChildren = graph.Where(pair => pair.Owner == cancelled.Id).ToList();
Check.Equal(4, cancelledChildren.Count, "the cancelled checkout still owns its four children");
Check.That(
    cancelledChildren.All(pair => pair.Task.Status == Durable.TaskStatus.Terminal),
    "every child of an aborted checkout is terminal");
Check.That(
    cancelledChildren.All(pair => pair.Task.Outcome?.GetProperty("status").GetString() == "aborted"),
    "every child of an aborted checkout is aborted, not completed");

// The cancelled checkout's four payments were aborted too, so the set now holds every
// payment the two fan-outs reached.
Check.Sequence(
    ["visa-3", "visa-4", "visa-5", "visa-6", "visa-7", "visa-8"],
    cancelledAborted,
    "both fan-outs aborted every payment they reached");

/// <summary>
/// Waits until the checkout is waiting on its payments and every payment has reached the
/// bank, so the graph is printed from a state the run can observe rather than from a delay.
/// </summary>
static async Task WaitForBankAsync(DurableHarness harness, string checkoutId)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (true)
    {
        var graph = harness.TaskGraph();
        var checkout = graph.FirstOrDefault(pair => pair.Task.Id == checkoutId).Task;
        var payments = graph.Where(pair => pair.Owner == checkoutId).ToList();

        if (checkout is not null
            && checkout.Status == Durable.TaskStatus.Waiting
            && payments.Count == 4
            && payments.All(pair => pair.Task.Status == Durable.TaskStatus.Running
                && pair.Task.Checkpoint.ValueKind == JsonValueKind.Object
                && pair.Task.Checkpoint.TryGetProperty("phase", out var phase)
                && phase.GetString() == "confirm"))
        {
            return;
        }

        if (DateTimeOffset.UtcNow > deadline)
        {
            throw new InvalidOperationException("the payments never reached the bank");
        }

        await Task.Delay(5);
    }
}

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

    // A task owned by a conversation rather than by another task is a root: the
    // conversation is not itself a node in the graph.
    foreach (var node in nodes.Where(candidate =>
        owners.GetValueOrDefault(candidate.Id) is not { } owner
        || !owners.ContainsKey(owner)))
    {
        Print(node, 0);
    }
}