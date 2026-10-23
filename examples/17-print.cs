// 17 — Print mode.
//
// Submit one prompt and print its answer, like `pi -p`.
//
// The host awaits its own submission, not global idle.
//
// Mirrors the Pi Durable example `17-print.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var prompt = args.Length > 0 ? args[0] : "What is in this directory?";

var store = new DurableStore();
var registry = new ExtensionRegistry();

registry.Install(new Extension(
    "coding",
    sections: [new PromptSection("preamble", _ => Task.FromResult<string?>("You are a concise coding assistant."), tag: false)]));

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync(new AgentChoices { Extensions = ["coding"] });

// The host awaits its own submission, not global idle.
var answer = await root.SubmitAsync(prompt);
Console.WriteLine(answer?.Data.Content ?? "(no answer)");