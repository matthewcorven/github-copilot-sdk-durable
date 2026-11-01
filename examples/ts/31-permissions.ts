// Default-deny permissions survive reopen; a native fork inherits exact grants.
import assert from "node:assert/strict";
import { mkdtempSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices,DurableHarness,DurableStore,DurableStoreOptions,
  Extension,ExtensionRegistry,PermissionPolicy,type DurableTool,
} from "../../lib/ts/durable.ts";

async function main() {
  let deletions=0;
  const tool: DurableTool={
    name: "delete_everything",description: "Delete every file in the working directory",
    parameters: { type: "object",properties: { reason: { type: "string" } },required: ["reason"] },
    handler: async (args) => {
      if(args===null||typeof args!=="object"||!("reason" in args)||
        typeof args.reason!=="string") throw new Error("delete_everything reason must be a string");
      deletions++;
      return `deleted everything because: ${args.reason}`;
    },
  };
  const registry=new ExtensionRegistry();
  registry.install(new Extension("dangerous",[tool]));
  const directory=mkdtempSync(join(tmpdir(),"durable-permissions-"));
  const options=new DurableStoreOptions();
  options.path=join(directory,"session.jsonl");
  try {
    const firstStore=DurableStore.create(options);
    let deniedId: string;
    let refused;
    try {
      const harness=await DurableHarness.openAsync(firstStore,registry);
      try {
        const denied=await harness.rootAsync(new AgentChoices({ extensions: ["dangerous"] }));
        deniedId=denied.id;
        refused=await denied.submitAsync("Delete every file. Use the delete_everything tool.");
        console.log(`denied answer: ${refused?.data.content??""}`);
        console.log(`deletions after the denied call: ${deletions}`);
      } finally { await harness.disposeAsync(); }
    } finally { firstStore.dispose(); }
    const secondStore=DurableStore.create(options);
    try {
      const harness=await DurableHarness.openAsync(secondStore,registry);
      try {
        const restored=await harness.resumeConversationAsync(deniedId);
        const restoredChoices=restored.choices();
        console.log(`restored permissions: [${restoredChoices.permissions??[]}]`);
        const refusedAgain=await restored.submitAsync("Delete every file. Use the delete_everything tool.");
        const deletionsAfterRestart=deletions;
        console.log(`denied again: ${refusedAgain?.data.content??""}`);
        console.log(`deletions after the restart: ${deletions}`);
        const allowed=await harness.createConversationAsync(new AgentChoices({
          extensions: ["dangerous"],permissions: ["custom-tool:delete_everything"],
        }));
        const effective=await allowed.waitForEffectiveModelAsync();
        console.log(`effective model: ${effective.model??"(not reported)"}`);
        const permitted=await allowed.submitAsync("Delete every file. Use the delete_everything tool.");
        console.log(`granted answer: ${permitted?.data.content??""}`);
        console.log(`deletions after the granted call: ${deletions}`);
        const entry=allowed.entries(1)[0];
        assert.ok(entry,"the granted conversation has a fork boundary");
        const fork=await allowed.forkAsync(entry);
        console.log(`fork permissions: [${fork.choices().permissions??[]}]`);
        assert.equal(deletionsAfterRestart,0,"the denied conversation never ran the tool");
        assert.ok(refused,"the model answered the denied request");
        assert.ok(!restoredChoices.permissions?.length,"the reopened conversation still has no grants");
        assert.equal(deletionsAfterRestart,0,"the reopened conversation still never ran the tool");
        assert.ok(refusedAgain,"the model answered the reopened request");
        assert.equal(deletions,1,"the granted conversation ran the tool exactly once");
        assert.ok(permitted,"the model answered the granted request");
        assert.deepEqual(fork.choices().permissions,["custom-tool:delete_everything"],"the fork inherited the parent's grants");
      } finally { await harness.disposeAsync(); }
    } finally { secondStore.dispose(); }
  } finally { rmSync(directory,{ recursive: true }); }
  assert.ok(PermissionPolicy.isGranted(["read:/tmp/work"],"read","/tmp/work/file.txt"),"a grant covers paths beneath its scope");
  assert.ok(!PermissionPolicy.isGranted(["read:/tmp/work"],"read","/tmp/workshop/file.txt"),"a grant does not cover a sibling path that merely shares a prefix");
  assert.ok(!PermissionPolicy.isGranted(null,"custom-tool","delete_everything"),"no grants means no capability");
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
