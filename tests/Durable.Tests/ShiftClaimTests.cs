using System.Text.Json;
using System.Text.Json.Nodes;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The durable shift claim and handoff: create-if-absent claiming, conflict reporting, resumption
/// from a committed handoff, and fail-closed behaviour on a state this build cannot read.
/// </summary>
public sealed class ShiftClaimTests
{
    private const string Key = "sessions/po-shift";

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(2);

    [Fact]
    public async Task Claim_CreatesTheRecordWithTheOwnerTokenAndAttemptOne()
    {
        using var store = DurableStore.CreateEphemeral();

        var result = await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        Assert.Equal(ShiftClaimOutcome.Acquired, result.Outcome);
        Assert.NotNull(result.Claim);
        Assert.Equal("run-a", result.Claim!.Owner);
        Assert.Equal(1, result.Claim.Attempt);
        Assert.Null(result.Claim.ResumedFrom);
        Assert.Equal(ShiftClaimStatus.Active, result.Claim.Status);

        // The store, not just the return value, holds it: a second reader sees the claim.
        var stored = DurableShifts.ReadClaim(store, Key);
        Assert.NotNull(stored);
        Assert.Equal("run-a", stored!.Owner);
        Assert.Equal(1, stored.Attempt);
    }

    [Fact]
    public async Task Claim_RefusesADifferentOwnerWhileTheShiftIsLiveAndNamesTheHolder()
    {
        using var store = DurableStore.CreateEphemeral();
        var first = await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        Assert.Equal(ShiftClaimOutcome.Acquired, first.Outcome);

        var second = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");

        // The conflict path is the whole point: the loser is told who holds it and the store is
        // not touched, so no duplicate run is admitted.
        Assert.Equal(ShiftClaimOutcome.Conflict, second.Outcome);
        Assert.Equal("run-a", second.Claim!.Owner);
        Assert.Contains("run-a", second.Message, StringComparison.Ordinal);

        var stored = DurableShifts.ReadClaim(store, Key)!;
        Assert.Equal("run-a", stored.Owner);
        Assert.Equal(1, stored.Attempt);
    }

    [Fact]
    public async Task Claim_IsIdempotentForTheOwnerThatAlreadyHoldsIt()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        var again = await DurableShifts.ClaimAsync(store, Key, "run-a", T1, "po", "session-a");

        Assert.Equal(ShiftClaimOutcome.Acquired, again.Outcome);
        Assert.Equal(1, again.Claim!.Attempt);
    }

    [Fact]
    public async Task Claim_DoesNotTakeOverAnInterruptedShiftFromAnotherSession()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        // The in-flight handoff is written before the turn. Another session cannot treat that as
        // permission to run, because the owner may still be alive and mid-turn.
        var wrote = await DurableShifts.WriteHandoffAsync(
            store, Key, "run-a", 1, T0, ShiftHandoffPhase.Running, selected: "issue-225");
        Assert.True(wrote);

        var other = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");

        Assert.Equal(ShiftClaimOutcome.Conflict, other.Outcome);
        Assert.Contains("in-flight", other.Message, StringComparison.Ordinal);
        Assert.Equal(1, DurableShifts.ReadClaim(store, Key)!.Attempt);
    }

    [Fact]
    public async Task Claim_ResumesAnInterruptedShiftForTheSameSessionFromItsHandoff()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        await DurableShifts.WriteHandoffAsync(
            store, Key, "run-a", 1, T0, ShiftHandoffPhase.Running, selected: "issue-225", next: "write the test");

        // The same session comes back after the process died. Its own in-flight handoff is its
        // resume point: the work stopped at a known place, so resuming is not duplicating it.
        var resumed = await DurableShifts.ClaimAsync(store, Key, "run-a-restarted", T2, "po", "session-a");

        Assert.Equal(ShiftClaimOutcome.Acquired, resumed.Outcome);
        Assert.Equal(2, resumed.Claim!.Attempt);
        Assert.Equal(1, resumed.Claim.ResumedFrom);

        var handoff = DurableShifts.ReadHandoff(store, Key)!;
        Assert.Equal(1, handoff.Attempt);
        Assert.Equal("issue-225", handoff.Selected);
        Assert.Equal("write the test", handoff.Next);
    }

    [Fact]
    public async Task Release_RefusesWithoutACommittedHandoffAndLeavesTheClaimHeld()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        var release = await DurableShifts.ReleaseAsync(store, Key, "run-a", T1);

        Assert.Equal(ShiftClaimOutcome.Conflict, release.Outcome);
        Assert.Contains("handoff", release.Message, StringComparison.Ordinal);
        Assert.Equal(ShiftClaimStatus.Active, DurableShifts.ReadClaim(store, Key)!.Status);
    }

    [Fact]
    public async Task Release_RefusesWhenSomeoneElseOwnsTheShift()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        await DurableShifts.WriteHandoffAsync(store, Key, "run-a", 1, T0);

        var release = await DurableShifts.ReleaseAsync(store, Key, "run-b", T1);

        Assert.Equal(ShiftClaimOutcome.Conflict, release.Outcome);
        Assert.Contains("run-a", release.Message, StringComparison.Ordinal);
        Assert.Equal(ShiftClaimStatus.Active, DurableShifts.ReadClaim(store, Key)!.Status);
    }

    [Fact]
    public async Task Release_PersistsTheHandoffBeforeTheReleasedClaimAndAllowsASuccessor()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        await DurableShifts.WriteHandoffAsync(
            store, Key, "run-a", 1, T0, ShiftHandoffPhase.HandedOff,
            selected: "issue-225", achieved: "claim", remaining: "tests", next: "run them");

        var release = await DurableShifts.ReleaseAsync(store, Key, "run-a", T1);

        Assert.Equal(ShiftClaimOutcome.Acquired, release.Outcome);
        var released = DurableShifts.ReadClaim(store, Key)!;
        Assert.Equal(ShiftClaimStatus.Released, released.Status);
        Assert.Equal(T1, released.ReleasedAt);

        // The handoff is durable and still readable after the release, which is what makes the
        // released claim resumable rather than merely free.
        var handoff = DurableShifts.ReadHandoff(store, Key)!;
        Assert.Equal("run them", handoff.Next);

        var successor = await DurableShifts.ClaimAsync(store, Key, "run-b", T2, "po", "session-b");
        Assert.Equal(ShiftClaimOutcome.Acquired, successor.Outcome);
        Assert.Equal(2, successor.Claim!.Attempt);
        Assert.Equal("run-b", successor.Claim.Owner);
    }

    [Fact]
    public async Task Release_IsIdempotentForTheOwnerThatAlreadyReleased()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        await DurableShifts.WriteHandoffAsync(store, Key, "run-a", 1, T0);
        await DurableShifts.ReleaseAsync(store, Key, "run-a", T1);

        var again = await DurableShifts.ReleaseAsync(store, Key, "run-a", T2);

        Assert.Equal(ShiftClaimOutcome.Acquired, again.Outcome);
        Assert.Equal(T1, again.Claim!.ReleasedAt);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("")]
    public async Task Claim_FailsReadOnlyOnAStatusThisBuildCannotRead(string statusText)
    {
        using var store = DurableStore.CreateEphemeral();
        await WriteClaimLiteralAsync(store, statusText);

        var claim = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");
        var release = await DurableShifts.ReleaseAsync(store, Key, "run-b", T1);

        Assert.Equal(ShiftClaimOutcome.ReadOnly, claim.Outcome);
        Assert.Equal(ShiftClaimOutcome.ReadOnly, release.Outcome);
        Assert.Equal(statusText, (await ReadRawStatusAsync(store)) ?? statusText);
    }

    [Fact]
    public async Task Claim_FailsReadOnlyWhenClaimAndHandoffDisagreeOnTheAttempt()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        // A handoff for attempt 7 against a claim at attempt 1 is not a resumable record; it is
        // a store someone edited, and the safe reading is "do not know".
        await store.CommitAsync(tx =>
        {
            var draft = tx.Doc(DurableShifts.HandoffDefinition, DurableShiftHandoff.OwnerId(Key));
            draft.Value = new DurableShiftHandoff
            {
                Key = Key,
                Attempt = 7,
                Owner = "run-a",
                Phase = ShiftHandoffPhase.HandedOff,
                WrittenAt = T0,
            };
            draft.Save(tx.AppendEntry(DurableShiftHandoff.OwnerId(Key), "shift-handoff"));
            return Task.CompletedTask;
        });

        var claim = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");

        Assert.Equal(ShiftClaimOutcome.ReadOnly, claim.Outcome);
        Assert.Contains("disagree", claim.Message, StringComparison.Ordinal);
        Assert.Equal(1, DurableShifts.ReadClaim(store, Key)!.Attempt);
    }

    [Fact]
    public async Task Claim_RefusesAHandoffThatWasNotWrittenByTheHolder()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        var wrote = await DurableShifts.WriteHandoffAsync(store, Key, "run-b", 1, T0);

        Assert.False(wrote);
        Assert.Null(DurableShifts.ReadHandoff(store, Key));
    }

    [Fact]
    public async Task Claim_SurvivesAReopenFromDiskAndReleasesThere()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-shift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.jsonl");
        try
        {
            using (var store = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl }))
            {
                await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
                await DurableShifts.WriteHandoffAsync(
                    store, Key, "run-a", 1, T0, ShiftHandoffPhase.HandedOff,
                    selected: "issue-225", remaining: "restart evidence");
            }

            // A second process is a second store over the same file. It reads the claim and the
            // handoff back, which is what makes recovery from a killed process possible at all.
            using (var reopened = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl }))
            {
                var claim = DurableShifts.ReadClaim(reopened, Key)!;
                Assert.Equal("run-a", claim.Owner);
                Assert.Equal(1, claim.Attempt);

                var handoff = DurableShifts.ReadHandoff(reopened, Key)!;
                Assert.Equal("issue-225", handoff.Selected);
                Assert.Equal("restart evidence", handoff.Remaining);

                var resumed = await DurableShifts.ClaimAsync(reopened, Key, "run-b", T1, "po", "session-b");
                Assert.Equal(ShiftClaimOutcome.Acquired, resumed.Outcome);
                Assert.Equal(1, resumed.Claim!.ResumedFrom);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Claim_AddsNoWireFormatChangeToAnExistingStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-shift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.jsonl");
        try
        {
            using (var store = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl }))
            {
                // A pre-existing document the feature never touches.
                await store.CommitAsync(tx =>
                {
                    var doc = tx.Doc(new DocumentDefinition<string>("test.existing"), "existing-owner");
                    doc.Value = "before";
                    doc.Save(tx.AppendEntry("existing-owner", "note"));
                    return Task.CompletedTask;
                });

                await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
            }

            // Every line is still the same envelope version, and the shift records are ordinary
            // document records inside it: a new kind, not a new record type.
            foreach (var line in File.ReadAllLines(path).Where(line => line.Length > 0))
            {
                var record = JsonNode.Parse(line)!.AsObject();
                Assert.Equal(1, (long?)record["v"]);
                Assert.NotNull(record["type"]);
                Assert.NotNull(record["payload"]);
            }

            using var reopened = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl });
            var existing = reopened.Document("test.existing", "existing-owner");
            Assert.Equal("before", existing!.Latest!.Value.GetString());
            Assert.Equal("run-a", DurableShifts.ReadClaim(reopened, Key)!.Owner);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Claim_ATwoWayRaceAdmitsExactlyOneOwner()
    {
        using var store = DurableStore.CreateEphemeral();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i =>
                DurableShifts.ClaimAsync(store, Key, $"run-{i}", T0, "po", $"session-{i}")));

        Assert.Single(results, result => result.Outcome == ShiftClaimOutcome.Acquired);
        Assert.All(results.Where(result => result.Outcome != ShiftClaimOutcome.Acquired),
            result => Assert.Equal(ShiftClaimOutcome.Conflict, result.Outcome));
        Assert.Equal(1, DurableShifts.ReadClaim(store, Key)!.Attempt);
    }

    [Fact]
    public async Task Claim_WritesNothingWhenTheTransactionRollsBack()
    {
        using var store = DurableStore.CreateEphemeral();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(async tx =>
            {
                var ownerId = DurableShiftClaim.OwnerId(Key);
                Assert.True(tx.TryCreateDocument(DurableShifts.ClaimDefinition, ownerId));
                tx.CommitDocument(DurableShifts.ClaimDefinition, ownerId, new DurableShiftClaim
                {
                    Key = Key,
                    Owner = "run-a",
                    Status = ShiftClaimStatus.Active,
                    StatusText = "active",
                    Attempt = 1,
                    ClaimedAt = T0,
                }, tx.AppendEntry(ownerId, "shift-claim"));
                await Task.Yield();
                throw new InvalidOperationException("simulated crash mid-claim");
            }));

        // Rollback must leave nothing behind: no document, no entry, no version. Otherwise the
        // next claimant would read a placeholder it never committed as an existing claim.
        Assert.Null(store.Document(DurableShiftClaim.DocumentKind, DurableShiftClaim.OwnerId(Key)));
        Assert.Empty(store.Entries(DurableShiftClaim.OwnerId(Key)));

        var claim = await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");
        Assert.Equal(ShiftClaimOutcome.Acquired, claim.Outcome);
        Assert.Equal(1, claim.Claim!.Attempt);
    }

    private static async Task WriteClaimLiteralAsync(DurableStore store, string statusText)
    {
        await store.CommitAsync(tx =>
        {
            var ownerId = DurableShiftClaim.OwnerId(Key);
            var draft = tx.Doc(DurableShifts.ClaimDefinition, ownerId);
            draft.Value = new DurableShiftClaim
            {
                Key = Key,
                Member = "po",
                Owner = "run-a",
                SessionId = "session-a",
                Status = ShiftClaimStatus.Active,
                StatusText = statusText,
                Attempt = 1,
                ClaimedAt = T0,
            };
            draft.Save(tx.AppendEntry(ownerId, "shift-claim"));
            return Task.CompletedTask;
        });
    }

    private static Task<string?> ReadRawStatusAsync(DurableStore store)
    {
        var document = store.Document(DurableShiftClaim.DocumentKind, DurableShiftClaim.OwnerId(Key));
        return Task.FromResult(document?.Latest is { } latest
            ? latest.Deserialize<DurableShiftClaim>(DurableJson.Options)?.StatusText
            : null);
    }
}
