using Durable;
using Xunit;

namespace Durable.Tests;

public class DurableClientTests
{
    [Fact]
    public void Build_WithNoStoreSource_Throws()
    {
        var builder = new DurableClientBuilder();

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("UseStore", error.Message);
    }

    [Fact]
    public void Build_WithTwoStoreSources_Throws()
    {
        var builder = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .Configure(options => options.StoreOptions = new DurableStoreOptions
            {
                Format = DurableStoreFormat.InMemory,
            });

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_WithOwnsStoreButNoStore_Throws()
    {
        var builder = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Configure(options => options.OwnsStore = true);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_ResolvesOneRegistry_AndOptionsAgreesWithTheProperty()
    {
        var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .Build();

        // Finding 1: the property and the options must report the same instance, and reading
        // it twice must not allocate a second registry.
        Assert.Same(client.Registry, client.Options.Registry);
        Assert.Same(client.Registry, client.Registry);
    }

    [Fact]
    public void Build_KeepsTheSuppliedRegistry()
    {
        var registry = new ExtensionRegistry();

        var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseRegistry(registry)
            .Build();

        Assert.Same(registry, client.Registry);
        Assert.Same(registry, client.Options.Registry);
    }

    [Fact]
    public void Build_PerformsNoInitialization()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Build();

        Assert.False(client.IsInitialized);
    }

    [Fact]
    public void StoreAndNativeClient_BeforeInitialization_ThrowNamingInitializeAsync()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Build();

        // Finding 2: the getters read the runtime's bound state, so an unbound runtime is the
        // only reason they throw — and the message says what to await.
        Assert.Contains("InitializeAsync", Assert.Throws<InvalidOperationException>(() => client.Store).Message);
        Assert.Contains("InitializeAsync", Assert.Throws<InvalidOperationException>(() => client.NativeClient).Message);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndDisposalIsExactlyOnce()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Build();

        await client.InitializeAsync();
        await client.InitializeAsync();

        Assert.True(client.IsInitialized);
        Assert.NotNull(client.Store);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task ConcurrentFirstOperations_ShareOneInitialization()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Build();

        var attempts = Enumerable.Range(0, 8).Select(_ => client.InitializeAsync()).ToArray();
        await Task.WhenAll(attempts);

        Assert.True(client.IsInitialized);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task BorrowedStore_IsNotDisposedByTheClient()
    {
        var store = DurableStore.CreateEphemeral();
        var client = new DurableClientBuilder().UseStore(store).Build();

        await client.InitializeAsync();
        await client.DisposeAsync();

        // The store is still usable: the client borrowed it.
        await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
        Assert.Single(store.Conversations);
        store.Dispose();
    }

    [Fact]
    public async Task OwnedStore_IsDisposedByTheClient()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .Build();

        await client.InitializeAsync();
        var store = client.Store;
        await client.DisposeAsync();

        // The store's gate is disposed, so a second transaction cannot start.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.CommitAsync(tx => Task.FromResult(tx.CreateConversation())));
    }
}
