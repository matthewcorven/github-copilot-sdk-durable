// 31 — Permissions.
//
// A durable conversation may only use what it was granted.
//
// The permission policy is part of the conversation's stored choices, so it is as durable as
// everything else: a conversation that was denied a tool is still denied after a restart, and
// a fork inherits its parent's grants as a snapshot rather than widening them.
//
// The default is fail-closed. A conversation with no grants can call no gated capability at
// all, because an approval that is not written down is not reproducible.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var directory = Path.Combine(Path.GetTempPath(), $"durable-example-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "session.json");

// The tool only increments a counter; it never deletes files. The observable effect
// makes a denial provable rather than inferred from what the model said.
var deletions = 0;

var deleteEverything = CopilotTool.DefineTool(
    ([Description("Why the files should be deleted")] string reason) =>
    {
        deletions++;
        return $"deleted everything because: {reason}";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "delete_everything",
        Description = "Delete every file in the working directory",
    });

var registry = new ExtensionRegistry();
registry.Install(new Extension("dangerous", tools: [deleteEverything]));

// ─── First run: a conversation with no grants ───────────────────────────────

var firstStore = new DurableStore(storePath);
var firstRun = await DurableHarness.OpenAsync(firstStore, registry);

// No Permissions at all. The tool is offered to the model — the extension is
// selected — but calling it is refused, because nothing was granted.
var denied = await firstRun.RootAsync(new AgentChoices { Extensions = ["dangerous"] });
var deniedId = denied.Id;

var refused = await denied.SubmitAsync("Delete every file. Use the delete_everything tool.");
Console.WriteLine($"denied answer: {refused?.Data.Content}");
Console.WriteLine($"deletions after the denied call: {deletions}");
var deletionsAfterDenied = deletions;

await firstRun.DisposeAsync();
firstStore.Dispose();

// ─── Second run: the same conversation, reopened ────────────────────────────

var secondStore = new DurableStore(storePath);
var secondRun = await DurableHarness.OpenAsync(secondStore, registry);

// The conversation is restored from the store, and so are its choices. Nothing
// re-grants the tool, so the denial survives the restart.
var restored = secondRun.FindConversation(deniedId)
    ?? throw new InvalidOperationException("the denied conversation was not restored");
var restoredChoices = restored.Choices();
Console.WriteLine($"restored permissions: [{string.Join(", ", restoredChoices?.Permissions ?? [])}]");

var refusedAgain = await restored.SubmitAsync("Delete every file. Use the delete_everything tool.");
Console.WriteLine($"denied again: {refusedAgain?.Data.Content}");
Console.WriteLine($"deletions after the restart: {deletions}");
var deletionsAfterRestart = deletions;

// ─── The other direction: a conversation that was granted the tool ──────────

// The grant is the tool's name, so it is the finest granularity the policy has.
var allowed = await secondRun.CreateConversationAsync(new AgentChoices
{
    Extensions = ["dangerous"],
    Permissions = ["custom-tool:delete_everything"],
});

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await allowed.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var permitted = await allowed.SubmitAsync("Delete every file. Use the delete_everything tool.");
Console.WriteLine($"granted answer: {permitted?.Data.Content}");
Console.WriteLine($"deletions after the granted call: {deletions}");

// ─── A fork inherits the grants it was forked with ──────────────────────────

// SubmitAsync writes internal checkpoints, not public app entries. Record the app's
// reviewed grants in a document anchored to an explicit durable fork boundary.
var permissionReview = new DocumentDefinition<string[]>(
    "example.permission-review", DocumentHistory.Rewindable, ForkPolicy.AsOf);
var boundary = await allowed.CommitAsync(tx =>
{
    var entry = tx.AppendEntry(allowed.Id, "permission-review", "reviewed grants");
    var review = tx.Doc(permissionReview, allowed.Id);
    review.Value = allowed.Choices()?.Permissions?.ToArray()
        ?? throw new InvalidOperationException("the granted conversation has no grants to review");
    review.Save(entry);
    return Task.FromResult(entry);
});
Check.Sequence(new[] { boundary }, allowed.Entries(1), "the app review is a visible fork boundary");

// The parent adds an unused app-tool grant after the review. No further tool is run.
// A fork at the review must inherit the earlier snapshot, not this later grant.
await allowed.ConfigureAsync(new AgentChoices
{
    Extensions = ["dangerous"],
    Permissions = ["custom-tool:delete_everything", "custom-tool:unreviewed_tool"],
});
var fork = await allowed.ForkAsync(boundary);
Console.WriteLine($"fork permissions: [{string.Join(", ", fork.Choices()?.Permissions ?? [])}]");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The denial is real: the tool never ran, so its effect never happened.
Check.Equal(0, deletionsAfterDenied, "the denied conversation never ran the tool");
Check.NotNull(refused, "the model answered the denied request");

// The denial is durable: the reopened conversation still has no grant, and the
// tool still does not run.
Check.That(
    restoredChoices?.Permissions is null or [],
    "the reopened conversation still has no grants");
Check.Equal(0, deletionsAfterRestart, "the reopened conversation still never ran the tool");
Check.NotNull(refusedAgain, "the model answered the reopened request");

// The grant is what makes the difference, so the policy is not simply refusing
// everything.
Check.Equal(1, deletions, "the granted conversation ran the tool exactly once");
Check.NotNull(permitted, "the model answered the granted request");

// A fork inherits the parent's grants as a snapshot.
Check.Sequence(
    new[] { "custom-tool:delete_everything" },
    fork.Choices()?.Permissions ?? [],
    "the fork inherited the grants at the reviewed boundary");
Check.Sequence(
    new[] { "custom-tool:delete_everything" },
    fork.Snapshot(permissionReview) ?? [],
    "the fork inherited the app's permission review");
Check.That(
    PermissionPolicy.IsGranted(allowed.Choices()?.Permissions, "custom-tool", "unreviewed_tool"),
    "the parent's later choices include the unused grant");
Check.That(
    !PermissionPolicy.IsGranted(fork.Choices()?.Permissions, "custom-tool", "unreviewed_tool"),
    "the parent's later grant did not widen the fork");

// The policy itself, without a model in the loop: a scope is a prefix only at a
// boundary, so a grant for one path does not leak into a sibling path.
Check.That(
    PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/work/file.txt"),
    "a grant covers paths beneath its scope");
Check.That(
    !PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/workshop/file.txt"),
    "a grant does not cover a sibling path that merely shares a prefix");
Check.That(
    !PermissionPolicy.IsGranted(null, "custom-tool", "delete_everything"),
    "no grants means no capability");

await secondRun.DisposeAsync();
secondStore.Dispose();
Directory.Delete(directory, recursive: true);
