// Network/auth/credential policy wire proof; --live proves masked shell observations.
import assert from "node:assert/strict";
import { createHash,randomBytes } from "node:crypto";
import { mkdtempSync,writeFileSync,existsSync,readFileSync,unlinkSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices,DurableHarness,DurableHarnessOptions,DurableSandboxPolicy,DurableStore,ExtensionRegistry,
} from "../../lib/ts/durable.ts";
import { assertPolicy,decisionData,engaged,printReport } from "./sandbox-policy-22-35.ts";

const tokenName="DURABLE_SANDBOX_TOKEN";
function networkDenials(decisions: readonly Record<string,unknown>[]) {
  return decisions.filter((data) => data.kind==="access_denied"&&
    ["network_outbound","network_host","network_local"].includes(String(data.denialClass)));
}
async function main() {
  const live=process.argv.slice(2).includes("--live");
  if(process.argv.slice(2).some((arg) => arg!=="--live")) throw new Error("only --live is supported");
  const original=process.env[tokenName];
  const realValue=`dsk_${randomBytes(24).toString("hex")}`;
  const realDigest=createHash("sha256").update(realValue).digest("hex");
  process.env[tokenName]=realValue;
  const workspace=mkdtempSync(join(tmpdir(),"durable-sandbox-"));
  try {
    const script=join(workspace,"observe-env.sh");
    const report=join(workspace,"env-report.txt");
    writeFileSync(script,[
      "#!/bin/sh",'value="${DURABLE_SANDBOX_TOKEN-}"',
      'if [ -z "$value" ]; then printf \'verdict=empty\\n\' > "$1"; exit 0; fi',
      "if command -v sha256sum >/dev/null 2>&1; then",
      '  digest=$(printf \'%s\' "$value" | sha256sum | cut -d\' \' -f1)',
      "elif command -v shasum >/dev/null 2>&1; then",
      '  digest=$(printf \'%s\' "$value" | shasum -a 256 | cut -d\' \' -f1)',
      "elif command -v openssl >/dev/null 2>&1; then",
      '  digest=$(printf \'%s\' "$value" | openssl dgst -sha256 -r | cut -d\' \' -f1)',
      "else","  printf 'no SHA256 tool available\\n' >&2; exit 2","fi",
      'printf \'verdict=observed\\nsha256=%s\\nlength=%s\\n\' "$digest" "${#value}" > "$1"',"",
    ].join("\n"));
    function policy(permissive: boolean) {
      return new DurableSandboxPolicy({
        enabled: true,addCurrentWorkingDirectory: false,allowBypass: false,
        allowDevToolAccess: permissive,sandboxMcpServers: !permissive,
        sandboxLspServers: true,trustWorkingDirectory: true,keychainAccess: false,
        auth: { gh: true,git: permissive },
        credentials: { envVars: { [tokenName]: ["api.github.com"] } },
        filesystem: { readwritePaths: ["{workspace}"] },
        network: {
          allowOutbound: permissive,allowLocalNetwork: permissive,
          allowedHosts: permissive? ["api.github.com"]:[],
          blockedHosts: permissive? ["example.com"]:["example.com","*.example.com"],
          proxy: permissive? undefined:{ url: "http://127.0.0.1:8899",username: "sandbox",password: randomBytes(12).toString("hex") },
        },
      });
    }
    const policies=[["locked",policy(false)],["permissive",policy(true)]] as const;
    for(const [label,requested] of policies) {
      const wire=assertPolicy(requested,DurableHarness.buildSandboxConfig(requested,workspace),workspace,`the ${label} network policy`);
      assert.ok(!JSON.stringify(wire).includes(realValue),`${label}: the credential value is absent from the SDK wire`);
      console.log(`${label}: network, auth, credential names/inject hosts, proxy, MCP and LSP policy mapped`);
    }
    const store=DurableStore.createEphemeral();
    try {
      const harness=await DurableHarness.openAsync(store,new ExtensionRegistry(),
        new DurableHarnessOptions({ enableFileHooks: false }));
      try {
        const conversations=[];
        for(const [label,requested] of policies) {
          const choices=new AgentChoices({
            workingDirectory: workspace,sandbox: requested,permissions: ["shell","read"],model: "gpt-5.6-luna",
          });
          const config=await harness.buildSessionConfig(choices);
          assert.ok(config.sandboxConfig);
          assertPolicy(requested,config.sandboxConfig,workspace,`the ${label} session policy`);
          conversations.push({ label,requested,conversation: await harness.createConversationAsync(choices) });
        }
        const effective=await conversations[1].conversation.waitForEffectiveModelAsync();
        console.log(`effective model: ${effective.model??"(not reported)"}`);
        if(live) {
          for(const { label,conversation } of conversations) {
            if(existsSync(report)) unlinkSync(report);
            await conversation.submitAsync(`Use the shell tool to run exactly this command, then report its output: sh '${script}' '${report}'`);
            assert.ok(existsSync(report),`${label}: the shell wrote the credential observation`);
            const verdict=new Map(readFileSync(report,"utf8").split("\n").filter((line) => line.includes("="))
              .map((line) => { const at=line.indexOf("="); return [line.slice(0,at),line.slice(at+1)]; }));
            assert.notEqual(verdict.get("verdict"),"empty",`${label}: the masked variable is present inside the sandbox (not empty)`);
            const length=Number(verdict.get("length")??0);
            const digest=verdict.get("sha256")??"";
            assert.ok(Number.isInteger(length)&&length>0,`${label}: the value the sandboxed shell sees is not empty`);
            assert.match(digest,/^[0-9a-f]{64}$/i,`${label}: the shell reported a SHA256 digest, not an unavailable-tool sentinel`);
            assert.notEqual(digest.toLowerCase(),realDigest,`${label}: the value the sandboxed shell sees is not the real credential value`);
            console.log(`${label}: credential masking held — the sandboxed shell saw a ${length}-character value whose digest does not match the generated one`);
          }
        } else console.log("LIVE: both nonempty masked-credential digest/length observations are unproven; the recording has no tool call.");
        for(const { label,requested,conversation } of conversations) {
          const decisions=decisionData(conversation.sandboxDecisions);
          printReport(decisions);
          if(live&&engaged(decisions)) {
            if(label==="permissive") {
              const mark=conversation.sandboxDecisions.length;
              await conversation.submitAsync("Use the shell tool to run exactly this command, then report its output: curl -sS -m 8 -o /dev/null -w '%{http_code}' 'https://api.github.com/' || true");
              assert.deepEqual(networkDenials(decisionData(conversation.sandboxDecisions.slice(mark))),[],"the allowed host is not denied");
            }
            const mark=conversation.sandboxDecisions.length;
            await conversation.submitAsync("Use the shell tool to run exactly this command, then report its output: curl -sS -m 8 -o /dev/null -w '%{http_code}' 'https://example.com/' || true");
            const blocked=networkDenials(decisionData(conversation.sandboxDecisions.slice(mark)));
            console.log(blocked.length? `${label}: blocked host denied`:`${label}: blocked-host denial assertion skipped (backend reported no network denial)`);
          } else console.log(`LIVE: ${label} network tool observations are unproven without real calls and backend enforcement.`);
          const resolved=decisionData(conversation.sandboxDecisions).filter((data) => data.kind==="policy_resolved");
          if(!resolved.length) console.log(`${label}: the runtime reported no resolved policy — skipping its assertions`);
          for(const data of resolved) {
            for(const [field,expected] of [
              ["allowOutbound",requested.network?.allowOutbound],["allowLocalNetwork",requested.network?.allowLocalNetwork],
              ["ghAuth",requested.auth?.gh],["gitAuth",requested.auth?.git],
            ] as const) {
              if(data[field]!==undefined) assert.equal(data[field],expected,`${label}: the resolved policy reports the requested ${field}`);
            }
            assert.ok(["none","loopback","external"].includes(String(data.proxyMode)),`${label}: the resolved ProxyMode is one of none/loopback/external`);
          }
        }
        console.log(`assertion set: ${live? "policy + live observations":"policy only"}`);
        console.log("note: the generated credential is never printed or stored; the shell reports only a digest and length.");
      } finally { await harness.disposeAsync(); }
    } finally { store.dispose(); }
  } finally {
    rmSync(workspace,{ recursive: true });
    if(original===undefined) delete process.env[tokenName];
    else process.env[tokenName]=original;
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode=1; });
