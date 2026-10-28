// 26 — Plan mode.
//
// The user switches a conversation into a read-only planning mode, the agent writes a plan
// into the extension's own document, and switching back restores the full tool set.
//
// Mirrors the Pi Durable example `26-plan-mode.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var directory = Path.Combine(Path.GetTempPath(), $"durable-plan-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
await File.WriteAllTextAsync(Path.Combine(directory, "server.ts"), "app.listen(3000);\n");

var store = new DurableStore();
var registry = new ExtensionRegistry();

// The current plan of a conversation. A fork keeps the plan it had at the fork entry.
var PlanDoc = new DocumentDefinition<List<string>>(
    "app.plan",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => []);

DurableConversation? chat = null;

// The tool writes the plan into the extension's own document.
var submitPlan = CopilotTool.DefineTool(
    async ([Description("The plan, as a list of steps")] string steps) =>
    {
        var conversation = chat!;
        await conversation.CommitAsync(async tx =>
        {
            var plan = tx.Doc(PlanDoc, conversation.Id);
            plan.Value = steps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            plan.Save(tx.AppendEntry(conversation.Id, "plan", "submitted"));
        });
        return "Plan submitted.";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "submit_plan",
        Description = "Submit the plan as a list of steps, one per line.",
    });

var read = CopilotTool.DefineTool(
    ([Description("File to read")] string path) =>
        File.Exists(Path.Combine(directory, path))
            ? File.ReadAllText(Path.Combine(directory, path))
            : $"error: {path} not found",
    // "read_file" is a built-in tool name, so this one has to say it means to
    // replace it. Without the override the built-in silently wins.
    toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
    factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a file" });

var Plan = new Extension(
    "plan",
    tools: [submitPlan],
    sections: [new PromptSection("plan_mode", _ =>
        Task.FromResult<string?>("You are in plan mode. Read the code, then call submit_plan. Change nothing."))]);

registry.Install(Plan);

// The coding extension brings the everyday tools. It is installed but not
// selected by default: conversations opt into plan mode.
registry.Install(new Extension("coding", tools: [read]));

await using var harness = await DurableHarness.OpenAsync(store, registry);
chat = await harness.RootAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = directory,
    // Grants are fixed when the session is created: ConfigureAsync can change the
    // tool list but not the permission handler, so the conversation is granted
    // everything it will need up front. Plan mode restricts the tool list, not
    // the grants.
    Permissions = ["custom-tool:read_file", "custom-tool:submit_plan", "read", "write"],
});

var tools = () => chat!.Agent().Tools.Select(tool => tool.Name).ToList();
Console.WriteLine($"tools: [{string.Join(", ", tools())}]");

// Plan mode is a change to the conversation's agent: select the plan extension and
// offer only reading and submitting.
await chat.ConfigureAsync(new AgentChoices
{
    Extensions = ["plan"],
    Tools = ["read_file", "submit_plan"],
    WorkingDirectory = directory,
    Permissions = ["custom-tool:read_file", "custom-tool:submit_plan", "read", "write"],
});
Console.WriteLine($"plan mode tools: [{string.Join(", ", tools())}]");
var planTools = tools();

var answer = await chat.SubmitAsync(
    "Make the port configurable. You are in plan mode: read server.ts, then call the submit_plan tool with the steps. Do not edit any file.");
Console.WriteLine($"answer: {answer?.Data.Content}");
Console.WriteLine($"plan: [{string.Join(" | ", chat.Snapshot(PlanDoc) ?? [])}]");

// Clearing both returns to the host's default selection and every tool.
await chat.ConfigureAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = directory,
    Permissions = ["custom-tool:read_file", "read", "write"],
});
Console.WriteLine($"tools again: [{string.Join(", ", tools())}]");

var second = await chat.SubmitAsync("Go ahead.");
Console.WriteLine($"answer: {second?.Data.Content}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// Plan mode swapped the tool list, and the plan was written to a document.
Check.Sequence(new[] { "read_file" }, tools(), "the conversation starts with the coding tools");
Check.Sequence(new[] { "submit_plan" }, planTools, "plan mode offers only the plan tool");
Check.Sequence(new[] { "read_file" }, tools(), "leaving plan mode restores the coding tools");

// The plan is durable state: it is in the document, not just in the answer.
var plan = chat.Snapshot(PlanDoc);
Check.That(plan is { Count: > 0 }, "the plan was written to the document");
Check.That(plan!.All(step => step.Trim().Length > 0), "every plan step has content");

Directory.Delete(directory, recursive: true);