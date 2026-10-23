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
registry.Install(Files);
Console.WriteLine($"files reinstalled: [{string.Join(", ", tools())}]");

// Settings changes need no commit; the next request uses the new value.
settings.Retry.MaxRetries = 10;
Console.WriteLine($"live settings: maxRetries={settings.Retry.MaxRetries} toolExecution={settings.ToolExecution}");

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