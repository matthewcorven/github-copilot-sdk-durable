using Durable;
using Xunit;

namespace Durable.Tests;

public class WatchHandleTests
{
    private static DocumentDefinition<Dictionary<string, string>> Definition() =>
        new(
            "watch.doc",
            DocumentHistory.Rewindable,
            ForkPolicy.AsOf,
            () => new Dictionary<string, string>());

    private static async Task CommitTextAsync(
        DurableStore store,
        DocumentDefinition<Dictionary<string, string>> definition,
        string ownerId,
        string text)
    {
        await store.CommitAsync(async transaction =>
        {
            var draft = transaction.Doc(definition, ownerId);
            draft.Value["text"] = text;
            draft.Save($"entry-{text}");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Watch_FirstFrameIsTheValueAtAcquisition()
    {
        var store = new DurableStore();
        var definition = Definition();

        await CommitTextAsync(store, definition, "owner", "before");

        using var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("before", frames.Current["text"]);
    }

    [Fact]
    public async Task Watch_FirstFrameIsInitialValue_WhenDocumentDoesNotExistYet()
    {
        var store = new DurableStore();
        var definition = Definition();

        using var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());
        Assert.Empty(frames.Current);
    }

    [Fact]
    public async Task Watch_DeliversFrameForEachCommit()
    {
        var store = new DurableStore();
        var definition = Definition();

        using var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());

        await CommitTextAsync(store, definition, "owner", "one");
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("one", frames.Current["text"]);

        await CommitTextAsync(store, definition, "owner", "two");
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("two", frames.Current["text"]);
    }

    [Fact]
    public async Task Watch_DoesNotDeliverFrameForRolledBackWrite()
    {
        var store = new DurableStore();
        var definition = Definition();

        using var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource();
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await store.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(definition, "owner");
                draft.Value["text"] = "rolled-back";
                draft.Save("entry-rolled-back");
                throw new InvalidOperationException("simulated crash mid-commit");
            });
        });

        // Nothing was committed, so the next frame never arrives: cancelling the
        // enumerator is what releases the pending read.
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await frames.MoveNextAsync());
    }

    [Fact]
    public async Task Watch_IgnoresOtherDocuments()
    {
        var store = new DurableStore();
        var definition = Definition();

        using var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource();
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());

        await CommitTextAsync(store, definition, "someone-else", "other");

        // A different owner's commit is not this watch's frame, so the read stays pending.
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await frames.MoveNextAsync());
    }

    [Fact]
    public async Task Watch_DisposeEndsTheEnumeration()
    {
        var store = new DurableStore();
        var definition = Definition();

        var watch = store.Watch(definition, "owner");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        Assert.True(await frames.MoveNextAsync());

        watch.Dispose();

        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task Watch_ResetsToCurrentValue_WhenConsumerFallsBehind()
    {
        var store = new DurableStore();
        var definition = Definition();

        var watch = store.Watch(definition, "owner");

        const int commits = WatchHandle<Dictionary<string, string>>.FrameLimit + 50;
        for (var i = 0; i < commits; i++)
        {
            await CommitTextAsync(store, definition, "owner", i.ToString());
        }

        Assert.True(
            watch.Buffered <= WatchHandle<Dictionary<string, string>>.FrameLimit,
            $"a lagging watch must not buffer past its limit: {watch.Buffered}");

        // Closing the watch lets the buffered frames drain without blocking on a new one.
        watch.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var frames = watch.GetAsyncEnumerator(cts.Token);

        var last = new Dictionary<string, string>();
        while (await frames.MoveNextAsync())
        {
            last = frames.Current;
        }

        Assert.Equal((commits - 1).ToString(), last["text"]);
    }
}
