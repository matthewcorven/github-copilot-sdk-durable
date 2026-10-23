// 06 — Configuration.
//
// Per-conversation agent choices and harness-wide settings.
//
// Apps may attach their own metadata to tools. Settings are harness-wide run policy, read at
// every use and never stored. `configure()` stores choices in the conversation's agent
// document, one commit per call. Extensions and tools are passed as objects and stored by
// name, so a typo cannot slip in.
//
// Mirrors the Pi Durable example `06-configuration.ts`.

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

// Apps may attach their own metadata to tools, such as a prompt snippet.
static AIFunction ExampleTool(string name, string description) =>
    CopilotTool.DefineTool(
        ([Description("File path")] string path) => $"{name} {path}",
        factoryOptions: new AIFunctionFactoryOptions { Name = name, Description = description });

var read = ExampleTool("read", "Read a file");
var write = ExampleTool("write", "Write a file");
var grep = ExampleTool("grep", "Search files");

var Files = new Extension("files", tools: [read, write]);
var Search = new Extension("search", tools: [grep]);
registry.Install(Files);
registry.Install(Search);

// Settings are harness-wide run policy, read at every use and never stored.
// A getter makes a value live, such as one backed by the app's settings file.
var settings = new HarnessSettings
{
    Retry = new RetrySettings { MaxRetries = 5 },
    ToolExecution = ToolExecutionMode.Sequential,
};

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync();

// A new conversation stores no choices and follows the host: every installed
// extension, all of their tools.
var tools = () => root.Agent().Tools.Select(tool => tool.Name).ToList();
Console.WriteLine($"default tools: [{string.Join(", ", tools())}]");

// configure() stores choices in the conversation's agent document, one commit per call.
// Extensions and tools are passed as objects and stored by name, so a typo cannot slip in.
await root.ConfigureAsync(new AgentChoices
{
    Model = "gpt-6-luna",
    ReasoningEffort = "high",
    Tools = ["write", "read"],
});

Console.WriteLine($"stored: {root.Choices()!.Model} / {root.Choices()!.ReasoningEffort} / [{string.Join(", ", root.Choices()!.Tools!)}]");
var agent = root.Agent();

// The stored choices as they stand right now, before a later configure() replaces them.
var storedModel = root.Choices()!.Model;
var storedEffort = root.Choices()!.ReasoningEffort;
var storedTools = root.Choices()!.Tools!;
Console.WriteLine($"model: {agent.Model} thinking: {agent.ReasoningEffort} tools: [{string.Join(", ", tools())}]");

// Deselect an extension; `null` clears a stored field back to the host default.
await root.ConfigureAsync(new AgentChoices
{
    Model = "gpt-6-luna",
    ReasoningEffort = "high",
    Extensions = ["files"],
    Tools = ["write", "read"],
});
Console.WriteLine($"without search: [{string.Join(", ", tools())}]");
var withoutSearch = tools();

// Stored names outlive the code. The conversation selects exactly these two
// extensions; uninstalling "files" leaves it selected, and requests just stop
// offering its tools until it is back.
await root.ConfigureAsync(new AgentChoices
{
    Model = "gpt-6-luna",
    Extensions = ["files", "search"],
});
registry.Uninstall("files");
Console.WriteLine($"files uninstalled: [{string.Join(", ", tools())}]");
var filesUninstalled = tools();
registry.Install(Files);
Console.WriteLine($"files reinstalled: [{string.Join(", ", tools())}]");
var filesReinstalled = tools();

// Settings changes need no commit; the next request uses the new value.
settings.Retry.MaxRetries = 10;
Console.WriteLine($"live settings: maxRetries={settings.Retry.MaxRetries} toolExecution={settings.ToolExecution}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The stored choices are the source of truth, and the resolved agent is those
// choices bound to the registry's code.
Check.Equal("gpt-6-luna", storedModel, "the stored model is the one that was configured");
Check.Equal("high", storedEffort, "the stored reasoning effort is the one that was configured");
Check.Sequence(new[] { "write", "read" }, storedTools, "the stored tool names are the ones that were configured");
Check.Sequence(new[] { "write", "read" }, agent.Tools.Select(t => t.Name), "the resolved tools follow the stored names");

// A later configure() replaces the stored choices wholesale: a field it omits
// is cleared, not merged.
Check.Equal<string?>(null, root.Choices()!.ReasoningEffort, "a later configure() clears the fields it omits");
Check.That(root.Choices()!.Tools is null, "a later configure() clears the tools it omits");

// Deselecting an extension removes its tools from the resolved agent.
Check.That(!withoutSearch.Contains("grep"), "the deselected extension's tools are gone");
Check.That(withoutSearch.Contains("read"), "the still-selected extension keeps its tools");

// Stored names outlive the code: uninstalling "files" leaves it selected, and
// the conversation just stops offering its tools until it is back.
Check.That(root.Choices()!.Extensions!.Contains("files"), "the uninstalled extension is still selected by name");
Check.That(!filesUninstalled.Contains("read"), "an uninstalled extension contributes no tools");
Check.That(filesUninstalled.Contains("grep"), "the other selected extension still contributes its tools");
Check.That(filesReinstalled.Contains("read"), "reinstalling the extension brings its tools back");

// Settings are read at every use and never stored, so a change takes effect
// immediately and leaves no trace in the store.
settings.Retry.MaxRetries = 10;
Check.Equal(10, settings.Retry.MaxRetries, "a settings change takes effect immediately");
Check.That(root.Choices()!.GetType().GetProperty("Retry") is null, "settings are not part of the stored choices");

/// <summary>Harness-wide run policy, read at every use and never stored.</summary>
public sealed class HarnessSettings
{
    /// <summary>The retry policy.</summary>
    public RetrySettings Retry { get; set; } = new();

    /// <summary>Whether tool calls run one at a time or in parallel.</summary>
    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.Sequential;
}

/// <summary>The retry policy.</summary>
public sealed class RetrySettings
{
    /// <summary>How many times a failed request is retried.</summary>
    public int MaxRetries { get; set; }
}

/// <summary>How tool calls are scheduled.</summary>
public enum ToolExecutionMode
{
    /// <summary>One tool call at a time.</summary>
    Sequential,

    /// <summary>Tool calls run in parallel.</summary>
    Parallel,
}