using System.Text.Json;
using Durable;
using GitHub.Copilot;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// Resuming interrupted work: the operation is awaited, and both entry points have the same
/// shape.
/// </summary>
/// <remarks>
/// A task is written to the store without being scheduled, which is what a process that died
/// between committing the record and running it leaves behind. Resuming is then the only thing
/// that can finish it, so the assertions are about the work actually having run by the time the
/// call returns.
/// </remarks>
public class TaskResumeTests
{
    [Fact]
    public async Task ResumeAsync_RunsTheInterruptedTaskBeforeItReturns()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var taskId = await SeedTaskAsync(store, definition, target: 2);
        Assert.Equal(TaskStatus.Pending, store.Task(taskId)!.Status);

        var outcomes = await harness.ResumeAsync();

        // The work is done when the call returns: a fire-and-forget start would leave the record
        // pending here.
        var outcome = Assert.Single(outcomes);
        Assert.Equal("completed", outcome.Status);
        Assert.Equal(2, outcome.Result!.Value.GetInt32());
        Assert.Equal(TaskStatus.Terminal, store.Task(taskId)!.Status);
    }

    [Fact]
    public async Task ResumeAsync_ReturnsOutcomesInStoreOrder()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var first = await SeedTaskAsync(store, definition, target: 1);
        var second = await SeedTaskAsync(store, definition, target: 3);

        var outcomes = await harness.ResumeAsync();

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(1, outcomes[0].Result!.Value.GetInt32());
        Assert.Equal(3, outcomes[1].Result!.Value.GetInt32());
        Assert.Equal([first, second], store.Tasks.Select(task => task.Id));
    }

    /// <summary>
    /// A parent that committed <c>Waiting</c> on a child is not finished: it has a checkpoint to
    /// continue from and a child to wait for. It holds no invocation, which is why a process that
    /// stopped there leaves it <c>Waiting</c> in the store, and why a resume that skipped it would
    /// leave the workflow half-done with nothing left to drive it.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_AParentLeftWaitingOnAChild_FinishesWhenTheChildDoes()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var parent = WaitingParentTask();
        var child = InstantChildTask();
        registry.Install(new Extension("waiting", tasks: [parent, child]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var parentId = await SeedWaitingParentAsync(store, parent, child);

        // The state a process that died while its child was still queued leaves behind: the
        // parent's Waiting commit and the child's Pending record are both durable.
        Assert.Equal(TaskStatus.Waiting, store.Task(parentId)!.Status);

        var outcomes = await harness.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // One outcome per task the resume re-entered, and the parent is one of them. Before the
        // fix the parent had no outcome at all: it stayed Waiting forever.
        Assert.Equal(2, outcomes.Count);
        var final = Assert.Single(outcomes, outcome => outcome.Result is { } result
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("parent", out _));
        Assert.Equal("completed", final.Status);
        Assert.Equal(TaskStatus.Terminal, store.Task(parentId)!.Status);

        // The parent's next phase ran, and it read the child's outcome rather than re-doing the
        // work the child had already done.
        Assert.Equal("child-done", final.Result!.Value.GetProperty("parent").GetString());
    }

    [Fact]
    public async Task ResumeAsync_WithNothingInterrupted_ReturnsNoOutcomes()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        Assert.Empty(await harness.ResumeAsync());
    }

    [Fact]
    public async Task Facade_ResumeTasksAsync_HasTheSameAwaitedShape()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        var taskId = await SeedTaskAsync(store, definition, target: 4);

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .UseClient(UnstartedClient())
            .Build();

        var outcomes = await client.ResumeTasksAsync();

        var outcome = Assert.Single(outcomes);
        Assert.Equal("completed", outcome.Status);
        Assert.Equal(4, outcome.Result!.Value.GetInt32());
        Assert.Equal(TaskStatus.Terminal, store.Task(taskId)!.Status);
    }

    [Fact]
    public async Task Facade_WaitForTaskAsync_ReturnsTheTerminalOutcome()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        var taskId = await SeedTaskAsync(store, definition, target: 5);

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .UseClient(UnstartedClient())
            .Build();

        var outcome = await client.WaitForTaskAsync(taskId);

        Assert.Equal("completed", outcome.Status);
        Assert.Equal(5, outcome.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Facade_WaitForTaskAsync_WithAnEmptyId_Throws()
    {
        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseClient(UnstartedClient())
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(() => client.WaitForTaskAsync(""));
    }

    [Fact]
    public async Task Facade_WaitForTaskAsync_WithAnUnknownId_ReportsAFailure()
    {
        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseClient(UnstartedClient())
            .Build();

        var outcome = await client.WaitForTaskAsync("t999");

        Assert.Equal("failed", outcome.Status);
        Assert.Equal("unknown task", outcome.Error);
    }

    /// <summary>
    /// A task that counts to its target, one step per phase, so a resumed run has a checkpoint
    /// to continue from rather than a single all-or-nothing step.
    /// </summary>
    private static DurableTaskDefinition CounterTask() =>
        DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
            "test.counter",
            () => new Dictionary<string, string> { ["phase"] = "step", ["n"] = "0" },
            new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["step"] = (input, checkpoint, runtime) =>
                {
                    var n = int.Parse(checkpoint["n"]) + 1;
                    return n >= input["target"]
                        ? runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(n)))
                        : runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(
                            new Dictionary<string, string> { ["phase"] = "step", ["n"] = n.ToString() })));
                },
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

    /// <summary>
    /// A parent that creates one child, waits on it, and then reads its outcome. The second phase
    /// is what a resume has to reach: a resume that treats <c>Waiting</c> as finished never does.
    /// </summary>
    private static DurableTaskDefinition WaitingParentTask() =>
        DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.waiting-parent",
            () => new Dictionary<string, string> { ["phase"] = "spawn" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["spawn"] = (_, checkpoint, runtime) => runtime.CommitAsync(async tx =>
                {
                    // Re-entered after the checkpoint already names the child: wait on it again
                    // rather than creating a second one.
                    if (checkpoint.TryGetValue("child", out var existing))
                    {
                        return TaskCommit.Waiting(
                            DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "collect", ["child"] = existing }),
                            [existing]);
                    }

                    var child = runtime.CreateChildTask(tx, InstantChildTask(), DurableJson.ToElement(new Dictionary<string, string>()));
                    return TaskCommit.Waiting(
                        DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "collect", ["child"] = child.Id }),
                        [child.Id]);
                }),
                ["collect"] = (_, checkpoint, runtime) => runtime.CommitAsync(() =>
                    TaskCommit.Completed(DurableJson.ToElement(
                        new Dictionary<string, string> { ["parent"] = "child-done", ["child"] = checkpoint["child"] }))),
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

    /// <summary>A child that finishes on its first phase, so the parent's wait is one step.</summary>
    private static DurableTaskDefinition InstantChildTask() =>
        DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.instant-child",
            () => new Dictionary<string, string> { ["phase"] = "work" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["work"] = (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Completed(
                    DurableJson.ToElement(new Dictionary<string, string> { ["child"] = "done" }))),
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

    /// <summary>
    /// Leaves the parent committed as <c>Waiting</c> on an unscheduled child: the task graph a
    /// process that died between the parent's waiting commit and the child's first phase leaves.
    /// </summary>
    private static async Task<string> SeedWaitingParentAsync(
        DurableStore store,
        DurableTaskDefinition parent,
        DurableTaskDefinition child)
    {
        var record = await store.CommitAsync(tx =>
        {
            var childRecord = tx.CreateTask(child, DurableJson.ToElement(new Dictionary<string, string>()));
            var parentRecord = tx.CreateTask(parent, DurableJson.ToElement(new Dictionary<string, string>()), ownerTaskId: null);
            parentRecord.Checkpoint = DurableJson.ToElement(new Dictionary<string, string>
            {
                ["phase"] = "collect",
                ["child"] = childRecord.Id,
            });
            parentRecord.Status = TaskStatus.Waiting;
            parentRecord.WaitingOn.Add(childRecord.Id);
            store.Provider.UpdateTask(parentRecord);
            return Task.FromResult(parentRecord);
        });

        await store.CommitAsync(_ => Task.CompletedTask);
        return record.Id;
    }

    /// <summary>
    /// Writes a task record without scheduling it, which is the state a process that stopped
    /// between committing the record and running it leaves behind.
    /// </summary>
    private static async Task<string> SeedTaskAsync(
        DurableStore store,
        DurableTaskDefinition definition,
        int target)
    {
        var record = await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = target }))));

        return record.Id;
    }

    [Fact]
    public async Task ResumeAsync_NumericPhase_FailsTheTaskAndReportsWhy()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        // JsonSerializerDefaults.Web serializes an enum as a number, so a checkpoint
        // whose phase property is enum-typed is written like this.
        var taskId = await SeedTaskAsync(
            store,
            definition,
            DurableJson.ToElement(new Dictionary<string, JsonElement> { ["phase"] = DurableJson.ToElement(0) }));

        // A run that escapes core used to leave the waiter here forever. The timeout is
        // the assertion: a hang fails the test instead of holding it open.
        var outcomes = await harness.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var outcome = Assert.Single(outcomes);
        Assert.Equal("failed", outcome.Status);
        Assert.Contains("checkpoint phase must be a string", outcome.Error);
        Assert.Contains("Number", outcome.Error);

        // The store agrees with the waiter: a supervisor polling it sees a failed task,
        // not one stuck Running.
        var record = store.Task(taskId)!;
        Assert.Equal(TaskStatus.Terminal, record.Status);
        Assert.NotNull(record.Outcome);
    }

    [Fact]
    public async Task WaitForTaskAsync_AThrowingPhaseRead_StillSettles()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .UseClient(UnstartedClient())
            .Build();

        var taskId = await SeedTaskAsync(
            store,
            definition,
            DurableJson.ToElement(new Dictionary<string, JsonElement> { ["phase"] = DurableJson.ToElement(true) }));

        var outcome = await client.WaitForTaskAsync(taskId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", outcome.Status);
        Assert.Contains("found Boolean", outcome.Error);
    }

    [Fact]
    public async Task RunTaskAsync_ARunThatFaultsOutsideThePhaseHandler_SettlesTheStoreAndTheWaiter()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();

        // A checkpoint value that serializes to something a JSON object can hold but a
        // phase name cannot: the read is the only thing that can fail here.
        var definition = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
            "test.throwing",
            () => new Dictionary<string, string> { ["phase"] = "step", ["n"] = "0" },
            new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["step"] = (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(1))),
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));
        registry.Install(new Extension("throwing", tasks: [definition]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var taskId = await SeedTaskAsync(
            store,
            definition,
            DurableJson.ToElement(new Dictionary<string, JsonElement> { ["phase"] = DurableJson.ToElement(3.5) }));

        var outcome = await harness.RunTaskAsync(taskId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", outcome.Status);
        Assert.Equal(TaskStatus.Terminal, store.Task(taskId)!.Status);
    }

    [Fact]
    public async Task WaitForTaskAsync_AFaultOutsideThePhaseLoop_SettlesInsteadOfHanging()
    {
        // The phase read cannot throw any more, so this needs a fault from elsewhere in the
        // loop. Committing the failure for a bad phase is outside the handler's try: if the
        // store throws there, the exception leaves RunTaskCoreAsync and only the settle in
        // RunTaskAsync can stop the waiter hanging.
        var provider = new FailOnceProvider();
        var store = DurableStore.Create(new DurableStoreOptions { Provider = _ => provider });
        var registry = new ExtensionRegistry();
        var definition = CounterTask();
        registry.Install(new Extension("counter", tasks: [definition]));

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .UseClient(UnstartedClient())
            .Build();

        var taskId = await SeedTaskAsync(
            store,
            definition,
            DurableJson.ToElement(new Dictionary<string, JsonElement> { ["phase"] = DurableJson.ToElement(0) }));
        provider.FailPhaseCommit = true;

        // Before the settle, this awaited a completion source nothing would ever settle:
        // the guard is what turns that into a failure rather than a hung test run.
        var outcome = await client.WaitForTaskAsync(taskId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", outcome.Status);
        Assert.Equal("checkpoint storage failed", outcome.Error);
    }

    /// <summary>
    /// An in-memory provider whose commit throws once the test arms it, so a fault can be
    /// raised from outside the phase handlers.
    /// </summary>
    private sealed class FailOnceProvider : InMemoryStoreProvider
    {
        /// <summary>Armed after the task is seeded, so only the run under test fails.</summary>
        public bool FailPhaseCommit { get; set; }

        public override void Commit()
        {
            if (FailPhaseCommit)
            {
                FailPhaseCommit = false;
                throw new InvalidOperationException("checkpoint storage failed");
            }
        }
    }

    /// <summary>
    /// Writes a task record without scheduling it, with an arbitrary initial checkpoint.
    /// </summary>
    private static async Task<string> SeedTaskAsync(
        DurableStore store,
        DurableTaskDefinition definition,
        JsonElement checkpoint)
    {
        var record = await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 }))));

        var task = store.Task(record.Id)!;
        task.Checkpoint = checkpoint;
        store.Provider.UpdateTask(task);
        await store.CommitAsync(_ => Task.CompletedTask);

        return task.Id;
    }

    private static CopilotClient UnstartedClient() => new(new CopilotClientOptions
    {
        Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
    });
}
