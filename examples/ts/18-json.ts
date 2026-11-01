// 18 - JSON mode (LIVE). Snapshot followed by real SDK event JSON lines.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection } from "../../lib/ts/durable.ts";

const prompt = process.argv[2] ?? "What is in this directory?";
const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
registry.install(new Extension("coding", [], [new PromptSection("preamble", async () => "You are a concise coding assistant.", false)]));
const harness = await DurableHarness.openAsync(store, registry);
let unsubscribe: (() => void) | undefined;
try {
  const root = await harness.rootAsync(new AgentChoices({ extensions: ["coding"], permissions: ["read"] }));
  const report = await root.waitForEffectiveModelAsync();
  console.error(`effective model: ${report.model ?? "(not reported)"}`);
  const lines: string[] = [];
  const emit = (value: object): void => {
    const line = JSON.stringify(value);
    lines.push(line);
    console.log(line);
  };
  emit({ type: "snapshot", events: (await root.contextAsync()).length });
  unsubscribe = root.session.on(event => {
    const data = event.type === "user.message" || event.type === "assistant.message"
      ? event.data.content : event.type === "tool.execution_start" ? event.data.toolName : null;
    emit({ type: "event", event: event.type, id: event.id,
      agentId: "agentId" in event ? event.agentId ?? null : null, data });
  });
  await root.submitAsync(prompt);
  await new Promise(resolve => setTimeout(resolve, 100));
  Check.that(lines.length >= 1, "the run produced at least one line");
  Check.that(lines.every(line => line.trimStart().startsWith("{")), "every line is a JSON object");
  Check.that(lines[0]?.includes('"type":"snapshot"') === true, "the first line is the snapshot");
  Check.that(lines.some(line => line.includes('"type":"event"')), "the run produced events");
} finally {
  unsubscribe?.();
  await harness.disposeAsync();
  store.dispose();
}
