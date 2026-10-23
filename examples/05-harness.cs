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
});

await root.CommitAsync(async tx =>
{
    var notes = tx.Doc(Notes, root.Id);
    notes.Value["text"] = "root notes";
    notes.Save(tx.AppendEntry(root.Id, "note", "root notes"));
});

Console.WriteLine($"root: {root.Id} {root.Snapshot(Notes)!["text"]}");

// The stored choices are names; Agent() resolves them against the registry.
Console.WriteLine($"stored agent: {root.Choices()!.ReasoningEffort}");
var agent = root.Agent();
Console.WriteLine($"resolved: {agent.ReasoningEffort} [{string.Join(", ", agent.Extensions.Select(e => e.Name))}] [{string.Join(", ", agent.Tools.Select(t => t.Name))}]");

// A second RootAsync() returns the same conversation and ignores the new options.
var again = await harness.RootAsync(new AgentChoices { ReasoningEffort = "high" });
Console.WriteLine($"root is stable: {again.Id == root.Id}");
Console.WriteLine($"stored agent unchanged: {root.Choices()!.ReasoningEffort}");