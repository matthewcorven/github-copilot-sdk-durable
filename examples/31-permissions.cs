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

// The tool has an effect the example can observe, so a denial is provable rather than
// inferred from what the model said.
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

await firstRun.DisposeAsync();
firstStore.Dispose();

// ─── Second run: the same conversation, reopened ────────────────────────────

var secondStore = new DurableStore(storePath);
var secondRun = await DurableHarness.OpenAsync(secondStore, registry);

// The conversation is restored from the store, and so are its choices. Nothing
// re-grants the tool, so the denial survives the restart.
var restored = secondRun.FindConversation(deniedId)!;
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

var permitted = await allowed.SubmitAsync("Delete every file. Use the delete_everything tool.");
Console.WriteLine($"granted answer: {permitted?.Data.Content}");
Console.WriteLine($"deletions after the granted call: {deletions}");

// ─── A fork inherits the grants it was forked with ──────────────────────────

// The fork copies the parent's choices as of the fork entry, so it starts with
// the parent's grants and can never silently widen them.
var fork = await allowed.ForkAsync(allowed.Entries(1)[0]);
Console.WriteLine($"fork permissions: [{string.Join(", ", fork.Choices()?.Permissions ?? [])}]");

await secondRun.DisposeAsync();
secondStore.Dispose();

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The denial is real: the tool never ran, so its effect never happened.
Check.Equal(0, deletionsAfterRestart, "the denied conversation never ran the tool");
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
    "the fork inherited the parent's grants");

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

Directory.Delete(directory, recursive: true);
