// 21 — A foreground subagent.
//
// A foreground subagent tool: the parent's tool call creates a child conversation it owns,
// runs one task there, and returns the child's answer. Aborting the tool call aborts the
// child.
//
// Mirrors the Pi Durable example `21-subagent-foreground.ts`.

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
await using var harness = await DurableHarness.OpenAsync(store, registry);

// The harness is captured before the tool is defined, so the tool handler can
// reach it.
DurableHarness? host = null;
DurableConversation? parent = null;

// The child is owned by this tool call's task, so aborting the call aborts the
// child, and the call finishes only once the child's work is done.
var subagent = CopilotTool.DefineTool(
    async ([Description("What the subagent should do")] string task) =>
    {
        // Safe to rerun after a crash: a rerun finds the child it already created.
        var child = await host!.CreateConversationAsync(
            new AgentChoices { Model = "mai-code-1.1-flash" },
            ownerConversationId: parent!.Id);

        // Without this extension, the child is not offered this tool.
        var answer = await child.SubmitAsync(task);
        return $"{answer?.Data.Content} (child conversation: {child.Id})";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "subagent",
        Description = "Delegate a self-contained task to a subagent and get its answer back.",
    });

registry.Install(new Extension("subagent", tools: [subagent]));

parent = await harness.RootAsync(new AgentChoices { Extensions = ["subagent"] });
host = harness;

// The parent's tool call creates the child, runs one task there, and returns the
// child's answer.
var answer = await parent.SubmitAsync(
    "Use the subagent tool to name three prime numbers, then tell me what it said.");

Console.WriteLine($"answer: {answer?.Data.Content}");

// The child conversation is durable: it is recorded in the store with its owner.
foreach (var conversation in harness.Store.Conversations)
{
    Console.WriteLine($"conversation {conversation.Id}: owner conversation={conversation.OwnerConversationId ?? "(none)"}");
}