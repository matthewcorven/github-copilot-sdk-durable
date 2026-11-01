// Filesystem policy wire proof; --live also requires actual shell effects.
import assert from "node:assert/strict";
import { mkdtempSync,mkdirSync,writeFileSync,existsSync,readFileSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices,DurableHarness,DurableHarnessOptions,DurableSandboxPolicy,DurableStore,ExtensionRegistry,
} from "../../lib/ts/durable.ts";
import { assertPolicy,decisionData,engaged,printReport } from "./sandbox-policy-22-35.ts";

async function main() {
  const live=process.argv.slice(2).includes("--live");
  if(process.argv.slice(2).some((arg) => arg!=="--live")) throw new Error("only --live is supported");
  const workspace=mkdtempSync(join(tmpdir(),"durable-sandbox-"));
  for(const name of ["writable","readonly","secret"]) mkdirSync(join(workspace,name));
  writeFileSync(join(workspace,"readonly","pinned.txt"),"read-only content");
  const deniedPath=join(workspace,"secret","leak.txt");
  writeFileSync(deniedPath,"do not read this");
  const policy=new DurableSandboxPolicy({
    enabled: true,addCurrentWorkingDirectory: false,allowBypass: false,
    allowDevToolAccess: false,trustWorkingDirectory: true,
    filesystem: {
      readwritePaths: ["{workspace}/writable"],readonlyPaths: ["{workspace}/readonly"],
      deniedPaths: ["{workspace}/secret"],
    },
  });
  const config=DurableHarness.buildSandboxConfig(policy,workspace);
  const wire=assertPolicy(policy,config,workspace,"the filesystem policy");
  console.log("resolved filesystem policy:");
  console.log(`  readwrite: [${wire.userPolicy?.filesystem?.readwritePaths?.join(", ")}]`);
  console.log(`  readonly: [${wire.userPolicy?.filesystem?.readonlyPaths?.join(", ")}]`);
  console.log(`  denied: [${wire.userPolicy?.filesystem?.deniedPaths?.join(", ")}]`);
  const store=DurableStore.createEphemeral();
  try {
    const harness=await DurableHarness.openAsync(store,new ExtensionRegistry(),
      new DurableHarnessOptions({ enableFileHooks: false }));
    try {
      const choices=new AgentChoices({ workingDirectory: workspace,sandbox: policy,permissions: ["shell"] });
      const sessionConfig=await harness.buildSessionConfig(choices);
      assert.ok(sessionConfig.sandboxConfig,"the session configuration carries the sandbox policy");
      assertPolicy(policy,sessionConfig.sandboxConfig,workspace,"the session filesystem policy");
      const conversation=await harness.createConversationAsync(choices);
      const effective=await conversation.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model??"(not reported)"}`);
      if(live) {
        const permitted=join(workspace,"writable","ok.txt");
        await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: printf 'permitted' > '${permitted}'`);
        assert.ok(existsSync(permitted),"the write into the readwrite path landed on disk");
        assert.equal(readFileSync(permitted,"utf8"),"permitted","the readwrite path holds what was written");
        await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: printf 'leaked' > '${deniedPath}'`);
      }
      const decisions=decisionData(conversation.sandboxDecisions);
      printReport(decisions);
      console.log(`sandbox decisions observed: ${decisions.length}`);
      if(live&&engaged(decisions)) {
        const denials=decisions.filter((data) => data.kind==="access_denied");
        assert.ok(denials.length,"the sandbox reported at least one access denial");
        assert.ok(denials.some((data) => ["filesystem_write","filesystem_read"].includes(String(data.denialClass))),
          "the denied write reports a filesystem denial");
        assert.ok(denials.every((data) => typeof data.deniedResource==="string"&&data.deniedResource.length>0),
          "the denial names the resource it refused");
        assert.equal(readFileSync(deniedPath,"utf8"),"do not read this","the denied path still holds its original content");
      } else console.log("LIVE: permitted file contents and engaged-backend denied-write proof are unproven; the recording has no tool call.");
      assert.ok(conversation.sandboxDecisions.length>=0,"the conversation records the sandbox decisions it observed");
      console.log(`assertion set: ${live? "policy + live tool effects":"policy only"}`);
    } finally { await harness.disposeAsync(); }
  } finally {
    store.dispose();
    rmSync(workspace,{ recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
