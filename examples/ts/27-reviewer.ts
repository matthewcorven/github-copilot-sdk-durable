// A read-only reviewer repeats its stop hook until no further findings remain.
import assert from "node:assert/strict";
import { mkdtempSync,writeFileSync,existsSync,readFileSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices,DurableHarness,DurableStore,Extension,ExtensionRegistry,
  PromptSection,type DurableConversation,type DurableTool,
} from "../../lib/ts/durable.ts";

const done="No further findings.";
async function main() {
  const worktree=mkdtempSync(join(tmpdir(),"durable-review-"));
  writeFileSync(join(worktree,"user.ts"),"export const name = (user) => user.name;\n");
  const read: DurableTool={
    name: "read_file",description: "Read a file",overridesBuiltInTool: true,
    parameters: { type: "object",properties: { path: { type: "string" } },required: ["path"] },
    handler: async (args) => {
      if(args===null||typeof args!=="object"||!("path" in args)||
        typeof args.path!=="string") throw new Error("read_file path must be a string");
      const path=args.path;
      const full=join(worktree,path);
      return existsSync(full)? readFileSync(full,"utf8"):`(no such file: ${path})`;
    },
  };
  const registry=new ExtensionRegistry();
  registry.install(new Extension("reviewer",[read],[
    new PromptSection("role",async () => "You review diffs. Report problems as a list. Never edit files."),
  ]));
  const store=DurableStore.createEphemeral();
  try {
    const harness=await DurableHarness.openAsync(store,registry);
    try {
      const root=await harness.rootAsync(new AgentChoices({
        extensions: ["reviewer"],tools: ["read_file"],workingDirectory: worktree,
        permissions: ["custom-tool:read_file","read"],
      }));
      const effective=await root.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model??"(not reported)"}`);
      let passes=0;
      let reviewer: DurableConversation|undefined;
      reviewer=await harness.createConversationAsync(new AgentChoices({
        model: "mai-code-1.1-flash",extensions: ["reviewer"],tools: ["read_file"],
        workingDirectory: worktree,permissions: ["custom-tool:read_file","read"],
        hooks: {
          onAgentStop: async () => {
            passes++;
            const answer=reviewer?.answers.at(-1)??"";
            return answer.toLowerCase().includes(done.toLowerCase())? undefined:{
              decision: "block",reason: `Look again for anything you missed. Say "${done}" when there is nothing left.`,
            };
          },
        },
      }));
      const agent=reviewer.agent();
      console.log(`reviewer: ${agent.model} [${agent.extensions.map((extension) => extension.name)}] [${agent.tools.map((tool) => tool.name)}] cwd=${agent.workingDirectory===worktree}`);
      await reviewer.submitAsync("Review user.ts.");
      console.log(`review passes: ${passes}`);
      for(const answer of reviewer.answers) console.log(`reviewer: ${answer}`);
      assert.equal(agent.model,"mai-code-1.1-flash","the reviewer uses its own model");
      assert.deepEqual(agent.extensions.map((extension) => extension.name),["reviewer"],"the reviewer selects its extension");
      assert.deepEqual(agent.tools.map((tool) => tool.name),["read_file"],"the reviewer has only the read tool");
      assert.equal(agent.workingDirectory,worktree,"the reviewer has its own working directory");
      assert.ok(passes>=2,"the review loop ran more than one pass");
      assert.ok(reviewer.answers.length>=2,"each pass produced an answer");
      assert.ok(reviewer.answers.at(-1)?.includes(done),"the last pass says there is nothing left");
    } finally {
      await harness.disposeAsync();
    }
  } finally {
    store.dispose();
    rmSync(worktree,{ recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
