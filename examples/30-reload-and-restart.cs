// 30 — Reload an extension while a call runs, then restart the process.
//
// Running work finishes on the code it started with; stored choices are names, so they
// survive a restart and bind to whatever code the new process installs.
//
// The example runs in two phases. The first phase reloads the extension while a tool
// call is in flight: the running call finishes on the old code, and the next call uses
// the new code. The second phase is a fresh process over the same store: the conversation
// still selects "versioned", but the new process has not installed it yet.
//
// The second phase is a separate process, so the example re-launches its own compiled
// assembly with a phase argument.
//
// Mirrors the Pi Durable example `30-reload-and-restart.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Diagnostics;

// The phase argument. The second phase is a separate process over the same store.
var phase = args.Length > 0 ? args[0] : "first";
var directory = Environment.GetEnvironmentVariable("DURABLE_RELOAD_DIR")
    ?? Path.Combine(Path.GetTempPath(), $"durable-reload-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "session.json");

// Stand-in for code loaded from disk: each call builds the extension as the file
// currently reads.
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var holdOpen = true;

AIFunction LoadVersioned(string version) => CopilotTool.DefineTool(
    async () =>
    {
        started.TrySetResult();
        // The gate holds the call open, so the reload happens while it runs. The
        // handler is async: blocking here would deadlock the SDK's tool dispatch.
        if (holdOpen)
        {
            await gate.Task;
        }

        return version;
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "version", Description = "Report the tool's code version" });

if (phase == "second")
{
    // ─── Second process ──────────────────────────────────────────────────────
    //
    // The conversation still selects "versioned", but this process has not installed
    // it yet.
    var secondRegistry = new ExtensionRegistry();
    var secondStore = new DurableStore(storePath);
    await using var secondHarness = await DurableHarness.OpenAsync(secondStore, secondRegistry);

    var secondRoot = await secondHarness.RootAsync();
    Console.WriteLine($"after restart, before install: [{string.Join(", ", secondRoot.Agent().Tools.Select(tool => tool.Name))}]");

    secondRegistry.Install(new Extension("versioned", tools: [LoadVersioned("v3")]));
    Console.WriteLine($"after install: [{string.Join(", ", secondRoot.Agent().Tools.Select(tool => tool.Name))}]");

    // The stored selection is a name, so it binds to the newly installed code.
    await secondRoot.ConfigureAsync(new AgentChoices
    {
        Extensions = ["versioned"],
        Tools = ["version"],
    });
    Console.WriteLine($"after install, configured: [{string.Join(", ", secondRoot.Agent().Tools.Select(tool => tool.Name))}]");
    return;
}

// ─── First process ──────────────────────────────────────────────────────────
//
// The store is file-backed, so the second process sees the same conversations.
var registry = new ExtensionRegistry();
var store = new DurableStore(storePath);
await using var harness = await DurableHarness.OpenAsync(store, registry);

registry.Install(new Extension("versioned", tools: [LoadVersioned("v1")]));

var root = await harness.RootAsync(new AgentChoices
{
    // Selected by name. The name is what is stored, never the code.
    Extensions = ["versioned"],
    Tools = ["version"],
});

// The file changes while a call runs: the running call finishes on v1, the next call
// uses v2.
var submission = root.SubmitAsync("Call the version tool and report what it returns.");
await started.Task.WaitAsync(TimeSpan.FromMinutes(2));

// The reload: the registry now holds the new code. The live session keeps the tool
// it was created with, so the app re-binds the resolved tools to pick up the new
// implementation.
registry.Install(new Extension("versioned", tools: [LoadVersioned("v2")]));
await root.ConfigureAsync(new AgentChoices
{
    Extensions = ["versioned"],
    Tools = ["version"],
});

// Release the running call. It finishes on the code it started with.
gate.TrySetResult();
await submission;

Console.WriteLine($"call running during the reload: {root.Answers.LastOrDefault()}");
var duringReload = root.Answers.LastOrDefault() ?? "";

// The next call uses the new code.
gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
holdOpen = false;
await root.SubmitAsync("Call the version tool again and report what it returns.");
Console.WriteLine($"next call: {root.Answers.LastOrDefault()}");
var afterReload = root.Answers.LastOrDefault() ?? "";

// ─── Restart ────────────────────────────────────────────────────────────────
//
// The second process opens the same store. The conversation still selects
// "versioned" by name.
var assembly = Environment.GetCommandLineArgs()[0];
var startInfo = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
startInfo.ArgumentList.Add(assembly);
startInfo.ArgumentList.Add("second");
startInfo.Environment["DURABLE_RELOAD_DIR"] = directory;
var process = Process.Start(startInfo)!;
var output = process.StandardOutput.ReadToEnd();
var error = process.StandardError.ReadToEnd();
process.WaitForExit();
Console.Write(output.Trim());
if (process.ExitCode != 0)
{
    Console.WriteLine($"second phase failed ({process.ExitCode}): {error}");
}

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// Running work finishes on the code it started with: the call that was in
// flight during the reload returned v1, and the next call used v2.
Check.Contains(duringReload, "v1", "the call running during the reload finished on the old code");
Check.Contains(afterReload, "v2", "the next call used the reloaded code");

// The second process opened the same store and found the conversation, still
// selecting "versioned" by name.
Check.Contains(output, "after restart, before install: []", "the restarted process had not installed the extension yet");
Check.Contains(output, "after install: [version]", "the restarted process bound the stored name to the new code");

Directory.Delete(directory, recursive: true);
