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

    private static CopilotClient UnstartedClient() => new(new CopilotClientOptions
    {
        Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
    });
}
