using System.Text.Json;
using Durable;
using Xunit;

namespace Durable.Tests;

public class DurableStoreTests
{
    [Fact]
    public async Task FailedTransaction_RollsBackAllWrites()
    {
        var store = new DurableStore();
        var doc = new DocumentDefinition<Dictionary<string, string>>(
            "test.doc",
            DocumentHistory.Rewindable,
            ForkPolicy.AsOf,
            () => new Dictionary<string, string>());

        var conv = await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));

        var beforeCount = store.Conversations.Count;
        var beforeDocs = store.Documents.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await store.CommitAsync(async tx =>
            {
                var c = tx.CreateConversation();
                var draft = tx.Doc(doc, c.Id);
                draft.Value["key"] = "value";
                draft.Save(tx.AppendEntry(c.Id, "entry1", "data"));
                throw new InvalidOperationException("simulated crash mid-commit");
            });
        });

        Assert.Equal(beforeCount, store.Conversations.Count);
        Assert.Equal(beforeDocs, store.Documents.Count);
    }

    [Fact]
    public async Task Fork_InheritsOnlyEntriesUpToForkPoint_AndHasIndependentState()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();
        await using var harness = await DurableHarness.OpenAsync(store, registry);

        var doc = new DocumentDefinition<Dictionary<string, string>>(
            "test.doc",
            DocumentHistory.Rewindable,
            ForkPolicy.AsOf,
            () => new Dictionary<string, string>());

        var chat = await harness.CreateConversationAsync();

        var entry1 = await chat.CommitAsync(async tx =>
        {
            var e = tx.AppendEntry(chat.Id, "note", "first");
            var d = tx.Doc(doc, chat.Id);
            d.Value["text"] = "v1";
            d.Save(e);
            return e;
        });

        var entry2 = await chat.CommitAsync(async tx =>
        {
            var e = tx.AppendEntry(chat.Id, "note", "second");
            var d = tx.Doc(doc, chat.Id);
            d.Value["text"] = "v2";
            d.Save(e);
            return e;
        });

        // Fork at entry1
        var fork = await harness.ForkAsync(chat, entry1);

        // Fork sees entry1 but not entry2
        Assert.Contains(entry1, fork.Entries(10));
        Assert.DoesNotContain(entry2, fork.Entries(10));

        // Fork has v1
        Assert.Equal("v1", fork.Snapshot(doc)!["text"]);

        // Mutating fork does not affect parent
        await fork.CommitAsync(async tx =>
        {
            var d = tx.Doc(doc, fork.Id);
            d.Value["text"] = "v1-forked";
            d.Save(tx.AppendEntry(fork.Id, "note", "fork-edit"));
        });

        Assert.Equal("v1-forked", fork.Snapshot(doc)!["text"]);
        Assert.Equal("v2", chat.Snapshot(doc)!["text"]);
    }

    [Fact]
    public async Task Recovery_ReopenedStoreResumesFromSavedCheckpointWithMemoIdempotency()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "session.json");

        try
        {
            var executions = new List<int>();

            var taskDef = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
                "test.counter",
                () => new Dictionary<string, string> { ["phase"] = "step", ["n"] = "1" },
                new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
                {
                    ["step"] = (input, checkpoint, runtime) =>
                    {
                        var n = int.Parse(checkpoint["n"]);
                        var memo = runtime.MemoAsync($"done-{n}").GetAwaiter().GetResult();
                        if (memo is null)
                        {
                            runtime.MemoAsync($"done-{n}", DurableJson.ToElement(true)).GetAwaiter().GetResult();
                            executions.Add(n);
                        }

                        if (n == input["target"])
                        {
                            return runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(n)));
                        }

                        return runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new Dictionary<string, string>
                        {
                            ["phase"] = "step",
                            ["n"] = (n + 1).ToString()
                        })));
                    }
                },
                (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

            var registry = new ExtensionRegistry();
            registry.Install(new Extension("counter", tasks: [taskDef]));

            // First run: execute step 1
            string taskId;
            {
                var store1 = new DurableStore(storePath);
                await using var harness1 = await DurableHarness.OpenAsync(store1, registry);
                var root = await harness1.RootAsync();
                var taskRec = await harness1.CreateTaskAsync(taskDef, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 3 }), root.Id);
                taskId = taskRec.Id;
            }

            // Second run: reopen and continue to completion
            {
                var store2 = new DurableStore(storePath);
                await using var harness2 = await DurableHarness.OpenAsync(store2, registry);
                var outcome = await harness2.WaitForTaskAsync(taskId);

                Assert.Equal("completed", outcome.Status);
                Assert.Equal(3, outcome.Result!.Value.GetInt32());
            }

            // Verify executions: each number executed exactly once despite store restart
            Assert.Equal([1, 2, 3], executions);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Abort_TerminatesParentAndChildrenDurably()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();

        var childAbortCount = 0;

        var childDef = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "child.task",
            () => new Dictionary<string, string> { ["phase"] = "work" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["work"] = (_, _, runtime) => runtime.SleepUntilAsync(
                    runtime.Now.AddSeconds(10),
                    () => TaskCommit.Completed(DurableJson.ToElement("done")))
            },
            (_, _, runtime) =>
            {
                Interlocked.Increment(ref childAbortCount);
                return runtime.CommitAsync(() => TaskCommit.Aborted());
            });

        var parentDef = DurableTask.DefinePhases<Dictionary<string, string>, Dictionary<string, string>>(
            "parent.task",
            () => new Dictionary<string, string> { ["phase"] = "spawn" },
            new Dictionary<string, Func<Dictionary<string, string>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["spawn"] = (_, _, runtime) => runtime.CommitAsync(async tx =>
                {
                    var c1 = runtime.CreateChildTask(tx, childDef, DurableJson.ToElement(new Dictionary<string, string>()));
                    var c2 = runtime.CreateChildTask(tx, childDef, DurableJson.ToElement(new Dictionary<string, string>()));
                    runtime.ScheduleTask(c1.Id);
                    runtime.ScheduleTask(c2.Id);
                    return TaskCommit.Waiting(DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "wait" }), [c1.Id, c2.Id]);
                })
            },
            (_, _, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

        registry.Install(new Extension("tree", tasks: [childDef, parentDef]));
        await using var harness = await DurableHarness.OpenAsync(store, registry);
        var root = await harness.RootAsync();

        var parentTask = await harness.CreateTaskAsync(parentDef, DurableJson.ToElement(new Dictionary<string, string>()), root.Id);
        await Task.Delay(50); // allow child creation

        await harness.AbortTaskAsync(parentTask.Id);

        var parentRecord = store.Task(parentTask.Id)!;
        Assert.Equal(TaskStatus.Terminal, parentRecord.Status);
        Assert.Equal("aborted", parentRecord.Outcome!.Value.GetProperty("status").GetString());

        // Children were aborted too
        var children = store.Tasks.Where(t => t.OwnerTaskId == parentTask.Id).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(TaskStatus.Terminal, c.Status));
        Assert.All(children, c => Assert.Equal("aborted", c.Outcome!.Value.GetProperty("status").GetString()));
        Assert.Equal(2, childAbortCount);
    }
}
