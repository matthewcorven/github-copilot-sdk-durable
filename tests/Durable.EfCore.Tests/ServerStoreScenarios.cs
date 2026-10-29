using Durable.EfCore.MySql;
using Durable.EfCore.PostgreSql;
using Durable.EfCore.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Durable.EfCore.Tests;

/// <summary>
/// The end-to-end scenarios every server provider runs, written once and driven by each
/// provider's test class.
/// </summary>
/// <remarks>
/// The scenarios are the same for all three servers on purpose: the model is
/// provider-neutral, so a difference in behaviour between SQL Server, PostgreSQL and MySQL
/// would be a defect in the provider wiring, not a difference the tests should encode.
/// </remarks>
public static class ServerStoreScenarios
{
    private static readonly DocumentDefinition<Dictionary<string, string>> Notes = new("notes");

    /// <summary>The fixture created the database and applied the checked-in migration.</summary>
    /// <param name="fixture">The provider's fixture.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    public static async Task Fixture_Applies_The_Checked_In_Migration(ServerStoreFixture fixture)
    {
        await using var context = fixture.OpenContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, name => name.EndsWith("_InitialCreate", StringComparison.Ordinal));

        // The migration assembly is the provider's own, and it holds exactly one migration:
        // a second provider's migration in the same assembly would be applied here too.
        var available = context.Database.GetMigrations().ToArray();
        Assert.Single(available);
        Assert.Equal(fixture.Kind switch
        {
            ServerKind.SqlServer => typeof(SqlServerProvider).Assembly.GetName().Name,
            ServerKind.PostgreSql => typeof(PostgreSqlProvider).Assembly.GetName().Name,
            ServerKind.MySql => typeof(MySqlProvider).Assembly.GetName().Name,
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        }, context.GetService<IMigrationsAssembly>().Assembly.GetName().Name);

        var tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("sessions", tables);
        Assert.Contains("turns", tables);
        Assert.Contains("events", tables);
    }

    /// <summary>A store writes, is disposed, and a new store over the same database reads it back.</summary>
    /// <param name="fixture">The provider's fixture.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    public static async Task Write_Then_Read_Back_Through_A_New_Store(ServerStoreFixture fixture)
    {
        string conversationId;
        string taskId;

        using (var first = fixture.OpenStore())
        {
            Assert.True(first.IsPersistent);

            (conversationId, taskId) = await first.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();

                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "written before restart" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

                var task = transaction.CreateTask(
                    DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                        "server.task",
                        () => new Dictionary<string, string> { ["phase"] = "run" },
                        (_, _, runtime) => runtime.CommitAsync(
                            () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                    DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                    conversation.Id);

                await Task.CompletedTask;
                return (conversation.Id, task.Id);
            });
        }

        using var second = fixture.OpenStore();

        var conversation = second.Conversation(conversationId);
        Assert.NotNull(conversation);

        var task = second.Task(taskId);
        Assert.NotNull(task);
        Assert.Equal("server.task", task!.Name);
        Assert.Equal(conversationId, task.OwnerConversationId);

        var document = second.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Single(document!.Versions);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "written before restart" },
            DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

        Assert.Equal([document.Versions[0].EntryId], second.Entries(conversationId));
    }

    /// <summary>A reopened store mints past every sequence it replayed.</summary>
    /// <param name="fixture">The provider's fixture.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    public static async Task Restart_Persistence_Mints_Past_The_Replayed_Sequence(ServerStoreFixture fixture)
    {
        string conversationId;
        long highestBefore;

        using (var first = fixture.OpenStore())
        {
            conversationId = await first.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();
                await Task.CompletedTask;
                return conversation.Id;
            });

            for (var index = 0; index < 3; index++)
            {
                var value = index;
                await first.CommitAsync(async transaction =>
                {
                    var draft = transaction.Doc(Notes, conversationId);
                    draft.Value = new Dictionary<string, string> { ["text"] = $"v{value}" };
                    draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
                    await Task.CompletedTask;
                });
            }

            highestBefore = first.Documents
                .SelectMany(document => document.Versions)
                .Max(version => version.Sequence);
        }

        using var second = fixture.OpenStore();

        var document = second.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Equal(3, document!.Versions.Count);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "v2" },
            DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

        await second.CommitAsync(async transaction =>
        {
            var draft = transaction.Doc(Notes, conversationId);
            draft.Value = new Dictionary<string, string> { ["text"] = "after restart" };
            draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
            await Task.CompletedTask;
        });

        var mintedAfterRestart = second.Documents
            .SelectMany(state => state.Versions)
            .Max(version => version.Sequence);

        Assert.True(
            mintedAfterRestart > highestBefore,
            $"a reopened store must mint past every replayed sequence: {mintedAfterRestart} <= {highestBefore}");
    }

    /// <summary>The provider is selectable by its registered name through the seam.</summary>
    /// <param name="fixture">The provider's fixture.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    public static async Task Provider_Selected_By_Name_Persists_Across_A_Restart(ServerStoreFixture fixture)
    {
        var name = fixture.Kind switch
        {
            ServerKind.SqlServer => SqlServerProvider.RegisteredName,
            ServerKind.PostgreSql => PostgreSqlProvider.RegisteredName,
            ServerKind.MySql => MySqlProvider.RegisteredName,
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };

        switch (fixture.Kind)
        {
            case ServerKind.SqlServer:
                SqlServerProvider.Register();
                break;
            case ServerKind.PostgreSql:
                PostgreSqlProvider.Register();
                break;
            case ServerKind.MySql:
                MySqlProvider.Register();
                break;
        }

        try
        {
            Assert.True(DurableStore.IsProviderRegistered(name));

            string conversationId;

            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = fixture.LockPath,
                ProviderName = name,
            }))
            {
                Assert.IsType<EfCoreStoreProvider>(first.Provider);

                conversationId = await first.CommitAsync(async transaction =>
                {
                    var conversation = transaction.CreateConversation();
                    var draft = transaction.Doc(Notes, conversation.Id);
                    draft.Value = new Dictionary<string, string> { ["text"] = "by name" };
                    draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                    await Task.CompletedTask;
                    return conversation.Id;
                });
            }

            using var second = DurableStore.Create(new DurableStoreOptions
            {
                Path = fixture.LockPath,
                ProviderName = name,
            });

            var document = second.Document("notes", conversationId);
            Assert.NotNull(document);
            Assert.Equal(
                new Dictionary<string, string> { ["text"] = "by name" },
                DurableJson.FromElement<Dictionary<string, string>>(document!.Latest!.Value));
        }
        finally
        {
            DurableStore.UnregisterProvider(name);
        }
    }
}
