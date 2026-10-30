// 14 — System prompt.
//
// System prompt sections and per-conversation instructions.
//
// The app stores no prompt state. Before each model request, the sections of the
// conversation's selected extensions render the desired prompt, and the runtime injects
// the difference into the request.
//
// Mirrors the Pi Durable example `14-system-prompt.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// The Coding extension contributes a preamble and the working directory.
// `tag: false` sends the text as is; by default it is wrapped in <key>...</key>.
var Coding = new Extension(
    "coding",
    sections:
    [
        new PromptSection("preamble", _ => Task.FromResult<string?>("You are a coding agent."), tag: false),
        new PromptSection("cwd", input => Task.FromResult(input.WorkingDirectory)),
    ]);

var AgentsMd = new Extension(
    "agents-md",
    sections: [new PromptSection("agents_md", _ => Task.FromResult<string?>("Run npm run check after changes."))]);

registry.Install(Coding);
registry.Install(AgentsMd);

await using var harness = await DurableHarness.OpenAsync(store, registry);

// The working directory must exist: the runtime runs the session there.
var repo = Path.Combine(Path.GetTempPath(), $"durable-repo-{Guid.NewGuid():N}");
Directory.CreateDirectory(repo);

var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["coding", "agents-md"],
    WorkingDirectory = repo,
});

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// A subagent deselects AGENTS.md and gets its own instructions.
var subagent = await harness.CreateConversationAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = repo,
    Instructions = "Only read; never edit files.",
});

// The rendered system message is what the model sees for each conversation.
var rootAgent = root.Agent();

// The instructions as they stand before the reconfigure below.
var rootInstructions = root.Agent().Instructions;
var subagentInstructions = subagent.Agent().Instructions;
var rootSections = rootAgent.Extensions.SelectMany(extension => extension.Sections).ToList();
Console.WriteLine("root system prompt:");
Console.WriteLine(harness.BuildSystemMessage(rootAgent, rootSections, root));

var subagentAgent = subagent.Agent();
var subagentSections = subagentAgent.Extensions.SelectMany(extension => extension.Sections).ToList();
Console.WriteLine("subagent system prompt:");
Console.WriteLine(harness.BuildSystemMessage(subagentAgent, subagentSections, subagent));

// When a section's output changes, the next request carries only the change.
var packages = Path.Combine(repo, "packages");
Directory.CreateDirectory(packages);
await root.ConfigureAsync(new AgentChoices
{
    Extensions = ["coding", "agents-md"],
    WorkingDirectory = packages,
});
Console.WriteLine($"root cwd now: {root.Agent().WorkingDirectory}");

// The stored choices are per conversation: the subagent is unaffected.
Console.WriteLine($"subagent cwd still: {subagent.Agent().WorkingDirectory}");
Console.WriteLine($"subagent instructions still: {subagent.Agent().Instructions}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The stored choices are per conversation: reconfiguring the root leaves the
// subagent alone.
Check.Equal(packages, root.Agent().WorkingDirectory, "the root's working directory changed");
Check.That(subagent.Agent().WorkingDirectory != packages, "the subagent's working directory is unchanged");
Check.Equal(subagentInstructions, subagent.Agent().Instructions, "the subagent's instructions are unchanged");

// The prompt sections are rendered per request, not stored: the app keeps no
// prompt state, so the same choices always produce the same prompt.
Check.Equal(rootInstructions, root.Agent().Instructions, "the root's instructions are unchanged");
Check.That(root.Agent().Extensions.Count >= 2, "the root selects both extensions");

Directory.Delete(repo, recursive: true);