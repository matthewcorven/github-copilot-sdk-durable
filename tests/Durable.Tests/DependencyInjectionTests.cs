using Durable;
using Durable.DependencyInjection;
using GitHub.Copilot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The dependency-injection registration: one singleton client, built by the same builder a
/// manual build uses, borrowing what the container already holds.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddDurableCopilotClient_RegistersOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());

        services.AddDurableCopilotClient();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DurableCopilotClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddDurableCopilotClient_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());

        services.AddDurableCopilotClient();
        services.AddDurableCopilotClient(options => options.Harness = new DurableHarnessOptions());

        // A second call must not register a second client: the container would then hand out
        // two runtimes over one store.
        Assert.Single(services, d => d.ServiceType == typeof(DurableCopilotClient));
    }

    [Fact]
    public void AddDurableCopilotClient_ReturnsTheSameCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddDurableCopilotClient());
    }

    [Fact]
    public void AddDurableCopilotClient_WithNullArguments_Throws()
    {
        IServiceCollection services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => DurableServiceCollectionExtensions.AddDurableCopilotClient(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddDurableCopilotClient(null!));
    }

    [Fact]
    public async Task AddDurableCopilotClient_ResolvesTheSameInstanceEveryTime()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<DurableCopilotClient>();
        var second = provider.GetRequiredService<DurableCopilotClient>();

        // One instance means one runtime: the client owns the runtime, so a second instance
        // would be a second runtime over the same store.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task AddDurableCopilotClient_DoesNoIoUntilTheClientIsResolved()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();

        // Building the container produced nothing; the client is constructed on first resolve
        // and still performs no I/O, so it is not initialized.
        var client = provider.GetRequiredService<DurableCopilotClient>();
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task AddDurableCopilotClient_RunsTheDelegateAtResolve()
    {
        var configured = 0;
        var harness = new DurableHarnessOptions();

        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());
        services.AddDurableCopilotClient(options =>
        {
            configured++;
            options.Harness = harness;
        });

        await using var provider = services.BuildServiceProvider();
        Assert.Equal(0, configured);

        var client = provider.GetRequiredService<DurableCopilotClient>();

        Assert.Equal(1, configured);
        Assert.Same(harness, client.Options.Harness);
    }

    [Fact]
    public async Task AddDurableCopilotClient_BorrowsTheRegisteredStore()
    {
        var store = DurableStore.CreateEphemeral();

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DurableCopilotClient>();

        await client.InitializeAsync();

        Assert.Same(store, client.Store);

        // The container disposes the client, and the client leaves the borrowed store alone.
        await provider.DisposeAsync();

        await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
        Assert.Single(store.Conversations);
        store.Dispose();
    }

    [Fact]
    public async Task AddDurableCopilotClient_BorrowsTheRegisteredRegistry()
    {
        var registry = new ExtensionRegistry();

        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());
        services.AddSingleton(registry);
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DurableCopilotClient>();

        Assert.Same(registry, client.Registry);
    }

    [Fact]
    public async Task AddDurableCopilotClient_WithoutAStore_ThrowsWhenResolved()
    {
        var services = new ServiceCollection();
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();

        // Registration is a description, so it succeeds; the missing store is reported when the
        // client is actually built, by the builder's own validation.
        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<DurableCopilotClient>());
        Assert.Contains("UseStore", error.Message);
    }

    [Fact]
    public async Task AddDurableCopilotClient_LeavesTheStoreToOneRuntime()
    {
        var store = DurableStore.CreateEphemeral();

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddDurableCopilotClient();

        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<DurableCopilotClient>();
        await registered.InitializeAsync();

        // A second client built by hand over the same store is a second runtime, and the store
        // refuses it. The registration is the single runtime for that store, not one of two.
        await using var manual = new DurableClientBuilder().UseStore(store).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manual.InitializeAsync());
        Assert.Contains("already driven by another durable runtime", error.Message);
    }

    [Fact]
    public async Task AddDurableCopilotClient_ContainerDisposesTheClientExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DurableStore.CreateEphemeral());
        services.AddDurableCopilotClient();

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DurableCopilotClient>();
        await client.InitializeAsync();

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.False(client.IsInitialized);
    }
}
