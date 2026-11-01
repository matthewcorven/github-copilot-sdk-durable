// 21 - Foreground subagent (LIVE). Parent tool call and an owned child SDK session.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
const harness = await DurableHarness.openAsync(store, registry);
let parent: Awaited<ReturnType<typeof harness.rootAsync>> | undefined;
try {
  const subagent = {
    name: "subagent",
    description: "Delegate a self-contained task to a subagent and get its answer back.",
    parameters: {
      type: "object", properties: { task: { type: "string", description: "What the subagent should do" } }, required: ["task"],
    },
    handler: async (args: unknown) => {
      if (args === null || typeof args !== "object") throw new Error("Tool arguments must be an object");
      const task: unknown = Reflect.get(args, "task");
      if (typeof task !== "string") throw new Error("Tool argument task must be a string");
      const owner = Check.notNull(parent, "the parent is ready before its tool runs");
      const child = await harness.createConversationAsync(new AgentChoices({ model: "mai-code-1.1-flash" }), null, owner.id);
      const answer = Check.notNull(await child.submitAsync(task), "the child answered");
      return `${answer.data.content} (child conversation: ${child.id})`;
    },
  };
  registry.install(new Extension("subagent", [subagent]));
  parent = await harness.rootAsync(new AgentChoices({ extensions: ["subagent"], permissions: ["custom-tool:subagent"] }));
  const report = await parent.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const answer = await parent.submitAsync("Use the subagent tool to name three prime numbers, then tell me what it said.");
  const children = store.conversations.filter(c => c.ownerConversationId === parent!.id);
  Check.equal(1, children.length, "the subagent tool created exactly one child conversation");
  Check.notNull(store.conversation(children[0]!.id), "the child conversation is in the store");
  Check.that(children[0]!.id !== parent.id, "the child is a different conversation");
  const answered = Check.notNull(answer, "the parent answered");
  Check.that(answered.data.content.includes("2"), "the parent's answer carries the child's answer back");
  console.log(`answer: ${answered.data.content}`);
  for (const conversation of store.conversations) {
    console.log(`conversation ${conversation.id}: owner conversation=${conversation.ownerConversationId ?? "(none)"}`);
  }
} finally {
  await harness.disposeAsync();
  store.dispose();
}
