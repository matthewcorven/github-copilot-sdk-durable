using Durable.EfCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore.Tests;

/// <summary>
/// The durable store's read and write paths against a real, file-backed SQLite database.
/// </summary>
/// <remarks>
/// These tests are the SQLite provider's end-to-end proof: the database is a file on disk,
/// the schema comes from the checked-in migration, and a store that is disposed and
/// reopened over the same file reads back what the previous store wrote. Nothing here
/// needs a database server, so the suite runs on a clean machine and in CI.
/// </remarks>
public sealed class SqliteIntegrationTests : IClassFixture<SqliteStoreFixture>
{
    private static readonly DocumentDefinition<Dictionary<string, string>> Notes = new("notes");

    private readonly SqliteStoreFixture _fixture;

    public SqliteIntegrationTests(SqliteStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Fixture_Creates_A_File_Backed_Database_And_Applies_The_Migration()
    {
        Assert.True(File.Exists(_fixture.DatabasePath), $"expected a database file at {_fixture.DatabasePath}");

        // A real SQLite file, not an empty placeholder: the format's 16-byte header.
        using (var stream = File.OpenRead(_fixture.DatabasePath))
        {
            var header = new byte[16];
            Assert.Equal(16, stream.Read(header, 0, header.Length));
            Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));
        }

        using var context = _fixture.OpenContext();
        var applied = context.Database.GetAppliedMigrations().ToArray();
        Assert.Contains(applied, name => name.EndsWith("_InitialCreate", StringComparison.Ordinal));

        var tables = context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToList();

        Assert.Contains("sessions", tables);
        Assert.Contains("turns", tables);
        Assert.Contains("events", tables);
    }

    [Fact]
    public async Task Fixture_Deletes_The_Database_And_Its_Directory_On_Dispose()
    {
        // A fixture of its own, so disposing it does not disturb the class fixture.
        var fixture = new SqliteStoreFixture();
        await fixture.InitializeAsync();

        var databasePath = fixture.DatabasePath;
        var directory = fixture.Directory;
        Assert.True(File.Exists(databasePath));

        await fixture.DisposeAsync();

        Assert.False(File.Exists(databasePath), "the database file must be deleted on dispose");
        Assert.False(Directory.Exists(directory), "the fixture directory must be deleted on dispose");
    }

    [Fact]
    public async Task Write_Then_Read_Back_Through_A_New_Store_Over_The_Same_File()
    {
        string conversationId;
        string taskId;

        using (var first = _fixture.OpenStore())
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
                        "sqlite.task",
                        () => new Dictionary<string, string> { ["phase"] = "run" },
                        (_, _, runtime) => runtime.CommitAsync(
                            () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                    DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                    conversation.Id);

                await Task.CompletedTask;
                return (conversation.Id, task.Id);
            });
        }

        // A new store, a new provider instance, the same file: this is the restart.
        using var second = _fixture.OpenStore();

        var conversation = second.Conversation(conversationId);
        Assert.NotNull(conversation);
        Assert.Single(second.Conversations);

        var task = second.Task(taskId);
        Assert.NotNull(task);
        Assert.Equal("sqlite.task", task!.Name);
        Assert.Equal(conversationId, task.OwnerConversationId);

        var document = second.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Single(document!.Versions);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "written before restart" },
            DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

        Assert.Equal([document.Versions[0].EntryId], second.Entries(conversationId));
    }

    [Fact]
    public async Task Restart_Persistence_Keeps_Every_Commit_And_Mints_Past_The_Replayed_Sequence()
    {
        string conversationId;
        long highestBefore;

        using (var first = _fixture.OpenStore())
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

        using var second = _fixture.OpenStore();

        var document = second.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Equal(3, document!.Versions.Count);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "v2" },
            DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));
        Assert.Equal(3, second.Entries(conversationId).Count);

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

    [Fact]
    public async Task Provider_Selected_By_Name_Persists_Across_A_Restart()
    {
        EfCoreStoreProvider.Register();
        try
        {
            string conversationId;

            using (var first = DurableStore.Create(new DurableStoreOptions
            {
                Path = _fixture.DatabasePath,
                ProviderName = EfCoreStoreProvider.RegisteredName,
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
                Path = _fixture.DatabasePath,
                ProviderName = EfCoreStoreProvider.RegisteredName,
            });

            var document = second.Document("notes", conversationId);
            Assert.NotNull(document);
            Assert.Equal(
                new Dictionary<string, string> { ["text"] = "by name" },
                DurableJson.FromElement<Dictionary<string, string>>(document!.Latest!.Value));
        }
        finally
        {
            DurableStore.UnregisterProvider(EfCoreStoreProvider.RegisteredName);
        }
    }

    [Fact]
    public async Task Provider_Applies_The_Migration_On_First_Use_Of_A_New_File()
    {
        var path = Path.Combine(_fixture.Directory, "first-use.db");
        Assert.False(File.Exists(path));

        using (var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = path,
            Provider = _ => new EfCoreStoreProvider(),
        }))
        {
            await store.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();
                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "created on open" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                await Task.CompletedTask;
            });
        }

        Assert.True(File.Exists(path), "opening the provider must create the database file");

        using var context = new DurableDbContext(EfCoreStoreProvider.ContextOptionsFor(path));
        Assert.Contains(
            context.Database.GetAppliedMigrations(),
            name => name.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Single(context.Sessions.AsNoTracking().ToList());
    }

    [Fact]
    public void Connection_String_Convention_Is_Data_Source_Of_The_Absolute_Path()
    {
        var connectionString = EfCoreStoreProvider.ConnectionStringFor(_fixture.DatabasePath);

        var parsed = new SqliteConnectionStringBuilder(connectionString);
        Assert.Equal(Path.GetFullPath(_fixture.DatabasePath), parsed.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, parsed.Mode);
    }

    [Fact]
    public void Connection_String_Convention_Resolves_A_Relative_Path_And_Escapes_It()
    {
        var relative = Path.Combine("relative", "store.db");
        var parsed = new SqliteConnectionStringBuilder(EfCoreStoreProvider.ConnectionStringFor(relative));

        Assert.Equal(Path.GetFullPath(relative), parsed.DataSource);
        Assert.True(Path.IsPathRooted(parsed.DataSource));

        // A path with the connection string's own separators stays one data source.
        var awkward = Path.Combine(_fixture.Directory, "a path;with=separators.db");
        var awkwardParsed = new SqliteConnectionStringBuilder(EfCoreStoreProvider.ConnectionStringFor(awkward));
        Assert.Equal(Path.GetFullPath(awkward), awkwardParsed.DataSource);
    }

    [Fact]
    public void Connection_String_Convention_Rejects_An_Empty_Path()
    {
        Assert.Throws<ArgumentException>(() => EfCoreStoreProvider.ConnectionStringFor(" "));
    }
}
