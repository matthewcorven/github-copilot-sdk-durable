// Persistent named subagents: spawn, send, stop, status, and registry recovery.
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices, DocumentDefinition, DocumentHistory, DurableHarness, DurableStore,
  DurableStoreOptions, Extension, ExtensionRegistry, ForkPolicy,
  type DurableConversation, type DurableTool,
} from "../../lib/ts/durable.ts";

async function main() {
  const subagents = new DocumentDefinition<Record<string, { conversationId: string; reported: string }>>(
    "app.subagents", DocumentHistory.Latest, ForkPolicy.Initial, () => ({}),
  );
  let host: DurableHarness;
  let chat: DurableConversation;
  const tool: DurableTool = {
    name: "subagent", description: "Manage persistent subagents: spawn, send, stop, status.",
    parameters: {
      type: "object", properties: {
        action: { type: "string" }, name: { type: "string", default: "" },
        message: { type: "string", default: "" },
      }, required: ["action"],
    },
    handler: async (args) => {
      if (args === null || typeof args !== "object" || !("action" in args) ||
        typeof args.action !== "string") throw new Error("subagent action must be a string");
      const action = args.action;
      const name = "name" in args && typeof args.name === "string" ? args.name : "";
      const message = "message" in args && typeof args.message === "string" ? args.message : "";
      const agents = chat.snapshot(subagents) ?? {};
      if (action === "status") {
        const lines: string[] = [];
        for (const each of name ? [name] : Object.keys(agents)) {
          const agent = Object.hasOwn(agents, each) ? agents[each] : undefined;
          if (agent) lines.push(`${each}: ${host.findConversation(agent.conversationId)?.busy ? "working" : "idle"}`);
        }
        return lines.length ? lines.join("\n") : "No subagents.";
      }
      if (!name) return `${action} needs a name.`;
      if (action === "spawn") {
        if (Object.hasOwn(agents, name)) return `${name} already exists; use send.`;
        const child = await host.createConversationAsync(new AgentChoices({
          model: "mai-code-1.1-flash",
          instructions: `You are the subagent "${name}". Answer the main agent's requests.`,
        }), null, chat.id);
        await chat.commitAsync((tx) => {
          const draft = tx.doc(subagents, chat.id);
          draft.value = { ...draft.value, [name]: { conversationId: child.id, reported: "0" } };
          draft.save(tx.appendEntry(chat.id, "subagent", `spawned ${name}`));
        });
        return `Started ${name}.`;
      }
      const agent = Object.hasOwn(agents, name) ? agents[name] : undefined;
      if (!agent) return `No subagent named ${name}.`;
      const target = await host.resumeConversationAsync(agent.conversationId);
      if (action === "stop") { await target.abortAsync(); return `Stopped ${name}.`; }
      if (!message) return `${action} needs a message.`;
      const answer = await target.submitAsync(message);
      return `[${name} answered] ${answer?.data.content ?? ""}`;
    },
  };
  const registry = new ExtensionRegistry();
  registry.install(new Extension("subagent-tools", [tool]));
  const choices = new AgentChoices({ extensions: ["subagent-tools"], permissions: ["custom-tool:subagent"] });
  const directory = mkdtempSync(join(tmpdir(), "durable-subagents-"));
  const options = new DurableStoreOptions();
  options.path = join(directory, "session.jsonl");
  try {
    const firstStore = DurableStore.create(options);
    let beforeReaderId: string;
    try {
      host = await DurableHarness.openAsync(firstStore, registry);
      try {
        chat = await host.rootAsync(choices);
        const effective = await chat.waitForEffectiveModelAsync();
        console.log(`effective model: ${effective.model ?? "(not reported)"}`);
        for (const [label, prompt] of [
          ["spawn", "Use the subagent tool to spawn a subagent named reader."],
          ["send", "Use the subagent tool to send 'What is the whale called?' to reader."],
          ["status", "Use the subagent tool to list my subagents."],
        ] as const) {
          const answer = await chat.submitAsync(prompt);
          console.log(`${label}: ${answer?.data.content ?? ""}`);
        }
        const snapshot = chat.snapshot(subagents);
        assert.ok(snapshot?.reader, "the first process registered the reader subagent");
        beforeReaderId = snapshot.reader.conversationId;
        console.log(`registry before restart: ${Object.keys(snapshot).join(", ")}`);
      } finally { await host.disposeAsync(); }
    } finally { firstStore.dispose(); }
    console.log("(process restarts)");
    const secondStore = DurableStore.create(options);
    try {
      host = await DurableHarness.openAsync(secondStore, registry);
      try {
        chat = await host.rootAsync(choices);
        const after = chat.snapshot(subagents);
        assert.ok(after?.reader, "the background subagent survived the restart");
        console.log(`registry after restart: ${Object.keys(after).join(", ")}`);
        const listed = await chat.submitAsync("Use the subagent tool to list my subagents.");
        console.log(`status after restart: ${listed?.data.content ?? ""}`);
        assert.equal(after.reader.conversationId, beforeReaderId,
          "the restarted process points at the same subagent conversation");
        assert.ok(listed, "the restarted process can list the subagent");
        assert.ok(listed.data.content.includes("reader"), "the restarted process still knows the subagent by name");
      } finally { await host.disposeAsync(); }
    } finally { secondStore.dispose(); }
  } finally { rmSync(directory, { recursive: true }); }
}

main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });
