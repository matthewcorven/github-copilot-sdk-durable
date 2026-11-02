// Select plan tools without widening the session's original permission grants.
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, existsSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import {
  AgentChoices, DocumentDefinition, DocumentHistory, DurableHarness, DurableStore,
  Extension, ExtensionRegistry, ForkPolicy, PromptSection,
  type DurableConversation, type DurableTool,
} from "../../lib/ts/durable.ts";

async function main() {
  const directory = mkdtempSync(join(tmpdir(), "durable-plan-"));
  writeFileSync(join(directory, "server.ts"), "app.listen(3000);\n");
  const planDoc = new DocumentDefinition<string[]>(
    "app.plan", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => [],
  );
  let chat: DurableConversation;
  const submitPlan: DurableTool = {
    name: "submit_plan", description: "Submit the plan, one step per line.",
    parameters: { type: "object", properties: { steps: { type: "string" } }, required: ["steps"] },
    handler: async (args) => {
      if (args === null || typeof args !== "object" || !("steps" in args) ||
        typeof args.steps !== "string") throw new Error("submit_plan steps must be a string");
      const steps = args.steps;
      await chat.commitAsync((tx) => {
        const draft = tx.doc(planDoc, chat.id);
        draft.value = steps.split("\n").map((step) => step.trim()).filter(Boolean);
        draft.save(tx.appendEntry(chat.id, "plan", "submitted"));
      });
      return "Plan submitted.";
    },
  };
  const read: DurableTool = {
    name: "read_file", description: "Read a file", overridesBuiltInTool: true,
    parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
    handler: async (args) => {
      if (args === null || typeof args !== "object" || !("path" in args) ||
        typeof args.path !== "string") throw new Error("read_file path must be a string");
      const path = args.path;
      const full = resolve(directory, path);
      return existsSync(full) ? readFileSync(full, "utf8") : `error: ${path} not found`;
    },
  };
  const registry = new ExtensionRegistry();
  registry.install(new Extension("plan", [submitPlan], [
    new PromptSection("plan_mode", async () => "You are in plan mode. Read the code, then call submit_plan. Change nothing."),
  ]));
  registry.install(new Extension("coding", [read]));
  const grants = ["custom-tool:read_file", "custom-tool:submit_plan", "read", "write"];
  const store = DurableStore.createEphemeral();
  try {
    const harness = await DurableHarness.openAsync(store, registry);
    try {
      chat = await harness.rootAsync(new AgentChoices({
        extensions: ["coding"], workingDirectory: directory, permissions: grants,
      }));
      const tools = () => chat.agent().tools.map((tool) => tool.name);
      const effective = await chat.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model ?? "(not reported)"}`);
      assert.deepEqual(tools(), ["read_file"], "the conversation starts with the coding tools");
      await chat.configureAsync(new AgentChoices({
        extensions: ["plan"], tools: ["read_file", "submit_plan"],
        workingDirectory: directory, permissions: grants,
      }));
      const planTools = tools();
      const answer = await chat.submitAsync(
        "Make the port configurable. You are in plan mode: read server.ts, then call the submit_plan tool with the steps. Do not edit any file.",
      );
      console.log(`answer: ${answer?.data.content ?? ""}`);
      console.log(`plan: ${JSON.stringify(chat.snapshot(planDoc))}`);
      await chat.configureAsync(new AgentChoices({
        extensions: ["coding"], workingDirectory: directory,
        permissions: ["custom-tool:read_file", "read", "write"],
      }));
      const second = await chat.submitAsync("Go ahead.");
      console.log(`answer: ${second?.data.content ?? ""}`);
      assert.deepEqual(planTools, ["submit_plan"], "plan mode offers only the plan tool");
      assert.deepEqual(tools(), ["read_file"], "leaving plan mode restores the coding tools");
      const plan = chat.snapshot(planDoc);
      assert.ok(plan !== null && plan.length > 0, "the plan was written to the document");
      assert.ok(plan.every((step) => step.trim().length > 0), "every plan step has content");
    } finally {
      await harness.disposeAsync();
    }
  } finally {
    store.dispose();
    rmSync(directory, { recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });
