// In-flight v1, next-call v2, and a second process rebinding the stored name to v3.
// The C# reference takes ~124s here. Allow 300s outside; the tool-start gate is 120s.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join,resolve } from "node:path";
import {
  AgentChoices,DurableHarness,DurableStore,DurableStoreOptions,
  Extension,ExtensionRegistry,type DurableTool,
} from "../../lib/ts/durable.ts";

async function run(directory: string,phase: string) {
  let start!: () => void;
  const started=new Promise<void>((resolve) => { start=resolve; });
  let release!: () => void;
  const gate=new Promise<void>((resolve) => { release=resolve; });
  let holdOpen=true;
  function load(version: string): DurableTool {
    return {
      name: "version",description: "Report the tool's code version",
      parameters: { type: "object",properties: {} },
      handler: async () => { start(); if(holdOpen) await gate; return version; },
    };
  }
  const registry=new ExtensionRegistry();
  const choices=new AgentChoices({
    extensions: ["versioned"],tools: ["version"],permissions: ["custom-tool:version"],
  });
  const options=new DurableStoreOptions();
  options.path=join(directory,"session.jsonl");
  const store=DurableStore.create(options);
  let duringReload="";
  let afterReload="";
  try {
    const harness=await DurableHarness.openAsync(store,registry);
    try {
      if(phase==="second") {
        const root=await harness.rootAsync();
        console.log(`after restart, before install: [${root.agent().tools.map((tool) => tool.name).join(", ")}]`);
        registry.install(new Extension("versioned",[load("v3")]));
        console.log(`after install: [${root.agent().tools.map((tool) => tool.name).join(", ")}]`);
        await root.configureAsync(choices);
        console.log(`after install, configured: [${root.agent().tools.map((tool) => tool.name).join(", ")}]`);
        return;
      }
      registry.install(new Extension("versioned",[load("v1")]));
      const root=await harness.rootAsync(choices);
      const effective=await root.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model??"(not reported)"}`);
      const submission=root.submitAsync("Call the version tool and report what it returns.");
      let timer: ReturnType<typeof setTimeout>|undefined;
      try {
        await Promise.race([
          started,
          new Promise<never>((_,reject) => { timer=setTimeout(() => reject(new Error("the version tool did not start within 120s")),120_000); }),
        ]);
        registry.install(new Extension("versioned",[load("v2")]));
        await root.configureAsync(choices);
      } finally {
        if(timer) clearTimeout(timer);
        release();
      }
      await submission;
      duringReload=root.answers.at(-1)??"";
      console.log(`call running during the reload: ${duringReload}`);
      holdOpen=false;
      await root.submitAsync("Call the version tool again and report what it returns.");
      afterReload=root.answers.at(-1)??"";
      console.log(`next call: ${afterReload}`);
    } finally { await harness.disposeAsync(); }
  } finally { store.dispose(); }
  // Preserve the current TS loader; close the first writer before spawning the second.
  const result=await new Promise<{ code: number|null; output: string; error: string }>((resolveResult,reject) => {
    const child=spawn(process.execPath,[...process.execArgv,resolve(process.argv[1]),"second"],{
      env: { ...process.env,DURABLE_RELOAD_DIR: directory },
      stdio: ["ignore","pipe","pipe"],
      timeout: 180_000,
    });
    let output=""; let error="";
    child.stdout.on("data",(chunk: Buffer) => { output+=chunk.toString(); });
    child.stderr.on("data",(chunk: Buffer) => { error+=chunk.toString(); });
    child.on("error",reject);
    child.on("close",(code) => resolveResult({ code,output,error }));
  });
  console.log(result.output.trim());
  assert.equal(result.code,0,`the second process succeeded: ${result.error}`);
  assert.ok(duringReload.includes("v1"),"the call running during the reload finished on the old code");
  assert.ok(afterReload.includes("v2"),"the next call used the reloaded code");
  assert.ok(result.output.includes("after restart, before install: []"),"the restarted process had not installed the extension yet");
  assert.ok(result.output.includes("after install: [version]"),"the restarted process bound the stored name to the new code");
}
async function main() {
  const phase=process.argv[2]??"first";
  if(!["first","second"].includes(phase)) throw new Error("phase must be first or second");
  const existing=process.env.DURABLE_RELOAD_DIR;
  if(existing) return run(existing,phase);
  if(phase==="second") throw new Error("the second process needs DURABLE_RELOAD_DIR");
  const directory=mkdtempSync(join(tmpdir(),"durable-reload-"));
  try { await run(directory,phase); }
  finally { rmSync(directory,{ recursive: true }); }
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
