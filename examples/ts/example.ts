import { DurableHarness, DurableStore, ExtensionRegistry } from "../../lib/ts/durable.ts";

/** Only lifecycle plumbing; all durable operations belong to the native library. */
export async function runExample(
  body: (harness: DurableHarness, store: DurableStore, registry: ExtensionRegistry) => Promise<void>,
): Promise<void> {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const harness = await DurableHarness.openAsync(store, registry);
  try {
    await body(harness, store, registry);
  } finally {
    await harness.disposeAsync();
    store.dispose();
  }
}
