using System.Runtime.CompilerServices;
using System.Text.Json;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// A task that waits for an external event: the event is not a child task, so the run ends
/// with the task <see cref="TaskStatus.Waiting"/> and <see cref="DurableStore.RaiseEventAsync"/>
/// is what makes it runnable again — in this process or in a later one over the same file.
/// </summary>
public sealed class ExternalEventTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"durable-external-event-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(_directory, "store.jsonl");

    public ExternalEventTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WaitingForEvent_StopsTheRunAndSurvivesAReopen()
    {
        var approval = Approval(out var asked);
        string taskId;
        DateTimeOffset deadline;
        {
            using var store = new DurableStore(StorePath);
            await using var harness = await DurableHarness.OpenAsync(store, Registry(approval));
            var root = await harness.RootAsync();
            var task = await harness.CreateTaskAsync(approval, Input(), root.Id);
            taskId = task.Id;

            var parked = await harness.WaitForTaskAsync(task.Id);

            // The run ended without finishing the task. A child-task wait would have waited,
            // and a task that will never be woken would have been reported as failed.
            Assert.Equal("pending", parked.Status);
            Assert.Equal(1, asked.Value);
            var record = store.Task(task.Id)!;
            Assert.Equal(TaskStatus.Waiting, record.Status);
            Assert.Equal([TaskCommit.ExternalEventPrefix + "approval"], record.WaitingOn);
            Assert.Equal("await", record.Checkpoint.GetProperty("phase").GetString());
            deadline = record.Checkpoint.GetProperty("deadline").GetDateTimeOffset();
        }

        // A later process reads the same park, deadline and all.
        using var reopened = new DurableStore(StorePath);
        var saved = reopened.Task(taskId)!;
        Assert.Equal(TaskStatus.Waiting, saved.Status);
        Assert.Equal(["external:approval"], saved.WaitingOn);
        Assert.Equal(deadline, saved.Checkpoint.GetProperty("deadline").GetDateTimeOffset());
        Assert.Equal(1, asked.Value);
    }

    [Fact]
    public async Task RaiseEvent_WakesTheParkedTaskInALaterProcessAtTheCheckpointNotTheStart()
    {
        var approval = Approval(out var asked);
        string taskId;
        DateTimeOffset deadline;
        {
            using var first = new DurableStore(StorePath);
            await using var harness = await DurableHarness.OpenAsync(first, Registry(approval));
            var root = await harness.RootAsync();
            taskId = (await harness.CreateTaskAsync(approval, Input(), root.Id)).Id;
            await harness.WaitForTaskAsync(taskId);
            deadline = first.Task(taskId)!.Checkpoint.GetProperty("deadline").GetDateTimeOffset();
        }

        using var second = new DurableStore(StorePath);
        await using var resumed = await DurableHarness.OpenAsync(second, Registry(approval));

        var woken = await second.RaiseEventAsync(
            "external:approval",
            DurableJson.ToElement(new Dictionary<string, string> { ["decision"] = "approved" }));

        Assert.Equal([taskId], woken);
        var running = second.Task(taskId)!;
        Assert.Equal(TaskStatus.Running, running.Status);
        Assert.Empty(running.WaitingOn);
        Assert.Equal("approved", running.Memos[DurableStore.ExternalEventMemoKey]
            .GetProperty("decision").GetString());

        var outcome = await resumed.WaitForTaskAsync(taskId);

        Assert.Equal("completed", outcome.Status);
        Assert.Equal("approved", outcome.Result!.Value.GetProperty("decision").GetString());

        // The ask phase ran once, in the process that parked: the wake-up re-entered at
        // the checkpoint the park wrote, so nothing before the approval ran again.
        Assert.Equal(1, asked.Value);
        var finished = second.Task(taskId)!;
        Assert.Equal(TaskStatus.Terminal, finished.Status);
        Assert.Equal(1, finished.Restarts);
        Assert.Equal(deadline, finished.Checkpoint.GetProperty("deadline").GetDateTimeOffset());
    }

    [Fact]
    public async Task RaiseEvent_DeliveredTwice_KeepsTheFirstAnswerAndWakesNobodyTheSecondTime()
    {
        var approval = Approval(out _);
        using var store = new DurableStore(StorePath);
        await using var harness = await DurableHarness.OpenAsync(store, Registry(approval));
        var root = await harness.RootAsync();
        var task = await harness.CreateTaskAsync(approval, Input(), root.Id);
        await harness.WaitForTaskAsync(task.Id);

        var first = await store.RaiseEventAsync(
            "external:approval", DurableJson.ToElement(new Dictionary<string, string> { ["decision"] = "approved" }));
        var second = await store.RaiseEventAsync(
            "external:approval", DurableJson.ToElement(new Dictionary<string, string> { ["decision"] = "denied" }));

        Assert.Equal([task.Id], first);
        Assert.Empty(second);
        Assert.Equal("approved", store.Task(task.Id)!.Memos[DurableStore.ExternalEventMemoKey]
            .GetProperty("decision").GetString());

        var outcome = await harness.WaitForTaskAsync(task.Id);
        Assert.Equal("completed", outcome.Status);
        Assert.Equal("approved", outcome.Result!.Value.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task WaitingForEvent_WithNoChildTasks_IsNotFailedByTheRunner()
    {
        // The regression this pins: a `waiting` commit whose names are external events must
        // not be read as child task ids. Looked up as ids they do not exist, and the runner
        // would fail the task instead of leaving it parked.
        var definition = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "tests.park-only",
            () => new Dictionary<string, string> { ["phase"] = "run" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["run"] = (_, _, runtime) => runtime.CommitAsync(() =>
                    TaskCommit.WaitingForEvent(DurableJson.ToElement(new Dictionary<string, string>()), "external:nothing")),
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

        using var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, Registry(definition));
        var root = await harness.RootAsync();
        var task = await harness.CreateTaskAsync(definition, DurableJson.ToElement(new Dictionary<string, string>()), root.Id);

        await harness.WaitForTaskAsync(task.Id);

        var record = store.Task(task.Id)!;
        Assert.Equal(TaskStatus.Waiting, record.Status);
        Assert.Null(record.Outcome);
    }

    [Fact]
    public async Task RaiseEvent_RejectsANameThatWouldReadBackAsAChildTask()
    {
        using var store = new DurableStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RaiseEventAsync("approval", DurableJson.ToElement(new Dictionary<string, string>())));
    }

    private static JsonElement Input() =>
        DurableJson.ToElement(new Dictionary<string, string> { ["requestId"] = "PA-1" });

    private static ExtensionRegistry Registry(DurableTaskDefinition definition)
    {
        var registry = new ExtensionRegistry();
        registry.Install(new Extension("external-event", tasks: [definition]));
        return registry;
    }

    /// <summary>
    /// An approval that parks on an external event with a durable deadline, and completes
    /// when the event's payload is the memo it was delivered as. The deadline lives in the
    /// checkpoint the park writes, so a later process reads the same instant.
    /// </summary>
    private static DurableTaskDefinition Approval(out StrongBox<int> asked)
    {
        var box = new StrongBox<int>(0);
        asked = box;
        return DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "tests.approval",
            () => new Dictionary<string, string> { ["phase"] = "ask" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["ask"] = (_, _, runtime) =>
                {
                    Interlocked.Increment(ref box.Value);
                    return runtime.CommitAsync(() => TaskCommit.WaitingForEvent(
                        DurableJson.ToElement(new Dictionary<string, string>
                        {
                            ["phase"] = "await",
                            ["deadline"] = runtime.Now.AddMinutes(15).ToString("O"),
                            ["requestId"] = "PA-1",
                        }),
                        TaskCommit.ExternalEventPrefix + "approval"));
                },
                ["await"] = (_, checkpoint, runtime) =>
                {
                    var answer = runtime.MemoAsync(DurableStore.ExternalEventMemoKey).GetAwaiter().GetResult();
                    if (answer is not null)
                    {
                        return runtime.CommitAsync(() => TaskCommit.Completed(answer.Value));
                    }

                    var deadline = DateTimeOffset.Parse(checkpoint["deadline"]);
                    if (runtime.Now >= deadline)
                    {
                        return runtime.CommitAsync(() => TaskCommit.Failed("approval deadline expired"));
                    }

                    // Still waiting: re-park with the deadline it already had. The run does
                    // not sleep -- a parked task holds no invocation.
                    return runtime.CommitAsync(() => TaskCommit.WaitingForEvent(
                        DurableJson.ToElement(checkpoint),
                        TaskCommit.ExternalEventPrefix + "approval"));
                },
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));
    }
}