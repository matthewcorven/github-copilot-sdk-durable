import { DurableHarness, DurableHarnessOptions, DurableStore, ExtensionRegistry } from "../../lib/ts/durable.ts";

/** Only lifecycle plumbing; all durable operations belong to the native library. */
export async function runExample(
  body: (harness: DurableHarness, store: DurableStore, registry: ExtensionRegistry) => Promise<void>,
): Promise<void> {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  try {
    const fixture = process.env["DURABLE_EXAMPLE_REPLAY_FIXTURE"];
    if (fixture && !process.env["DURABLE_RUNTIME_PATH"]) {
      throw new Error("A replay fixture requires an explicit DURABLE_RUNTIME_PATH");
    }
    const options = new DurableHarnessOptions({ runtimeArguments: fixture ? ["--fixture", fixture] : [] });
    const harness = await DurableHarness.openAsync(store, registry, options);
    try {
      await body(harness, store, registry);
    } finally {
      await harness.disposeAsync();
    }
  } finally {
    store.dispose();
  }
}
