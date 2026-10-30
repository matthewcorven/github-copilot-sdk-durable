// 05 — The harness.
//
// Open a harness with a registry of extensions.
//
// A harness is a store plus conversation handles plus durable tasks. Extension code (tools,
// prompt sections, hooks, tasks) comes in named extensions installed in a registry the
// application owns. Nothing in the registry is saved; it is this process's code.
//
// Mirrors the Pi Durable example `05-harness.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A harness is a store plus conversation handles plus durable tasks. Extension
// code (tools, prompt sections, hooks, tasks) comes in named extensions
// installed in a registry the application owns. Nothing in the registry is
// saved; it is this process's code.
var read = CopilotTool.DefineTool(
    ([Description("File path")] string path) => $"contents of {path}",
    // "read" is a built-in tool name, so this one has to say it means to replace it.
    toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "read",
        Description = "Read a file",
    });

var Files = new Extension("files", tools: [read]);
registry.Install(Files);

await using var harness = await DurableHarness.OpenAsync(store, registry);

var Notes = new DocumentDefinition<Dictionary<string, string>>(
    "example.notes",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => new Dictionary<string, string> { ["text"] = "" });

// The root conversation always has the same id. The first RootAsync() call creates it,
// its built-in documents, the `agent` choices, and whatever `init` writes, all
// in one commit. Later calls, including after a restart, return it and ignore
// both options.
var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["files"],
    ReasoningEffort = "low",
    // Fail-closed: the conversation may call the "read" tool and nothing else.
    Permissions = ["custom-tool:read"],
});

await root.CommitAsync(async tx =>
{
    var notes = tx.Doc(Notes, root.Id);
    notes.Value["text"] = "root notes";
    notes.Save(tx.AppendEntry(root.Id, "note", "root notes"));
});

Console.WriteLine($"root: {root.Id} {root.Snapshot(Notes)!["text"]}");

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// The stored choices are names; Agent() resolves them against the registry.
Console.WriteLine($"stored agent: {root.Choices()!.ReasoningEffort}");
var agent = root.Agent();
Console.WriteLine($"resolved: {agent.ReasoningEffort} [{string.Join(", ", agent.Extensions.Select(e => e.Name))}] [{string.Join(", ", agent.Tools.Select(t => t.Name))}]");

// A second RootAsync() returns the same conversation and ignores the new options.
var again = await harness.RootAsync(new AgentChoices { ReasoningEffort = "high" });
Console.WriteLine($"root is stable: {again.Id == root.Id}");
Console.WriteLine($"stored agent unchanged: {root.Choices()!.ReasoningEffort}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The harness resolves the stored names against the registry, so the agent is
// the stored choices bound to live code.
Check.Equal("low", root.Choices()!.ReasoningEffort, "the stored reasoning effort is the one that was configured");
Check.Equal("low", agent.ReasoningEffort, "the resolved agent carries the stored reasoning effort");
Check.Sequence(new[] { "files" }, agent.Extensions.Select(e => e.Name), "the resolved agent carries the selected extension");
Check.Sequence(new[] { "read" }, agent.Tools.Select(t => t.Name), "the resolved agent carries the selected tool");

// The root conversation is stable: a second RootAsync() returns the same one and
// does not overwrite the stored choices.
Check.Equal(root.Id, again.Id, "the root conversation is stable across calls");
Check.Equal("low", root.Choices()!.ReasoningEffort, "a second RootAsync() does not overwrite the stored choices");

// The document written in a commit is readable.
Check.Equal("root notes", root.Snapshot(Notes)!["text"], "the committed document value is readable");