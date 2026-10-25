// 25 — A coding agent.
//
// A small coding agent: the built-in coding tools, a prompt that knows the working
// directory, settings backed by the app's settings file, and an environment that follows
// the conversation's directory.
//
// Mirrors the Pi Durable example `25-coding-agent.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Diagnostics;
using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var workspace = Path.Combine(Path.GetTempPath(), $"durable-agent-{Guid.NewGuid():N}");
var app = Path.Combine(workspace, "app");
Directory.CreateDirectory(app);

var store = new DurableStore();
var registry = new ExtensionRegistry();

// The environment follows the conversation's directory: changing the conversation's
// working directory needs no restart.
var CurrentDirectory = workspace;

// The app's own prompt, next to the coding tools.
var bash = CopilotTool.DefineTool(
    ([Description("Shell command")] string command) =>
    {
        var start = Stopwatch.StartNew();
        var process = Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return $"{output.Trim()} (exit {process.ExitCode}, {start.ElapsedMilliseconds} ms)";
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "run_shell", Description = "Run a shell command" });

registry.Install(new Extension(
    "coding",
    tools: [bash],
    sections:
    [
        new PromptSection("preamble", _ => Task.FromResult<string?>("You are a coding agent. Use the tools to inspect the project."), tag: false),
        new PromptSection("cwd", input => Task.FromResult(input.WorkingDirectory)),
    ]));

// Settings the user edits while the agent runs, like a settings.json the app watches.
// The getters make every read see the current file; nothing is copied or stored.
var userSettings = new UserSettings { ParallelTools = true, MaxRetries = 3 };
var settings = new HarnessSettings
{
    ToolExecution = () => userSettings.ParallelTools ? ToolExecutionMode.Parallel : ToolExecutionMode.Sequential,
    Retry = () => new RetrySettings { MaxRetries = userSettings.MaxRetries },
};

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = workspace,
    Permissions = ["custom-tool:run_shell"],
});

// The tool runs in the conversation's directory.
CurrentDirectory = root.Agent().WorkingDirectory ?? workspace;

var answer = await root.SubmitAsync("Use the run_shell tool to run 'pwd', then tell me the directory.");
Console.WriteLine($"answer: {answer?.Data.Content}");
var first = answer;
var firstDirectory = root.Agent().WorkingDirectory;

// The user switches the project directory.
await root.ConfigureAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = app,
    Permissions = ["custom-tool:run_shell"],
});
CurrentDirectory = root.Agent().WorkingDirectory ?? workspace;
userSettings.ParallelTools = false;

var second = await root.SubmitAsync("Use the run_shell tool to run 'pwd' again, then tell me the directory.");
Console.WriteLine($"answer: {second?.Data.Content}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The environment follows the conversation's directory: changing the stored
// working directory changes where the tool runs, with no restart.
Check.Equal(workspace, firstDirectory, "the conversation's working directory is the one that was configured");
Check.Contains(first?.Data.Content ?? "", workspace, "the tool ran in the conversation's directory");
Check.Equal(app, root.Agent().WorkingDirectory, "the conversation's working directory changed");
Check.Contains(second?.Data.Content ?? "", app, "the tool followed the conversation's new directory");
// The app directory is inside the workspace, so the answer contains the
// workspace path as a prefix. What matters is that it names the app directory.
Check.Contains(second?.Data.Content ?? "", app, "the tool ran in the new directory");

Console.WriteLine($"live settings: maxRetries={settings.Retry().MaxRetries} toolExecution={settings.ToolExecution()}");

Directory.Delete(workspace, recursive: true);

/// <summary>Settings the user edits while the agent runs.</summary>
public sealed class UserSettings
{
    /// <summary>Whether tool calls run in parallel.</summary>
    public bool ParallelTools { get; set; }

    /// <summary>How many times a failed request is retried.</summary>
    public int MaxRetries { get; set; }
}

/// <summary>Harness-wide run policy, read at every use and never stored.</summary>
public sealed class HarnessSettings
{
    /// <summary>Whether tool calls run one at a time or in parallel.</summary>
    public Func<ToolExecutionMode> ToolExecution { get; init; } = () => ToolExecutionMode.Sequential;

    /// <summary>The retry policy.</summary>
    public Func<RetrySettings> Retry { get; init; } = () => new RetrySettings();
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