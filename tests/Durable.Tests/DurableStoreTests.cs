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
                using var store1 = new DurableStore(storePath);
                await using var harness1 = await DurableHarness.OpenAsync(store1, registry);
                var root = await harness1.RootAsync();
                var taskRec = await harness1.CreateTaskAsync(taskDef, DurableJson.ToElement(new Dictionary<string, int> { ["target"] = 3 }), root.Id);
                taskId = taskRec.Id;
            }

            // Second run: reopen and continue to completion
            {
                using var store2 = new DurableStore(storePath);
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

    [Fact]
    public void Open_SecondWriterOnSamePath_IsRefusedNamingTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "session.jsonl");

        try
        {
            using var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            });

            var error = Assert.Throws<InvalidOperationException>(() =>
                DurableStore.Create(new DurableStoreOptions
                {
                    Path = storePath,
                    Format = DurableStoreFormat.Jsonl,
                }));

            Assert.Contains(storePath, error.Message);
            Assert.Contains("already open", error.Message);
            Assert.Contains(Environment.ProcessId.ToString(), error.Message);
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
    public async Task Reopen_MintsSequenceGreaterThanEveryReplayedOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-seq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "session.jsonl");

        try
        {
            var definition = new DocumentDefinition<Dictionary<string, string>>("notes");
            long highestBefore;

            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            }))
            {
                await first.CommitAsync(async transaction =>
                {
                    var draft = transaction.Doc(definition, "owner");
                    draft.Value = new Dictionary<string, string> { ["text"] = "one" };
                    draft.Save("e1");
                    await Task.CompletedTask;
                });

                await first.CommitAsync(async transaction =>
                {
                    var draft = transaction.Doc(definition, "owner");
                    draft.Value = new Dictionary<string, string> { ["text"] = "two" };
                    draft.Save("e2");
                    await Task.CompletedTask;
                });

                highestBefore = first.Documents
                    .SelectMany(document => document.Versions)
                    .Max(version => version.Sequence);
            }

            using var second = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            });

            // The replayed versions keep the sequence each was minted with, not the
            // envelope sequence of the commit that wrote it.
            var replayed = second.Documents
                .SelectMany(document => document.Versions)
                .Select(version => version.Sequence)
                .ToList();
            Assert.Equal(2, replayed.Count);
            Assert.Equal(highestBefore, replayed.Max());
            Assert.Equal(replayed.Count, replayed.Distinct().Count());

            long mintedAfterReopen = 0;
            await second.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(definition, "owner");
                draft.Value = new Dictionary<string, string> { ["text"] = "three" };
                draft.Save("e3");
                await Task.CompletedTask;
            });

            mintedAfterReopen = second.Documents
                .SelectMany(document => document.Versions)
                .Max(version => version.Sequence);

            Assert.True(
                mintedAfterReopen > highestBefore,
                $"a reopened store must mint past every replayed sequence: {mintedAfterReopen} <= {highestBefore}");
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
    public async Task JsonlBackend_SurvivesReopenWithIdenticalState()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-jsonl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "state.jsonl");

        try
        {
            var definition = new DocumentDefinition<Dictionary<string, string>>("notes");
            var taskDefinition = DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                "reopen.task",
                () => new Dictionary<string, string> { ["phase"] = "run" },
                (_, _, runtime) => runtime.CommitAsync(
                    () => TaskCommit.Completed(DurableJson.ToElement("done"))));

            string conversationId;
            string taskId;

            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            }))
            {
                (conversationId, taskId) = await first.CommitAsync(async transaction =>
                {
                    var conversation = transaction.CreateConversation();
                    var draft = transaction.Doc(definition, conversation.Id);
                    draft.Value = new Dictionary<string, string> { ["text"] = "first" };
                    draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                    var task = transaction.CreateTask(
                        taskDefinition,
                        DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                        conversation.Id);
                    await Task.CompletedTask;
                    return (conversation.Id, task.Id);
                });

                await first.CommitAsync(async transaction =>
                {
                    var draft = transaction.Doc(definition, conversationId);
                    draft.Value = new Dictionary<string, string> { ["text"] = "second" };
                    draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
                    await Task.CompletedTask;
                });
            }

            using var second = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            });

            var conversation2 = second.Conversation(conversationId);
            Assert.NotNull(conversation2);
            Assert.Single(second.Conversations);

            var task2 = second.Task(taskId);
            Assert.NotNull(task2);
            Assert.Equal("reopen.task", task2!.Name);
            Assert.Equal(TaskStatus.Pending, task2.Status);
            Assert.Equal(conversationId, task2.OwnerConversationId);
            Assert.Single(second.Tasks);

            var document = second.Document("notes", conversationId);
            Assert.NotNull(document);
            Assert.Equal(2, document!.Versions.Count);
            Assert.Equal(
                new Dictionary<string, string> { ["text"] = "second" },
                DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

            var entries = second.Entries(conversationId);
            Assert.Equal(2, entries.Count);
            Assert.Equal(document.Versions.Select(version => version.EntryId), entries);
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
    public async Task JsonlBackend_DiscardsTruncatedFinalLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-torn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "state.jsonl");

        try
        {
            var definition = new DocumentDefinition<Dictionary<string, string>>("notes");

            string conversationId;
            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            }))
            {
                conversationId = await first.CommitAsync(async transaction =>
                {
                    var conversation = transaction.CreateConversation();
                    var draft = transaction.Doc(definition, conversation.Id);
                    draft.Value = new Dictionary<string, string> { ["text"] = "committed" };
                    draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                    await Task.CompletedTask;
                    return conversation.Id;
                });
            }

            // A crash mid-append leaves a partial record with no terminator.
            var fragment = "{\"v\":1,\"seq\":9999,\"ty";
            File.AppendAllText(storePath, fragment);
            Assert.EndsWith(fragment, File.ReadAllText(storePath), StringComparison.Ordinal);

            using var second = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.Jsonl,
            });

            // The torn tail is gone from the file, and everything before it survived.
            Assert.DoesNotContain(fragment, File.ReadAllText(storePath));
            Assert.EndsWith("\n", File.ReadAllText(storePath), StringComparison.Ordinal);

            var document = second.Document("notes", conversationId);
            Assert.NotNull(document);
            Assert.Single(document!.Versions);
            Assert.Equal(
                new Dictionary<string, string> { ["text"] = "committed" },
                DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

            // The store is still writable after the truncation.
            await second.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(definition, conversationId);
                draft.Value = new Dictionary<string, string> { ["text"] = "after" };
                draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
                await Task.CompletedTask;
            });

            Assert.Equal(2, second.Document("notes", conversationId)!.Versions.Count);
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
    public async Task DurableStoreOptions_ExplicitPathWinsOverDefaultPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-explicit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var explicitPath = Path.Combine(dir, "explicit.jsonl");
        var fileName = $"durable-test-default-{Guid.NewGuid():N}.jsonl";
        var defaultPath = DurableStore.DefaultStorePath(fileName);

        try
        {
            string conversationId;
            using (var store = DurableStore.Create(new DurableStoreOptions
            {
                Path = explicitPath,
                FileName = fileName,
            }))
            {
                Assert.True(store.IsPersistent);
                conversationId = await store.CommitAsync(async transaction =>
                {
                    await Task.CompletedTask;
                    return transaction.CreateConversation().Id;
                });
            }

            // The writes landed in the explicit file, not in the per-user default one.
            Assert.True(File.Exists(explicitPath));
            Assert.Contains(conversationId, File.ReadAllText(explicitPath), StringComparison.Ordinal);
            Assert.False(File.Exists(defaultPath));
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
    public void DurableStoreOptions_DefaultPathResolvesUnderPerUserDirectory()
    {
        var fileName = $"durable-test-default-{Guid.NewGuid():N}.jsonl";
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state");
        var expected = Path.Combine(root, "durable", fileName);

        var resolved = DurableStore.DefaultStorePath(fileName);

        Assert.Equal(expected, resolved);
        Assert.Equal("durable-store.jsonl", Path.GetFileName(DurableStore.DefaultStorePath()));
        Assert.Equal("durable", Path.GetFileName(Path.GetDirectoryName(DurableStore.DefaultStorePath())!));
    }

    [Fact]
    public async Task LegacyJsonStore_OpensAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"durable-test-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "state.json");

        try
        {
            var definition = new DocumentDefinition<Dictionary<string, string>>("notes");
            var taskDefinition = DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                "legacy.task",
                () => new Dictionary<string, string> { ["phase"] = "run" },
                (_, _, runtime) => runtime.CommitAsync(
                    () => TaskCommit.Completed(DurableJson.ToElement("done"))));

            string conversationId;
            string taskId;

            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.LegacyJson,
            }))
            {
                (conversationId, taskId) = await first.CommitAsync(async transaction =>
                {
                    var conversation = transaction.CreateConversation();
                    var draft = transaction.Doc(definition, conversation.Id);
                    draft.Value = new Dictionary<string, string> { ["text"] = "legacy" };
                    draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                    var task = transaction.CreateTask(
                        taskDefinition,
                        DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                        conversation.Id);
                    await Task.CompletedTask;
                    return (conversation.Id, task.Id);
                });
            }

            // The legacy backend writes one JSON object for the whole file.
            using (var parsed = JsonDocument.Parse(File.ReadAllText(storePath)))
            {
                Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
                Assert.True(parsed.RootElement.TryGetProperty("sequence", out _));
            }

            using var second = DurableStore.Create(new DurableStoreOptions
            {
                Path = storePath,
                Format = DurableStoreFormat.LegacyJson,
            });

            var conversation = second.Conversation(conversationId);
            Assert.NotNull(conversation);
            Assert.Single(second.Conversations);

            var task = second.Task(taskId);
            Assert.NotNull(task);
            Assert.Equal("legacy.task", task!.Name);
            Assert.Equal(TaskStatus.Pending, task.Status);
            Assert.Equal(conversationId, task.OwnerConversationId);
            Assert.Single(second.Tasks);

            var document = second.Document("notes", conversationId);
            Assert.NotNull(document);
            Assert.Single(document!.Versions);
            Assert.Equal(
                new Dictionary<string, string> { ["text"] = "legacy" },
                DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));
            Assert.Single(second.Entries(conversationId));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
