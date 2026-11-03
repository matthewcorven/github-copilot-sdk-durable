import { appendFileSync } from "node:fs";
import {
  DurableStore, DurableStoreOptions, DurableTask, DurableTaskRunner,
  Extension, ExtensionRegistry, TaskCommit,
} from "./durable.js";

const [file, effects, mode] = process.argv.slice(2);
if (!file || !effects || !mode || !["create", "interrupt", "complete"].includes(mode)) {
  throw new Error("Expected store, effects file and create|interrupt|complete");
}
const store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file }));
const definition = DurableTask.define("parity.once", () => ({ phase: "run" }),
  async (_input, checkpoint, runtime) => {
    await runtime.commitAsync(() => TaskCommit.running(checkpoint));
    if (await runtime.memoAsync("charged") === null) {
      appendFileSync(effects, "charged\n");
      await runtime.memoAsync("charged", true);
    }
    if (mode !== "complete") process.exit(73);
    return runtime.commitAsync(() => TaskCommit.completed("receipt"));
  });
const registry = new ExtensionRegistry();
registry.install(new Extension("parity", [], [], [definition]));
const runner = new DurableTaskRunner(store, registry);
try {
  if (mode === "create") {
    const task = await store.commitAsync(tx => tx.createTask(definition, null));
    await runner.runTaskAsync(task.id);
  } else {
    const outcomes = await runner.resumeAsync();
    if (outcomes.length !== 1 || outcomes[0]?.status !== "completed") {
      throw new Error(`Recovery must complete exactly one task: ${JSON.stringify(outcomes)}`);
    }
  }
} finally {
  await runner.disposeAsync();
  store.dispose();
}
