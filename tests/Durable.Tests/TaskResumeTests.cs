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

    /// <summary>
    /// A parent that is re-entered while it is still <c>Waiting</c> owes the wait it recorded. Its
    /// checkpoint is where it will continue from, and the children it named may not have run yet —
    /// so entering the next phase first would let the parent read an outcome that does not exist.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_AParentReEnteredWhileWaiting_FinishesItsChildrenFirst()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var parent = WaitingParentTask();
        var child = SlowChildTask(TimeSpan.FromMilliseconds(150));
        registry.Install(new Extension("waiting", tasks: [parent, child]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var parentId = await SeedWaitingParentAsync(store, parent, child);

        // The parent is re-entered on its own, not through a resume that happens to run the child
        // first: the child is still Pending when this call starts, which is the state a process
        // that died between the parent's waiting commit and the child's first phase leaves.
        var final = await harness.RunTaskAsync(parentId).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("completed", final.Status);

        // The parent's second phase saw a finished child. Without the wait being recovered on
        // re-entry it ran against a child that had not started, and recorded "child-Pending".
        Assert.Equal("child-done", final.Result!.Value.GetProperty("parent").GetString());
        Assert.Equal(TaskStatus.Terminal, store.Task(parentId)!.Status);
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
                ["collect"] = async (_, checkpoint, runtime) =>
                {
                    // Read the child's outcome the way a real phase does. A resume that skipped the
                    // recorded wait reaches this with the child still Pending.
                    var outcomes = await runtime.OutcomesAsync([checkpoint["child"]]);
                    var seen = outcomes.Count > 0 ? outcomes[0].Status : "missing";
                    return await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
                        new Dictionary<string, string>
                        {
                            ["parent"] = seen == "completed" ? "child-done" : "child-" + seen,
                            ["child"] = checkpoint["child"],
                        })));
                },
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

    /// <summary>A child whose first phase takes real time, so a parent that skips its wait sees it unfinished.</summary>
    private static DurableTaskDefinition SlowChildTask(TimeSpan delay) =>
        DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.slow-child",
            () => new Dictionary<string, string> { ["phase"] = "work" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["work"] = async (_, _, runtime) =>
                {
                    await Task.Delay(delay, runtime.CancellationToken);
                    return await runtime.CommitAsync(() => TaskCommit.Completed(
                        DurableJson.ToElement(new Dictionary<string, string> { ["child"] = "done" })));
                },
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
    /// The shape that used to hang: `TaskRuntime.MemoAsync` called from inside a commit callback
    /// now throws a catchable exception that names the fix, instead of waiting for the gate the
    /// commit already holds.
    /// </summary>
    /// <remarks>
    /// Before the guard this was an infinite wait — the inner `WaitAsync` queued behind the outer
    /// hold and the outer hold was awaiting the inner call — so the expectation is deliberately
    /// expressed as an exception rather than as a completion. The watchdog is still here: a
    /// regression to the old behaviour must fail this test in seconds rather than hang CI.
    /// </remarks>
    [Fact]
    public async Task MemoAsyncInsideCommit_ThrowsAndNamesTransactionMemo_InsteadOfHanging()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.memo-async-inside-commit",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(async (Transaction tx) =>
                {
                    var memo = runtime.MemoAsync("k", DurableJson.ToElement("written"));
                    Assert.True(
                        ReferenceEquals(memo, await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(5)))),
                        "the re-entry did not fail within 5s; it is waiting on the commit gate again");

                    var error = await Assert.ThrowsAsync<DurableCommitReentryException>(() => memo);
                    Assert.Equal("TaskRuntime.MemoAsync", error.Operation);
                    Assert.Contains("Transaction.Memo", error.Message);

                    // The guard rejected the call before it touched any state, so the documented
                    // route still works in the same callback: one commit for the effect and its
                    // record.
                    tx.Memo(runtime.Task.Id, "k", DurableJson.ToElement("written"));
                    return TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "done" }));
                }),
                ["done"] = (_, _, runtime) => runtime.CommitAsync(
                    () => TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, string> { ["ok"] = "yes" }))),
            });

        var registry = new ExtensionRegistry();
        registry.Install(new Extension("memo", tasks: [definition]));
        await using var harness = await DurableHarness.OpenAsync(store, registry);
        var record = await WithTimeout(harness.CreateTaskAsync(
            definition,
            DurableJson.ToElement(new Dictionary<string, string>())));

        var outcome = await WithTimeout(harness.WaitForTaskAsync(record.Id));
        Assert.Equal("completed", outcome.Status);
        Assert.Equal("written", store.Task(record.Id)!.Memos["k"].GetString());
    }

    /// <summary>
    /// The memo re-entry is refused by, and inside, a `DurableStore.CommitAsync` transaction too —
    /// the other public way into the gate.
    /// </summary>
    [Fact]
    public async Task MemoAsyncInsideStoreCommit_ThrowsTheSameException()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = CounterTask();
        var record = await WithTimeout(store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 })))));

        // The refusal is thrown out of the inner call, so it fails the enclosing commit and the
        // caller sees it from `CommitAsync` -- a catchable error at the call site, not a stuck
        // await. The inner call is awaited under a watchdog so a regression to the hang fails here
        // rather than stalling the suite.
        var error = await WithTimeout(Assert.ThrowsAsync<DurableCommitReentryException>(() =>
            store.CommitAsync(async tx =>
            {
                var memo = store.MemoAsync(record.Id, "k", DurableJson.ToElement("written"));
                await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(5)));
                await memo;
                return tx;
            })));

        Assert.Equal("TaskRuntime.MemoAsync", error.Operation);
        Assert.Equal("DurableStore.CommitAsync", error.CommitOperation);

        // Nothing was written: the refusal happens before the inner call reaches the provider.
        Assert.False(store.Task(record.Id)!.Memos.ContainsKey("k"));
    }

    /// <summary>
    /// A different task calling `MemoAsync` while a commit is in flight waits for the commit and
    /// then completes: the guard keys on the async flow, not on the store.
    /// </summary>
    /// <remarks>
    /// This is the false-positive control. A detector that keyed on the store — "a commit is in
    /// progress, so every memo call fails" — would pass the re-entry test above and break every
    /// legitimate concurrent caller; the assertion here is what distinguishes the two.
    /// </remarks>
    [Fact]
    public async Task MemoAsyncFromAnotherTaskWhileACommitIsInFlight_StillWaitsAndCompletes()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = CounterTask();
        var record = await WithTimeout(store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 })))));

        var insideCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var letTheCommitFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var commit = store.CommitAsync(async (Transaction tx) =>
        {
            insideCommit.SetResult(true);
            await letTheCommitFinish.Task;
            return tx;
        });

        await WithTimeout(insideCommit.Task);

        // Started while the commit holds the gate. It must not throw; it must queue behind the
        // commit, which is the behaviour every concurrent caller depends on.
        var memo = store.MemoAsync(record.Id, "k", DurableJson.ToElement("written"));
        Assert.NotSame(memo, await Task.WhenAny(memo, Task.Delay(TimeSpan.FromSeconds(1))));

        letTheCommitFinish.SetResult(true);
        await WithTimeout(commit);

        Assert.Equal("written", (await WithTimeout(memo))!.Value.GetString());
        Assert.Equal("written", store.Task(record.Id)!.Memos["k"].GetString());
    }

    /// <summary>
    /// A task created and scheduled from inside a commit's callback runs on its own flow, so its
    /// own commit must succeed: the scope belongs to the committing flow, not to work that flow
    /// started.
    /// </summary>
    /// <remarks>
    /// This is the false-positive control that the concurrent-caller test above does not cover.
    /// The scope is an `AsyncLocal`, so a fan-out that schedules a child from inside the callback
    /// inherited it, and the child's first commit was refused as a re-entry it was not — which
    /// broke `examples/23-child-tasks.cs`. The child here is deliberately scheduled from inside
    /// the callback, which is exactly the shape that regressed.
    /// </remarks>
    [Fact]
    public async Task ChildTaskScheduledFromInsideACommit_CommitsOnItsOwnFlow()
    {
        var store = DurableStore.CreateEphemeral();
        var registry = new ExtensionRegistry();
        var childDefinition = CounterTask();
        var parentDefinition = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
            "test.parent",
            () => new Dictionary<string, string> { ["phase"] = "fan-out" },
            new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["fan-out"] = (_, _, runtime) => runtime.CommitAsync(async tx =>
                {
                    var child = runtime.CreateChildTask(
                        tx,
                        childDefinition,
                        DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 }));
                    runtime.ScheduleTask(child.Id);
                    return TaskCommit.Completed(DurableJson.ToElement(new Dictionary<string, int>()));
                }),
            });
        registry.Install(new Extension("fan-out", tasks: [parentDefinition, childDefinition]));

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var parent = await harness.CreateTaskAsync(
            parentDefinition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 }));
        await WithTimeout(harness.WaitForTaskAsync(parent.Id));

        var child = Assert.Single(store.Tasks, candidate => candidate.OwnerTaskId == parent.Id);
        var outcome = await WithTimeout(harness.WaitForTaskAsync(child.Id));

        // The refusal would surface as a failed child, so the status is the assertion that the
        // child's own commit was allowed.
        Assert.Equal("completed", outcome.Status);
        Assert.Equal(Durable.TaskStatus.Terminal, store.Task(child.Id)!.Status);
    }

    /// <summary>
    /// The ordinary published path is unchanged: a memo from a phase handler, outside any commit,
    /// writes and reads back.
    /// </summary>
    [Fact]
    public async Task MemoAsyncOutsideACommit_StillReadsAndWrites()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = CounterTask();
        var record = await WithTimeout(store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 1 })))));

        Assert.Null(await WithTimeout(store.MemoAsync(record.Id, "k", null)));
        Assert.Equal("written", (await WithTimeout(store.MemoAsync(record.Id, "k", DurableJson.ToElement("written"))))!
            .Value.GetString());
        Assert.Equal("written", (await WithTimeout(store.MemoAsync(record.Id, "k", null)))!.Value.GetString());
    }

    /// <summary>
    /// A fan-out started from inside a commit callback commits on its own flow: `Task.Run` and
    /// `ThreadPool.QueueUserWorkItem` children both run once the outer commit has released.
    /// </summary>
    /// <remarks>
    /// The scope is an `AsyncLocal`, so both children inherit it, and the original guard refused
    /// them as re-entries they were not. The guard now keys on the commit that holds the gate, so
    /// an inherited scope whose commit has finished is a fan-out and queues normally. Both store
    /// tiers are exercised because the guard lives in the store, not the provider.
    /// <para>
    /// The children are started inside the callback but reach their own commit only after the
    /// outer commit returns. A child that committed while the callback still held the gate would
    /// wait for a release the callback cannot give while it awaits the child — a deadlock — and is
    /// still refused; that is the case a fan-out must not be built on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FanOutStartedInsideACommit_CommitsAfterTheOuterCommitReleases()
    {
        foreach (var persistent in new[] { false, true })
        {
            var directory = persistent
                ? Path.Combine(Path.GetTempPath(), $"durable-fanout-{Guid.NewGuid():N}")
                : null;
            var store = CreateStore(directory);
            try
            {
                var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var children = new List<Task>();
                var fromTaskRun = default(Task<ConversationRecord>);
                var fromQueue = default(Task<ConversationRecord>);

                await WithTimeout(store.CommitAsync(async _tx =>
                {
                    // Both children are started from inside the callback but commit only once the
                    // outer commit has released: this is the fan-out shape that regressed.
                    fromTaskRun = Task.Run(async () =>
                    {
                        await released.Task;
                        return await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
                    });
                    ThreadPool.QueueUserWorkItem(async _ =>
                    {
                        await released.Task;
                        fromQueue = store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
                        await fromQueue;
                    });
                    await Task.CompletedTask;
                    return true;
                }));

                released.SetResult(true);
                await WithTimeout(fromTaskRun!);
                await WithTimeout(fromQueue!);

                // The parent's transaction wrote no conversation; each child's did.
                Assert.Equal(2, store.Conversations.Count);
            }
            finally
            {
                store.Dispose();
                if (directory is not null)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        static DurableStore CreateStore(string? directory)
        {
            if (directory is null)
            {
                return DurableStore.CreateEphemeral();
            }

            Directory.CreateDirectory(directory);
            return DurableStore.Create(new DurableStoreOptions
            {
                Path = Path.Combine(directory, "store.jsonl"),
                Format = DurableStoreFormat.Jsonl,
            });
        }
    }

    /// <summary>
    /// A child that runs *after* its parent's commit releases, started from inside the callback,
    /// also commits: the inherited scope must not outlive the commit it came from.
    /// </summary>
    [Fact]
    public async Task ChildFlowStartedInsideACommit_ThatRunsAfterRelease_Commits()
    {
        using var store = DurableStore.CreateEphemeral();

        Task<ConversationRecord>? deferred = null;
        await WithTimeout(store.CommitAsync(async _tx =>
        {
            // Started inside the callback, but its body does not reach the gate until the
            // continuation runs — after this commit has returned.
            deferred = Task.Run(async () =>
            {
                await Task.Delay(150);
                return await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
            });
            return true;
        }));

        await WithTimeout(deferred!);
        Assert.Single(store.Conversations);
    }

    /// <summary>
    /// The genuine re-entry is still refused: a commit awaited synchronously on the committing
    /// flow inside the callback would deadlock, so it throws the named error.
    /// </summary>
    [Fact]
    public async Task CommitAwaitedOnTheCommittingFlow_StillThrowsAndNamesTheOuterCommit()
    {
        using var store = DurableStore.CreateEphemeral();

        var error = await WithTimeout(Assert.ThrowsAsync<DurableCommitReentryException>(() =>
            store.CommitAsync(async tx =>
            {
                await store.CommitAsync(inner => Task.FromResult(inner));
                return tx;
            })));

        Assert.Equal("DurableStore.CommitAsync", error.Operation);
        Assert.Equal("DurableStore.CommitAsync", error.CommitOperation);
        Assert.Empty(store.Conversations);
    }

    /// <summary>
    /// A rollback discards a memo written inside the transaction, and the *next* failed transaction
    /// does not bring it back.
    /// </summary>
    /// <remarks>
    /// The second half is the point. `InMemoryStoreProvider.UpdateTask` keeps a baseline of each
    /// task's last known state so the next transaction can arm its own undo with it, and it had
    /// already moved that baseline forward to the state the failed transaction wrote. Restoring only
    /// the record left the baseline ahead of it, so the following failure restored the memo this one
    /// had discarded — the write came back one rollback later, which is the opposite of what a
    /// rollback is for. The TypeScript port reset the baseline; the C# one did not.
    /// </remarks>
    [Fact]
    public async Task MemoDiscardedByARollback_DoesNotReappearInTheNextFailedTransaction()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.memo-rollback-then-fail-again",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Running(
                    DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run" }))),
            });

        var taskId = await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, string>())).Id));

        // A: write, then fail. The memo goes.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(tx =>
        {
            tx.Memo(taskId, "effect/once", DurableJson.ToElement("written"));
            throw new InvalidOperationException("the effect failed after the memo was staged");
        }));
        Assert.False(store.Task(taskId)!.Memos.ContainsKey("effect/once"),
            "the first rollback discarded the memo");

        // B: an unrelated transaction that fails without touching the memo at all. Its rollback must
        // not reinstate A's write.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(tx =>
        {
            tx.Memo(taskId, "effect/never-written", DurableJson.ToElement("staged"));
            throw new InvalidOperationException("a later transaction failed");
        }));

        Assert.False(store.Task(taskId)!.Memos.ContainsKey("effect/once"),
            "the memo discarded by the first rollback must not reappear in the next one");
        Assert.False(store.Task(taskId)!.Memos.ContainsKey("effect/never-written"),
            "the second transaction's own memo is discarded too");
    }

    /// <summary>
    /// The same sequence for a checkpoint, because the defect was never memo-specific.
    /// </summary>
    /// <remarks>
    /// `UpdateTask` is how every in-place task mutation is recorded, so the stale baseline applied to
    /// the checkpoint and the status as well. This test uses no memos at all, which is what makes it
    /// evidence that the fix is in the provider rather than in `Transaction.Memo`.
    /// </remarks>
    [Fact]
    public async Task CheckpointDiscardedByARollback_DoesNotReappearInTheNextFailedTransaction()
    {
        var store = DurableStore.CreateEphemeral();
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "test.checkpoint-rollback-then-fail-again",
            () => new Dictionary<string, string> { ["phase"] = "run", ["n"] = "0" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Running(
                    DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run", ["n"] = "0" }))),
            });

        var taskId = await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, string>())).Id));

        // A committed advance to n=1: this one must survive everything below.
        await store.CommitTaskAsync(taskId, () => TaskCommit.Running(
            DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run", ["n"] = "1" })));

        // A transaction that stages n=2 in place and then fails.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(tx =>
        {
            var task = store.Task(taskId)!;
            task.Checkpoint = DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run", ["n"] = "2" });
            store.Provider.UpdateTask(task);
            throw new InvalidOperationException("the phase failed after moving the checkpoint");
        }));

        Assert.True(store.Task(taskId)!.Checkpoint.GetProperty("n").GetString() == "1",
            "the rollback restored the committed checkpoint");

        // A second failed transaction that also stages a write must not bring n=2 back. It has to
        // stage something: a rollback only restores what the transaction armed, which is why the
        // stale baseline needs a second `UpdateTask` — and the real path always supplies one, since
        // `CommitTaskAsync` records the task unconditionally before it commits.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(tx =>
        {
            var task = store.Task(taskId)!;
            task.Checkpoint = DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "run", ["n"] = "3" });
            store.Provider.UpdateTask(task);
            throw new InvalidOperationException("a later transaction failed");
        }));

        Assert.True(store.Task(taskId)!.Checkpoint.GetProperty("n").GetString() == "1",
            "the checkpoint discarded by the first rollback must not reappear in the next one");
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

    private static async Task WithTimeout(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(finished, task), "the operation did not complete within 10s");
        await task;
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
