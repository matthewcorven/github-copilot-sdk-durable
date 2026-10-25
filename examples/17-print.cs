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
// Fail-closed: the prompt asks about the directory, so the conversation is granted
// the built-in read tools and nothing else.
var root = await harness.RootAsync(new AgentChoices { Extensions = ["coding"], Permissions = ["read"] });

// The host awaits its own submission, not global idle.
var answer = await root.SubmitAsync(prompt);
Console.WriteLine(answer?.Data.Content ?? "(no answer)");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The host awaited its own submission: the answer is complete, not a fragment.
Check.That(answer is not null, "the model answered");
Check.That(answer!.Data.Content.Trim().Length > 0, "the answer is not empty");
Check.That(root.Answers.Count == 1, "the turn produced exactly one answer");