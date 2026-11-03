// 10 — Extension state. LIVE: a recorded answer cannot prove a tool wrote state.
import { defineTool } from "@github/copilot-sdk";
import { AgentChoices, DocumentDefinition, DocumentHistory, Extension, ForkPolicy, PromptSection, type DurableConversation } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store, registry) => {
  const todos = new DocumentDefinition<string[]>("example.todos", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => []);
  let chat: DurableConversation | null = null;
  const todo = defineTool("todo", {
    description: "Add an item to your todo list",
    parameters: { type: "object", properties: { item: { type: "string", description: "Item to add" } }, required: ["item"] },
    handler: async args => {
      const item = (args as { item: string }).item;
      const conversation = Check.notNull(chat, "the todo tool has a conversation");
      await conversation.commitAsync(tx => {
        const draft = tx.doc(todos, conversation.id);
        draft.value.push(item);
        draft.save(tx.appendEntry(conversation.id, "todo", item));
      });
      return `added ${item}`;
    },
  });
  registry.install(new Extension("todo", [todo], [
    new PromptSection("todos", async input => {
      const value = input.conversation === null ? null : store.document(todos.kind, input.conversation.id)?.latest;
      return Array.isArray(value) && value.length > 0 ? value.join("\n") : null;
    }),
  ]));
  chat = await harness.rootAsync(new AgentChoices({ extensions: ["todo"], permissions: ["custom-tool:todo"] }));
  console.log(`effective model: ${(await chat.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const reply = await chat.submitAsync("Add 'fix the build' to my todo list, then confirm.");
  console.log(`answer: ${reply?.data.content ?? "(no answer)"}`);
  console.log(`todos: [${(chat.snapshot(todos) ?? []).join(", ")}]`);
  for (const event of await chat.contextAsync()) {
    if (event.type === "system.message") console.log(`system message: ${event.data.content}`);
  }
  Check.sequence(["fix the build"], chat.snapshot(todos) ?? [], "the tool wrote the todo into the document");
  Check.notNull(reply, "the model answered");
  const rendered = await harness.renderSectionsAsync(chat);
  Check.contains(rendered, "fix the build", "the section renders the todo list from the document");
  Check.sequence(["fix the build"], chat.snapshot(todos) ?? [], "the document holds the todo");
});
