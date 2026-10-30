// 18 — JSON mode.
//
// Stream one prompt's run as JSON lines, like `pi --mode json`.
//
// Each line is one session event, serialized as JSON. The first line is the session's
// current state; later lines are only what changes after that.
//
// Mirrors the Pi Durable example `18-json.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using System.Text.Json;
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

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await root.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// Attach before submitting, so the stream covers the whole run.
var lines = new List<string>();
var print = (object? value) =>
{
    var line = JsonSerializer.Serialize(value, value!.GetType(), DurableJson.Options);
    lines.Add(line);
    Console.WriteLine(line);
};

// The first line is the current state: the snapshot a late joiner would see.
var snapshot = await root.ContextAsync();
print(new { type = "snapshot", events = snapshot.Count });

// Later lines are only what changes after that.
using var subscription = root.Session.On<SessionEvent>(evt =>
{
    print(new
    {
        type = "event",
        @event = evt.Type,
        id = evt.Id,
        agentId = evt.AgentId,
        data = evt switch
        {
            UserMessageEvent user => user.Data.Content,
            AssistantMessageEvent assistant => assistant.Data.Content,
            ToolExecutionStartEvent tool => tool.Data.ToolName,
            _ => null,
        },
    });
});

await root.SubmitAsync(prompt);

// Let the last batch reach the listener before stopping.
await Task.Delay(100);

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// Every line is one JSON event, and the run produced a snapshot and at least
// one event.
Check.That(lines.Count >= 1, "the run produced at least one line");
Check.That(lines.All(line => line.TrimStart().StartsWith('{')), "every line is a JSON object");
Check.That(lines.Any(line => line.Contains("\"type\":\"snapshot\"")), "the first line is the snapshot");
Check.That(lines.Any(line => line.Contains("\"type\":\"event\"")), "the run produced events");