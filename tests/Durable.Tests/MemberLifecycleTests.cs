using System.Text.Json;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The member lifecycle: hire, activate, pause, resume, drain and replace, each an audited
/// transition on an ordinary document. The tests read the store back rather than trusting the
/// return value, because the contract is that a transition is durable, not that a call returned.
/// </summary>
public sealed class MemberLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hire_CommitsTheBenchedStateAndAnOwnedEntry()
    {
        using var store = DurableStore.CreateEphemeral();

        var result = await DurableMembers.HireAsync(store, "alice", "core", T0, "initial hire");

        Assert.Equal(MemberTransitionOutcome.Succeeded, result.Outcome);
        var stored = DurableMembers.Read(store, "alice");
        Assert.NotNull(stored);
        Assert.Equal(MemberLifecycleState.Benched, stored!.State);
        Assert.Equal("hire", stored.Transition);
        Assert.Equal("initial hire", stored.Reason);
        Assert.Equal(1, stored.Epoch);

        // The transition is audited in the store, not only in the return value: the entry is
        // owned by the member and the document's version is anchored to it.
        var entries = store.EntriesOf(DurableMemberState.OwnerId("alice"));
        var entry = Assert.Single(entries);
        var document = store.Document(DurableMemberState.DocumentKind, DurableMemberState.OwnerId("alice"))!;
        Assert.Equal(entry, Assert.Single(document.Versions).EntryId);
    }

    [Fact]
    public async Task Hire_RefusesToRehireAnExistingMember()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);

        var second = await DurableMembers.HireAsync(store, "alice", "core", T0.AddMinutes(1));

        Assert.Equal(MemberTransitionOutcome.Refused, second.Outcome);
        Assert.Contains("already hired", second.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal did not append a version or an entry.
        Assert.Single(store.EntriesOf(DurableMemberState.OwnerId("alice")));
    }

    [Fact]
    public async Task Activate_FromBenched_IsPersistedAndIncrementsTheEpoch()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);

        var result = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));

        Assert.Equal(MemberTransitionOutcome.Succeeded, result.Outcome);
        var stored = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Active, stored.State);
        Assert.Equal(2, stored.Epoch);
        Assert.Equal("activate", stored.Transition);
        Assert.Equal(2, store.EntriesOf(DurableMemberState.OwnerId("alice")).Count);
    }

    [Fact]
    public async Task InvalidTransition_IsRefusedWithTheCurrentStateAndTheValidStates()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);

        // benched -> benched is not a transition. The refusal has to name what is valid so the
        // operator can act on it without reading the source.
        var result = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Benched, "core", T0.AddMinutes(1));

        Assert.Equal(MemberTransitionOutcome.Refused, result.Outcome);
        Assert.Contains("benched", result.Message, StringComparison.Ordinal);
        Assert.Contains("active", result.Message, StringComparison.Ordinal);
        Assert.Contains("paused", result.Message, StringComparison.Ordinal);
        Assert.Equal(MemberLifecycleState.Benched, DurableMembers.Read(store, "alice")!.State);
        Assert.Single(store.EntriesOf(DurableMemberState.OwnerId("alice")));
    }

    [Fact]
    public async Task TransitionOnAnUnhiredMember_IsRefused()
    {
        using var store = DurableStore.CreateEphemeral();

        var result = await DurableMembers.TransitionAsync(
            store, "nobody", MemberLifecycleState.Active, "core", T0);

        Assert.Equal(MemberTransitionOutcome.Refused, result.Outcome);
        Assert.Contains("not hired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(DurableMembers.Read(store, "nobody"));
    }

    [Fact]
    public async Task StaleEpoch_IsRefusedAndTheStoreKeepsTheNewerTransition()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));

        // The caller read epoch 1 before the activation and tries to pause against it. The
        // activation is the newer truth; a stale write must not overwrite it.
        var stale = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Paused, "core", T0.AddMinutes(2), expectedEpoch: 1);

        Assert.Equal(MemberTransitionOutcome.Refused, stale.Outcome);
        Assert.Contains("epoch", stale.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MemberLifecycleState.Active, DurableMembers.Read(store, "alice")!.State);
    }

    [Fact]
    public async Task Pause_LeavesInFlightWorkInPlaceAndResumeRestoresIt()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));

        var task = await SeedTaskAsync(store, "alice");
        var recorded = await DurableMembers.RecordInFlightAsync(store, "alice", [task.Id], "core", T0.AddMinutes(2));
        Assert.Equal(MemberTransitionOutcome.Succeeded, recorded.Outcome);

        var paused = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Paused, "core", T0.AddMinutes(3));
        Assert.Equal(MemberTransitionOutcome.Succeeded, paused.Outcome);

        // Pause stops admission without draining: the task id is still recorded and the task is
        // still running. Resume restores the same record rather than losing the work.
        var afterPause = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Paused, afterPause.State);
        Assert.Equal([task.Id], afterPause.InFlight);
        Assert.Equal(TaskStatus.Pending, store.Task(task.Id)!.Status);

        var resumed = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(4));
        Assert.Equal(MemberTransitionOutcome.Succeeded, resumed.Outcome);
        var afterResume = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Active, afterResume.State);
        Assert.Equal([task.Id], afterResume.InFlight);
        Assert.Equal(TaskStatus.Pending, store.Task(task.Id)!.Status);
    }

    [Fact]
    public async Task Pause_DoesNotLetASecondClaimantTakeTheMember()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Paused, "core", T0.AddMinutes(2));

        // A paused member is not active, so a second claimant cannot resume it without winning
        // the epoch. The refusal is the duplicate-ownership guard, not a silent no-op.
        var stale = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Active, "other", T0.AddMinutes(3), expectedEpoch: 2);

        Assert.Equal(MemberTransitionOutcome.Refused, stale.Outcome);
        Assert.Equal(MemberLifecycleState.Paused, DurableMembers.Read(store, "alice")!.State);
    }

    [Fact]
    public async Task Drain_CompletesOnlyWhenInFlightWorkIsTerminal()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));
        var task = await SeedTaskAsync(store, "alice");
        await DurableMembers.RecordInFlightAsync(store, "alice", [task.Id], "core", T0.AddMinutes(2));

        var draining = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Draining, "core", T0.AddMinutes(3));
        Assert.Equal(MemberTransitionOutcome.Succeeded, draining.Outcome);
        Assert.Equal(MemberLifecycleState.Draining, DurableMembers.Read(store, "alice")!.State);

        var early = await DurableMembers.CompleteDrainAsync(store, "alice", "core", T0.AddMinutes(4));
        Assert.Equal(MemberTransitionOutcome.Refused, early.Outcome);
        Assert.Contains("in flight", early.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MemberLifecycleState.Draining, DurableMembers.Read(store, "alice")!.State);

        store.Task(task.Id)!.Status = TaskStatus.Terminal;
        await store.CommitAsync(_ => Task.CompletedTask);

        var finished = await DurableMembers.CompleteDrainAsync(store, "alice", "core", T0.AddMinutes(5));
        Assert.Equal(MemberTransitionOutcome.Succeeded, finished.Outcome);
        Assert.Equal(MemberLifecycleState.Benched, DurableMembers.Read(store, "alice")!.State);
    }

    [Fact]
    public async Task Replace_PreservesHandoffContextAndCommitsTheSuccessor()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));
        var task = await SeedTaskAsync(store, "alice");
        await DurableMembers.RecordInFlightAsync(store, "alice", [task.Id], "core", T0.AddMinutes(2));

        var result = await DurableMembers.ReplaceAsync(
            store, "alice", "bob", "Alice was mid-review; the checklist is in the transcript.", "core", T0.AddMinutes(3));

        Assert.Equal(MemberTransitionOutcome.Succeeded, result.Outcome);

        var predecessor = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Replaced, predecessor.State);
        Assert.Equal("bob", predecessor.ReplacedBy);
        Assert.Equal(DurableMemberHandoff.DocumentKind, predecessor.HandoffKind);
        Assert.Equal([task.Id], predecessor.InFlight);

        var handoff = DurableMembers.ReadHandoff(store, "alice");
        Assert.NotNull(handoff);
        Assert.Equal("alice", handoff!.Predecessor);
        Assert.Equal("bob", handoff.Successor);
        Assert.Equal("Alice was mid-review; the checklist is in the transcript.", handoff.Context);
        Assert.Equal([task.Id], handoff.InFlight);

        var successor = DurableMembers.Read(store, "bob")!;
        Assert.Equal(MemberLifecycleState.Benched, successor.State);
        Assert.Equal(1, successor.Epoch);
    }

    [Fact]
    public async Task Replace_RefusesATerminalPredecessorAndAnAlreadyHiredSuccessor()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.HireAsync(store, "bob", "core", T0);

        var taken = await DurableMembers.ReplaceAsync(store, "alice", "bob", "context", "core", T0.AddMinutes(1));
        Assert.Equal(MemberTransitionOutcome.Refused, taken.Outcome);
        Assert.Contains("already hired", taken.Message, StringComparison.OrdinalIgnoreCase);

        await DurableMembers.ReplaceAsync(store, "alice", "carol", "context", "core", T0.AddMinutes(2));
        var again = await DurableMembers.ReplaceAsync(store, "alice", "dave", "context", "core", T0.AddMinutes(3));
        Assert.Equal(MemberTransitionOutcome.Refused, again.Outcome);
        Assert.Contains("replaced", again.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The successor's create-if-absent guard must run before anything is staged, because
    /// <see cref="DurableStore.CommitAsync{T}"/> rolls back on a throw and not on a returned
    /// result. When it ran second, the handoff and the predecessor's `replaced` version were
    /// committed alongside the refusal: the predecessor was terminally replaced, pointing at a
    /// successor that was never hired, while the caller was told "uncertain, retry".
    /// </summary>
    /// <remarks>
    /// The state that triggers it is a successor document that exists but holds no version, which
    /// <see cref="DurableMembers.Read"/> reports as absent (it needs `Latest`) while
    /// <see cref="Transaction.TryCreateDocument{T}"/> reports as present (it only needs the key).
    /// That is reachable whenever a document is created before its first commit -- including a
    /// transaction that threw after creating it.
    /// </remarks>
    [Fact]
    public async Task Replace_UncertainSuccessor_CommitsNothing()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);
        await DurableMembers.TransitionAsync(store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));

        // Create bob's document without committing a version, so Read(bob) is null but the
        // create-if-absent guard fails.
        await store.CommitAsync(tx =>
        {
            Assert.True(tx.TryCreateDocument(DurableMembers.StateDefinition, DurableMemberState.OwnerId("bob")));
            return Task.CompletedTask;
        });
        Assert.Null(DurableMembers.Read(store, "bob"));

        var result = await DurableMembers.ReplaceAsync(
            store, "alice", "bob", "context", "core", T0.AddMinutes(2));

        Assert.Equal(MemberTransitionOutcome.Uncertain, result.Outcome);

        // The whole point: nothing from the failed replacement may survive.
        var predecessor = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Active, predecessor.State);
        Assert.Null(predecessor.ReplacedBy);
        Assert.Null(predecessor.HandoffKind);
        Assert.Null(DurableMembers.ReadHandoff(store, "alice"));
    }

    /// <summary>
    /// The same ordering rule in <see cref="DurableMembers.HireAsync"/>: a refusal must not leave
    /// a transcript entry behind for a hire that never happened.
    /// </summary>
    [Fact]
    public async Task Hire_UncertainMember_CommitsNoEntry()
    {
        using var store = DurableStore.CreateEphemeral();
        await store.CommitAsync(tx =>
        {
            Assert.True(tx.TryCreateDocument(DurableMembers.StateDefinition, DurableMemberState.OwnerId("ghost")));
            return Task.CompletedTask;
        });
        Assert.Null(DurableMembers.Read(store, "ghost"));

        var result = await DurableMembers.HireAsync(store, "ghost", "core", T0);

        Assert.Equal(MemberTransitionOutcome.Uncertain, result.Outcome);
        Assert.Null(DurableMembers.Read(store, "ghost"));
        Assert.Empty(store.EntriesOf(DurableMemberState.OwnerId("ghost")));
    }

    [Fact]
    public async Task AStoreWithNoMemberRecords_StillReadsAndRefusesCleanly()
    {
        // Existing stores remain readable: a store that predates the member document kind has no
        // member documents, and a read is null rather than an error. The new kinds are additive.
        using var store = DurableStore.CreateEphemeral();
        await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));

        Assert.Null(DurableMembers.Read(store, "alice"));
        Assert.Null(DurableMembers.ReadHandoff(store, "alice"));
        Assert.DoesNotContain(store.Documents, document => document.Kind == DurableMemberState.DocumentKind);
    }

    [Fact]
    public async Task AnExistingStoreReadsAfterTheMemberKindIsAdded()
    {
        // The persisted tier is the real test: a store written before a member was hired must
        // still open and read, and the member document must append without rewriting the file.
        var directory = Path.Combine(Path.GetTempPath(), $"durable-member-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.jsonl");
        try
        {
            using (var first = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl }))
            {
                await first.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
            }

            string before;
            using (var reopened = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl }))
            {
                before = File.ReadAllText(path);
                Assert.Null(DurableMembers.Read(reopened, "alice"));
                await DurableMembers.HireAsync(reopened, "alice", "core", T0);
            }

            using var third = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl });
            Assert.Equal(MemberLifecycleState.Benched, DurableMembers.Read(third, "alice")!.State);
            Assert.Single(third.Conversations);

            // The append is additive: the records that were already there are still there.
            var after = File.ReadAllText(path);
            Assert.StartsWith(before.TrimEnd(), after.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnreadableState_IsReportedUncertainRatherThanOverwritten()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableMembers.HireAsync(store, "alice", "core", T0);

        // A store written by a newer build. The reader must not guess, and must not overwrite it.
        var owner = DurableMemberState.OwnerId("alice");
        var document = store.Document(DurableMemberState.DocumentKind, owner)!;
        var json = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["stateText"] = "quarantined",
            ["epoch"] = 7,
            ["since"] = T0,
        }, DurableJson.Options);
        document.Versions.Add(new DocumentVersion("e-newer", json, long.MaxValue));

        var state = DurableMembers.Read(store, "alice")!;
        Assert.Equal(MemberLifecycleState.Uncertain, state.State);

        var result = await DurableMembers.TransitionAsync(
            store, "alice", MemberLifecycleState.Active, "core", T0.AddMinutes(1));
        Assert.Equal(MemberTransitionOutcome.Uncertain, result.Outcome);
        Assert.Equal("quarantined", DurableMembers.Read(store, "alice")!.StateText);
    }

    private static async Task<DurableTaskRecord> SeedTaskAsync(DurableStore store, string owner)
    {
        var definition = DurableTask.DefinePhases<Dictionary<string, int>, Dictionary<string, string>>(
            "test.member-work",
            () => new Dictionary<string, string> { ["phase"] = "wait" },
            new Dictionary<string, Func<Dictionary<string, int>, Dictionary<string, string>, TaskRuntime, Task<TaskCommit>>>
            {
                ["wait"] = (_, _, _) => Task.FromResult(TaskCommit.Running(
                    DurableJson.ToElement(new Dictionary<string, string> { ["phase"] = "wait" }))),
            });

        return await store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, DurableJson.ToElement(new Dictionary<string, int> { ["owner"] = 1 }))));
    }
}
