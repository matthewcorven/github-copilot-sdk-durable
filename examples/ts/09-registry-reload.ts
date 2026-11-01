// 09 — Registry reload. Installing a name replaces its code without reconfiguration.
import { AgentChoices, Extension } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";
import { fileTool } from "./tools.ts";

await runExample(async (harness, _store, registry) => {
  const loadFiles = (description: string) => new Extension("files", [
    fileTool("read", "Read a file"), fileTool("grep", description),
  ]);
  registry.install(loadFiles("Search files"));
  const root = await harness.rootAsync(new AgentChoices({ permissions: ["custom-tool:read", "custom-tool:grep"] }));
  console.log(`effective model: ${(await root.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  registry.install(loadFiles("Search files, faster"));
  registry.install(new Extension("audit", [fileTool("read", "Read a file (audited)")]));
  console.log(`with audit: ${root.agent().tools.map(t => `${t.name}: ${t.description}`).join(", ")}`);
  registry.uninstall("audit");
  Check.equal(1, registry.extensions.filter(e => e.name === "files").length, "the reload replaced the extension rather than adding one");
  Check.equal("Search files, faster", registry.find("files")!.tools.find(t => t.name === "grep")!.description, "the registry holds the new code");
  Check.equal("Search files, faster", root.agent().tools.find(t => t.name === "grep")!.description, "the conversation resolves the reloaded code");
  registry.uninstall("audit");
  Check.equal(null, registry.find("audit"), "the uninstalled extension is gone from the registry");
  Check.equal("Read a file", root.agent().tools.find(t => t.name === "read")!.description, "the conversation falls back to the remaining code");
});
