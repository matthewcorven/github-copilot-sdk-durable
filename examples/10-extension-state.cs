// 10 — Extension state.
//
// An extension that keeps its own per-conversation document.
//
// A todo list per conversation. Rewindable with `asOf`, so a fork starts with the todos its
// parent had at the fork entry. The tool writes the document; the section shows it to the
// model before every request.
//
// Mirrors the Pi Durable example `10-extension-state.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A todo list per conversation. Rewindable with `asOf`, so a fork starts with
// the todos its parent had at the fork entry.
var Todos = new DocumentDefinition<List<string>>(
    "example.todos",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => []);

await using var harness = await DurableHarness.OpenAsync(store, registry);

// The tool writes the document; the section shows it to the model before every
// request. Nothing needs to create it up front: tx.Doc() creates it on first
// write, and the section treats a missing document as an empty list.
DurableConversation? chat = null;

var todo = CopilotTool.DefineTool(
    ([Description("Item to add")] string item) =>
    {
        var conversation = chat!;
        conversation.CommitAsync(async tx =>
        {
            var todos = tx.Doc(Todos, conversation.Id);
            todos.Value.Add(item);
            todos.Save(tx.AppendEntry(conversation.Id, "todo", item));
        }).GetAwaiter().GetResult();
        return $"added {item}";
    },
    factoryOptions: new AIFunctionFactoryOptions
    {
        Name = "todo",
        Description = "Add an item to your todo list",
    });

var Todo = new Extension(
    "todo",
    tools: [todo],
    sections:
    [
        new PromptSection("todos", input =>
        {
            var todos = input.Conversation?.Snapshot(Todos);
            return Task.FromResult<string?>(
                todos is null || todos.Count == 0 ? null : string.Join("\n", todos));
        }),
    ]);

registry.Install(Todo);

chat = await harness.RootAsync(new AgentChoices
{
    Extensions = ["todo"],
    Permissions = ["custom-tool:todo"],
});

// The tool writes the document; the section shows it to the model before every request.
var effectiveModel = await chat.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var reply = await chat.SubmitAsync("Add 'fix the build' to my todo list, then confirm.");
Console.WriteLine($"answer: {reply?.Data.Content}");

Console.WriteLine($"todos: [{string.Join(", ", chat.Snapshot(Todos) ?? [])}]");

// The next request's system prompt carries the list.
var context = await chat.ContextAsync();
foreach (var evt in context)
{
    if (evt is not SystemMessageEvent system)
    {
        continue;
    }

    Console.WriteLine($"system message: {system.Data.Content}");
}

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// The tool wrote the document, and the document is the durable state: it
// outlives the turn that produced it.
Check.Sequence(new[] { "fix the build" }, chat.Snapshot(Todos) ?? [], "the tool wrote the todo into the document");
Check.That(reply is not null, "the model answered");

// The section is rendered per request and injected as additional context, so the
// model sees the list on the next request without the app storing any prompt
// state. The section reads the document, so it sees the value the tool wrote.
var rendered = await harness.RenderSectionsAsync(chat);
Check.Contains(rendered, "fix the build", "the section renders the todo list from the document");

// The document is the durable state: it holds the todo the tool wrote.
Check.Sequence(new[] { "fix the build" }, chat.Snapshot(Todos) ?? [], "the document holds the todo");