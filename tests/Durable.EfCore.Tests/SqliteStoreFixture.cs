using Durable.EfCore;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore.Tests;

/// <summary>
/// A file-backed SQLite database for one test: a fresh directory under the system temp
/// path, a migrated database inside it, and a store opened over that database.
/// </summary>
/// <remarks>
/// The database is a real file, not an in-memory SQLite connection, because the point of
/// these tests is that the state survives the process that wrote it. An in-memory
/// connection is dropped when the last connection to it closes, so it cannot prove that.
/// <para>
/// The fixture applies the checked-in migration itself, before any store opens, so a test
/// can assert that the schema is the migration's and not something the provider created
/// on the fly. <see cref="DisposeAsync"/> deletes the directory, including the database,
/// its <c>-wal</c>/<c>-shm</c> sidecars and the store's lock files, and it runs whether
/// the test passed or failed.
/// </para>
/// </remarks>
public sealed class SqliteStoreFixture : IAsyncLifetime
{
    private readonly List<DurableStore> _stores = [];

    /// <summary>Creates the fixture. The directory is made by <see cref="InitializeAsync"/>.</summary>
    public SqliteStoreFixture()
    {
        Directory = Path.Combine(
            Path.GetTempPath(),
            $"durable-efcore-sqlite-{Guid.NewGuid():N}");
    }

    /// <summary>The directory that holds the database and the store's lock files.</summary>
    public string Directory { get; }

    /// <summary>The database file the stores in this fixture share.</summary>
    public string DatabasePath => Path.Combine(Directory, "durable.db");

    /// <summary>The connection string the provider uses for <see cref="DatabasePath"/>.</summary>
    public string ConnectionString => EfCoreStoreProvider.ConnectionStringFor(DatabasePath);

    /// <summary>Creates the directory and applies the migration to a fresh database file.</summary>
    public Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);

        using var context = new DurableDbContext(EfCoreStoreProvider.ContextOptionsFor(DatabasePath));
        context.Database.Migrate();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a store over the fixture's database, selected through the provider seam.
    /// The store is disposed with the fixture, so a test that forgets to dispose it still
    /// releases the lock before the directory is deleted.
    /// </summary>
    public DurableStore OpenStore()
    {
        var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = DatabasePath,
            Provider = _ => new EfCoreStoreProvider(),
        });

        _stores.Add(store);
        return store;
    }

    /// <summary>Opens a context over the fixture's database, for asserting on the rows.</summary>
    public DurableDbContext OpenContext() =>
        new(EfCoreStoreProvider.ContextOptionsFor(DatabasePath));

    /// <summary>Disposes every store the fixture opened, then deletes the directory.</summary>
    public Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        _stores.Clear();

        // SQLite pools its connections, so a pooled handle can outlive the context that
        // opened it and keep the file open on Windows. Clearing the pool releases them.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        return Task.CompletedTask;
    }
}
