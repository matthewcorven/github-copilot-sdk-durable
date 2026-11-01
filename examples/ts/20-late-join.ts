// 20 - Late join (LIVE). Requires new deltas after joining an in-progress run.
import { AgentChoices, Check, DurableHarness, DurableStore, ExtensionRegistry } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
let unsubscribe: (() => void) | undefined;
try {
  const root = await harness.rootAsync(new AgentChoices({ streaming: true }));
  const report = await root.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const run = root.submitAsync("Count slowly from one to five, one number per sentence, then say DONE.");
  await new Promise(resolve => setTimeout(resolve, 1500));
  const snapshot = await root.contextAsync();
  console.log(`snapshot events: ${snapshot.length}`);
  const deltas: string[] = [];
  unsubscribe = root.session.on(event => {
    if (event.type === "assistant.message_delta" && event.data.deltaContent) {
      deltas.push(event.data.deltaContent);
      console.log(`event text delta: ${event.data.deltaContent}`);
    } else {
      console.log(`event: ${event.type}`);
    }
  });
  const answer = await run;
  Check.that(snapshot.length >= 1, "the late joiner received the current state");
  Check.that(snapshot.some(e => e.type === "user.message"), "the snapshot holds the user message");
  Check.that(deltas.length >= 1, "the late joiner received live deltas");
  const completed = Check.notNull(answer, "the run finished");
  Check.that(completed.data.content.trim().length > 0, "the answer is not empty");
  console.log(`answer: ${completed.data.content}; deltas received after joining: ${deltas.length}`);
} finally {
  unsubscribe?.();
  await harness.disposeAsync();
  store.dispose();
}
