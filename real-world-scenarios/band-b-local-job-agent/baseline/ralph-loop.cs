// Ralph Loop baseline (Band B).
//
// A port of the vendored `upstream/ralph-loop.ts` recipe: one prompt per iteration, a fresh
// session per iteration, and the plan file on disk as the only memory between iterations.
//
// The loop's position — which iteration is running — lives in a local variable in
// `RalphLoopAsync`. It is not written to a store and no event is journaled. Killing the
// process loses it.
//
// Part of the point is the seam: this file names no type from `lib/Durable.cs`. The SDK
// bindings are confined to `RunOneIterationAsync`; the control flow above it is plain C#
// so another language port only has to swap that one method for its own SDK calls.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001

using System.Diagnostics;
using System.Text;
using GitHub.Copilot;

const int MaxIterations = 10;
const string PlanFile = "PLAN.md";
// The model `upstream/ralph-loop.ts` pins:
//     model: "gpt-5.1-codex-mini",
const string PinnedModel = "gpt-5.1-codex-mini";

var exitCode = await RalphLoopAsync(args);
return exitCode;

// ─── The loop ───────────────────────────────────────────────────────────────
//
// Plain control flow. No store, no checkpoint, no task, no admission, no conversation
// document. The iteration index is the only record of where the run stands, and it is a
// local variable.
static async Task<int> RalphLoopAsync(string[] args)
{
    var target = Directory.GetCurrentDirectory();
    var planPath = Path.Combine(target, PlanFile);
    if (!File.Exists(planPath))
    {
        Console.Error.WriteLine($"baseline: no {PlanFile} in {target}");
        return 2;
    }

    var maxIterations = MaxIterations;
    if (args.Length > 0 && int.TryParse(args[0], out var requested) && requested > 0)
    {
        maxIterations = requested;
    }

    var promptPath = Path.Combine(target, "PROMPT.md");
    // The prompt is fixed for the whole run; only the plan changes between iterations.
    var prompt = File.ReadAllText(promptPath);

    // RALPH_MODEL overrides the pinned model. Unset means the pinned model.
    var model = Environment.GetEnvironmentVariable("RALPH_MODEL") is { Length: > 0 } overridden
        ? overridden
        : PinnedModel;

    Console.WriteLine($"target:    {target}");
    Console.WriteLine($"model:     {model}{(model == PinnedModel ? " (pinned)" : " (RALPH_MODEL)")}");
    Console.WriteLine($"plan:      {planPath}");
    Console.WriteLine($"prompt:    {promptPath}");
    Console.WriteLine($"max:       {maxIterations} iterations");
    Console.WriteLine(new string('━', 60));

    // The loop position. Nothing else remembers it.
    var iteration = 0;

    await using var client = new CopilotClient();
    await client.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

    try
    {
        while (true)
        {
            var plan = await File.ReadAllTextAsync(planPath);
            if (IsComplete(plan))
            {
                Console.WriteLine($"{new string('━', 60)}");
                Console.WriteLine($"plan reports complete after {iteration} iteration(s).");
                return 0;
            }

            if (iteration == maxIterations)
            {
                Console.WriteLine($"{new string('━', 60)}");
                Console.WriteLine($"iteration cap reached: {maxIterations} iterations and the plan is not complete.");
                Console.WriteLine($"first open item: {FirstOpenItem(plan) ?? "(none found)"}");
                return 3;
            }

            iteration++;
            Console.WriteLine($"{new string('━', 60)}");
            Console.WriteLine($"=== Iteration {iteration}/{maxIterations} ===");

            // A fresh session per iteration: context isolation is the point, and the plan
            // file is the hand-off. Nothing is resumed.
            await RunOneIterationAsync(client, target, model, iteration, prompt);
        }
    }
    finally
    {
        await client.StopAsync();
    }
}

// ─── SDK bindings ───────────────────────────────────────────────────────────
//
// Everything that knows about the Copilot SDK lives here. A port to another language
// replaces this method; the loop above it is the same shape.
static async Task RunOneIterationAsync(
    CopilotClient client, string target, string model, int iteration, string prompt)
{
    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        WorkingDirectory = target,
        // Unattended: the agent edits the target files and the plan without a prompt.
        OnPermissionRequest = PermissionHandler.ApproveAll,
    });

    using var subscription = session.On<SessionEvent>(evt =>
    {
        if (evt is ToolExecutionStartEvent tool)
        {
            Console.WriteLine($"  ⚙ {tool.Data.ToolName}");
        }
    });

    var answer = await session.SendAndWaitAsync(
        new MessageOptions { Prompt = prompt },
        TimeSpan.FromMinutes(10));

    var text = answer?.Data.Content ?? "(no answer)";
    var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(empty)";
    Console.WriteLine($"  answer: {Truncate(firstLine.Trim(), 160)}");
    Console.WriteLine($"iteration {iteration} complete.");
}

// ─── Helpers ────────────────────────────────────────────────────────────────
//
// The markers are part of the target's own contract, not the SDK's.

static bool IsComplete(string plan) =>
    plan.Contains("Status: complete", StringComparison.OrdinalIgnoreCase);

static string? FirstOpenItem(string plan)
{
    foreach (var raw in plan.Split('\n'))
    {
        var line = raw.Trim();
        if (line.StartsWith("- [ ]", StringComparison.Ordinal))
        {
            return line;
        }
    }

    return null;
}

static string Truncate(string text, int max) =>
    text.Length <= max ? text : text[..max] + "…";
