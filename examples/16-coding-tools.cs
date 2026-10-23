// 16 — Coding tools.
//
// A coding-agent tool turn: the model reads, edits, and runs commands, then answers.
//
// The tools reach files and processes only through the working directory the conversation
// carries. A hook times a call, the way Pi Durable's `beforeTool`/`afterTool` hooks do.
//
// Mirrors the Pi Durable example `16-coding-tools.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Diagnostics;
using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var workspace = Path.Combine(Path.GetTempPath(), $"durable-coding-{Guid.NewGuid():N}");
Directory.CreateDirectory(workspace);
await File.WriteAllTextAsync(Path.Combine(workspace, "notes.txt"), "hello world\n");

var store = new DurableStore();
var registry = new ExtensionRegistry();

// The tools reach files and processes only through the working directory the
// conversation carries.
var read = CopilotTool.DefineTool(
    ([Description("File to read, relative to the working directory")] string path) =>
        File.Exists(Path.Combine(workspace, path))
            ? File.ReadAllText(Path.Combine(workspace, path))
            : $"error: {path} not found",
    // "read_file" is a built-in tool name, so this one has to say it means to
    // replace it. Without the override the built-in silently wins.
    toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
    factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a file" });

var edit = CopilotTool.DefineTool(
    ([Description("File to edit")] string path, [Description("Text to replace")] string oldText,
     [Description("Replacement text")] string newText) =>
    {
        var file = Path.Combine(workspace, path);
        if (!File.Exists(file))
        {
            return $"error: {path} not found";
        }

        var content = File.ReadAllText(file);
        if (!content.Contains(oldText, StringComparison.Ordinal))
        {
            return $"error: {oldText} not found in {path}";
        }

        File.WriteAllText(file, content.Replace(oldText, newText, StringComparison.Ordinal));
        return $"edited {path}";
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "edit_file", Description = "Edit a file" });

var bash = CopilotTool.DefineTool(
    ([Description("Shell command")] string command) =>
    {
        var start = Stopwatch.StartNew();
        var process = Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return $"{output.Trim()} (exit {process.ExitCode}, {start.ElapsedMilliseconds} ms)";
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "run_shell", Description = "Run a shell command" });

registry.Install(new Extension("coding", tools: [read, edit, bash]));

// Hooks see every call before and after execution; these time the bash calls.
var timings = new List<long>();
var started = new Dictionary<string, long>();
var hooks = new SessionHooks
{
    OnPreToolUse = (input, _) =>
    {
        if (input.ToolName == "run_shell")
        {
            started[input.ToolArgs?.GetRawText() ?? ""] = Stopwatch.GetTimestamp();
        }

        return Task.FromResult<PreToolUseHookOutput?>(null);
    },
    OnPostToolUse = (input, _) =>
    {
        if (input.ToolName == "run_shell")
        {
            var key = input.ToolArgs?.GetRawText() ?? "";
            if (started.Remove(key, out var begin))
            {
                timings.Add(Stopwatch.GetElapsedTime(begin).Milliseconds);
            }
        }

        return Task.FromResult<PostToolUseHookOutput?>(null);
    },
};

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["coding"],
    WorkingDirectory = workspace,
    Hooks = hooks,
});

// Each tool call runs as a durable task owned by the generation, which waits for
// them and continues the run with the next generation.
var answer = await root.SubmitAsync(
    "Use the read tool on notes.txt, then the edit tool to replace 'world' with 'durable', then the bash tool to run 'cat notes.txt'. Then tell me what the file says.");

Console.WriteLine($"status: {(answer is null ? "no answer" : "answered")}");
Console.WriteLine($"answer: {answer?.Data.Content}");

// The transcript holds the tool calls and their results.
var context = await root.ContextAsync();
foreach (var evt in context)
{
    switch (evt)
    {
        case ToolExecutionStartEvent tool:
            Console.WriteLine($"tool start: {tool.Data.ToolName}");
            break;
        case ToolExecutionCompleteEvent tool:
            Console.WriteLine($"tool complete: {(tool.Data.Error is null ? "ok" : tool.Data.Error.Message)}");
            break;
    }
}

Console.WriteLine($"file: {File.ReadAllText(Path.Combine(workspace, "notes.txt")).Trim()}");
Console.WriteLine($"bash calls timed: {timings.Count}");

Directory.Delete(workspace, recursive: true);