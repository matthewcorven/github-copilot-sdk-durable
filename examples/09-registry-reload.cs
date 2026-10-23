// 09 — Registry reload.
//
// Reload extension code through the registry.
//
// Reloading is installing a new object with the same name: it replaces the old one in place,
// in one publication, so no conversation ever sees it missing. A task phase that already
// started keeps the snapshot it took; the next phase uses the new code.
//
// Mirrors the Pi Durable example `09-registry-reload.ts`.

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

// Stand-in for code loaded from disk: each call builds the extension as the file
// currently reads.
static Extension LoadFiles(string grepDescription)
{
    // "read" and "grep" are built-in tool names, so these have to say they mean to
    // replace them. Without the override the built-in silently wins.
    var read = CopilotTool.DefineTool(
        ([Description("File path")] string path) => $"contents of {path}",
        toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
        factoryOptions: new AIFunctionFactoryOptions { Name = "read", Description = "Read a file" });

    var grep = CopilotTool.DefineTool(
        ([Description("Pattern")] string pattern) => $"matches for {pattern}",
        toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
        factoryOptions: new AIFunctionFactoryOptions { Name = "grep", Description = grepDescription });

    return new Extension("files", tools: [read, grep]);
}

registry.Install(LoadFiles("Search files"));

await using var harness = await DurableHarness.OpenAsync(store, registry);
// A conversation with no stored selection follows the host: every installed
// extension, all of their tools.
var root = await harness.RootAsync();

var tools = () => root.Agent().Tools.Select(tool => $"{tool.Name}: {tool.Description}").ToList();
Console.WriteLine($"after load: [{string.Join(", ", tools())}]");

// Reloading is installing a new object with the same name: it replaces the old
// one in place, in one publication, so no conversation ever sees it missing.
registry.Install(LoadFiles("Search files, faster"));
Console.WriteLine($"after reload: [{string.Join(", ", tools())}]");

// Another extension may wrap a tool by name. The wrap applies where the
// wrapping extension is selected, and survives reloads of the wrapped tool.
var audit = new Extension("audit", tools:
[
    CopilotTool.DefineTool(
        ([Description("File path")] string path) => $"audited: {path}",
        toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
        factoryOptions: new AIFunctionFactoryOptions { Name = "read", Description = "Read a file (audited)" }),
]);
registry.Install(audit);
Console.WriteLine($"with audit: [{string.Join(", ", tools())}]");

// Uninstall removes an extension by name; conversations that select it stop
// getting its code.
registry.Uninstall("audit");
Console.WriteLine($"after uninstall: [{string.Join(", ", tools())}]");

// The stored selection is by name, so it survives the reload: the conversation
// still selects "files", and the new code is what it resolves to.
Console.WriteLine($"stored extensions: [{string.Join(", ", root.Choices()?.Extensions ?? ["(host default)"])}]");
Console.WriteLine($"resolved extensions: [{string.Join(", ", root.Agent().Extensions.Select(e => e.Name))}]");