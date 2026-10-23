// 22 — Background subagents.
//
// Persistent background subagents. One `subagent` tool lets the main agent start named
// subagents, message them, stop them mid-answer, and list them. Subagents keep working
// while the main agent answers the user, and each answer is delivered back to the main
// agent as a new message once it arrives. Everything survives a restart.
//
// Mirrors the Pi Durable example `22-subagent-background.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var directory = Path.Combine(Path.GetTempPath(), $"durable-subagents-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "session.json");

// Each subagent is its own conversation with its own transcript, so it remembers
// earlier messages. The main conversation keeps a small document that maps subagent
// names to their conversations.
var Subagents = new DocumentDefinition<Dictionary<string, Dictionary<string, string>>>(
    "app.subagents",
    DocumentHistory.Latest,
    ForkPolicy.Initial,
    () => []);

DurableHarness? host = null;
DurableConversation? main = null;

// One `subagent` tool: spawn, send, stop, status.
//
// A parameter with a default value is optional in the tool's schema; a nullable
// reference type is not, so the optional ones carry an empty-string default.
var subagentTool = CopilotTool.DefineTool(
    async ([Description("Action: spawn, send, stop, or status")] string action,
          [Description("Subagent name. Required for spawn, send, and stop; omit for status.")] string name = "",
          [Description("Message to deliver")] string message = "") =>
    {
        var registry = main!.Snapshot(Subagents) ?? [];

        if (action == "status")
        {
            var names = string.IsNullOrEmpty(name) ? registry.Keys.ToList() : [name];
            var lines = new List<string>();
            foreach (var each in names)
            {
                if (!registry.TryGetValue(each, out var entry))
                {
                    continue;
                }

                // A subagent is busy while it has a run in flight.
                var child = host!.FindConversation(entry["conversationId"]);
                var busy = child is not null && child.Busy;
                lines.Add($"{each}: {(busy ? "working" : "idle")}");
            }

            return lines.Count == 0 ? "No subagents." : string.Join("\n", lines);
        }

        if (string.IsNullOrEmpty(name))
        {
            return $"{action} needs a name.";
        }

        if (action == "spawn")
        {
            if (registry.ContainsKey(name))
            {
                return $"{name} already exists; use send.";
            }

            // The child is owned by a task of the main conversation: it starts as a
            // copy of the main agent.
            var child = await host!.CreateConversationAsync(
                new AgentChoices
                {
                    Model = "mai-code-1.1-flash",
                    Instructions = $"You are the subagent \"{name}\". Answer the main agent's requests.",
                },
                ownerConversationId: main!.Id);

            await main.CommitAsync(async tx =>
            {
                var state = tx.Doc(Subagents, main.Id);
                state.Value[name] = new Dictionary<string, string>
                {
                    ["conversationId"] = child.Id,
                    ["reported"] = "0",
                };
                state.Save(tx.AppendEntry(main.Id, "subagent", $"spawned {name}"));
            });

            return $"Started {name}.";
        }

        if (!registry.TryGetValue(name, out var agent))
        {
            return $"No subagent named {name}.";
        }

        if (action == "stop")
        {
            // Aborts the subagent's current answer and tools. It stays usable.
            var child = host!.FindConversation(agent["conversationId"]);
            if (child is not null)
            {
                await child.Session.AbortAsync();
            }

            return $"Stopped {name}.";
        }

        if (string.IsNullOrEmpty(message))
        {
            return $"{action} needs a message.";
        }

        // send: deliver the message to the subagent's own conversation.
        var target = host!.FindConversation(agent["conversationId"]);
        if (target is null)
        {
            return $"No subagent named {name}.";
        }

        var answer = await target.SubmitAsync(message);
        return $"[{name} answered] {answer?.Data.Content}";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "subagent",
        Description = "Manage persistent subagents that work in the background. " +
            "Actions: 'spawn' (name, message), 'send' (name, message), 'stop' (name), " +
            "'status' (name, or omit the name for all subagents). " +
            "Always call this tool with action='status' to list the subagents.",
    });

var registryExtensions = new ExtensionRegistry();
registryExtensions.Install(new Extension("subagent-tools", tools: [subagentTool]));

// First process: start a subagent and message it.
var first = await DurableHarness.OpenAsync(new DurableStore(storePath), registryExtensions);
host = first;
main = await first.RootAsync(new AgentChoices { Extensions = ["subagent-tools"] });

var spawned = await main.SubmitAsync("Use the subagent tool to spawn a subagent named reader.");
Console.WriteLine($"spawn: {spawned?.Data.Content}");

var sent = await main.SubmitAsync("Use the subagent tool to send 'What is the whale called?' to reader.");
Console.WriteLine($"send: {sent?.Data.Content}");

var listed = await main.SubmitAsync("Use the subagent tool to list my subagents.");
Console.WriteLine($"status: {listed?.Data.Content}");

// The registry is durable: it survives closing the harness.
var before = main.Snapshot(Subagents)!;
Console.WriteLine($"registry before restart: {string.Join(", ", before.Keys)}");

await first.DisposeAsync();
Console.WriteLine("(process restarts)");

// Second process: the registry is still there, and the subagent's conversation
// still exists.
var second = await DurableHarness.OpenAsync(new DurableStore(storePath), registryExtensions);
host = second;
main = await second.RootAsync(new AgentChoices { Extensions = ["subagent-tools"] });

var after = main.Snapshot(Subagents)!;
Console.WriteLine($"registry after restart: {string.Join(", ", after.Keys)}");
foreach (var (name, agent) in after)
{
    Console.WriteLine($"  {name}: conversation={agent["conversationId"]}");
}

var listedAgain = await main.SubmitAsync("Use the subagent tool to list my subagents.");
Console.WriteLine($"status after restart: {listedAgain?.Data.Content}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The registry is durable: the background subagent survived the restart, and
// the restarted process still knows about it.
Check.That(after.ContainsKey("reader"), "the background subagent survived the restart");
Check.Equal(
    before["reader"]["conversationId"],
    after["reader"]["conversationId"],
    "the restarted process points at the same subagent conversation");
Check.That(listedAgain is not null, "the restarted process can list the subagent");
Check.Contains(listedAgain!.Data.Content, "reader", "the restarted process still knows the subagent by name");

await second.DisposeAsync();
Directory.Delete(directory, recursive: true);