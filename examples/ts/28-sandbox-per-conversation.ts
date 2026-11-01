// Policy mode by default; --live also proves actual per-conversation tool writes.
// The C# sandbox document provides custom-tool isolation, not OS enforcement.
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
  AgentChoices, DocumentDefinition, DocumentHistory, DurableHarness, DurableHarnessOptions,
  DurableSandboxPolicy, DurableStore, Extension, ExtensionRegistry, ForkPolicy,
  type DurableConversation, type DurableTool,
} from "../../lib/ts/durable.ts";
import { assertPolicy } from "./sandbox-policy-22-35.ts";

async function main() {
  const live = process.argv.slice(2).includes("--live");
  if (process.argv.slice(2).some((arg) => arg !== "--live")) throw new Error("only --live is supported");
  const sandboxDoc = new DocumentDefinition<{ path: string | null }>(
    "example.sandbox", DocumentHistory.Latest, ForkPolicy.Initial, () => ({ path: null }),
  );
  const bySession = new Map<string, DurableConversation>();
  const write: DurableTool = {
    name: "write_file", description: "Write a file into your sandbox", overridesBuiltInTool: true,
    parameters: { type: "object", properties: { path: { type: "string" }, content: { type: "string" } }, required: ["path", "content"] },
    handler: async (args, invocation) => {
      if (args === null || typeof args !== "object" || !("path" in args) || !("content" in args) ||
        typeof args.path !== "string" || typeof args.content !== "string") throw new Error("write_file needs path and content strings");
      const conversation = bySession.get(invocation.sessionId);
      if (!conversation) return "(no conversation for this session)";
      const sandbox = conversation.snapshot(sandboxDoc);
      if (!sandbox?.path) return "(this conversation has no sandbox)";
      const base = resolve(sandbox.path);
      const full = resolve(base, args.path);
      if (!full.startsWith(base + sep)) throw new Error("write_file path must remain inside this conversation's directory");
      mkdirSync(resolve(full, ".."), { recursive: true });
      writeFileSync(full, args.content);
      return `wrote ${args.path} (${args.content.length} bytes)`;
    },
  };
  const registry = new ExtensionRegistry();
  registry.install(new Extension("sandbox", [write]));
  const alicePath = mkdtempSync(join(tmpdir(), "durable-sandbox-alice-"));
  const bobPath = mkdtempSync(join(tmpdir(), "durable-sandbox-bob-"));
  const store = DurableStore.createEphemeral();
  try {
    const harness = await DurableHarness.openAsync(store, registry,
      new DurableHarnessOptions({ enableFileHooks: false, tolerateHostHookFailures: live }));
    try {
      async function conversationFor(path: string) {
        const policy = new DurableSandboxPolicy({
          enabled: true, addCurrentWorkingDirectory: false, allowBypass: false,
          allowDevToolAccess: false, trustWorkingDirectory: true,
          filesystem: { readwritePaths: ["{workspace}"], readonlyPaths: [], deniedPaths: [] },
        });
        const choices = new AgentChoices({
          extensions: ["sandbox"], tools: ["write_file"], workingDirectory: path,
          sandbox: policy, permissions: ["custom-tool:write_file"],
        });
        const config = await harness.buildSessionConfig(choices);
        assert.ok(config.sandboxConfig, "the session configuration carries the sandbox policy");
        assertPolicy(policy, config.sandboxConfig, path, "the conversation policy");
        const conversation = await harness.createConversationAsync(choices);
        await conversation.commitAsync((tx) => {
          const draft = tx.doc(sandboxDoc, conversation.id);
          draft.value = { path };
          draft.save(tx.appendEntry(conversation.id, "sandbox", path));
        });
        assert.ok(conversation.sessionId, "the conversation has a native session ID");
        bySession.set(conversation.sessionId, conversation);
        return conversation;
      }
      const alice = await conversationFor(alicePath);
      const bob = await conversationFor(bobPath);
      const effective = await alice.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model ?? "(not reported)"}`);
      assert.notEqual(alicePath, bobPath, "the two conversations have different sandboxes");
      assert.equal(alice.snapshot(sandboxDoc)?.path, alicePath, "alice's document points at her sandbox");
      assert.equal(bob.snapshot(sandboxDoc)?.path, bobPath, "bob's document points at his sandbox");
      if (live) {
        await alice.submitAsync("Use the write_file tool to write 'from alice' into note.txt.");
        await bob.submitAsync("Use the write_file tool to write 'from bob' into note.txt.");
        assert.equal(readFileSync(join(alicePath, "note.txt"), "utf8"), "from alice", "alice's note landed in alice's sandbox");
        assert.equal(readFileSync(join(bobPath, "note.txt"), "utf8"), "from bob", "bob's note landed in bob's sandbox");
      } else console.log("LIVE: alice/bob tool-written note contents are unproven; the recording has no tool call.");
      console.log(`alice's document: ${alice.snapshot(sandboxDoc)?.path}`);
      console.log(`bob's document: ${bob.snapshot(sandboxDoc)?.path}`);
      console.log(`assertion set: ${live ? "policy + tool effects" : "policy only"}`);
    } finally { await harness.disposeAsync(); }
  } finally {
    store.dispose();
    rmSync(alicePath, { recursive: true });
    rmSync(bobPath, { recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });
