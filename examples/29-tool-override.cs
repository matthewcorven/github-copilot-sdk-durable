// 29 — Replace a tool for some conversations, and decorate whichever tool wins.
//
// Two extensions define a tool with the same name. The one selected later replaces the
// other in place, so a conversation that selects both gets the later one. A third
// extension wraps the tool that won, whichever it is, so reloading either implementation
// keeps the wrapper.
//
// The wrapper is an `AIFunction` subclass: it forwards the inner function's schema and
// delegates the call, so the model sees the same tool either way.
//
// Mirrors the Pi Durable example `29-tool-override.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

// The project the shell tool runs in.
var project = Path.Combine(Path.GetTempPath(), $"durable-venv-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(project, ".venv", "bin"));
await File.WriteAllTextAsync(
    Path.Combine(project, ".venv", "bin", "activate"),
    $"export VIRTUAL_ENV=\"{project}/.venv\"\n");

var store = new DurableStore();
var registry = new ExtensionRegistry();

// ─── Product code ───────────────────────────────────────────────────────────

// A shell with the same name: where selected after the plain one, it replaces it in
// place.
var venvShell = CopilotTool.DefineTool(
    ([Description("Shell command")] string command) =>
    {
        var output = RunShell(project, $"source .venv/bin/activate && {command}");
        return output;
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "run_shell", Description = "Run a shell command" });

// The plain shell.
var plainShell = CopilotTool.DefineTool(
    ([Description("Shell command")] string command) => RunShell(project, command),
    factoryOptions: new AIFunctionFactoryOptions { Name = "run_shell", Description = "Run a shell command" });

// The timings the wrapper collects.
var timings = new List<long>();

// The wrapper. It never captures a base tool: it names the tool it decorates, so
// whichever implementation of that name wins is the one that gets timed.
registry.Install(new Extension("shell", tools: [plainShell]));
registry.Install(new Extension(
    "timing",
    wraps:
    [
        new ToolWrapper("run_shell", inner => new TimedTool(inner, "run_shell", "Run a shell command (timed)", timings)),
    ]));
registry.Install(new Extension("venv", tools: [venvShell]));

await using var harness = await DurableHarness.OpenAsync(store, registry);

// The plain conversation selects the plain shell and the timing wrapper.
var plain = await harness.RootAsync(new AgentChoices
{
    Extensions = ["shell", "timing"],
    Tools = ["run_shell"],
});

// The python conversation selects the venv shell after the plain one, so the venv shell
// replaces it in place. The timing wrapper still wraps whatever won.
var python = await harness.CreateConversationAsync(new AgentChoices
{
    Extensions = ["shell", "timing", "venv"],
    Tools = ["run_shell"],
});

Console.WriteLine($"plain tools: [{string.Join(", ", plain.Agent().Tools.Select(tool => tool.Name))}]");
Console.WriteLine($"python tools: [{string.Join(", ", python.Agent().Tools.Select(tool => tool.Name))}]");

await plain.SubmitAsync("Use the run_shell tool to run 'echo plain: $VIRTUAL_ENV', then tell me the output.");
await python.SubmitAsync("Use the run_shell tool to run 'echo venv: $VIRTUAL_ENV', then tell me the output.");

Console.WriteLine($"plain answer: {plain.Answers.LastOrDefault()}");
Console.WriteLine($"python answer: {python.Answers.LastOrDefault()}");
Console.WriteLine($"timed bash calls: {timings.Count}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The later extension replaced the earlier one: the python conversation's shell
// runs inside the virtualenv, and the plain one does not.
Check.Contains(python.Answers.LastOrDefault() ?? "", ".venv", "the venv shell replaced the plain one");
Check.That(
    !(plain.Answers.LastOrDefault() ?? "").Contains(".venv"),
    "the plain conversation kept the plain shell");

// The wrapper decorated whichever tool won, so both calls were timed.
Check.Equal(2, timings.Count, "the wrapper timed both shell calls");

Directory.Delete(project, recursive: true);

static string RunShell(string workingDirectory, string command)
{
    var process = Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output.Trim();
}

/// <summary>A tool that times the tool it wraps, and forwards its schema.</summary>
public sealed class TimedTool(
    AIFunction inner,
    string name,
    string description,
    List<long> timings) : AIFunction
{
    /// <inheritdoc />
    public override string Name => name;

    /// <inheritdoc />
    public override string Description => description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => inner.JsonSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments args, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.InvokeAsync(args, cancellationToken);
        }
        finally
        {
            timings.Add(stopwatch.ElapsedMilliseconds);
        }
    }
}
