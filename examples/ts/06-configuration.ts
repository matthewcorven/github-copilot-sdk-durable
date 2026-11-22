// 06 — Configuration. Each configure replaces choices rather than merging.
import { AgentChoices, Extension } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";
import { fileTool } from "./tools.ts";

await runExample(async (harness, _store, registry) => {
  const files = new Extension("files", [fileTool("read", "Read a file"), fileTool("write", "Write a file")]);
  registry.install(files);
  registry.install(new Extension("search", [fileTool("grep", "Search files")]));
  const settings = { retry: { maxRetries: 5 }, toolExecution: "sequential" };
  const root = await harness.rootAsync();
  console.log(`effective model: ${(await root.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const tools = () => root.agent().tools.map(t => t.name);
  console.log(`default tools: ${tools().join(", ")}`);
  await root.configureAsync(new AgentChoices({
    model: "gpt-6-luna", reasoningEffort: "high", tools: ["write", "read"],
    permissions: ["custom-tool:write", "custom-tool:read"],
  }));
  const storedModel = root.choices()!.model;
  const storedEffort = root.choices()!.reasoningEffort;
  const storedTools = root.choices()!.tools!;
  const agent = root.agent();
  await root.configureAsync(new AgentChoices({
    model: "gpt-6-luna", reasoningEffort: "high", extensions: ["files"], tools: ["write", "read"],
    permissions: ["custom-tool:write", "custom-tool:read"],
  }));
  const withoutSearch = tools();
  await root.configureAsync(new AgentChoices({
    model: "gpt-6-luna", extensions: ["files", "search"],
    permissions: ["custom-tool:read", "custom-tool:write", "custom-tool:grep"],
  }));
  registry.uninstall("files");
  const filesUninstalled = tools();
  registry.install(files);
  const filesReinstalled = tools();
  settings.retry.maxRetries = 10;
  Check.equal("gpt-6-luna", storedModel, "the stored model is the one that was configured");
  Check.equal("high", storedEffort, "the stored reasoning effort is the one that was configured");
  Check.sequence(["write", "read"], storedTools, "the stored tool names are the ones that were configured");
  Check.sequence(["write", "read"], agent.tools.map(t => t.name), "the resolved tools follow the stored names");
  Check.equal(null, root.choices()!.reasoningEffort, "a later configure() clears the fields it omits");
  Check.equal(null, root.choices()!.tools, "a later configure() clears the tools it omits");
  Check.that(!withoutSearch.includes("grep"), "the deselected extension's tools are gone");
  Check.that(withoutSearch.includes("read"), "the still-selected extension keeps its tools");
  Check.that(root.choices()!.extensions!.includes("files"), "the uninstalled extension is still selected by name");
  Check.that(!filesUninstalled.includes("read"), "an uninstalled extension contributes no tools");
  Check.that(filesUninstalled.includes("grep"), "the other selected extension still contributes its tools");
  Check.that(filesReinstalled.includes("read"), "reinstalling the extension brings its tools back");
  Check.equal(10, settings.retry.maxRetries, "a settings change takes effect immediately");
  Check.that(!("retry" in root.choices()!), "settings are not part of the stored choices");
});
