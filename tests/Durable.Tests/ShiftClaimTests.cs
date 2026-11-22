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

    /// <summary>
    /// The canonical wire spelling of a handoff phase is the ports' lowercase form, in both
    /// directions: what C# writes is what TypeScript and Python read, and what they write is what
    /// this build reads.
    /// </summary>
    /// <remarks>
    /// The previous build wrote the enum's own names, which the ports read as <c>handedOff</c>.
    /// For a <c>running</c> handoff that misreading let a second session take a live shift, which
    /// is duplicate execution — the exact failure the record exists to prevent. The wire text is
    /// asserted directly rather than only through the typed property, because the property would
    /// agree with itself either way.
    /// </remarks>
    [Fact]
    public async Task Handoff_WritesAndReadsTheCanonicalLowercasePhaseSpelling()
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
                    store, Key, "run-a", 1, T0, ShiftHandoffPhase.Running, selected: "issue-225");
            }

            var handoffVersion = Assert.Single(File.ReadAllLines(path), line =>
                line.Contains("\"type\":\"document-version\"", StringComparison.Ordinal)
                && line.Contains("\"durable.shift-handoff\"", StringComparison.Ordinal));
            var payload = JsonNode.Parse(handoffVersion)!["payload"]!["value"]!;
            Assert.Equal("running", payload["phase"]!.GetValue<string>());

            using var reopened = DurableStore.Create(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl });
            Assert.Equal(ShiftHandoffPhase.Running, DurableShifts.ReadHandoff(reopened, Key)!.Phase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A store written by the previous build holds <c>"Running"</c> / <c>"HandedOff"</c>. The new
    /// build must still read it, and must still refuse a different session a live shift: reading
    /// the old spelling as <c>handedOff</c> would let a second session duplicate the run.
    /// </summary>
    [Fact]
    public async Task Handoff_ReadsThePreviousBuildCapitalisedSpellingAndStillRefusesADifferentSession()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        // Written exactly as the previous C# build wrote it: the enum's capitalized name.
        await WriteHandoffLiteralAsync(store, "Running");

        Assert.Equal(ShiftHandoffPhase.Running, DurableShifts.ReadHandoff(store, Key)!.Phase);

        var other = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");
        Assert.Equal(ShiftClaimOutcome.Conflict, other.Outcome);
        Assert.Contains("in-flight", other.Message, StringComparison.Ordinal);

        // The same session still resumes from it: backward compatibility must not be bought by
        // making the old record unresumable.
        var resumed = await DurableShifts.ClaimAsync(store, Key, "run-a-restarted", T2, "po", "session-a");
        Assert.Equal(ShiftClaimOutcome.Acquired, resumed.Outcome);
        Assert.Equal(2, resumed.Claim!.Attempt);

        // And the literal is read as the phase it says, not the permissive fallback.
        Assert.Equal(ShiftHandoffPhase.HandedOff, ShiftHandoffPhaseConverter.FromText("HandedOff"));
        Assert.Equal(ShiftHandoffPhase.Running, ShiftHandoffPhaseConverter.FromText("Running"));
        Assert.Equal(ShiftHandoffPhase.Running, ShiftHandoffPhaseConverter.FromText("running"));
        Assert.Equal(ShiftHandoffPhase.HandedOff, ShiftHandoffPhaseConverter.FromText("handedOff"));
        Assert.Equal("handedOff", ShiftHandoffPhaseConverter.ToText(ShiftHandoffPhase.HandedOff));
        Assert.Equal("running", ShiftHandoffPhaseConverter.ToText(ShiftHandoffPhase.Running));
    }

    /// <summary>
    /// A claim whose <c>status</c> is a value this build has never heard of reads as
    /// <see cref="ShiftClaimStatus.Uncertain"/>: the typed property is a view of the text, and an
    /// unknown text is "unknown", not the default.
    /// </summary>
    [Fact]
    public void Claim_StatusIsAComputedViewOfTheStoredText()
    {
        var active = new DurableShiftClaim { Key = Key, Owner = "run-a", StatusText = "active", Attempt = 1 };
        var released = new DurableShiftClaim { Key = Key, Owner = "run-a", StatusText = "released", Attempt = 1 };
        var unknown = new DurableShiftClaim { Key = Key, Owner = "run-a", StatusText = "suspended", Attempt = 1 };
        var missing = new DurableShiftClaim { Key = Key, Owner = "run-a", Attempt = 1 };

        Assert.Equal(ShiftClaimStatus.Active, active.Status);
        Assert.Equal(ShiftClaimStatus.Released, released.Status);
        Assert.Equal(ShiftClaimStatus.Uncertain, unknown.Status);
        Assert.Equal(ShiftClaimStatus.Uncertain, missing.Status);

        // No text is never read as the typed property's default: a record this build cannot
        // verify is unknown, not active.
        Assert.Equal((ShiftClaimStatus.Uncertain, (string?)null), DurableShifts.ReadStatus(null));
        Assert.Equal((ShiftClaimStatus.Uncertain, (string?)null), DurableShifts.ReadStatus("  "));
        Assert.Equal((ShiftClaimStatus.Uncertain, (string?)"suspended"), DurableShifts.ReadStatus("suspended"));
        Assert.Equal("active", DurableShifts.Text(ShiftClaimStatus.Active));
        Assert.Equal("released", DurableShifts.Text(ShiftClaimStatus.Released));
    }

    /// <summary>
    /// A wire record carrying a <c>status</c> value this build does not know deserializes without
    /// throwing, and the claim operations refuse it read-only.
    /// </summary>
    /// <remarks>
    /// This is the defect that made <c>Uncertain</c> unreachable: a typed enum deserializer throws
    /// <see cref="JsonException"/> before the claim reader can answer "unknown". The assertion is
    /// on the operations, because a throw there is the failure mode being fixed.
    /// </remarks>
    [Theory]
    [InlineData("suspended")]
    [InlineData("archived")]
    public async Task Claim_WithAnUnknownWireStatusFailsReadOnlyInBothDirections(string unknownStatus)
    {
        using var store = DurableStore.CreateEphemeral();

        // A newer build wrote a status this one has no name for. It is the `status` field that
        // carries it — a typed enum deserializer threw here before the claim reader could answer
        // "unknown", which is why `Uncertain` was unreachable.
        var raw = new JsonObject
        {
            ["key"] = Key,
            ["member"] = "po",
            ["owner"] = "run-a",
            ["sessionId"] = "session-a",
            ["status"] = unknownStatus,
            ["statusText"] = unknownStatus,
            ["attempt"] = 1,
            ["claimedAt"] = T0.ToString("O"),
        };

        // The document is written through a JsonElement-typed definition over the same kind, which
        // is exactly how a foreign build's bytes reach this one: the record is valid JSON of the
        // right kind, and its `status` field is a value this build's vocabulary lacks.
        await store.CommitAsync(tx =>
        {
            var ownerId = DurableShiftClaim.OwnerId(Key);
            var definition = new DocumentDefinition<JsonNode>(
                DurableShiftClaim.DocumentKind, DocumentHistory.Rewindable, ForkPolicy.Initial);
            Assert.True(tx.TryCreateDocument(definition, ownerId));
            tx.CommitDocument(definition, ownerId, raw, tx.AppendEntry(ownerId, "shift-claim"));
            return Task.CompletedTask;
        });

        // Reading the record does not throw; it reports the unknown state.
        var stored = DurableShifts.ReadClaim(store, Key);
        Assert.NotNull(stored);
        Assert.Equal(ShiftClaimStatus.Uncertain, stored!.Status);
        Assert.Equal(unknownStatus, stored.StatusText);

        var claim = await DurableShifts.ClaimAsync(store, Key, "run-b", T1, "po", "session-b");
        var release = await DurableShifts.ReleaseAsync(store, Key, "run-a", T1);

        Assert.Equal(ShiftClaimOutcome.ReadOnly, claim.Outcome);
        Assert.Equal(ShiftClaimOutcome.ReadOnly, release.Outcome);

        // The record is reported, never rewritten into a value this build understands.
        Assert.Equal(unknownStatus, DurableShifts.ReadClaim(store, Key)!.StatusText);
    }

    /// <summary>
    /// A claim record carries no computed <c>grants</c> field: it is not a stored property in any
    /// port, and writing it here would be an undocumented wire asymmetry nobody reads.
    /// </summary>
    [Fact]
    public async Task Claim_DoesNotPersistTheComputedGrantsProperty()
    {
        using var store = DurableStore.CreateEphemeral();
        await DurableShifts.ClaimAsync(store, Key, "run-a", T0, "po", "session-a");

        var document = store.Document(DurableShiftClaim.DocumentKind, DurableShiftClaim.OwnerId(Key))!;
        var payload = JsonNode.Parse(document.Latest!.Value.GetRawText())!.AsObject();

        Assert.Equal(1, payload["attempt"]!.GetValue<long>());
        Assert.False(payload.ContainsKey("grants"), "the computed `grants` property must not be written");
        Assert.Equal(1, DurableShifts.ReadClaim(store, Key)!.Grants);
    }

    private static async Task WriteHandoffLiteralAsync(DurableStore store, string phase)
    {
        // Written the way the previous C# build wrote it: a capitalized phase text on the wire.
        // Going through the converter would write the canonical spelling, so the record is
        // committed as a raw value with the old text in place.
        var raw = new JsonObject
        {
            ["key"] = Key,
            ["attempt"] = 1,
            ["owner"] = "run-a",
            ["phase"] = phase,
            ["selected"] = "issue-225",
            ["writtenAt"] = T0.ToString("O"),
        };

        await store.CommitAsync(tx =>
        {
            var ownerId = DurableShiftHandoff.OwnerId(Key);
            var definition = new DocumentDefinition<JsonNode>(
                DurableShiftHandoff.DocumentKind, DocumentHistory.Rewindable, ForkPolicy.Initial);
            Assert.True(tx.TryCreateDocument(definition, ownerId));
            tx.CommitDocument(definition, ownerId, raw, tx.AppendEntry(ownerId, "shift-handoff"));
            return Task.CompletedTask;
        });
    }
}
