// 27 — A reviewer agent next to the main one.
//
// A reviewer agent: a cheaper model, a review role, a review loop, read-only tools, and
// its own checkout of the project.
//
// Everything the reviewer is, is stored on its conversation: the model, exactly these
// extensions in this order, only the read tool, and its directory. A restart keeps all of
// it, because the stored choices are names, not code.
//
// The review loop is a session hook: `OnAgentStop` sees every answer, and answering
// "block" sends the agent back for another pass. The reviewer stops when its answer
// contains the done marker.
//
// Mirrors the Pi Durable example `27-reviewer.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

const string Done = "No further findings.";

// The reviewer works in its own checkout, in practice a `git worktree add`.
var worktree = Path.Combine(Path.GetTempPath(), $"durable-review-{Guid.NewGuid():N}");
Directory.CreateDirectory(worktree);
await File.WriteAllTextAsync(Path.Combine(worktree, "user.ts"), "export const name = (user) => user.name;\n");

var store = new DurableStore();
var registry = new ExtensionRegistry();

// ─── Product code: the reviewer extension ───────────────────────────────────

// The read-only tool. The reviewer may look, never touch.
var readFile = CopilotTool.DefineTool(
    ([Description("File to read, relative to the working directory")] string path) =>
    {
        var full = Path.Combine(worktree, path);
        return File.Exists(full) ? File.ReadAllText(full) : $"(no such file: {path})";
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a file" });

// The role, and the review loop: every answer that still has findings gets a second pass.
var Reviewer = new Extension(
    "reviewer",
    tools: [readFile],
    sections:
    [
        new PromptSection("role", _ => Task.FromResult<string?>(
            "You review diffs. Report problems as a list. Never edit files.")),
    ]);

registry.Install(Reviewer);

await using var harness = await DurableHarness.OpenAsync(store, registry);

// The main agent selects only the reviewer extension's tools; the reviewer opts in.
var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["reviewer"],
    Tools = ["read_file"],
    WorkingDirectory = worktree,
});

// The review loop. The hook is code-level configuration, so it is supplied when the
// conversation is created rather than stored with the choices.
var passes = 0;
DurableConversation? reviewer = null;
var reviewerHooks = new SessionHooks
{
    OnAgentStop = (input, _) =>
    {
        passes++;
        var answer = reviewer?.Answers.LastOrDefault() ?? string.Empty;
        if (answer.Contains(Done, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing left to find: let the agent stop.
            return Task.FromResult<AgentStopHookOutput?>(null);
        }

        // Still findings: send it back for another pass.
        return Task.FromResult<AgentStopHookOutput?>(new AgentStopHookOutput
        {
            Decision = "block",
            Reason = $"Look again for anything you missed. Say \"{Done}\" when there is nothing left.",
        });
    },
};

// Everything the reviewer is, stored on its conversation: the model, exactly these
// extensions in this order, only the read tool, and its directory. A restart keeps all
// of it.
reviewer = await harness.CreateConversationAsync(new AgentChoices
{
    Model = "mai-code-1.1-flash",
    Extensions = ["reviewer"],
    Tools = ["read_file"],
    WorkingDirectory = worktree,
    Hooks = reviewerHooks,
});

var agent = reviewer.Agent();
Console.WriteLine(
    $"reviewer: {agent.Model} " +
    $"[{string.Join(", ", agent.Extensions.Select(extension => extension.Name))}] " +
    $"[{string.Join(", ", agent.Tools.Select(tool => tool.Name))}] " +
    $"cwd={(agent.WorkingDirectory == worktree)}");

await reviewer.SubmitAsync("Review user.ts.");
Console.WriteLine($"review passes: {passes}");
foreach (var answer in reviewer.Answers)
{
    Console.WriteLine($"reviewer: {answer}");
}

Directory.Delete(worktree, recursive: true);
