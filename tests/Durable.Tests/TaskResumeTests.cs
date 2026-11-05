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

    // ─── A memo written inside a commit ──────────────────────────────────────────────────────

    /// <summary>
    /// A memo written through <see cref="Transaction.Memo"/> commits with the checkpoint that
    /// surrounded it, and is readable afterwards.
    /// </summary>
    /// <remarks>
    /// The reason this exists: `TaskRuntime.MemoAsync` takes the store's commit gate, and the
    /// commit running the phase handler already holds it, so calling it from inside a commit
    /// callback waits for a release that cannot happen. The failure is a hang, not an exception, so
    /// a test that merely awaited it would hang the suite instead of failing. Every await here goes
    /// through <see cref="WithTimeout{T}"/> for that reason.
    /// </remarks>
    [Fact]
    public async Task MemoInsideCommit_CommitsWithTheCheckpoint_AndIsReadableAfterwards()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();

        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.memo-in-transaction",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = async (_, _, runtime) =>
                {
                    var commit = await WithTimeout(runtime.CommitAsync(async tx =>
                    {
                        // The read before the write, the write, and the read back: all three are
                        // what a caller recording an external effect needs in one transaction.
                        Assert.Null(tx.Memo(runtime.Task.Id, "comment/101/abc"));
                        tx.Memo(runtime.Task.Id, "comment/101/abc", DurableJson.ToElement(new Dictionary<string, string>
                        {
                            ["posted"] = "yes",
                        }));
                        Assert.Equal("yes", tx.Memo(runtime.Task.Id, "comment/101/abc")!
                            .Value.Deserialize<Dictionary<string, string>>()!["posted"]);
                        return TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "done" }));
                    }));

                    return await WithTimeout(runtime.CommitAsync(
                        () => TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }))));
                },
                ["done"] = (_, _, runtime) => WithTimeout(runtime.CommitAsync(
                    () => TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" })))),
            });

        registry.Install(new Extension("memo", tasks: [definition]));
        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var record = await WithTimeout(harness.CreateTaskAsync(
            definition,
            DurableJson.ToElement(new Dictionary<string, string>())));
        var outcome = await WithTimeout(harness.WaitForTaskAsync(record.Id));

        Assert.Equal("completed", outcome.Status);
        Assert.Equal("yes", store.Task(record.Id)!.Memos["comment/101/abc"]
            .Deserialize<Dictionary<string, string>>()!["posted"]);
    }

    /// <summary>
    /// The behaviour this pair replaces: `TaskRuntime.MemoAsync` called from inside a commit does
    /// not return, because it takes the gate the commit already holds.
    /// </summary>
    /// <remarks>
    /// This is the before half of the before/after control, and it compiles and runs against the
    /// layer without the transaction-scoped overload. It deliberately exercises the *public* failure
    /// a caller meets, with a budget, so the hang is a failing assertion rather than a hung suite.
    /// Delete it when the memo-inside-commit shape is no longer a trap worth fixturing; keep it while
    /// two entry points for the same concept exist, because the wrong one is the obvious one.
    /// </remarks>
    [Fact]
    public async Task MemoAsyncInsideCommit_DoesNotReturn_BecauseTheGateIsNotReentrant()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.memo-async-inside-commit",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(async tx =>
                {
                    var memo = runtime.MemoAsync("k", DurableJson.ToElement("written"));
                    var finished = await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(2)));
                    Assert.NotSame(memo, finished);
                    return TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string>()));
                }),
            });

        var registry = new ExtensionRegistry();
        registry.Install(new Extension("memo", tasks: [definition]));
        await using var harness = await DurableHarness.OpenAsync(store, registry);
        var record = await WithTimeout(harness.CreateTaskAsync(
            definition,
            DurableJson.ToElement(new Dictionary<string, string>())));

        // The task never settles: the phase handler is stuck in the inner gate wait, so the run it
        // sits in cannot finish. The assertion above is what the test is really about; this proves
        // the stuck run is observable as unfinished rather than as a spurious completion.
        var settled = await Task.WhenAny(harness.WaitForTaskAsync(record.Id), Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(settled, harness.WaitForTaskAsync(record.Id));
    }

    /// <summary>
    /// A memo written inside a transaction that then throws is rolled back with the rest of it.
    /// </summary>
    /// <remarks>
    /// The other half of "commits together": if the write survived a failed transaction, a host
    /// that recorded its effect and then failed the commit would come back believing the effect had
    /// happened — it would skip the effect, and the store would hold a memo for work nobody did.
    /// </remarks>
    [Fact]
    public async Task MemoInsideFailedTransaction_IsRolledBack()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.memo-rollback",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Running(
                    DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run" }))),
            });

        var taskId = await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, string>())).Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(tx =>
        {
            tx.Memo(taskId, "effect/half-done", DurableJson.ToElement("written"));
            throw new InvalidOperationException("the effect failed after the memo was staged");
        }));

        Assert.False(store.Task(taskId)!.Memos.ContainsKey("effect/half-done"));
    }

    /// <summary>
    /// Awaits a task, or fails the assertion when it does not finish in time.
    /// </summary>
    /// <remarks>
    /// The layer's gate is not reentrant, so the failure this suite exists to catch is an await
    /// that never completes. Without a budget, a regression would hang the test run instead of
    /// failing it — which is exactly how the defect this covers reached a scenario.
    /// </remarks>
    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(finished, task), "the operation did not complete within 10s");
        return await task;
    }

    private static Task<T> WithTimeout<T>(ValueTask<T> task) => WithTimeout(task.AsTask());

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
