// Cross-port shift fixtures; uses the canonical implementation, not a port.
#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../Durable.cs

using System.Text.Json;
using System.Text.Json.Nodes;
using Durable;

// Modes:
//   create <file>          claim, write a `running` handoff, release-write a `handedOff` claim
//   create-legacy <file>   the same, but the handoff carries the previous build's "Running"
//   inspect <file>         print the claim and handoff as C# reads them (JSON on stdout)
var file = args[1];
var key = "sessions/po-shift";
var t0 = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
var t1 = t0.AddMinutes(1);
var t2 = t0.AddMinutes(2);

DurableStore Open() => DurableStore.Create(new DurableStoreOptions
{
    Path = file,
    Format = DurableStoreFormat.Jsonl,
});

switch (args[0])
{
    case "create":
    case "create-legacy":
    {
        using var store = Open();
        // Attempt 1 is running: claim, running handoff. That is the record the ports must read
        // as `running` so a different session is refused.
        await DurableShifts.ClaimAsync(store, key, "run-a", t0, "po", "session-a");
        if (args[0] == "create")
        {
            await DurableShifts.WriteHandoffAsync(
                store, key, "run-a", 1, t0, ShiftHandoffPhase.Running, selected: "issue-225", next: "resume");
        }
        else
        {
            await WriteLegacyRunningHandoffAsync(store, key);
        }
        Console.WriteLine("{\"mode\":\"" + args[0] + "\"}");
        break;
    }
    case "inspect":
    {
        using var store = Open();
        var claim = DurableShifts.ReadClaim(store, key);
        var handoff = DurableShifts.ReadHandoff(store, key);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            claimOwner = claim?.Owner,
            claimAttempt = claim?.Attempt,
            claimStatus = claim is null ? null : DurableShifts.Text(claim.Status),
            claimStatusText = claim?.StatusText,
            handoffAttempt = handoff?.Attempt,
            handoffPhase = handoff is null ? null : ShiftHandoffPhaseConverter.ToText(handoff.Phase),
            handoffSelected = handoff?.Selected,
            handoffNext = handoff?.Next,
        }, DurableJson.Options));
        break;
    }
    default:
        throw new InvalidOperationException($"unknown mode '{args[0]}'");
}

static async Task WriteLegacyRunningHandoffAsync(DurableStore store, string key)
{
    var raw = new JsonObject
    {
        ["key"] = key,
        ["attempt"] = 1,
        ["owner"] = "run-a",
        ["phase"] = "Running",
        ["selected"] = "issue-225",
        ["next"] = "resume",
        ["writtenAt"] = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero).ToString("O"),
    };
    await store.CommitAsync(tx =>
    {
        var ownerId = DurableShiftHandoff.OwnerId(key);
        var definition = new DocumentDefinition<JsonNode>(
            DurableShiftHandoff.DocumentKind, DocumentHistory.Rewindable, ForkPolicy.Initial);
        if (!tx.TryCreateDocument(definition, ownerId))
        {
            throw new InvalidOperationException("the legacy handoff fixture expected a fresh document");
        }
        tx.CommitDocument(definition, ownerId, raw, tx.AppendEntry(ownerId, "shift-handoff"));
        return Task.CompletedTask;
    });
}
