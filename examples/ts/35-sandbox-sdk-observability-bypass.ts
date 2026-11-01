// Deterministic policy/observations; backend and offered-escalation proofs are conditional.
import assert from "node:assert/strict";
import { mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices,DurableHarness,DurableHarnessOptions,DurableSandboxPolicy,DurableStore,
  ExtensionRegistry,type SandboxConfig,
} from "../../lib/ts/durable.ts";
import { assertPolicy,decisionData,engaged,printReport } from "./sandbox-policy-22-35.ts";

function effectiveKeychain(config: SandboxConfig) {
  return config.userPolicy?.seatbelt!==undefined? config.userPolicy.seatbelt.keychainAccess
    :config.userPolicy?.experimental?.seatbelt?.keychainAccess;
}
async function main() {
  // --live cannot force backend engagement or a bypass prompt.
  if(process.argv.slice(2).some((argument) => argument!=="--live")) throw new Error("only --live is supported");
  const workspace=mkdtempSync(join(tmpdir(),"durable-sandbox-"));
  try {
    for(const name of ["writable","readonly","secret"]) mkdirSync(join(workspace,name));
    writeFileSync(join(workspace,"readonly","pinned.txt"),"read-only content");
    const deniedPath=join(workspace,"secret","leak.txt");
    writeFileSync(deniedPath,"do not read this");
    const policy=new DurableSandboxPolicy({
      enabled: true,addCurrentWorkingDirectory: false,allowBypass: false,
      allowDevToolAccess: false,trustWorkingDirectory: true,keychainAccess: true,
      filesystem: { readwritePaths: ["{workspace}/writable"],readonlyPaths: ["{workspace}/readonly"],deniedPaths: ["{workspace}/secret"] },
    });
    const config=DurableHarness.buildSandboxConfig(policy,workspace);
    const wire=assertPolicy(policy,config,workspace,"the observability policy");
    console.log("resolved filesystem policy:");
    console.log(`  readwrite: [${wire.userPolicy?.filesystem?.readwritePaths?.join(", ")}]`);
    console.log(`  readonly: [${wire.userPolicy?.filesystem?.readonlyPaths?.join(", ")}]`);
    console.log(`  denied: [${wire.userPolicy?.filesystem?.deniedPaths?.join(", ")}]`);
    console.log(`  allowBypass: ${policy.allowBypass? "True":"False"}, keychainAccess: ${policy.keychainAccess? "True":"False"}`);
    const legacy=DurableHarness.buildSandboxConfig(new DurableSandboxPolicy({
      enabled: true,filesystem: policy.filesystem,
    }),workspace);
    assert.equal(legacy.userPolicy?.seatbelt,undefined,"a policy without KeychainAccess leaves the top-level seatbelt block unset");
    assert.equal(legacy.userPolicy?.experimental?.seatbelt,undefined,"the durable layer never writes the deprecated experimental.seatbelt block");
    assert.ok(legacy.userPolicy);
    legacy.userPolicy.experimental={ seatbelt: { keychainAccess: true } };
    assert.equal(effectiveKeychain(legacy),true,"the deprecated location is consulted only when the top-level seatbelt block is absent");
    const conflicting=structuredClone(config);
    assert.ok(conflicting.userPolicy);
    conflicting.userPolicy.experimental={ seatbelt: { keychainAccess: false } };
    assert.equal(conflicting.userPolicy.seatbelt?.keychainAccess,true,"the top-level seatbelt block carries the durable policy's keychain decision");
    assert.equal(effectiveKeychain(conflicting),true,"the legacy location is not consulted while the top-level seatbelt block is present");

    const store=DurableStore.createEphemeral();
    let decisions: Record<string,unknown>[]=[];
    try {
      const harness=await DurableHarness.openAsync(store,new ExtensionRegistry(),
        new DurableHarnessOptions({ enableFileHooks: false }));
      try {
        const choices=new AgentChoices({ workingDirectory: workspace,sandbox: policy,permissions: ["shell"] });
        const sessionConfig=await harness.buildSessionConfig(choices);
        assert.equal(sessionConfig.trustWorkingDirectory,"user","TrustWorkingDirectory = true maps to WorkingDirectoryTrust.User");
        assert.ok(sessionConfig.sandboxConfig);
        assertPolicy(policy,sessionConfig.sandboxConfig,workspace,"the session observability policy");
        const trustConfig=await harness.buildSessionConfig(new AgentChoices({
          workingDirectory: workspace,permissions: ["shell"],
          sandbox: new DurableSandboxPolicy({ enabled: true,trustWorkingDirectory: false }),
        }));
        assert.equal(trustConfig.trustWorkingDirectory,"session","TrustWorkingDirectory = false maps to WorkingDirectoryTrust.Session");
        const conversation=await harness.createConversationAsync(choices);
        assert.ok(conversation.session,"the session was created with TrustWorkingDirectory set");
        const effective=await conversation.waitForEffectiveModelAsync();
        console.log(`effective model: ${effective.model??"(not reported)"}`);
        await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: printf 'leaked' > '${deniedPath}'`);
        const control=join(workspace,"writable","control.txt");
        await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: printf 'control' > '${control}'`);
        decisions=decisionData(conversation.sandboxDecisions);
        const context=await conversation.contextAsync();
        console.log();
        printReport(decisions);
        console.log(`observed decision kinds, in order (${decisions.length}):`);
        for(const data of decisions) console.log(`  ${data.kind}`);
        console.log();
        if(engaged(decisions)) {
          for(const kind of ["policy_resolved","spawn_completed","enforcement_state"]) {
            assert.ok(decisions.some((data) => data.kind===kind),`the runtime reported ${kind}`);
          }
          const denial=decisions.find((data) => data.kind==="access_denied");
          assert.ok(denial&&typeof denial.toolCallId==="string","the denial names the tool call it refused");
          const permissions=context.filter((event) => event.type==="permission.completed"&&event.data.toolCallId===denial.toolCallId);
          assert.ok(permissions.length,"the blocked shell call produced a permission completion");
          for(const event of permissions) {
            if(event.type==="permission.completed") assert.ok(!event.data.result.kind.toLowerCase().includes("denied"),"the permission result is not a denial");
          }
          assert.ok(["filesystem_read","filesystem_write"].includes(String(denial.denialClass)),"the blocked write reports a filesystem denial");
          assert.ok(typeof denial.deniedResource==="string"&&denial.deniedResource.length>0,"the denial names the resource it refused");
          assert.equal(denial.control,"filesystem","the denial is a filesystem denial");
          assert.equal(denial.enforcementPoint,"shell","the denial came from the shell enforcement point");
          assert.equal(denial.outcome,"denied","the denial reports the denied outcome");
          const tool=context.find((event) => event.type==="tool.execution_complete"&&event.data.toolCallId===denial.toolCallId);
          assert.ok(tool?.type==="tool.execution_complete","the blocked call has a completion event");
          const text=[tool.data.result?.content,tool.data.result?.detailedContent,tool.data.error?.message].filter(Boolean).join("\n");
          assert.ok(["denied","not permitted","permission","sandbox","refus","blocked"].some((marker) => text.toLowerCase().includes(marker)),
            "the tool output shows the refusal");
          assert.equal(readFileSync(deniedPath,"utf8"),"do not read this","the denied path still holds its original content");
          assert.equal(readFileSync(control,"utf8"),"control","the control write into the readwrite path succeeded");
          if(tool.data.sandboxed!==undefined) assert.equal(tool.data.sandboxed,true,"the blocked shell call ran sandboxed");
          if(process.platform==="darwin") assert.equal(decisions.find((data) => data.kind==="policy_resolved")?.keychainAccess,true,
            "macOS granted the keychain access the policy requested");
        } else {
          console.log("no sandbox backend engaged on this host: the policy was replayed, not enforced");
          console.log("the three-part proof needs a backend that actually applies the policy, so it is skipped");
        }
        assert.equal(decisions.filter((data) => data.kind==="bypass_decided").length,0,"a session with AllowBypass = false recorded no bypass decision");
      } finally { await harness.disposeAsync(); }
    } finally { store.dispose(); }

    let hostInvocations=0; let escalationPrompts=0; let declinedBypasses=0;
    const inspected: string[]=[];
    const bypassPolicy=new DurableSandboxPolicy({ ...policy,allowBypass: true });
    assert.equal(bypassPolicy.allowBypass,true,"the second policy asks for the bypass capability");
    const bypassStore=DurableStore.createEphemeral();
    try {
      const harness=await DurableHarness.openAsync(bypassStore,new ExtensionRegistry(),
        new DurableHarnessOptions({
          enableFileHooks: false,
          permissionHandler: (request,invocation) => {
            hostInvocations++;
            const prompt=invocation.promptRequest;
            const fields=prompt?.kind==="commands"? prompt:request.kind==="shell"? request:undefined;
            const escalation=!!(fields?.requestSandboxBypass||fields?.requestSandboxPermissive||fields?.sandboxPathGrant);
            const seen=`request #${hostInvocations}: kind=${prompt?.kind??request.kind}, bypass=${fields?.requestSandboxBypass??false}, permissive=${fields?.requestSandboxPermissive??false}, pathGrant=${JSON.stringify(fields?.sandboxPathGrant??null)}`;
            inspected.push(seen);
            console.log(`  host handler saw ${seen}`);
            if(escalation) {
              escalationPrompts++; declinedBypasses++;
              return { kind: "reject",feedback: "declined by the host: commands must stay inside the sandbox" };
            }
            return { kind: "approve-once" };
          },
        }));
      try {
        const conversation=await harness.createConversationAsync(new AgentChoices({
          workingDirectory: workspace,sandbox: bypassPolicy,permissions: ["shell"],
        }));
        const effective=await conversation.waitForEffectiveModelAsync();
        console.log(`effective model: ${effective.model??"(not reported)"}`);
        const command=`printf 'leaked' > '${join(workspace,"secret","bypass.txt")}'`;
        await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: ${command}. If the sandbox refuses the write, request the sandbox bypass for the same command and report what the host decides.`);
        await conversation.submitAsync(`Use the shell tool with mode="async" and detach=true to start this command in the background, then report what happened: ${command}`);
        const bypassDecisions=decisionData(conversation.sandboxDecisions);
        console.log();
        console.log(`second session, AllowBypass = true: ${bypassDecisions.length} decision(s)`);
        const bypasses=bypassDecisions.filter((data) => data.kind==="bypass_decided");
        if(escalationPrompts>0||bypasses.length>0) {
          assert.equal(declinedBypasses,1,"exactly one host-handler invocation declined the bypass");
          assert.ok(hostInvocations>=declinedBypasses,"every declined bypass came from an inspected prompt");
          assert.equal(bypasses.length,1,"the declined bypass is recorded as one bypass_decided event");
          assert.equal(bypasses[0].outcome,"declined","the bypass decision records the host's decline");
          assert.equal(bypasses[0].source,"model_requested","the bypass was requested by the model");
        } else {
          assert.equal(declinedBypasses,0,"no bypass was declined because none was offered");
          assert.equal(bypasses.length,0,"no bypass_decided event was reported because none was offered");
          console.log("notice: the runtime offered no sandbox-bypass escalation for this session, so the decline path was not exercised. The host handler was installed and the policy set AllowBypass = true; the runtime decides whether to surface the prompt, and the SDK exposes no call that forces it. The handler's decline branch is therefore unproven here.");
        }
      } finally { await harness.disposeAsync(); }
    } finally { bypassStore.dispose(); }
    console.log();
    console.log(`assertion set: ${engaged(decisions)? "policy + enforcement":"policy only"}`);
    console.log(`host-handler prompts inspected: ${inspected.length}, escalation prompts seen: ${escalationPrompts}, bypasses declined: ${declinedBypasses}`);
  } finally { rmSync(workspace,{ recursive: true }); }
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
